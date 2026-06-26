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
