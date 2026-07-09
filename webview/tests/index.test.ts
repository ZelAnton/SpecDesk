/**
 * Integration-style tests for two index.ts glue scenarios that don't fit any single module's own suite:
 * the `diff.result` malformed-payload guard (lives in the ipc handler in index.ts) and the Split
 * cross-pane mirror's debounce race guard (exercised here through the real MarkdownEditor /
 * FormattedEditor via the exported `shouldMirrorInto`, the same decision index.ts's onEditorChange /
 * onFormattedChange make — see index.ts).
 */
// @vitest-environment jsdom
import type { Transaction } from "prosemirror-state";
import type { EditorView } from "prosemirror-view";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { FormattedEditor } from "../src/editors/formatted.js";
import type { Pane } from "../src/sync/sync-coordinator.js";
import type { IpcMessage } from "../src/wire/ipc.js";
import { Kinds } from "../src/wire/protocol.js";

/** A fake `window.external` — the "mock IPC host" the design calls for (mirrors ipc.test.ts). */
function mockBridge() {
  const sent: IpcMessage[] = [];
  let callback: ((message: string) => void) | undefined;
  return {
    sent,
    sendMessage: (message: string) => {
      sent.push(JSON.parse(message) as IpcMessage);
    },
    receiveMessage: (cb: (message: string) => void) => {
      callback = cb;
    },
    /** Simulate a native->webview frame. */
    emit: (message: IpcMessage) => callback?.(JSON.stringify(message)),
  };
}

/** A minimal fake `DataTransferItem` — just the bits `attachImageCapture` reads (mirrors
 *  image-capture.test.ts's helper of the same name). */
function fileItem(file: File): DataTransferItem {
  return { kind: "file", type: file.type, getAsFile: () => file } as unknown as DataTransferItem;
}

/** A fake `clipboardData`: an item list (array-like, not iterable — matches the real
 *  DataTransferItemList) plus `getData("text/plain")` (mirrors image-capture.test.ts). */
function clipboard(items: DataTransferItem[], text = ""): DataTransfer {
  const list: Record<number, DataTransferItem> & { length: number } = { length: items.length };
  items.forEach((item, i) => {
    list[i] = item;
  });
  return {
    items: list as unknown as DataTransferItemList,
    getData: (format: string) => (format === "text/plain" ? text : ""),
  } as unknown as DataTransfer;
}

/** Build and dispatch a real `Event` with `clipboardData` monkey-patched onto it — jsdom has no
 *  working `ClipboardEvent` constructor that actually carries file data (mirrors image-capture.test.ts). */
function dispatchPaste(dom: EventTarget, clipboardData: DataTransfer): Event {
  const event = new Event("paste", { cancelable: true });
  Object.defineProperty(event, "clipboardData", { value: clipboardData });
  dom.dispatchEvent(event);
  return event;
}

/** jsdom implements neither: index.ts's theme setup reads matchMedia unconditionally at boot. */
function installMatchMediaStub(): void {
  if (!window.matchMedia) {
    window.matchMedia = ((query: string) =>
      ({
        matches: false,
        media: query,
        addEventListener: () => {},
        removeEventListener: () => {},
        addListener: () => {},
        removeListener: () => {},
        dispatchEvent: () => false,
        onchange: null,
      }) as unknown as MediaQueryList) as typeof window.matchMedia;
  }
}

// Only #editor/#preview/#formatted are load-bearing (wire() bails without them); every other element
// index.ts queries is used behind an optional-chain/null-check, so the rest of the chrome (toolbar
// buttons, dialogs, sign-in, reviews panel) is safely absent — mirroring how signin.test.ts/
// reviews-panel.test.ts mount only the markup each test actually needs, not the whole index.html shell.
// `panesMarkup` optionally adds #panes (with its `data-mode`, the single declared source of truth for
// the starting mode) and the mode radiogroup buttons, for the startup-mode test below; every other test
// omits it, exactly mirroring index.html's real fallback when the radiogroup chrome is absent.
function setupDom(panesMarkup = ""): void {
  document.body.innerHTML = `
    <div id="editor"></div>
    <div id="preview"></div>
    <div id="formatted"></div>
    <button id="compare-btn" type="button" aria-pressed="false"></button>
    <div id="review-empty-bar" hidden></div>
    <div id="review-overflow-bar" hidden></div>
    ${panesMarkup}
  `;
}

// wire() registers window-level listeners ("resize", "focus") it has no way to unregister. Each
// mountApp() call gives the test a FRESH module instance (vi.resetModules()), but `window` itself
// is shared across the whole file, so without this cleanup it keeps accumulating one stale listener
// per prior mountApp() call — each still closing over that prior test's own module state. That was
// previously inert, but the Split-echo mocks below simulate a coordinator-written scroll by calling
// straight back into the shared editorCallbacks/formattedCallbacks (which by then point at the
// CURRENT test's callbacks) — so a stale listener firing on a later test's
// window.dispatchEvent(resize) invokes the CURRENT test's real onScroll handler carrying the PRIOR
// test's stale leadingPane, corrupting it. Strip the previous mountApp()'s listeners before wiring a
// fresh one.
let wiredWindowListeners: Array<[string, EventListenerOrEventListenerObject]> = [];

/** Boot the real index.ts wiring against a mocked host bridge. The ipc.ts singleton reads
 *  `window.external` at module-eval time, so the bridge must be installed and the module graph reset
 *  before each fresh import. */
async function mountApp(panesMarkup = ""): Promise<ReturnType<typeof mockBridge>> {
  setupDom(panesMarkup);
  installMatchMediaStub();
  const bridge = mockBridge();
  Object.defineProperty(globalThis, "external", { value: bridge, configurable: true });

  for (const [type, listener] of wiredWindowListeners) {
    window.removeEventListener(type, listener);
  }
  wiredWindowListeners = [];
  const rawAddEventListener = window.addEventListener.bind(window);
  window.addEventListener = ((
    type: string,
    listener: EventListenerOrEventListenerObject,
    options?: boolean | AddEventListenerOptions,
  ) => {
    wiredWindowListeners.push([type, listener]);
    rawAddEventListener(type, listener, options);
  }) as typeof window.addEventListener;
  try {
    vi.resetModules();
    await import("../src/index.js");
  } finally {
    window.addEventListener = rawAddEventListener;
  }
  return bridge;
}

describe("index.ts: diff.result malformed-payload guard (jsdom)", () => {
  afterEach(() => {
    Reflect.deleteProperty(globalThis as Record<string, unknown>, "external");
    document.body.innerHTML = "";
  });

  it("drops a malformed diff.result payload rather than rendering a false empty diff", async () => {
    const bridge = await mountApp();
    const compareBtn = document.querySelector<HTMLButtonElement>("#compare-btn");
    const reviewEmptyEl = document.querySelector<HTMLElement>("#review-empty-bar");

    // Toggle the overlay on: sends diff.request stamped with the live docVersion (0 for a freshly booted,
    // unloaded document).
    compareBtn?.click();
    const request = bridge.sent.find((m) => m.kind === Kinds.diffRequest);
    expect(request).toBeDefined();
    const version = request?.version ?? 0;

    // A malformed reply (payload decodes to null, e.g. a transport/contract glitch): every other handler
    // in index.ts drops a null-decoded payload outright. Before the fix this one instead fell through to
    // an empty entries list, washing nothing and surfacing "No changes since the last saved version" —
    // a false report indistinguishable from a real empty diff.
    bridge.emit({ kind: Kinds.diffResult, version, payload: null });
    expect(reviewEmptyEl?.hidden).toBe(true);

    // A well-formed empty reply is the genuine "nothing changed" case — confirms only the malformed
    // payload is dropped, not the handler's legitimate empty-diff notice.
    bridge.emit({ kind: Kinds.diffResult, version, payload: { entries: [] } });
    expect(reviewEmptyEl?.hidden).toBe(false);
  });

  // T-081: an overflowing diff.result carries an `overflow` count instead of a full `entries` array —
  // the overflow notice must show (distinct from the "no changes" one) and nothing should be painted.
  it("shows the overflow notice, not the empty-diff one, for an overflowing diff.result", async () => {
    const bridge = await mountApp();
    const compareBtn = document.querySelector<HTMLButtonElement>("#compare-btn");
    const reviewEmptyEl = document.querySelector<HTMLElement>("#review-empty-bar");
    const reviewOverflowEl = document.querySelector<HTMLElement>("#review-overflow-bar");

    compareBtn?.click();
    const request = bridge.sent.find((m) => m.kind === Kinds.diffRequest);
    const version = request?.version ?? 0;

    bridge.emit({
      kind: Kinds.diffResult,
      version,
      payload: { entries: [], overflow: { removedCount: 5000, addedCount: 5000 } },
    });
    expect(reviewOverflowEl?.hidden).toBe(false);
    expect(reviewEmptyEl?.hidden).toBe(true);
  });
});

describe("index.ts: Split cross-pane mirror debounce race (jsdom)", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
    Reflect.deleteProperty(globalThis as Record<string, unknown>, "external");
    document.body.innerHTML = "";
  });

  it("does not let a stale mirror clobber a pane's own not-yet-reported edit", async () => {
    await mountApp();
    // wire() doesn't expose its internal pane instances (an implementation detail) — what matters is
    // exercising the real decision, shouldMirrorInto, against real MarkdownEditor/FormattedEditor
    // instances, the exact way index.ts's onEditorChange/onFormattedChange do.
    const { MarkdownEditor } = await import("../src/editors/editor.js");
    const { shouldMirrorInto } = await import("../src/index.js");

    const sourceHost = document.createElement("div");
    document.body.appendChild(sourceHost);
    let sourceReported = "";
    const source = new MarkdownEditor(sourceHost, {
      onChange: (text) => {
        sourceReported = text;
      },
      onScroll: () => {},
      onScrollSettle: () => {},
      onCursor: () => {},
      onHover: () => {},
      onGeometryChange: () => {},
      onEditAttempt: () => {},
      onFocus: () => {},
      onOpenLink: () => {},
    });
    source.setEditable(true);

    const destHost = document.createElement("div");
    document.body.appendChild(destHost);
    let destReported = "";
    const dest = new FormattedEditor(destHost, {
      onChange: (text) => {
        destReported = text;
      },
      onEditAttempt: () => {},
      onScroll: () => {},
      onCursor: () => {},
      onHover: () => {},
      onContentResize: () => {},
      onFocus: () => {},
      onActiveChange: () => {},
      onOpenLink: () => {},
    });
    dest.setEditable(true);
    dest.setText("shared\n");
    source.setText("shared\n", true);

    // t=0: the author edits `source` (pane A) — starts A's 120ms debounce.
    source.setText("edited by A\n");

    // t=50: within A's still-pending debounce window, the author also edits `dest` (pane B) — starts
    // B's own 120ms debounce, independently timed.
    vi.advanceTimersByTime(50);
    const view = (dest as unknown as { view: EditorView }).view;
    view.dispatch(view.state.tr.insertText("X", 1) as Transaction);
    expect(dest.getText()).toBe("Xshared\n");

    // t=120: A's debounce (started first, at t=0) fires now. Its onChange would normally mirror A's text
    // into `dest` — but `dest` has its own not-yet-reported edit pending (B's own debounce, started at
    // t=50, doesn't fire until t=170), so shouldMirrorInto must refuse the mirror here.
    vi.advanceTimersByTime(70);
    expect(sourceReported).toBe("edited by A\n");
    expect(shouldMirrorInto(sourceReported, dest)).toBe(false);
    // The guard is exactly what protects `dest`'s content from being clobbered — confirm it still holds
    // B's own unreported edit, not A's stale mirror.
    expect(dest.getText()).toBe("Xshared\n");

    // t=170: B's debounce now fires; `source` has no pending edit of its own, so the mirror proceeds —
    // no keystroke was ever silently lost on either side.
    vi.advanceTimersByTime(50);
    expect(destReported).toBe("Xshared\n");
    expect(shouldMirrorInto(destReported, source)).toBe(true);
  });
});

describe("index.ts: startup view mode has a single source of truth (jsdom)", () => {
  afterEach(() => {
    Reflect.deleteProperty(globalThis as Record<string, unknown>, "external");
    document.body.innerHTML = "";
  });

  // #panes' `data-mode` (T-070): index.ts must read the STARTING mode from it — not repeat a "split"
  // literal of its own — and reflect that same value into the radiogroup through applyMode's own
  // setSelected path, so the TS `mode` variable, `#panes[data-mode]` (what the Split-only logic —
  // spacer/scroll/height-sync — actually gates on via isSplit(mode)) and the buttons' aria-checked/
  // tabindex can never disagree at boot, whichever mode the markup declares.
  it("derives the TS mode and the radiogroup selection from #panes[data-mode], not a hardcoded split", async () => {
    await mountApp(`
      <div id="panes" data-mode="code"></div>
      <span id="view-modes" role="radiogroup">
        <button id="mode-code" type="button" role="radio" aria-checked="false" tabindex="-1">Code</button>
        <button id="mode-split" type="button" role="radio" aria-checked="false" tabindex="-1">Split</button>
        <button id="mode-formatted" type="button" role="radio" aria-checked="false" tabindex="-1">Formatted</button>
      </span>
    `);

    const panesEl = document.querySelector<HTMLElement>("#panes");
    const codeBtn = document.querySelector<HTMLButtonElement>("#mode-code");
    const splitBtn = document.querySelector<HTMLButtonElement>("#mode-split");
    const formattedBtn = document.querySelector<HTMLButtonElement>("#mode-formatted");

    // The DOM-declared mode is untouched (index.ts never overwrites it with a "split" default).
    expect(panesEl?.dataset.mode).toBe("code");
    // The radiogroup was reflected from that same DOM-declared mode via setSelected, exactly the path
    // a user click/arrow-key uses on a later switch — not left at its inert "all unchecked" markup.
    expect(codeBtn?.getAttribute("aria-checked")).toBe("true");
    expect(codeBtn?.tabIndex).toBe(0);
    expect(splitBtn?.getAttribute("aria-checked")).toBe("false");
    expect(splitBtn?.tabIndex).toBe(-1);
    expect(formattedBtn?.getAttribute("aria-checked")).toBe("false");
    expect(formattedBtn?.tabIndex).toBe(-1);

    // Switching to Split afterwards still works (existing Code/Split/Formatted toggling behaviour is
    // unaffected by reading the starting mode from the DOM).
    splitBtn?.click();
    expect(panesEl?.dataset.mode).toBe("split");
    expect(splitBtn?.getAttribute("aria-checked")).toBe("true");
    expect(codeBtn?.getAttribute("aria-checked")).toBe("false");
  });
});

describe("index.ts: Split geometry changes re-align the passive pane (T-086, jsdom)", () => {
  type EditorCallbacks = {
    onChange: (text: string) => void;
    onScroll: () => void;
    onScrollSettle: () => void;
    onCursor: (line: number | null, navigated: boolean) => void;
    onHover: (line: number | null) => void;
    onGeometryChange: () => void;
    onEditAttempt: () => void;
    onFocus: () => void;
    onOpenLink: (url: string) => void;
  };

  type FormattedCallbacks = {
    onChange: (text: string) => void;
    onEditAttempt: () => void;
    onScroll: () => void;
    onCursor: (line: number | null, navigated: boolean) => void;
    onHover: (line: number | null) => void;
    onContentResize: () => void;
    onFocus: () => void;
    onActiveChange: () => void;
    onOpenLink: (url: string) => void;
  };

  class MockPane {
    readonly contentDOM = document.createElement("div");
    text = "";
    scroll = 0;

    getText(): string {
      return this.text;
    }

    hasPendingChange(): boolean {
      return false;
    }

    setText(text: string): void {
      this.text = text;
    }

    mirror(text: string): void {
      this.text = text;
    }

    setEditable(): void {
      return;
    }

    setActiveLine(): void {
      return;
    }

    setHoverLine(): void {
      return;
    }

    setDiff(): void {
      return;
    }

    clearDiff(): void {
      return;
    }

    applyFormat(): void {
      return;
    }

    format(): void {
      return;
    }

    setLineWrapping(): void {
      return;
    }

    setDocDir(): void {
      return;
    }

    insertAtMarker(): void {
      return;
    }

    discardMarker(): void {
      return;
    }

    trackPosition(): number {
      return 0;
    }

    selectionHead(): number {
      return 0;
    }

    posAtCoords(): number | null {
      return null;
    }

    refresh(): void {
      return;
    }

    invalidateGeometry(): void {
      return;
    }

    focus(): void {
      return;
    }

    activeFormats(): Set<never> {
      return new Set();
    }

    disabledFormats(): Set<never> {
      return new Set();
    }

    blockGeometry(): [] {
      return [];
    }

    contentWidth(): number {
      return 0;
    }

    topLine(): number {
      return 0;
    }

    topsForLines(lines: readonly number[]): number[] {
      return lines.map(() => 0);
    }

    blockAnchors(): [] {
      return [];
    }

    scrollTop(): number {
      return this.scroll;
    }

    setScrollTop(px: number): void {
      this.scroll = px;
    }

    reveal(): void {
      return;
    }

    scrollToLine(): void {
      return;
    }
  }

  class MockSplitSync {
    readonly syncFromCalls: Pane[] = [];
    readonly scrollCalls: Pane[] = [];
    private readonly echoPanes = new Set<Pane>();

    constructor() {
      splitSyncInstances.push(this);
    }

    invalidate(): void {
      return;
    }

    absorb(): void {
      return;
    }

    reset(): void {
      return;
    }

    reveal(): void {
      return;
    }

    restore(): void {
      return;
    }

    syncFrom(pane: Pane): void {
      this.syncFromCalls.push(pane);
      this.emitEcho(pane === "editor" ? "formatted" : "editor");
    }

    onEditorScroll(): void {
      this.scrollCalls.push("editor");
      if (!this.isEcho("editor")) {
        this.emitEcho("formatted");
      }
    }

    onFormattedScroll(): void {
      this.scrollCalls.push("formatted");
      if (!this.isEcho("formatted")) {
        this.emitEcho("editor");
      }
    }

    isEcho(pane: Pane): boolean {
      return this.echoPanes.has(pane);
    }

    private emitEcho(pane: Pane): void {
      this.echoPanes.add(pane);
      try {
        if (pane === "editor") {
          editorCallbacks?.onScroll();
        } else {
          formattedCallbacks?.onScroll();
        }
      } finally {
        this.echoPanes.delete(pane);
      }
    }
  }

  let editorCallbacks: EditorCallbacks | undefined;
  let formattedCallbacks: FormattedCallbacks | undefined;
  let splitSyncInstances: MockSplitSync[];

  async function flushFrame(): Promise<void> {
    await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
  }

  beforeEach(() => {
    splitSyncInstances = [];
    editorCallbacks = undefined;
    formattedCallbacks = undefined;

    vi.doMock("../src/editors/editor.js", () => ({
      MarkdownEditor: class extends MockPane {
        constructor(_host: HTMLElement, callbacks: EditorCallbacks) {
          super();
          editorCallbacks = callbacks;
        }
      },
    }));
    vi.doMock("../src/editors/formatted.js", () => ({
      FormattedEditor: class extends MockPane {
        constructor(_host: HTMLElement, callbacks: FormattedCallbacks) {
          super();
          formattedCallbacks = callbacks;
        }
      },
    }));
    vi.doMock("../src/sync/height-sync.js", () => ({
      HeightSync: class {
        reconcile(): void {
          return;
        }
      },
    }));
    vi.doMock("../src/sync/sync-coordinator.js", () => ({
      SplitSync: MockSplitSync,
    }));
  });

  afterEach(() => {
    vi.doUnmock("../src/editors/editor.js");
    vi.doUnmock("../src/editors/formatted.js");
    vi.doUnmock("../src/sync/height-sync.js");
    vi.doUnmock("../src/sync/sync-coordinator.js");
    Reflect.deleteProperty(globalThis as Record<string, unknown>, "external");
    document.body.innerHTML = "";
  });

  it("uses the last manually scrolled pane as the resize re-align source", async () => {
    await mountApp();
    expect(editorCallbacks).toBeDefined();
    expect(splitSyncInstances).toHaveLength(1);

    editorCallbacks?.onScroll();
    window.dispatchEvent(new Event("resize"));
    await flushFrame();

    // Two "formatted" scroll events: the user's editor scroll couples formatted once, and the
    // resize reflow's syncFrom("editor") couples it again — both are the coordinator's own echoes
    // (isEcho("formatted") is true both times), so neither reassigns leadingPane.
    expect(splitSyncInstances[0]?.scrollCalls).toEqual(["editor", "formatted", "formatted"]);
    expect(splitSyncInstances[0]?.syncFromCalls).toEqual(["editor"]);
  });

  it("re-snaps the formatted pane through the coordinator when editor scrolling settles", async () => {
    await mountApp();
    expect(editorCallbacks).toBeDefined();
    expect(splitSyncInstances).toHaveLength(1);

    editorCallbacks?.onScrollSettle();

    expect(splitSyncInstances[0]?.syncFromCalls).toEqual(["editor"]);
  });

  it("keeps the same leading pane through repeated reflow echoes with no user scroll between them", async () => {
    await mountApp();
    expect(editorCallbacks).toBeDefined();
    expect(splitSyncInstances).toHaveLength(1);

    editorCallbacks?.onScroll();
    window.dispatchEvent(new Event("resize"));
    await flushFrame();
    window.dispatchEvent(new Event("resize"));
    await flushFrame();

    expect(splitSyncInstances[0]?.syncFromCalls).toEqual(["editor", "editor"]);
  });

  it("lets focused pane override the last scrolled pane for resize re-align", async () => {
    await mountApp();
    expect(editorCallbacks).toBeDefined();
    expect(formattedCallbacks).toBeDefined();
    expect(splitSyncInstances).toHaveLength(1);

    editorCallbacks?.onScroll();
    formattedCallbacks?.onFocus();
    window.dispatchEvent(new Event("resize"));
    await flushFrame();

    expect(splitSyncInstances[0]?.syncFromCalls).toEqual(["formatted"]);
  });
});

describe("index.ts: doc.loaded hydration is silent (T-069, jsdom)", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
    Reflect.deleteProperty(globalThis as Record<string, unknown>, "external");
    document.body.innerHTML = "";
  });

  // Before the fix, the source editor's doc.loaded hydration used a non-silent setText: 120ms later the
  // debounced onChange fired and sent editor.changed straight back to the host with the very text it
  // just sent — an unnecessary round-trip that bumped the host's docVersion and re-ran a full render for
  // every open document, and only avoided a false "Unsaved changes" by the accident of the load also
  // resetting the lifecycle to Published (MarkDirtyAndScheduleDiskAutosave gates on the editing state).
  it("does not emit editor.changed after loading a document, even once the debounce window elapses", async () => {
    const bridge = await mountApp();

    bridge.emit({
      kind: Kinds.docLoaded,
      payload: { path: "docs/spec.md", text: "# Hello\n", docDir: "docs" },
    });

    // Past the 120ms debounce the (non-silent, pre-fix) source-editor onChange would have fired.
    vi.advanceTimersByTime(500);

    expect(bridge.sent.some((message) => message.kind === Kinds.editorChanged)).toBe(false);

    // The loaded document still reaches both panes — silencing the change notification must not
    // degrade what the author actually sees.
    expect(document.querySelector("#editor .cm-content")?.textContent).toBe("# Hello");
    expect(document.querySelector("#formatted")?.textContent).toContain("Hello");
  });
});

describe("index.ts: doc.loaded drops a stale image-insert marker (R-01, jsdom)", () => {
  afterEach(() => {
    Reflect.deleteProperty(globalThis as Record<string, unknown>, "external");
    document.body.innerHTML = "";
  });

  // R-01: setText's `sameDocument` parameter defaults to `silent` (not `false`) — the doc.loaded
  // handler's silent hydration must pass `sameDocument: false` explicitly, or a marker left pending by
  // an in-flight image paste against the PREVIOUS document gets restored (clamped) into the newly
  // loaded, unrelated one instead of being dropped, and a belated insertAtMarker then splices the old
  // paste's markdown into it.
  it("does not land a pending image insert (from the previous document) in a freshly loaded document", async () => {
    const bridge = await mountApp();
    const contentDom = document.querySelector<HTMLElement>("#editor .cm-content");
    expect(contentDom).not.toBeNull();

    // Paste an image at the (empty, freshly booted) editor's caret — registers a tracked marker and
    // fires the async image.paste round-trip to the host.
    const file = new File(["fake-bytes"], "photo.png", { type: "image/png" });
    const event = dispatchPaste(contentDom as HTMLElement, clipboard([fileItem(file)]));
    expect(event.defaultPrevented).toBe(true);

    const request = await vi.waitFor(() => {
      const found = bridge.sent.find((message) => message.kind === Kinds.imagePaste);
      if (!found) {
        throw new Error("image.paste was not sent yet");
      }
      return found;
    });
    const requestId = request.id;
    if (!requestId) {
      throw new Error("image.paste request had no correlation id");
    }

    // The author switches to a different document while that paste round-trip is still in flight.
    bridge.emit({
      kind: Kinds.docLoaded,
      payload: { path: "docs/other.md", text: "# Other doc\n", docDir: "docs" },
    });
    expect(document.querySelector("#editor .cm-content")?.textContent).toBe("# Other doc");

    // The host's belated reply for the ORIGINAL (now-abandoned) paste arrives after the load.
    bridge.emit({
      kind: Kinds.imageInserted,
      id: requestId,
      payload: { markdown: "![photo](img/photo.png)" },
    });
    // Let the resolved request's continuation (insertAtMarker, or the no-op it should be) run.
    await new Promise((resolve) => setTimeout(resolve, 20));

    // The stale marker must have been dropped by doc.loaded's setText(..., true, false), so
    // insertAtMarker is a no-op: the freshly loaded document is untouched by the old paste.
    expect(document.querySelector("#editor .cm-content")?.textContent).toBe("# Other doc");
  });
});

describe("index.ts: doc.loaded resets both panes' scroll (T-087, jsdom)", () => {
  afterEach(() => {
    Reflect.deleteProperty(globalThis as Record<string, unknown>, "external");
    document.body.innerHTML = "";
  });

  // Before the fix, setText hydrated the panes' CONTENT on doc.loaded but never touched scrollTop, so a
  // pane kept whichever position the PREVIOUS document had left it at — an arbitrary depth for a shorter
  // old doc, the browser's clamp for a longer one, and the two panes generally disagreeing with each
  // other. Both must land back at the very top on every load, not just the first.
  it("resets a scrolled-down editor and formatted pane back to the top on a second load", async () => {
    const bridge = await mountApp();

    bridge.emit({
      kind: Kinds.docLoaded,
      payload: { path: "docs/first.md", text: "# First\n", docDir: "docs" },
    });

    const scroller = document.querySelector<HTMLElement>("#editor .cm-scroller");
    const formattedEl = document.querySelector<HTMLElement>("#formatted");
    expect(scroller).not.toBeNull();
    expect(formattedEl).not.toBeNull();

    // Simulate the author having scrolled down into the first document before switching away.
    (scroller as HTMLElement).scrollTop = 500;
    (formattedEl as HTMLElement).scrollTop = 300;

    bridge.emit({
      kind: Kinds.docLoaded,
      payload: { path: "docs/second.md", text: "# Second\n\nBody text.\n", docDir: "docs" },
    });

    expect((scroller as HTMLElement).scrollTop).toBe(0);
    expect((formattedEl as HTMLElement).scrollTop).toBe(0);
  });
});
