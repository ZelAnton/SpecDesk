/**
 * Split scroll-sync DELIVERY smoke (T-108).
 *
 * The per-module suites already prove the anchor map, the spacer math, the coordinator and the reconcile
 * scheduler in isolation — but an isolated suite cannot notice that those modules are ABSENT from the tree
 * that actually ships, or mis-wired by index.ts, or dropped by the bundle. This gate closes that hole: it
 * runs the standard bundle process, checks the T-107 content manifest, and then drives the SAME
 * `wwwroot/webview.js` the host serves through the real Split wiring — no `SplitSync`, height-sync,
 * CodeMirror or ProseMirror doubles; the one seam is a deterministic layout adapter for the geometry jsdom
 * cannot render (see harness.ts).
 *
 * Revision / fingerprint contract (relied on by the processor's post-publish invocation): the gate runs
 * `node scripts/bundle.mjs` itself and asserts the T-107 manifest is `up-to-date` AND that the sha-256 of
 * the `webview.js` it loads equals the hash the manifest records — so the artifact under test is provably
 * the one built from the CURRENT inputs. When the processor runs this against the restored main working
 * copy after publish, `up-to-date` therefore means "built from published-main sources", and the loaded
 * fingerprint is the published one; a stale bundle or an old source root fails the manifest gate here
 * rather than passing silently. CI runs it right after `npm run bundle` (see .github/workflows/ci.yml).
 */
// @vitest-environment jsdom
import { beforeAll, beforeEach, describe, expect, it } from "vitest";
import {
  type BundleArtifact,
  buildBundle,
  codeContent,
  codeScroller,
  delay,
  findLeaf,
  flushFrames,
  formattedContent,
  formattedPane,
  formattedTopOf,
  installLayoutAdapter,
  loadDocument,
  setLeafHeight,
  spacerElements,
  VerifyStatus,
  type WiredApp,
  wire,
} from "./harness.js";

/**
 * A fixture with everything the anchor projection must handle as its OWN unit: headings, paragraphs of
 * different heights, a genuinely multi-line block, a table with several rows, and a list with several
 * items. Built line-by-line so the fenced block's back-ticks stay readable.
 */
const FIXTURE = [
  "# Heading One",
  "",
  "Short para.",
  "",
  "A much longer paragraph that should be considerably taller than the short one for sure indeed.",
  "",
  "```",
  "line1",
  "line2",
  "line3",
  "```",
  "",
  "| A | B |",
  "| - | - |",
  "| r1a | r1b |",
  "| r2a | r2b |",
  "",
  "- item one",
  "- item two",
  "- item three",
  "",
].join("\n");

const isTag = (tag: string) => (el: Element) => el.tagName === tag;
const isPara = (needle: string) => (el: Element) =>
  el.tagName === "P" && (el.textContent ?? "").includes(needle);
const isRow = (needle: string) => (el: Element) =>
  el.tagName === "TR" && (el.textContent ?? "").includes(needle);
const isItem = (needle: string) => (el: Element) =>
  el.tagName === "LI" && (el.textContent ?? "").includes(needle);

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
 * wall-clock timing between steps, so it made this gate NON-DETERMINISTIC — a green run proved nothing.
 * Settling each gesture fully removes the cross-step leak and makes every scenario below reproducible.
 */
async function scrollPane(el: HTMLElement, top: number): Promise<void> {
  el.scrollTop = top;
  el.dispatchEvent(new Event("scroll"));
  await flushFrames(3); // the live rAF couple
  await delay(SCROLL_SETTLE_MS + 40); // let the scroll-settle debounce fire (the gesture is now over)
  await flushFrames(3); // drain the settle's own re-couple + its suppressed echo
}

/** Fail unless height-sync applied at least one real, non-zero source spacer. Shared with the sensitivity
 *  control below, so the control proves THIS check (not a throwaway) catches a missing-spacer regression. */
function assertSpacersApplied(): void {
  const spacers = spacerElements();
  if (spacers.length === 0) {
    throw new Error("no .cm-sync-spacer widgets were applied to the source editor");
  }
  for (const spacer of spacers) {
    const height = Number.parseFloat(spacer.style.height);
    if (!(height > 0)) {
      throw new Error(`a source spacer has non-positive height: "${spacer.style.height}"`);
    }
  }
}

/** Fail unless a pane's scrollTop matches the expected content top within one CSS pixel. Shared with the
 *  Code→Formatted sensitivity control, so the control proves THIS check catches a coupling regression. */
function assertPaneAt(el: HTMLElement, expected: number, label: string): void {
  const diff = Math.abs(el.scrollTop - expected);
  if (diff > 1) {
    throw new Error(
      `${label}: expected scrollTop ≈ ${expected}, got ${el.scrollTop} (Δ ${diff.toFixed(2)})`,
    );
  }
}

describe("Split scroll-sync delivery smoke (built webview.js)", () => {
  let artifact: BundleArtifact;
  let app: WiredApp;

  beforeAll(() => {
    // Run the STANDARD bundle process and install the layout adapter once for the whole file.
    artifact = buildBundle();
    installLayoutAdapter();
  });

  beforeEach(async () => {
    app = wire(artifact.code, artifact.html, artifact.css);
    await loadDocument(app, FIXTURE);
  });

  it("bundles, and the loaded webview.js matches the T-107 manifest (revision/fingerprint gate)", () => {
    expect(artifact.verification.status).toBe(VerifyStatus.UpToDate);
    const manifest = artifact.manifest;
    expect(manifest).not.toBeNull();
    if (manifest === null) {
      return;
    }
    expect(manifest.schema).toBe(1);
    expect(manifest.kind).toBe("specdesk-webview-bundle");
    expect(manifest.inputFingerprint.startsWith("sha256:")).toBe(true);
    expect(manifest.outputFingerprint.startsWith("sha256:")).toBe(true);
    // The artifact this gate actually loads and drives IS the one the manifest (and the host verifier)
    // pin — not a re-derivation of it.
    const recorded = manifest.outputs.find((output) => output.path === "webview.js");
    expect(recorded?.sha256).toBe(artifact.jsSha256);
  });

  it("wires both real editors from a single doc.loaded (modules present in the shipped tree)", () => {
    expect(document.querySelectorAll("#editor .cm-editor")).toHaveLength(1);
    expect(document.querySelectorAll("#formatted .ProseMirror")).toHaveLength(1);
    expect(app.sent.map((frame) => frame.kind)).toContain("ready");
    expect(formattedContent().textContent).toContain("much longer paragraph");
    expect(codeContent().textContent).toContain("Heading One");
  });

  it("applies real, non-zero source spacers at the semantic boundaries", () => {
    assertSpacersApplied();
    // The formatted blocks are taller than the estimated source lines, so more than a couple of spacers
    // are needed — enough to align the per-row / per-item boundaries, not just the top-level blocks.
    expect(spacerElements().length).toBeGreaterThanOrEqual(4);
  });

  it("aligns Code with the given formatted geometry within 1px at every anchor, incl. each row and item", async () => {
    // Every laid-out leaf is its OWN anchor: driving the formatted pane to a leaf's top must land the
    // padded source editor at the same pixel top (height-sync's whole purpose). A table row / list item
    // that was NOT its own anchor would couple to an interpolated position and miss by well over a pixel —
    // so this is also the per-row / per-item granularity assertion (T-101).
    const targets: Array<[string, Element]> = [
      ["short paragraph", findLeaf(isPara("Short para"))],
      ["tall paragraph", findLeaf(isPara("much longer"))],
      ["code block", findLeaf(isTag("PRE"))],
      ["table header row", findLeaf(isRow("AB"))],
      ["table body row 1", findLeaf(isRow("r1a"))],
      ["table body row 2", findLeaf(isRow("r2a"))],
      ["list item one", findLeaf(isItem("item one"))],
      ["list item two", findLeaf(isItem("item two"))],
      ["list item three", findLeaf(isItem("item three"))],
    ];
    for (const [label, leaf] of targets) {
      const top = formattedTopOf(leaf);
      await scrollPane(formattedPane(), top);
      assertPaneAt(codeScroller(), top, `Code padded top for ${label}`);
    }
  });

  it("moves Formatted to the first visible semantic line on a real Code scroll, and the reverse", async () => {
    const row2 = findLeaf(isRow("r2a"));
    const top = formattedTopOf(row2);

    // Code → Formatted: a user scroll of the real CodeMirror scroller drives the formatted pane to the
    // first visible semantic line (the padded Code top of a row equals that row's formatted top ±1px).
    await scrollPane(codeScroller(), top);
    assertPaneAt(formattedPane(), top, "Formatted after a Code scroll");

    // Formatted → Code: symmetric.
    const item = findLeaf(isItem("item three"));
    const itemTop = formattedTopOf(item);
    await scrollPane(formattedPane(), itemTop);
    assertPaneAt(codeScroller(), itemTop, "Code after a Formatted scroll");
  });

  it("switches the active pane by focus + scroll (Code↔Formatted)", async () => {
    // Focus the formatted pane → it leads; a scroll there drives the source editor.
    formattedContent().dispatchEvent(new Event("focus"));
    const itemTwo = formattedTopOf(findLeaf(isItem("item two")));
    await scrollPane(formattedPane(), itemTwo);
    assertPaneAt(codeScroller(), itemTwo, "Code driven while Formatted is active");

    // Focus the source editor → it leads; a scroll there drives the formatted pane.
    codeContent().dispatchEvent(new Event("focus"));
    const header = formattedTopOf(findLeaf(isRow("AB")));
    await scrollPane(codeScroller(), header);
    assertPaneAt(formattedPane(), header, "Formatted driven while Code is active");
  });

  it("suppresses the echo of a programmatic scroll (no ping-pong)", async () => {
    // Code leads and drives Formatted to a row.
    const top = formattedTopOf(findLeaf(isRow("r2a")));
    await scrollPane(codeScroller(), top);
    assertPaneAt(formattedPane(), top, "Formatted coupled from Code");
    const codeAfterCouple = codeScroller().scrollTop;

    // The browser now fires Formatted's own scroll event for the value the coordinator just wrote — its
    // echo. It must NOT drive the source editor back nor re-declare Formatted active.
    formattedPane().dispatchEvent(new Event("scroll"));
    await flushFrames();
    expect(codeScroller().scrollTop).toBe(codeAfterCouple);
    expect(formattedPane().scrollTop).toBe(top);
  });

  it("re-settles after a formatted block grows, and does not jump once steady", async () => {
    const row2 = findLeaf(isRow("r2a"));
    const beforeTop = formattedTopOf(row2);
    await scrollPane(formattedPane(), beforeTop);
    assertPaneAt(codeScroller(), beforeTop, "Code before the height change");

    // A block above the row grows (as an image finishing decode would): every following leaf shifts down.
    setLeafHeight(findLeaf(isPara("much longer")), 240);
    window.dispatchEvent(new Event("resize"));
    await flushFrames();

    const afterTop = formattedTopOf(row2);
    expect(afterTop).toBeGreaterThan(beforeTop + 50);
    // The reconcile re-measured the new geometry, so alignment holds against the row's NEW top.
    await scrollPane(formattedPane(), afterTop);
    assertPaneAt(codeScroller(), afterTop, "Code after the height change re-settle");

    // Steady: a further reconcile with no geometry change makes no visible backward jump.
    const steadyFormatted = formattedPane().scrollTop;
    const steadyCode = codeScroller().scrollTop;
    window.dispatchEvent(new Event("resize"));
    await flushFrames();
    expect(formattedPane().scrollTop).toBe(steadyFormatted);
    assertPaneAt(codeScroller(), steadyCode, "Code holds steady with no backward jump");
  });

  it("re-snaps on the debounced scroll-settle once momentum stops", async () => {
    const top = formattedTopOf(findLeaf(isItem("item two")));
    formattedPane().scrollTop = top;
    formattedPane().dispatchEvent(new Event("scroll"));
    // The 120ms scroll-settle debounce fires onScrollSettle → coordinator.settle → the same couple.
    await delay(180);
    await flushFrames();
    assertPaneAt(codeScroller(), top, "Code after the Formatted scroll settled");
  });

  // Sensitivity controls (T-108 crit. 7): each proves one of the two guarantees is checked by an
  // INDEPENDENT assertion that fails precisely when that feature's real effect is removed from the live
  // run — spacer application, and the Code→Formatted coupling. The mutations are applied to the artifact's
  // OWN observed output, and each control confirms the sibling check is unaffected (true independence).
  describe("sensitivity controls", () => {
    it("the spacer check fails if spacer application is removed (coupling check unaffected)", async () => {
      assertSpacersApplied(); // real run applied them
      const top = formattedTopOf(findLeaf(isRow("r2a")));
      await scrollPane(formattedPane(), top);
      assertPaneAt(codeScroller(), top, "coupling before the spacer mutation");

      // Mutation: strip the applied spacers, as a build that never called setSpacers would leave the tree.
      for (const spacer of spacerElements()) {
        spacer.remove();
      }
      expect(() => assertSpacersApplied()).toThrow(/spacer/i);
      // The coupling maps were already built, so the coupling check still passes — the two are independent.
      expect(() => assertPaneAt(codeScroller(), top, "coupling")).not.toThrow();
    });

    it("the coupling check fails if Code→Formatted never moved the pane (spacer check unaffected)", async () => {
      const top = formattedTopOf(findLeaf(isRow("r2a")));
      await scrollPane(codeScroller(), top);
      assertPaneAt(formattedPane(), top, "coupling in the real run");

      // Mutation: return the formatted pane to its pre-scroll baseline, as a missing Code→Formatted wiring
      // would leave it — the coupling assertion must now fail.
      formattedPane().scrollTop = 0;
      expect(() => assertPaneAt(formattedPane(), top, "coupling")).toThrow(/expected scrollTop/);
      // The spacers are untouched, so the spacer check still passes — the two are independent.
      expect(() => assertSpacersApplied()).not.toThrow();
    });
  });
});
