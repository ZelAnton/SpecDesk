// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { MarkdownEditor } from "../src/editor.js";

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
        lineStart: 0,
        lineEnd: 0,
        anchorLine: -1,
        removedText: "",
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
        lineStart: 0,
        lineEnd: 0,
        anchorLine: -1,
        removedText: "",
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
        lineStart: 0,
        lineEnd: 0,
        anchorLine: -1,
        removedText: "",
        baseSource: "The quick brown fox leaps over the lazy dog today.",
      },
      { kind: "removed", lineStart: 0, lineEnd: 0, anchorLine: 2, removedText: "gone block" },
    ]);

    expect(host.querySelectorAll(".cm-diff-word-added").length).toBeGreaterThan(0);
    expect(host.querySelectorAll(".cm-diff-word-removed").length).toBeGreaterThan(0);
    expect(host.querySelector(".cm-diff-removed-marker")?.textContent).toContain("gone block");
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
