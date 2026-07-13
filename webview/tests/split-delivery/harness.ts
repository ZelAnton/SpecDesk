/**
 * Delivery-smoke harness for the Split scroll-sync gate (T-108).
 *
 * This is NOT a bag of test doubles. It runs the STANDARD bundle process (`node scripts/bundle.mjs`,
 * the same command CI's `npm run bundle` runs), checks the T-107 content manifest, and then executes the
 * exact `wwwroot/webview.js` the host serves — the real `SplitSync` coordinator, the real `HeightSync`,
 * the real CodeMirror source editor and the real ProseMirror formatted editor, wired by the real
 * `index.ts` entrypoint. Nothing in the Split path is stubbed.
 *
 * The single thing jsdom cannot provide is LAYOUT: `getBoundingClientRect`/client rects all read 0 with
 * no rendering engine. So the one permitted seam is a deterministic layout adapter ({@link installLayoutAdapter})
 * that gives every rendered formatted leaf a fixed, content-derived height and lays them out top-to-bottom.
 * The CodeMirror side needs no adapter: under jsdom its height map falls back to a uniform estimated line
 * height (14 px per line), which is deterministic, and the spacer widgets report their exact pixel height
 * into it — so the padded source geometry the gate drives is exercised through the SHIPPED build's real
 * reconcile / coupling / scheduler code, not a re-implementation of it.
 *
 * KNOWN LIMITATION — this gate proves WIRING, not real-engine GEOMETRY. Because the formatted leaves get
 * synthetic heights (40–120 px) while CodeMirror keeps its uniform 14 px estimated line, the formatted pane
 * always outgrows the source here, so height-sync's pad-only plan can reach EXACT alignment and spacers
 * always appear. A real WebView2 renders both panes for real, where a source region can be as tall as (or
 * taller than) its rendered counterpart — so the spacer COUNT and the sub-pixel alignment are engine
 * geometry the jsdom numbers cannot stand in for. What this gate does prove is that the real modules are
 * present in the shipped tree, wired by the real `index.ts`, and that a genuine user scroll couples the
 * sibling through the real maps/scheduler with the real 120 ms scroll-settle (see {@link scrollPane} in the
 * spec). Treat "spacers appear / align within 1 px" as a WIRING assertion on rigged geometry, not a promise
 * about a real engine's pixels; real-engine spacer/alignment geometry is covered one rung up by the Layer 1
 * Playwright suite (`e2e/`, real Chromium) — see docs/testing.md. This gate stays as the fast browserless
 * wiring check.
 *
 * Loading the artifact: the bundle is an ES module whose only export is `shouldMirrorInto` and whose body
 * runs `wire()` on load. We strip that trailing `export { … }` and execute the identical body via
 * `new Function`, so the DOM, the mock host bridge and the layout adapter are all in place before the real
 * wiring runs — and each scenario re-executes it against a fresh DOM for isolation.
 */

import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdirSync, readFileSync, rmSync, statSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { readManifest, VerifyStatus, verifyBundle } from "../../scripts/webview-manifest.mjs";

const harnessDir = dirname(fileURLToPath(import.meta.url));
export const webviewDir = join(harnessDir, "..", "..");
const scriptsDir = join(webviewDir, "scripts");
export const wwwrootDir = join(webviewDir, "..", "src", "SpecDesk.Host", "wwwroot");

/** The height (px) a rendered formatted leaf is laid out at, by tag. Paragraphs/headings without a fixed
 *  entry fall to a content-length-derived height, so a fixture's paragraphs get genuinely different
 *  heights and a longer block is taller — the geometry the alignment assertions lean on. */
const FIXED_HEIGHTS: Record<string, number> = {
  H1: 40,
  H2: 34,
  TR: 30,
  LI: 32,
  PRE: 100,
  BLOCKQUOTE: 46,
  HR: 16,
};

/** Per-element height overrides, for the "content grew, re-settle" scenario. Cleared on every re-wire. */
const heightOverrides = new Map<Element, number>();

function leafHeight(el: Element): number {
  const override = heightOverrides.get(el);
  if (override !== undefined) {
    return override;
  }
  const fixed = FIXED_HEIGHTS[el.tagName];
  if (fixed !== undefined) {
    return fixed;
  }
  const length = el.textContent?.length ?? 0;
  return 30 + 30 * Math.floor(length / 30);
}

/** The rendered leaf units of the formatted pane in document order — top-level blocks, plus each table
 *  row and each list item as its own leaf (matching the semantic anchors sync-anchors.ts projects). */
function formattedLeaves(root: Element): Element[] {
  const out: Element[] = [];
  for (const child of Array.from(root.children)) {
    if (child.tagName === "TABLE") {
      out.push(...Array.from(child.querySelectorAll("tr")));
    } else if (child.tagName === "UL" || child.tagName === "OL") {
      out.push(...Array.from(child.querySelectorAll("li")));
    } else {
      out.push(child);
    }
  }
  return out;
}

/** The current synthetic layout of the formatted pane: each leaf's content-relative top and height. */
export function formattedLayout(): Map<Element, { top: number; height: number }> {
  const layout = new Map<Element, { top: number; height: number }>();
  const root = document.querySelector("#formatted .ProseMirror");
  if (root === null) {
    return layout;
  }
  let y = 0;
  for (const leaf of formattedLeaves(root)) {
    const height = leafHeight(leaf);
    layout.set(leaf, { top: y, height });
    y += height;
  }
  return layout;
}

/** The content-relative top the layout adapter reports for a formatted leaf (its "given geometry"). */
export function formattedTopOf(el: Element): number {
  const box = formattedLayout().get(el);
  if (box === undefined) {
    throw new Error("element is not a laid-out formatted leaf");
  }
  return box.top;
}

/** Grow/shrink one leaf's laid-out height (the "an image finished decoding" case), then a resize/reconcile
 *  re-measures it. */
export function setLeafHeight(el: Element, height: number): void {
  heightOverrides.set(el, height);
}

function domRect(top: number, height: number): DOMRect {
  return {
    top,
    height,
    bottom: top + height,
    left: 0,
    right: 320,
    width: 320,
    x: 0,
    y: top,
    toJSON: () => ({}),
  } as DOMRect;
}

/** Height (px) the formatted pane viewport reports — large enough that reveal math has room to work. */
const PANE_VIEWPORT = 400;

/**
 * Install the deterministic layout adapter: `getBoundingClientRect` returns the synthetic layout for the
 * formatted pane and its leaves, and falls straight through to jsdom's own (zeroed) implementation for
 * everything else — CodeMirror's internals keep their estimated-line-height geometry untouched. Range
 * client-rect methods are stubbed too, since jsdom lacks them and CodeMirror probes them while measuring.
 */
export function installLayoutAdapter(): void {
  const originalRect = Element.prototype.getBoundingClientRect;
  const emptyRectList = Object.assign([] as unknown[], {
    item: () => null,
  }) as unknown as DOMRectList;
  Range.prototype.getClientRects = () => emptyRectList;
  Range.prototype.getBoundingClientRect = () => domRect(0, 0);
  Element.prototype.getBoundingClientRect = function getBoundingClientRect(this: Element): DOMRect {
    const formatted = document.querySelector("#formatted");
    if (formatted === null) {
      return originalRect.call(this);
    }
    if (this === formatted) {
      return domRect(0, PANE_VIEWPORT);
    }
    if (!formatted.contains(this)) {
      return originalRect.call(this);
    }
    const box = formattedLayout().get(this);
    if (box === undefined) {
      return originalRect.call(this);
    }
    const scrollTop = formatted instanceof HTMLElement ? formatted.scrollTop : 0;
    return domRect(box.top - scrollTop, box.height);
  };
}

/** A native→webview frame the host would send; the harness relays it into the wired `ipc` client. */
export interface HostFrame {
  kind: string;
  version?: number;
  payload?: unknown;
}

/** The mock host bridge a wired app talks to: messages it sent out, and a way to push frames in. */
export interface WiredApp {
  readonly sent: HostFrame[];
  emit(frame: HostFrame): void;
}

function installMatchMedia(): void {
  const stub = (query: string): MediaQueryList =>
    ({
      matches: false,
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
      addListener: () => {},
      removeListener: () => {},
      dispatchEvent: () => false,
      onchange: null,
    }) as unknown as MediaQueryList;
  window.matchMedia = stub as unknown as typeof window.matchMedia;
}

// wire() registers window-level "resize"/"focus" listeners it never removes; each re-wire against a fresh
// DOM would otherwise leave a prior scenario's listener firing into detached editors. Track and strip them.
let priorWindowListeners: Array<[string, EventListenerOrEventListenerObject]> = [];

// Compiling the ~2 MB bundle body is the costly part; the code is identical across scenarios, so compile
// it once (still a fresh, isolated execution — its own module scope and `ipc` — on every call).
let compiledBundle: { code: string; run: () => void } | null = null;
function bundleRunner(code: string): () => void {
  if (compiledBundle === null || compiledBundle.code !== code) {
    compiledBundle = { code, run: new Function(code) as () => void };
  }
  return compiledBundle.run;
}

/**
 * Execute the built bundle body against a fresh DOM built from the shipped index.html + styles.css and a
 * fresh mock host bridge — the real `wire()` runs exactly as it does in the host shell. Returns the bridge.
 */
export function wire(code: string, html: string, css: string): WiredApp {
  document.documentElement.innerHTML = html.replace(/<script[\s\S]*?<\/script>/g, "");
  const style = document.createElement("style");
  style.textContent = css;
  document.head.appendChild(style);
  installMatchMedia();
  heightOverrides.clear();

  for (const [type, listener] of priorWindowListeners) {
    window.removeEventListener(type, listener);
  }
  priorWindowListeners = [];
  const rawAdd = window.addEventListener.bind(window);
  window.addEventListener = ((
    type: string,
    listener: EventListenerOrEventListenerObject,
    options?: boolean | AddEventListenerOptions,
  ): void => {
    priorWindowListeners.push([type, listener]);
    rawAdd(type, listener, options);
  }) as typeof window.addEventListener;

  const sent: HostFrame[] = [];
  let receive: ((raw: string) => void) | undefined;
  Object.defineProperty(globalThis, "external", {
    value: {
      sendMessage: (raw: string) => sent.push(JSON.parse(raw) as HostFrame),
      receiveMessage: (callback: (raw: string) => void) => {
        receive = callback;
      },
    },
    configurable: true,
  });

  bundleRunner(code)();
  window.addEventListener = rawAdd;

  return {
    sent,
    emit: (frame) => receive?.(JSON.stringify(frame)),
  };
}

/** Feed a `doc.loaded` frame (the host's "here is the document") and let the reconcile settle. */
export async function loadDocument(app: WiredApp, text: string): Promise<void> {
  app.emit({ kind: "doc.loaded", payload: { path: "spec.md", text, docDir: "", readOnly: false } });
  await flushFrames();
}

/** Run a handful of animation frames + macrotasks so the rAF-throttled scroll handlers and the
 *  generation-aware reconcile scheduler drain to a fixed point (applying spacers re-measures, which can
 *  schedule one more frame). */
export async function flushFrames(frames = 8): Promise<void> {
  for (let i = 0; i < frames; i++) {
    await new Promise<void>((resolve) => {
      requestAnimationFrame(() => resolve());
    });
    await new Promise<void>((resolve) => {
      setTimeout(resolve, 0);
    });
  }
}

/** Wait real wall-clock time — only for the 120 ms scroll-settle debounce, which is genuinely timer-based. */
export function delay(ms: number): Promise<void> {
  return new Promise((resolve) => {
    setTimeout(resolve, ms);
  });
}

/** The editors' scroll-settle debounce (editor.ts / formatted.ts `SCROLL_SETTLE_MS`). A real scroll arms it
 *  on every event and it fires this long after the LAST one; a faithful "the author scrolled a pane" gesture
 *  is not over until it has fired. */
const SCROLL_SETTLE_MS = 120;

/**
 * Model ONE complete user scroll of a real scroll container: move it, let the rAF-throttled coordinator
 * couple the sibling, and then let the 120 ms scroll-settle debounce FULLY FIRE before returning — exactly
 * as a human scrolls a pane and pauses before doing anything else.
 *
 * Draining the settle here is load-bearing, not cosmetic. The real editors arm a scroll-settle debounce on
 * every scroll event; if a step returns while that timer is still pending, it fires LATER — during the next
 * step — where `SplitSync.settle` re-couples from a now-stale scroll position and races that step's own
 * throttled scroll (the settle can land first, drag the pane the author just scrolled back to the previous
 * pane's line, and then the genuine scroll is misread as an echo and dropped). That leak is a function of
 * wall-clock timing between steps, so it made these gates NON-DETERMINISTIC — a green run proved nothing.
 * Settling each gesture fully removes the cross-step leak and makes every scenario reproducible.
 */
export async function scrollPane(el: HTMLElement, top: number): Promise<void> {
  el.scrollTop = top;
  el.dispatchEvent(new Event("scroll"));
  await flushFrames(3); // the live rAF couple
  await delay(SCROLL_SETTLE_MS + 40); // let the scroll-settle debounce fire (the gesture is now over)
  await flushFrames(3); // drain the settle's own re-couple + its suppressed echo
}

function must<T extends Element>(selector: string): T {
  const el = document.querySelector<T>(selector);
  if (el === null) {
    throw new Error(`expected element ${selector} to exist`);
  }
  return el;
}

/** The real CodeMirror scroll container (`view.scrollDOM`) — the coordinator reads/writes its scrollTop. */
export function codeScroller(): HTMLElement {
  return must<HTMLElement>("#editor .cm-scroller");
}

/** The real CodeMirror editable content element (focus target). */
export function codeContent(): HTMLElement {
  return must<HTMLElement>("#editor .cm-content");
}

/** The formatted pane's own scroll container (`#formatted`). */
export function formattedPane(): HTMLElement {
  return must<HTMLElement>("#formatted");
}

/** The real ProseMirror content root inside the formatted pane. */
export function formattedContent(): HTMLElement {
  return must<HTMLElement>("#formatted .ProseMirror");
}

/** The live source-editor spacer widgets (`.cm-sync-spacer`) height-sync applied. */
export function spacerElements(): HTMLElement[] {
  return Array.from(document.querySelectorAll<HTMLElement>("#editor .cm-sync-spacer"));
}

/** Find the first laid-out formatted leaf matching `predicate` (e.g. the table row containing "r2a"). */
export function findLeaf(predicate: (el: Element) => boolean): HTMLElement {
  for (const el of formattedLayout().keys()) {
    if (predicate(el) && el instanceof HTMLElement) {
      return el;
    }
  }
  throw new Error("no formatted leaf matched the predicate");
}

/** What one bundle build produced, ready to load and assert on. */
export interface BundleArtifact {
  readonly code: string;
  readonly html: string;
  readonly css: string;
  readonly jsSha256: string;
  readonly verification: { status: string; reason: string };
  readonly manifest: {
    schema: number;
    kind: string;
    inputFingerprint: string;
    outputFingerprint: string;
    outputs: { path: string; sha256: string }[];
  } | null;
}

/**
 * Serialize the bundle build + artifact read across the delivery test files. Vitest runs test FILES in
 * parallel forks and more than one delivery gate calls {@link buildBundle} in its `beforeAll`; two
 * concurrent `bundle.mjs` runs write the SAME `wwwroot` outputs, so an unlocked second build can tear the
 * files out from under the first gate's read. A `mkdir`-based lock (atomic on every platform) held across
 * build AND read keeps each gate's artifact self-consistent; a lock older than a minute is treated as a
 * crashed holder's leftover and reclaimed. The lock lives in the OS temp dir (shared by the forks, out
 * of wwwroot so the manifest never sees it, and invisible to git if a crash leaks it).
 */
function withBundleLock<T>(body: () => T): T {
  const lock = join(tmpdir(), "specdesk-webview-bundle-build-lock");
  const waitStart = Date.now();
  for (;;) {
    try {
      mkdirSync(lock);
      break;
    } catch {
      // Lock held. Reclaim it if stale (holder crashed), give up loudly after two minutes, else wait a
      // beat and retry.
      try {
        if (Date.now() - statSync(lock).mtimeMs > 60_000) {
          rmSync(lock, { recursive: true, force: true });
          continue;
        }
      } catch {
        // The lock vanished between mkdir failing and stat — retry immediately.
        continue;
      }
      if (Date.now() - waitStart > 120_000) {
        throw new Error("timed out waiting for the webview bundle build lock");
      }
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 150);
    }
  }
  try {
    return body();
  } finally {
    rmSync(lock, { recursive: true, force: true });
  }
}

/**
 * Run the standard bundle process and gather the built artifact + its manifest. Shelling out to
 * `node scripts/bundle.mjs` is deliberately the same path `npm run bundle` drives, so the normal gate proves
 * the whole pipeline (esbuild + manifest write), not an in-process re-implementation of it. A sandbox that
 * forbids esbuild child-process spawn may set `SPECDESK_DELIVERY_PREBUILT=1` after building with the native
 * executable; manifest verification remains mandatory. Build and read happen under the cross-file lock.
 */
export function buildBundle(): BundleArtifact {
  return withBundleLock(() => {
    if (process.env.SPECDESK_DELIVERY_PREBUILT !== "1") {
      execFileSync(process.execPath, [join(scriptsDir, "bundle.mjs")], {
        cwd: webviewDir,
        stdio: "inherit",
      });
    }
    const verification = verifyBundle(webviewDir, wwwrootDir);
    const manifest = readManifest(wwwrootDir);
    const jsBytes = readFileSync(join(wwwrootDir, "webview.js"));
    const jsSha256 = createHash("sha256").update(jsBytes).digest("hex");
    const rawJs = readFileSync(join(wwwrootDir, "webview.js"), "utf8");
    const code = rawJs.replace(/\bexport\s*\{[\s\S]*?\};?\s*$/, "");
    const html = readFileSync(join(wwwrootDir, "index.html"), "utf8");
    const css = readFileSync(join(wwwrootDir, "styles.css"), "utf8");
    return { code, html, css, jsSha256, verification, manifest };
  });
}

export { VerifyStatus };
