// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MarkdownEditor } from "../../src/editors/editor.js";

// Runtime check of the Code-pane diff overlay (CodeMirror): the inline source word-diff decorations.

function mount(): { ed: MarkdownEditor; host: HTMLDivElement } {
  const host = document.createElement("div");
  document.body.appendChild(host);
  const ed = new MarkdownEditor(host, {
    onChange: () => {},
    onScroll: () => {},
    onScrollSettle: () => {},
    onCursor: () => {},
    onHover: () => {},
    onGeometryChange: () => {},
    onEditAttempt: () => {},
    onFocus: () => {},
    onOpenLink: () => {},
  });
  return { ed, host };
}

describe("MarkdownEditor diff overlay (jsdom)", () => {
  it("highlights changed source words inline in a changed paragraph, keeping the line wash", () => {
    const { ed, host } = mount();
    ed.setText("The quick brown fox jumps over the lazy dog today.\n");
    ed.setDiff([
      {
        kind: "changed",
        sub: false,
        lineStart: 0,
        lineEnd: 0,
        baseText: "The quick brown fox leaps over the lazy dog today.",
        baseSource: "The quick brown fox leaps over the lazy dog today.",
      },
    ]);

    const added = [...host.querySelectorAll(".cm-diff-word-added")];
    expect(added.some((w) => w.textContent?.includes("jumps"))).toBe(true);
    const removed = [...host.querySelectorAll(".cm-diff-word-removed")];
    expect(removed.some((w) => w.textContent?.includes("leaps"))).toBe(true);
    // The whole-line wash stays as the block-level signal (no annotation pill in the Code pane).
    expect(host.querySelectorAll(".cm-diff-changed").length).toBeGreaterThan(0);
  });

  it("washes the lines without word marks when too much of the paragraph changed", () => {
    const { ed, host } = mount();
    ed.setText("totally different wording here now\n");
    ed.setDiff([
      {
        kind: "changed",
        sub: false,
        lineStart: 0,
        lineEnd: 0,
        baseText: "alpha beta gamma delta",
        baseSource: "alpha beta gamma delta",
      },
    ]);

    expect(host.querySelectorAll(".cm-diff-word-added")).toHaveLength(0);
    expect(host.querySelectorAll(".cm-diff-changed").length).toBeGreaterThan(0);
  });

  it("renders inline word marks, a del ghost, and a removed-block marker in one set without error", () => {
    const { ed, host } = mount();
    ed.setText("The quick brown fox jumps over the lazy dog today.\n\nkeep\n");
    // A changed paragraph (line wash + inline word marks + a del ghost) AND a removed block (a block
    // widget) in the same decoration set — exercises the heterogeneous mix.
    ed.setDiff([
      {
        kind: "changed",
        sub: false,
        lineStart: 0,
        lineEnd: 0,
        baseText: "The quick brown fox leaps over the lazy dog today.",
        baseSource: "The quick brown fox leaps over the lazy dog today.",
      },
      { kind: "removed", sub: false, anchorLine: 2, removedText: "gone block" },
    ]);

    expect(host.querySelectorAll(".cm-diff-word-added").length).toBeGreaterThan(0);
    expect(host.querySelectorAll(".cm-diff-word-removed").length).toBeGreaterThan(0);
    expect(host.querySelector(".cm-diff-removed-marker")?.textContent).toContain("gone block");
  });
});

describe("MarkdownEditor.naturalLineTops lead-invariance (jsdom, T-061)", () => {
  // The height-sync lead is a stable fixed point only because `naturalLineTops` is invariant to the
  // leading spacer we apply: CodeMirror folds a leading block widget (side −1 at pos 0) into line 0's
  // block as `spaceAbove`, so `lineBlockAt(0).top` reports the region top (unchanged), and
  // spacerHeightAbove correctly does NOT subtract the lead at pos 0. If a CodeMirror upgrade ever
  // changed that, the lead would oscillate (grow without bound) between reconciles — this locks it in.
  it("keeps the first line's natural top unchanged when a leading spacer is applied", () => {
    const { ed } = mount();
    ed.setText("# Welcome to SpecDesk\n\nHello world.\n\n## Section\n\nMore.\n");

    const before = ed.naturalLineTops([0, 2, 4]);
    ed.setSpacers([], 60); // apply a 60px lead, no block spacers
    const after = ed.naturalLineTops([0, 2, 4]);

    // Every anchor's natural (spacer-free) top is identical before and after — the whole point of the
    // fixed point. In particular the FIRST anchor does not swing by the lead height.
    expect(after).toEqual(before);
  });
});

describe("MarkdownEditor.topVisibleLine / adjustScrollTop (jsdom, T-066)", () => {
  // Wiring-level sanity against a real CodeMirror view (the actual delta math lives in and is
  // exhaustively covered by height-sync.test.ts's computeScrollCompensation suite) — this just confirms
  // both methods work against a real `EditorView`/`scrollDOM` without throwing, since jsdom lacks real
  // layout and `topVisibleLine` deliberately avoids `posAtCoords`/`getBoundingClientRect` for that reason.
  it("reports line 0 at the default (unscrolled) position and does not throw applying spacers", () => {
    const { ed } = mount();
    ed.setText("one\ntwo\nthree\n");

    expect(ed.topVisibleLine()).toBe(0);
    expect(() => ed.setSpacers([{ lineEnd: 0, height: 40 }], 10)).not.toThrow();
  });

  it("adjustScrollTop nudges the live scrollDOM.scrollTop by the given delta", () => {
    const { ed, host } = mount();
    ed.setText("one\ntwo\nthree\n");
    const scroller = host.querySelector(".cm-scroller") as HTMLElement;
    scroller.scrollTop = 25;

    ed.adjustScrollTop(15);
    expect(scroller.scrollTop).toBe(40);

    ed.adjustScrollTop(-10);
    expect(scroller.scrollTop).toBe(30);
  });
});

describe("MarkdownEditor.topVisibleLineExact (jsdom, T-064)", () => {
  // jsdom has no real layout, so posAtCoords always misses (returns null) — that's exactly the
  // "measure not settled" case this method must survive without collapsing to line 0. `posAtCoords`
  // is stubbed to drive both branches deterministically.
  it("falls back to the last resolved value instead of 0 when posAtCoords misses", () => {
    const { ed } = mount();
    ed.setText("one\ntwo\nthree\n");

    // No successful resolution yet — the default fallback is 0 (unscrolled, never desynced).
    expect(ed.topVisibleLineExact()).toBe(0);

    // A successful probe lands mid document-line 2 (0-based line 1).
    vi.spyOn(ed, "posAtCoords").mockReturnValue(ed.getText().indexOf("two"));
    expect(ed.topVisibleLineExact()).toBe(1);

    // The next probe misses (mid-measure) — the previously resolved value must be reused, not 0.
    vi.spyOn(ed, "posAtCoords").mockReturnValue(null);
    expect(ed.topVisibleLineExact()).toBe(1);
  });

  it("probes the content area's left edge (contentDOM), not the scrollDOM rect which includes the gutter", () => {
    const { ed } = mount();
    ed.setText("one\ntwo\nthree\n");

    const contentRect = {
      left: 42,
      top: 7,
      right: 0,
      bottom: 0,
      width: 0,
      height: 0,
      x: 42,
      y: 7,
      toJSON: () => ({}),
    };
    vi.spyOn(ed.contentDOM, "getBoundingClientRect").mockReturnValue(contentRect as DOMRect);
    const posSpy = vi.spyOn(ed, "posAtCoords").mockReturnValue(0);

    ed.topVisibleLineExact();

    expect(posSpy).toHaveBeenCalledWith(43, 8);
  });
});

describe("MarkdownEditor.hasPendingChange (jsdom, T-042)", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("is true only while an edit's debounced onChange hasn't fired yet", () => {
    const { ed } = mount();
    expect(ed.hasPendingChange()).toBe(false);

    // A non-silent setText is a genuine document change at the update-listener level — the same path a
    // real edit takes — and starts the 120ms debounce (see DEBOUNCE_MS in editor.ts).
    ed.setText("edited\n");
    expect(ed.hasPendingChange()).toBe(true);
    vi.advanceTimersByTime(119);
    expect(ed.hasPendingChange()).toBe(true);
    vi.advanceTimersByTime(1);
    expect(ed.hasPendingChange()).toBe(false);
  });

  it("stays false across a silent mirror setText (no change notification is scheduled)", () => {
    const { ed } = mount();
    ed.setText("mirrored\n", true);
    expect(ed.hasPendingChange()).toBe(false);
  });

  // T-069: doc.loaded hydration is silent (sameDocument: false, see the marker-tracking suite below) —
  // suppressChange is driven only by `silent`, independently of `sameDocument`, so this must stay false
  // exactly like the same-document silent mirror above.
  it("stays false across a silent-but-different-document setText (doc.loaded)", () => {
    const { ed } = mount();
    ed.setText("a freshly loaded document\n", true, false);
    expect(ed.hasPendingChange()).toBe(false);
  });
});

describe("MarkdownEditor image-insert marker tracking (jsdom, T-034/M-21)", () => {
  // The bug: the insert position was captured once, at paste time, then used as-is after an async
  // host round-trip — stale once the document changed in the meantime, and shared across several
  // images pasted together (so out-of-order replies clobbered each other). trackPosition/
  // insertAtMarker replace that raw position with a marker remapped through every intervening edit.

  it("inserts at the marker's remapped position after text is typed elsewhere during the round-trip", () => {
    const { ed } = mount();
    ed.setText("one two three\n");
    // Captured right after "one" (position 3), as if the image had just been pasted there.
    const id = ed.trackPosition(3);
    // The author types more text at the very start while the host round-trip is still in flight.
    ed.insertAt(0, "XXX ");
    // The host reply now arrives: insertAtMarker must land after "one" in the CURRENT document
    // (position 7), not at the now-stale raw offset 3 (which would land inside "XXX ").
    ed.insertAtMarker(id, "[IMG]");

    expect(ed.getText()).toBe("XXX one[IMG] two three\n");
  });

  it("resolves two images captured at the same position independently, even out of order", () => {
    const { ed } = mount();
    ed.setText("abcdef\n");
    // Two images pasted together land at the very same initial position (3).
    const first = ed.trackPosition(3);
    const second = ed.trackPosition(3);

    // The SECOND image's host reply arrives first.
    ed.insertAtMarker(second, "[B]");
    // The first image's reply arrives after — it must not overwrite/collide with [B]; it resolves
    // to wherever position 3 now maps, i.e. right after what [B] already inserted.
    ed.insertAtMarker(first, "[A]");

    expect(ed.getText()).toBe("abc[B][A]def\n");
  });

  it("insertAtMarker is a no-op once the marker was already resolved or discarded", () => {
    const { ed } = mount();
    ed.setText("hello\n");
    const id = ed.trackPosition(2);
    ed.discardMarker(id);
    ed.insertAtMarker(id, "[late]");

    expect(ed.getText()).toBe("hello\n");
  });

  it("drops a pending marker across a non-silent whole-document setText (a different file was loaded) instead of inserting into the wrong doc", () => {
    const { ed } = mount();
    ed.setText("first document\n");
    const id = ed.trackPosition(5);
    ed.setText("a completely different document\n");
    ed.insertAtMarker(id, "[stale]");

    expect(ed.getText()).toBe("a completely different document\n");
  });

  // R-01 (T-034 review): a SILENT whole-document setText (the Split mirror / a mode-switch hydration)
  // carries the SAME logical content across — not a different document — so a pending marker must
  // survive it, unlike the docLoaded case above. Before the fix, setText unconditionally dropped every
  // pending marker, so pasting an image into one Split pane while the other pane's edit silently
  // mirrored back (or a mode switch re-hydrated a pane) during the async host round-trip silently lost
  // the image reference with no error and no trace.
  it("keeps a pending marker across a silent whole-document setText (Split mirror / mode switch), so a paste survives a round-trip alongside it", () => {
    const { ed } = mount();
    ed.setText("one two three\n");
    // Captured right after "one" (position 3), as if the image had just been pasted there.
    const id = ed.trackPosition(3);
    // The sibling pane mirrors the SAME content back in silently (Split's onFormattedChange /
    // applyMode's silent hydration) — content unchanged, so the marker must not be dropped.
    ed.setText("one two three\n", true);
    // The host reply now arrives: insertAtMarker must still land after "one".
    ed.insertAtMarker(id, "[IMG]");

    expect(ed.getText()).toBe("one[IMG] two three\n");
  });

  // T-069: doc.loaded hydrates the source editor SILENTLY (the host already has this text — no change
  // notification should round-trip back out as editor.changed), but it IS a genuinely different document
  // (a file was just opened/loaded), unlike the Split-mirror/mode-switch silent calls above — so a
  // pending marker from the PREVIOUS document must still be dropped, not restored at a now-meaningless
  // clamped position. `sameDocument` (defaulting to `silent`) is the explicit override for this case.
  it("drops a pending marker across a silent-but-different-document setText (doc.loaded), unlike a same-document silent mirror", () => {
    const { ed } = mount();
    ed.setText("one two three\n");
    const id = ed.trackPosition(3);
    ed.setText("a whole new document just loaded\n", true, false);
    ed.insertAtMarker(id, "[stale]");

    expect(ed.getText()).toBe("a whole new document just loaded\n");
  });

  it("clamps a restored marker to the new (shorter) document length after a silent setText", () => {
    const { ed } = mount();
    ed.setText("one two three\n");
    const id = ed.trackPosition(13); // just before the trailing newline
    // The sibling pane mirrors in a shorter edit (e.g. the author deleted trailing words there).
    ed.setText("one\n", true);
    ed.insertAtMarker(id, "[IMG]");

    expect(ed.getText()).toBe("one\n[IMG]");
  });

  it("clears (rather than pins to the last line) the active-line highlight once a shorter setText leaves it out of range (M-34)", () => {
    const { ed, host } = mount();
    ed.setText("one\ntwo\nthree\nfour\nfive\n");
    ed.setActiveLine(4); // "five" — the last line
    expect(host.querySelectorAll(".cm-active-line")).toHaveLength(1);

    // The Split mirror re-applies the last synced active line across a whole-document setText; here the
    // new document is shorter, so that line no longer exists. setText re-dispatches the remembered
    // activeLineValue via the same effect, so this reproduces the mirror path without depending on it.
    ed.setText("one\ntwo\n", true);

    expect(host.querySelectorAll(".cm-active-line")).toHaveLength(0);
  });
});

describe("MarkdownEditor.applyFormat at caret position 0 (jsdom, S-15)", () => {
  // The real CodeMirror dispatch path (not just the pure formatMarkdown computation): a document with a
  // leading blank line, caret at position 0 (Ctrl+Home), then every block-format toolbar button. Before
  // the fix this threw `RangeError: Invalid change range 1 to 0` straight out of view.dispatch.
  for (const command of ["h1", "h2", "bullet", "ordered", "quote", "code"] as const) {
    it(`does not throw for ${command}`, () => {
      const { ed } = mount();
      ed.setText("\n# Title\n\npara\n");
      ed.setEditable(true);
      // A fresh setText leaves the caret at document position 0 (no explicit selection given).
      expect(() => ed.applyFormat(command)).not.toThrow();
    });
  }
});
