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
function setupDom(panesMarkup = "", extraMarkup = ""): void {
  document.body.innerHTML = `
		<span id="status"></span>
		<div id="editor"></div>
    <div id="preview"></div>
    <div id="formatted"></div>
    <button id="compare-btn" type="button" aria-pressed="false"></button>
    <div id="review-empty-bar" hidden></div>
    <div id="review-overflow-bar" hidden></div>
    ${panesMarkup}
    ${extraMarkup}
  `;
}

// wire() registers window-level listeners ("resize", "focus") it has no way to unregister. Each
// mountApp() call gives the test a FRESH module instance (vi.resetModules()), but `window` itself
// is shared across the whole file, so without this cleanup it keeps accumulating one stale listener
// per prior mountApp() call — each still closing over that prior test's own module state. That was
// previously inert, but the Split-echo mocks below simulate a coordinator-written scroll by calling
// straight back into the shared editorCallbacks/formattedCallbacks (which by then point at the
// CURRENT test's callbacks) — so a stale listener firing on a later test's
// window.dispatchEvent(resize) would invoke the CURRENT test's real onScroll handler through a prior
// module's coordinator mock, cross-firing the two tests' state. Strip the previous mountApp()'s
// listeners before wiring a fresh one.
let wiredWindowListeners: Array<[string, EventListenerOrEventListenerObject]> = [];

/** Boot the real index.ts wiring against a mocked host bridge. The ipc.ts singleton reads
 *  `window.external` at module-eval time, so the bridge must be installed and the module graph reset
 *  before each fresh import. */
async function mountApp(
  panesMarkup = "",
  extraMarkup = "",
): Promise<ReturnType<typeof mockBridge>> {
  setupDom(panesMarkup, extraMarkup);
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

describe("index.ts: correlated repository-operation completion", () => {
  it("ignores stale completions and accepts only the active request", async () => {
    const { isMatchingRepositoryOperationCompletion } = await import("../src/index.js");

    expect(isMatchingRepositoryOperationCompletion(12, 11)).toBe(false);
    expect(isMatchingRepositoryOperationCompletion(12, 12)).toBe(true);
    expect(isMatchingRepositoryOperationCompletion(null, 12)).toBe(false);
  });
});

describe("index.ts: correlated discard transition", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    Reflect.deleteProperty(globalThis as Record<string, unknown>, "external");
    document.body.innerHTML = "";
  });

  it("flushes pending panes oldest-first and locks before a slow discard is sent", async () => {
    const { runDiscardTransition } = await import("../src/index.js");
    const { debounce } = await import("../src/util/debounce.js");
    const events: string[] = [];
    const first = debounce(() => events.push("first edit"), 120);
    const second = debounce(() => events.push("second edit"), 120);
    const pane = (pending: typeof first) => ({
      pendingChangeOrder: () => pending.pendingOrder,
      flushPendingChange: () => pending.flush(),
    });
    first();
    vi.advanceTimersByTime(10);
    second();

    const panes = [pane(first), pane(second)];
    const started = runDiscardTransition(
      panes,
      false,
      () => events.push("locked"),
      () => events.push("discard sent"),
    );
    const duplicateStarted = runDiscardTransition(
      panes,
      true,
      () => events.push("duplicate lock"),
      () => events.push("duplicate send"),
    );
    vi.advanceTimersByTime(500);

    expect(started).toBe(true);
    expect(duplicateStarted).toBe(false);
    expect(events).toEqual(["first edit", "second edit", "locked", "discard sent"]);
  });

  it("keeps a slow discard locked and unlocks only its matching failure", async () => {
    const bridge = await mountApp(
      "",
      '<button id="discard-btn" type="button">Discard</button><fieldset id="format-bar"></fieldset>',
    );
    const discard = document.querySelector<HTMLButtonElement>("#discard-btn");
    const formatBar = document.querySelector<HTMLFieldSetElement>("#format-bar");
    bridge.emit({
      kind: Kinds.docLoaded,
      payload: { path: "docs/spec.md", text: "# Draft\n", docDir: "docs", readOnly: false },
    });
    bridge.emit({
      kind: Kinds.status,
      payload: { state: "draft", label: "Draft", branch: "spec/draft" },
    });
    expect(formatBar?.disabled).toBe(false);

    discard?.click();
    const request = bridge.sent.find((message) => message.kind === Kinds.docDiscard);
    const requestId = (request?.payload as { requestId?: number } | undefined)?.requestId;
    expect(requestId).toBeTypeOf("number");
    expect(formatBar?.disabled).toBe(true);

    vi.advanceTimersByTime(500);
    expect(formatBar?.disabled).toBe(true);
    bridge.emit({
      kind: Kinds.docDiscardCompleted,
      payload: { requestId: Number(requestId) + 1, succeeded: false },
    });
    expect(formatBar?.disabled).toBe(true);
    bridge.emit({
      kind: Kinds.docDiscardCompleted,
      payload: { requestId, succeeded: false },
    });
    expect(formatBar?.disabled).toBe(false);
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

  it("flushes reverse-order dual-pending edits oldest first so the newest Code edit wins", async () => {
    await mountApp();
    const { MarkdownEditor } = await import("../src/editors/editor.js");
    const { FormattedEditor: FreshFormattedEditor } = await import("../src/editors/formatted.js");
    const { flushPendingChangesInOrder, shouldMirrorInto } = await import("../src/index.js");
    const reports: string[] = [];

    const sourceHost = document.createElement("div");
    const formattedHost = document.createElement("div");
    document.body.append(sourceHost, formattedHost);
    let source: InstanceType<typeof MarkdownEditor>;
    let formatted: InstanceType<typeof FreshFormattedEditor>;
    source = new MarkdownEditor(sourceHost, {
      onChange: (text) => {
        reports.push(["code:", text].join(""));
        if (shouldMirrorInto(text, formatted)) formatted.mirror(text);
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
    formatted = new FreshFormattedEditor(formattedHost, {
      onChange: (text) => {
        reports.push(["formatted:", text].join(""));
        if (shouldMirrorInto(text, source)) source.mirror(text);
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
    source.setEditable(true);
    formatted.setEditable(true);
    source.setText("shared\n", true);
    formatted.setText("shared\n");

    const formattedView = (formatted as unknown as { view: EditorView }).view;
    formattedView.dispatch(formattedView.state.tr.insertText("older ", 1) as Transaction);
    vi.advanceTimersByTime(10);
    source.setText("newest Code edit\n");

    flushPendingChangesInOrder([source, formatted]);

    expect(reports).toEqual(["formatted:older shared\n", "code:newest Code edit\n"]);
    expect(source.getText()).toBe("newest Code edit\n");
    expect(formatted.getText()).toBe("newest Code edit\n");
  });

  it("publishes both pending pane edits before window.close", async () => {
    await mountApp();
    const { MarkdownEditor } = await import("../src/editors/editor.js");
    const { FormattedEditor: FreshFormattedEditor } = await import("../src/editors/formatted.js");
    const { runWindowClose, shouldMirrorInto } = await import("../src/index.js");
    const reports: string[] = [];

    const sourceHost = document.createElement("div");
    const formattedHost = document.createElement("div");
    document.body.append(sourceHost, formattedHost);
    let source: InstanceType<typeof MarkdownEditor>;
    let formatted: InstanceType<typeof FreshFormattedEditor>;
    source = new MarkdownEditor(sourceHost, {
      onChange: (text) => {
        reports.push(`code:${text}`);
        if (shouldMirrorInto(text, formatted)) formatted.mirror(text);
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
    formatted = new FreshFormattedEditor(formattedHost, {
      onChange: (text) => {
        reports.push(`formatted:${text}`);
        if (shouldMirrorInto(text, source)) source.mirror(text);
      },
      onEditAttempt: () => {},
      onScroll: () => {},
      onScrollSettle: () => {},
      onCursor: () => {},
      onHover: () => {},
      onContentResize: () => {},
      onFocus: () => {},
      onActiveChange: () => {},
      onOpenLink: () => {},
    });
    source.setEditable(true);
    formatted.setEditable(true);
    source.setText("shared\n", true, false);
    formatted.setText("shared\n");

    const formattedView = (formatted as unknown as { view: EditorView }).view;
    formattedView.dispatch(formattedView.state.tr.insertText("older ", 1) as Transaction);
    vi.advanceTimersByTime(10);
    source.setText("newest\n");

    await runWindowClose(
      [source, formatted],
      async () => true,
      () => reports.push("window.close"),
    );

    expect(reports).toEqual(["formatted:older shared\n", "code:newest\n", "window.close"]);
    vi.advanceTimersByTime(200);
    expect(reports.at(-1)).toBe("window.close");
  });

  it("waits for comment durability and refuses the close ACK when that boundary fails", async () => {
    const { runWindowClose } = await import("../src/index.js");
    const reports: string[] = [];
    let release: ((succeeded: boolean) => void) | undefined;
    const comments = new Promise<boolean>((resolve) => {
      release = resolve;
    });
    const target = {
      pendingChangeOrder: () => 1,
      flushPendingChange: () => {
        reports.push("editor.flush");
        return true;
      },
    };

    const closing = runWindowClose(
      [target],
      () => comments,
      () => reports.push("window.close"),
    );
    expect(reports).toEqual(["editor.flush"]);
    release?.(false);
    expect(await closing).toBe(false);
    expect(reports).toEqual(["editor.flush"]);

    expect(
      await runWindowClose(
        [target],
        async () => true,
        () => reports.push("window.close"),
      ),
    ).toBe(true);
    expect(reports.at(-1)).toBe("window.close");
  });

  it("times out an unresponsive comment flush without sending the close ACK", async () => {
    const { runWindowClose } = await import("../src/index.js");
    const reports: string[] = [];
    const target = {
      pendingChangeOrder: () => null,
      flushPendingChange: () => false,
    };

    const closing = runWindowClose(
      [target],
      () => new Promise<boolean>(() => undefined),
      () => reports.push("window.close"),
      5,
    );
    await vi.advanceTimersByTimeAsync(5);
    expect(await closing).toBe(false);
    expect(reports).toHaveLength(0);
  });

  const modeSwitches = [
    { from: "formatted", to: "code", first: "formatted", second: "editor" },
    { from: "formatted", to: "split", first: "formatted", second: "editor" },
    { from: "split", to: "code", first: "formatted", second: "editor" },
    { from: "code", to: "formatted", first: "editor", second: "formatted" },
    { from: "code", to: "split", first: "editor", second: "formatted" },
    { from: "split", to: "formatted", first: "editor", second: "formatted" },
  ] as const;

  it.each(
    modeSwitches,
  )("settles $first before $from → $to so the immediate $second edit preserves both", async ({
    first,
    second,
  }) => {
    const { MarkdownEditor } = await import("../src/editors/editor.js");
    const { resolveModeSwitchText, shouldMirrorInto } = await import("../src/index.js");
    const reports: string[] = [];
    let active: Pane = first;

    const sourceHost = document.createElement("div");
    const formattedHost = document.createElement("div");
    document.body.append(sourceHost, formattedHost);
    let source: InstanceType<typeof MarkdownEditor>;
    let formatted: FormattedEditor;
    source = new MarkdownEditor(sourceHost, {
      onChange: (text) => {
        active = "editor";
        reports.push(text);
        if (shouldMirrorInto(text, formatted)) formatted.mirror(text);
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
    formatted = new FormattedEditor(formattedHost, {
      onChange: (text) => {
        active = "formatted";
        reports.push(text);
        if (shouldMirrorInto(text, source)) source.mirror(text);
      },
      onEditAttempt: () => {},
      onScroll: () => {},
      onScrollSettle: () => {},
      onCursor: () => {},
      onHover: () => {},
      onContentResize: () => {},
      onFocus: () => {},
      onActiveChange: () => {},
      onOpenLink: () => {},
    });
    source.setText("base\n", true, false);
    formatted.setText("base\n");
    source.setEditable(true);
    formatted.setEditable(true);

    const edit = (pane: Pane, word: string): void => {
      if (pane === "editor") {
        source.setText(`${word} ${source.getText()}`);
        return;
      }
      const view = (formatted as unknown as { view: EditorView }).view;
      view.dispatch(view.state.tr.insertText(`${word} `, 1) as Transaction);
    };

    // The first edit is still inside its 120 ms trailing debounce when the author switches modes.
    edit(first, "first");
    vi.advanceTimersByTime(50);
    const canonical = resolveModeSwitchText(source, formatted, true, () => active);
    expect(canonical).toBe("first base\n");
    expect(source.getText()).toBe(canonical);
    expect(formatted.getText()).toBe(canonical);

    // The newly selected pane starts from the flushed text, so its immediate edit composes with — rather
    // than overwrites — the first pane's edit. Advancing beyond both original deadlines must produce no
    // stale third persistence callback: resolveModeSwitchText cancelled the first pane's timer by flush.
    active = second;
    edit(second, "second");
    vi.advanceTimersByTime(200);

    expect(reports).toEqual(["first base\n", "second first base\n"]);
    expect(source.getText()).toBe("second first base\n");
    expect(formatted.getText()).toBe("second first base\n");
  });

  it("keeps a read-only mode switch presentation-only instead of flushing a delayed edit", async () => {
    const { resolveModeSwitchText } = await import("../src/index.js");
    let flushes = 0;
    const pane = (text: string, order: number) => ({
      getText: () => text,
      hasPendingChange: () => true,
      pendingChangeOrder: () => order,
      flushPendingChange: () => {
        flushes += 1;
        return true;
      },
    });
    const source = pane("source identity\n", 1);
    const formatted = pane("formatted identity\n", 2);

    expect(resolveModeSwitchText(source, formatted, false, () => "formatted")).toBe(
      "formatted identity\n",
    );
    expect(flushes).toBe(0);
  });

  const documentNavigations = [
    { mode: "code", first: "editor" },
    { mode: "formatted", first: "formatted" },
    { mode: "split", first: "formatted" },
  ] as const;

  it.each(
    documentNavigations,
  )("persists a pending $mode edit before doc.open and retires it before hydration", async ({
    first,
  }) => {
    const { MarkdownEditor } = await import("../src/editors/editor.js");
    const { retirePendingChanges, runDocumentNavigation, shouldMirrorInto } = await import(
      "../src/index.js"
    );
    const events: string[] = [];

    const sourceHost = document.createElement("div");
    const formattedHost = document.createElement("div");
    document.body.append(sourceHost, formattedHost);
    let source: InstanceType<typeof MarkdownEditor>;
    let formatted: FormattedEditor;
    source = new MarkdownEditor(sourceHost, {
      onChange: (text) => {
        events.push(`changed:${text}`);
        if (shouldMirrorInto(text, formatted)) formatted.mirror(text);
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
    formatted = new FormattedEditor(formattedHost, {
      onChange: (text) => {
        events.push(`changed:${text}`);
        if (shouldMirrorInto(text, source)) source.mirror(text);
      },
      onEditAttempt: () => {},
      onScroll: () => {},
      onScrollSettle: () => {},
      onCursor: () => {},
      onHover: () => {},
      onContentResize: () => {},
      onFocus: () => {},
      onActiveChange: () => {},
      onOpenLink: () => {},
    });
    source.setText("old\n", true, false);
    formatted.setText("old\n");
    source.setEditable(true);
    formatted.setEditable(true);

    const edit = (pane: Pane, word: string): void => {
      if (pane === "editor") {
        source.setText(`${word} ${source.getText()}`);
        return;
      }
      const view = (formatted as unknown as { view: EditorView }).view;
      view.dispatch(view.state.tr.insertText(`${word} `, 1) as Transaction);
    };

    edit(first, "saved");
    vi.advanceTimersByTime(50);
    expect(
      runDocumentNavigation(
        [source, formatted],
        true,
        false,
        () => {
          source.setEditable(false);
          formatted.setEditable(false);
          events.push("transition");
        },
        () => events.push("doc.open"),
      ),
    ).toBe(true);
    expect(events).toEqual(["changed:saved old\n", "transition", "doc.open"]);

    // A real editor transaction arriving after doc.open but before hydration must be rejected by the
    // synchronous identity lock. Code and Formatted cover their independent read-only mechanisms; Split
    // runs the same boundary while both panes are present.
    if (first === "editor") {
      source.applyFormat("bold");
    } else {
      const view = (formatted as unknown as { view: EditorView }).view;
      view.dispatch(view.state.tr.insertText("blocked ", 1) as Transaction);
    }
    vi.advanceTimersByTime(200);
    expect(events).toEqual(["changed:saved old\n", "transition", "doc.open"]);
    expect(source.getText()).toBe("saved old\n");
    expect(formatted.getText()).toBe("saved old\n");

    retirePendingChanges([source, formatted]);
    source.setText("new document\n", true, false);
    formatted.setText("new document\n");
    vi.advanceTimersByTime(200);

    expect(events).toEqual(["changed:saved old\n", "transition", "doc.open"]);
    expect(source.getText()).toBe("new document\n");
    expect(formatted.getText()).toBe("new document\n");
  });

  it("guards read-only and repository-transitioning document navigation", async () => {
    const { runDocumentNavigation } = await import("../src/index.js");
    let flushes = 0;
    let opens = 0;
    const pane = {
      pendingChangeOrder: () => 1,
      flushPendingChange: () => {
        flushes += 1;
        return true;
      },
    };

    expect(
      runDocumentNavigation(
        [pane],
        false,
        false,
        () => {},
        () => (opens += 1),
      ),
    ).toBe(true);
    expect({ flushes, opens }).toEqual({ flushes: 0, opens: 1 });

    expect(
      runDocumentNavigation(
        [pane],
        true,
        true,
        () => {},
        () => (opens += 1),
      ),
    ).toBe(false);
    expect({ flushes, opens }).toEqual({ flushes: 0, opens: 1 });
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

describe("index.ts: persisted UI preferences (T-077, jsdom)", () => {
  afterEach(() => {
    Reflect.deleteProperty(globalThis as Record<string, unknown>, "external");
    document.body.innerHTML = "";
  });

  const prefsMarkup = `
    <div id="panes" data-mode="split"></div>
    <span id="view-modes" role="radiogroup">
      <button id="mode-code" type="button" role="radio" aria-checked="false" tabindex="-1">Code</button>
      <button id="mode-split" type="button" role="radio" aria-checked="true" tabindex="0">Split</button>
      <button id="mode-formatted" type="button" role="radio" aria-checked="false" tabindex="-1">Formatted</button>
    </span>
    <button id="wrap-btn" type="button" aria-pressed="true">Wrap: on</button>
    <button id="theme-btn" type="button" aria-checked="false">Dark theme</button>
  `;

  it("asks the host for the saved preferences once wiring is complete", async () => {
    const bridge = await mountApp(prefsMarkup);
    expect(bridge.sent.some((m) => m.kind === Kinds.preferencesRequest)).toBe(true);
  });

  it("applies a saved theme/wrap/view-mode reply over the OS-derived/DOM-derived startup defaults", async () => {
    const bridge = await mountApp(prefsMarkup);
    const wrapBtn = document.querySelector<HTMLButtonElement>("#wrap-btn");
    const panesEl = document.querySelector<HTMLElement>("#panes");
    const formattedBtn = document.querySelector<HTMLButtonElement>("#mode-formatted");

    // Before the reply: the OS colour scheme (stubbed to "not dark"), wrap on, and the DOM-declared split.
    expect(document.documentElement.dataset.theme).toBeUndefined();
    expect(wrapBtn?.getAttribute("aria-pressed")).toBe("true");
    expect(panesEl?.dataset.mode).toBe("split");

    bridge.emit({
      kind: Kinds.preferencesState,
      payload: { theme: "dark", wrap: false, viewMode: "formatted" },
    });

    expect(document.documentElement.dataset.theme).toBe("dark");
    expect(wrapBtn?.getAttribute("aria-pressed")).toBe("false");
    expect(wrapBtn?.textContent).toBe("Wrap: off");
    expect(panesEl?.dataset.mode).toBe("formatted");
    expect(formattedBtn?.getAttribute("aria-checked")).toBe("true");
  });

  it("a saved preference with no theme override leaves the OS-derived theme untouched", async () => {
    const bridge = await mountApp(prefsMarkup);

    bridge.emit({ kind: Kinds.preferencesState, payload: { wrap: true, viewMode: "split" } });

    // theme absent (never overridden): the webview keeps following the OS colour scheme, not forced light.
    expect(document.documentElement.dataset.theme).toBeUndefined();
  });

  it("a malformed preferences.state reply is dropped rather than corrupting the toolbar", async () => {
    const bridge = await mountApp(prefsMarkup);
    const wrapBtn = document.querySelector<HTMLButtonElement>("#wrap-btn");

    bridge.emit({ kind: Kinds.preferencesState, payload: { wrap: true, viewMode: "not-a-mode" } });

    expect(wrapBtn?.getAttribute("aria-pressed")).toBe("true");
  });

  it("toggling wrap round-trips the full current theme/wrap/view-mode triple to the host", async () => {
    const bridge = await mountApp(prefsMarkup);
    const wrapBtn = document.querySelector<HTMLButtonElement>("#wrap-btn");

    wrapBtn?.click();

    const update = [...bridge.sent].reverse().find((m) => m.kind === Kinds.preferencesUpdate);
    expect(update?.payload).toEqual({ theme: "light", wrap: false, viewMode: "split" });
  });

  it("toggling the theme round-trips the full current triple to the host", async () => {
    const bridge = await mountApp(prefsMarkup);
    const themeBtn = document.querySelector<HTMLButtonElement>("#theme-btn");

    themeBtn?.click();

    const update = [...bridge.sent].reverse().find((m) => m.kind === Kinds.preferencesUpdate);
    expect(update?.payload).toEqual({ theme: "dark", wrap: true, viewMode: "split" });
  });

  it("switching the view mode round-trips the full current triple to the host", async () => {
    const bridge = await mountApp(prefsMarkup);
    const formattedBtn = document.querySelector<HTMLButtonElement>("#mode-formatted");

    formattedBtn?.click();

    const update = [...bridge.sent].reverse().find((m) => m.kind === Kinds.preferencesUpdate);
    expect(update?.payload).toEqual({ theme: "light", wrap: true, viewMode: "formatted" });
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
    onScrollSettle: () => void;
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

    constructor(private readonly pane: Pane) {}

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
      paneEvents.push(`${this.pane}.mirror`);
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

    setComments(): void {
      paneEvents.push(`${this.pane}.comments`);
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

  // A stand-in for the real coordinator that only RECORDS the calls index.ts makes (the "which pane leads"
  // policy now lives inside the real SplitSync and is unit-tested in sync-coordinator.test.ts; here we
  // verify index.ts delegates to the coordinator at the right moments). It still emits an echo — a
  // coordinator write fires the passive pane's scroll event, which index.ts routes back to onScroll — so
  // the re-entrancy of that wiring stays covered (index.ts must not loop or re-fire on the coordinator's
  // own echo).
  class MockSplitSync {
    readonly syncFromCalls: Pane[] = [];
    readonly scrollCalls: Pane[] = [];
    readonly settleCalls: Pane[] = [];
    readonly focusCalls: Pane[] = [];
    reconciledCount = 0;
    private active: Pane = "editor";
    // Panes we have "written" whose echo scroll event hasn't fired yet — a faithful one-shot model of the
    // real coordinator's value-based echo suppression: a coordinator write makes the passive pane's NEXT
    // scroll event its echo (consumed here), while a later genuine scroll of that pane is not.
    private readonly pendingEcho = new Set<Pane>();

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

    readingLine(): number {
      return 0;
    }

    activePane(): Pane {
      return this.active;
    }

    syncFrom(pane: Pane): void {
      this.syncFromCalls.push(pane);
      this.active = pane;
      this.emitEcho(this.other(pane));
    }

    reconciled(): void {
      this.reconciledCount += 1;
      // Re-aligns the passive pane from the active one — its write is the passive pane's echo.
      this.emitEcho(this.other(this.active));
    }

    settle(pane: Pane): void {
      this.settleCalls.push(pane);
      this.drive(pane);
    }

    onFocus(pane: Pane): void {
      this.focusCalls.push(pane);
      this.active = pane;
      this.emitEcho(this.other(pane));
    }

    onEditorScroll(): void {
      this.scrollCalls.push("editor");
      this.drive("editor");
    }

    onFormattedScroll(): void {
      this.scrollCalls.push("formatted");
      this.drive("formatted");
    }

    isEcho(pane: Pane): boolean {
      return this.pendingEcho.has(pane);
    }

    private drive(pane: Pane): void {
      if (this.pendingEcho.delete(pane)) {
        return; // this pane's own echo — consumed, no drive-back
      }
      this.active = pane;
      this.emitEcho(this.other(pane));
    }

    private other(pane: Pane): Pane {
      return pane === "editor" ? "formatted" : "editor";
    }

    private emitEcho(pane: Pane): void {
      this.pendingEcho.add(pane);
      if (pane === "editor") {
        editorCallbacks?.onScroll();
      } else {
        formattedCallbacks?.onScroll();
      }
    }
  }

  let editorCallbacks: EditorCallbacks | undefined;
  let formattedCallbacks: FormattedCallbacks | undefined;
  let splitSyncInstances: MockSplitSync[];
  let paneEvents: string[];

  async function flushFrame(): Promise<void> {
    await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
  }

  beforeEach(() => {
    splitSyncInstances = [];
    paneEvents = [];
    editorCallbacks = undefined;
    formattedCallbacks = undefined;

    vi.doMock("../src/editors/editor.js", () => ({
      MarkdownEditor: class extends MockPane {
        constructor(_host: HTMLElement, callbacks: EditorCallbacks) {
          super("editor");
          editorCallbacks = callbacks;
        }
      },
    }));
    vi.doMock("../src/editors/formatted.js", () => ({
      FormattedEditor: class extends MockPane {
        constructor(_host: HTMLElement, callbacks: FormattedCallbacks) {
          super("formatted");
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

  it("delegates the resize re-align to the coordinator (reconciled), not a pane choice of its own", async () => {
    await mountApp();
    expect(editorCallbacks).toBeDefined();
    expect(splitSyncInstances).toHaveLength(1);

    // A user editor scroll (declares the editor active in the coordinator), then a window resize reflows
    // the panes: index.ts runs height-sync and hands the re-align to the coordinator's reconciled(), which
    // couples the passive pane from whichever pane is active — index.ts no longer picks the pane itself.
    editorCallbacks?.onScroll();
    window.dispatchEvent(new Event("resize"));
    await flushFrame();

    // "editor" (user scroll) → "formatted" (its coupling echo) → "formatted" (reconciled()'s re-align echo).
    expect(splitSyncInstances[0]?.scrollCalls).toEqual(["editor", "formatted", "formatted"]);
    expect(splitSyncInstances[0]?.reconciledCount).toBe(1);
    // index.ts no longer chooses a re-align source; that is the coordinator's active-pane state now.
    expect(splitSyncInstances[0]?.syncFromCalls).toEqual([]);
  });

  it("mirrors a formatted edit before resolving the new comment anchors in either pane", async () => {
    await mountApp();
    paneEvents = [];

    formattedCallbacks?.onChange("inserted before an existing comment\n");

    expect(paneEvents).toEqual(["editor.mirror", "editor.comments", "formatted.comments"]);
  });

  it("delegates a settling scroll to the coordinator symmetrically for both panes", async () => {
    await mountApp();
    expect(editorCallbacks).toBeDefined();
    expect(formattedCallbacks).toBeDefined();
    expect(splitSyncInstances).toHaveLength(1);

    // Both panes now re-snap through the SAME coordinator settle path (the editor had it before; the
    // formatted pane now does too) — index.ts just forwards which pane settled and lets the coordinator
    // suppress an echo settle.
    editorCallbacks?.onScrollSettle();
    formattedCallbacks?.onScrollSettle();

    expect(splitSyncInstances[0]?.settleCalls).toEqual(["editor", "formatted"]);
  });

  it("declares a scrolled pane active through the coordinator, not a local leadingPane", async () => {
    await mountApp();
    expect(editorCallbacks).toBeDefined();
    expect(formattedCallbacks).toBeDefined();
    expect(splitSyncInstances).toHaveLength(1);

    // index.ts forwards every genuine scroll to the coordinator, which owns the active/echo decision. The
    // coordinator's own echo (the passive pane's scroll event) is routed straight back here as an onScroll
    // too, proving index.ts doesn't loop or special-case it — that is all the coordinator's job now.
    editorCallbacks?.onScroll();
    formattedCallbacks?.onScroll();

    expect(splitSyncInstances[0]?.scrollCalls).toEqual([
      "editor",
      "formatted", // the editor scroll's coupling echo, forwarded back
      "formatted",
      "editor", // the formatted scroll's coupling echo, forwarded back
    ]);
  });

  it("declares a focused pane active through the coordinator", async () => {
    await mountApp();
    expect(editorCallbacks).toBeDefined();
    expect(formattedCallbacks).toBeDefined();
    expect(splitSyncInstances).toHaveLength(1);

    // Focus now goes to the coordinator (onFocus), which declares the pane active and best-effort couples
    // the sibling — index.ts no longer tracks a leadingPane of its own.
    editorCallbacks?.onFocus();
    formattedCallbacks?.onFocus();

    expect(splitSyncInstances[0]?.focusCalls).toEqual(["editor", "formatted"]);
    expect(splitSyncInstances[0]?.activePane()).toBe("formatted");
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
      payload: { path: "docs/spec.md", text: "# Hello\n", docDir: "docs", readOnly: false },
    });

    // Past the 120ms debounce the (non-silent, pre-fix) source-editor onChange would have fired.
    vi.advanceTimersByTime(500);

    expect(bridge.sent.some((message) => message.kind === Kinds.editorChanged)).toBe(false);

    // The loaded document still reaches both panes — silencing the change notification must not
    // degrade what the author actually sees.
    expect(document.querySelector("#editor .cm-content")?.textContent).toBe("# Hello");
    expect(document.querySelector("#formatted")?.textContent).toContain("Hello");
  });

  it("shows a plain repository path for a remote preview, never its internal capability URI", async () => {
    const bridge = await mountApp();

    bridge.emit({
      kind: Kinds.docLoaded,
      payload: {
        path: "github://octo/specs/main/docs%2Fguide.md",
        text: "# Guide\n",
        docDir: "",
        readOnly: true,
        repository: "octo/specs",
        branch: "main",
        repositoryPath: "docs/guide.md",
      },
    });

    expect(document.querySelector("#status")?.textContent).toBe("docs/guide.md");
    expect(document.body.textContent).not.toContain("github://");
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
      payload: { path: "docs/other.md", text: "# Other doc\n", docDir: "docs", readOnly: false },
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
      payload: { path: "docs/first.md", text: "# First\n", docDir: "docs", readOnly: false },
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
      payload: {
        path: "docs/second.md",
        text: "# Second\n\nBody text.\n",
        docDir: "docs",
        readOnly: false,
      },
    });

    expect((scroller as HTMLElement).scrollTop).toBe(0);
    expect((formattedEl as HTMLElement).scrollTop).toBe(0);
  });
});
