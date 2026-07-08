import { describe, expect, it } from "vitest";
import type { DiffMark } from "../../src/review/diff-marks.js";
import { buildOverlayPlan, removedMarkerText } from "../../src/review/overlay-plan.js";

// overlay-plan is the single pane-independent layer both review panes render from (T-078): it owns the
// ONE removed-marker anchoring policy and the ONE removed-text policy, so the Code and Formatted panes
// place a deletion identically and never leak Markdown syntax. Pure and DOM-free, so pin its branches
// directly here; the DOM-level parity is checked in editor.test.ts / formatted.test.ts.

describe("removedMarkerText", () => {
  it("previews a single removed line", () => {
    expect(removedMarkerText("a heading", false)).toBe("Deleted by you — a heading");
  });

  it("shows a placeholder when the removed block is empty", () => {
    expect(removedMarkerText("", false)).toBe("Deleted by you — (empty block)");
    expect(removedMarkerText("   ", false)).toBe("Deleted by you — (empty block)");
  });

  it("previews the first line and counts the lines for a multi-line block", () => {
    expect(removedMarkerText("first\nsecond\nthird", false)).toBe(
      "Deleted by you — first (… 3 lines)",
    );
  });

  it("trims the previewed first line", () => {
    expect(removedMarkerText("  spaced  \nmore", false)).toBe(
      "Deleted by you — spaced (… 2 lines)",
    );
  });

  // The asymmetry T-078 removes: a whole removed block arrives as RAW source, which used to light up
  // Markdown syntax in the WYSIWYG pane. The single policy flattens the previewed line's leading block
  // markers so it reads as plain language in both panes.
  it("strips leading block markers from a whole block's raw-source preview", () => {
    expect(removedMarkerText("## Section title", false)).toBe("Deleted by you — Section title");
    expect(removedMarkerText("- a list item\n- another", false)).toBe(
      "Deleted by you — a list item (… 2 lines)",
    );
    expect(removedMarkerText("> a quote", false)).toBe("Deleted by you — a quote");
    expect(removedMarkerText("1. first ordered\n2. second", false)).toBe(
      "Deleted by you — first ordered (… 2 lines)",
    );
    expect(removedMarkerText("* star bullet", false)).toBe("Deleted by you — star bullet");
  });

  it("strips nested leading markers (a list inside a blockquote)", () => {
    expect(removedMarkerText("> - quoted item", false)).toBe("Deleted by you — quoted item");
  });

  it("leaves a plain paragraph's first line untouched", () => {
    expect(removedMarkerText("Just a normal paragraph.", false)).toBe(
      "Deleted by you — Just a normal paragraph.",
    );
  });

  // A row/item's text already arrives flattened from native, so a sub marker is shown as-is — no stripping
  // that could mangle legitimate content (e.g. an item whose flattened text starts with "1. ").
  it("uses a sub (row/item) text verbatim — no re-stripping", () => {
    expect(removedMarkerText("gone item", true)).toBe("Deleted by you — gone item");
    expect(removedMarkerText("1 | 2", true)).toBe("Deleted by you — 1 | 2");
    expect(removedMarkerText("- literal dash kept", true)).toBe(
      "Deleted by you — - literal dash kept",
    );
  });
});

describe("buildOverlayPlan — instruction shapes", () => {
  it("maps added/moved marks to fill instructions", () => {
    const marks: DiffMark[] = [
      { kind: "added", sub: false, lineStart: 1, lineEnd: 2 },
      { kind: "moved", sub: true, lineStart: 5, lineEnd: 5 },
    ];
    expect(buildOverlayPlan(marks, [])).toEqual([
      { type: "fill", kind: "added", sub: false, lineStart: 1, lineEnd: 2 },
      { type: "fill", kind: "moved", sub: true, lineStart: 5, lineEnd: 5 },
    ]);
  });

  it("maps a changed mark to an inline instruction carrying both bases", () => {
    const marks: DiffMark[] = [
      {
        kind: "changed",
        sub: false,
        lineStart: 0,
        lineEnd: 0,
        baseText: "flat",
        baseSource: "raw",
      },
    ];
    expect(buildOverlayPlan(marks, [])).toEqual([
      { type: "inline", sub: false, lineStart: 0, lineEnd: 0, baseText: "flat", baseSource: "raw" },
    ]);
  });

  it("preserves mark order in the plan", () => {
    const marks: DiffMark[] = [
      { kind: "removed", sub: false, anchorLine: 0, removedText: "gone" },
      { kind: "added", sub: false, lineStart: 0, lineEnd: 0 },
    ];
    const plan = buildOverlayPlan(marks, [0]);
    expect(plan.map((i) => i.type)).toEqual(["removed", "fill"]);
  });
});

describe("buildOverlayPlan — the single removed-anchor policy", () => {
  // Head blocks start at source lines 0, 3, 7 (three top-level blocks with blank lines / multi-line
  // content between them). Both panes derive the same starts from the same head, so this scan is the one
  // place the anchor is resolved.
  const starts = [0, 3, 7];

  const anchorOf = (anchorLine: number, sub = false) => {
    const plan = buildOverlayPlan([{ kind: "removed", sub, anchorLine, removedText: "x" }], starts);
    const instr = plan[0];
    if (instr?.type !== "removed") {
      throw new Error("expected a removed instruction");
    }
    return instr.anchor;
  };

  it("anchors a top-level removal before the first block starting at/after the anchor line", () => {
    // Anchor exactly on a block start.
    expect(anchorOf(3)).toEqual({ at: "block", blockIndex: 1, line: 3 });
    // Anchor on a blank/interior line BETWEEN blocks (e.g. line 5, inside the gap before block 2) snaps
    // to the following block's start — the edge where the old Code-pane line-clamp and Formatted-pane
    // block-scan used to diverge.
    expect(anchorOf(5)).toEqual({ at: "block", blockIndex: 2, line: 7 });
  });

  it("anchors a leading removal (before all content) at the first block", () => {
    expect(anchorOf(0)).toEqual({ at: "block", blockIndex: 0, line: 0 });
  });

  it("anchors a trailing removal (past every block) at the end — no block starts at/after it", () => {
    // The 'clamp by lineCount' edge from the old Code pane and the 'nothing at/after the anchor' edge from
    // the old Formatted pane now resolve to the same single 'end' placement.
    expect(anchorOf(9)).toEqual({ at: "end" });
    expect(anchorOf(1000)).toEqual({ at: "end" });
  });

  it("anchors a removed row/item at its child source line (resolved to the node by each pane)", () => {
    expect(anchorOf(4, true)).toEqual({ at: "child", line: 4 });
    // A sub anchor past the document does NOT scan top-level blocks — it stays a child anchor, and each
    // pane falls back to the document end when the line resolves to no node.
    expect(anchorOf(1000, true)).toEqual({ at: "child", line: 1000 });
  });

  it("resolves to the end when there are no head blocks at all", () => {
    const plan = buildOverlayPlan(
      [{ kind: "removed", sub: false, anchorLine: 0, removedText: "x" }],
      [],
    );
    const instr = plan[0];
    expect(instr?.type === "removed" && instr.anchor).toEqual({ at: "end" });
  });
});
