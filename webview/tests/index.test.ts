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
function setupDom(): void {
  document.body.innerHTML = `
    <div id="editor"></div>
    <div id="preview"></div>
    <div id="formatted"></div>
    <button id="compare-btn" type="button" aria-pressed="false"></button>
    <div id="review-empty-bar" hidden></div>
  `;
}

/** Boot the real index.ts wiring against a mocked host bridge. The ipc.ts singleton reads
 *  `window.external` at module-eval time, so the bridge must be installed and the module graph reset
 *  before each fresh import. */
async function mountApp(): Promise<ReturnType<typeof mockBridge>> {
  setupDom();
  installMatchMediaStub();
  const bridge = mockBridge();
  Object.defineProperty(globalThis, "external", { value: bridge, configurable: true });
  vi.resetModules();
  await import("../src/index.js");
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
