import { describe, expect, it } from "vitest";
import { expandDiffMarks } from "../../src/review/diff-marks.js";
import type { ChildDiffPayload, DiffEntryPayload } from "../../src/wire/protocol.js";

/** A changed plain (childless) block entry — carries its inline word-diff bases. */
function changedBlock(over: {
  lineStart: number;
  lineEnd: number;
  baseText?: string;
  baseSource?: string;
}): DiffEntryPayload {
  return {
    kind: "changed",
    lineStart: over.lineStart,
    lineEnd: over.lineEnd,
    children: [],
    baseText: over.baseText ?? "",
    baseSource: over.baseSource ?? "",
  };
}

/** A changed container entry carrying per-child diffs. */
function changedContainer(
  lineStart: number,
  lineEnd: number,
  children: ChildDiffPayload[],
): DiffEntryPayload {
  return { kind: "changed", lineStart, lineEnd, children, baseText: "", baseSource: "" };
}

describe("expandDiffMarks", () => {
  it("passes a plain (childless) block through as a whole-block mark", () => {
    const marks = expandDiffMarks(
      [changedBlock({ lineStart: 2, lineEnd: 2, baseText: "old", baseSource: "old src" })],
      "# H\n\nnew\n",
    );
    expect(marks).toHaveLength(1);
    expect(marks[0]).toMatchObject({
      kind: "changed",
      sub: false,
      lineStart: 2,
      lineEnd: 2,
      baseText: "old",
      baseSource: "old src",
    });
  });

  it("resolves a changed list's child to the item's source line range (sub, with baseText and baseSource)", () => {
    // List items at source lines 0, 1, 2; the second item changed.
    const marks = expandDiffMarks(
      [
        changedContainer(0, 2, [
          { kind: "changed", childIndex: 1, baseText: "two", baseSource: "- two" },
        ]),
      ],
      "- one\n- two changed\n- three\n",
    );
    expect(marks).toHaveLength(1);
    expect(marks[0]).toMatchObject({
      kind: "changed",
      lineStart: 1,
      lineEnd: 1,
      sub: true,
      baseText: "two",
      // A sub (row/item) changed mark carries its own code-pane source too, symmetrically with baseText.
      baseSource: "- two",
    });
  });

  it("anchors a removed child at the following item's line (sub removed)", () => {
    const marks = expandDiffMarks(
      [changedContainer(0, 2, [{ kind: "removed", anchorIndex: 1, removedText: "gone" }])],
      "- one\n- two\n- three\n",
    );
    expect(marks).toHaveLength(1);
    expect(marks[0]).toMatchObject({
      kind: "removed",
      anchorLine: 1,
      removedText: "gone",
      sub: true,
    });
  });

  it("resolves a first-block table's child even with leading blank lines before it", () => {
    // Two leading blank lines ride with the table as its "head" content (md-blocks.ts
    // contentLineStart), so the block's own lineStart is pulled back to 0 while its real token —
    // and the Markdig AST's entry.lineStart — start at line 2. childLineStarts must be keyed by
    // that real start, not the pulled-back one, or the container falls back to a whole-block wash.
    const marks = expandDiffMarks(
      [
        changedContainer(2, 4, [
          { kind: "changed", childIndex: 1, baseText: "1 | 2", baseSource: "| 1 | 2 |" },
        ]),
      ],
      "\n\n| a | b |\n| --- | --- |\n| 1 | 2 |\n",
    );
    expect(marks).toHaveLength(1);
    expect(marks[0]).toMatchObject({
      kind: "changed",
      sub: true,
      lineStart: 4,
      lineEnd: 4,
      baseText: "1 | 2",
      baseSource: "| 1 | 2 |",
    });
  });

  it("falls back to a whole-block mark when the container's children can't be resolved", () => {
    // entry.lineStart=5 matches no block in the (single-paragraph) text → no childLineStarts.
    const marks = expandDiffMarks(
      [
        changedContainer(5, 5, [
          { kind: "changed", childIndex: 0, baseText: "x", baseSource: "x" },
        ]),
      ],
      "just a paragraph\n",
    );
    expect(marks).toHaveLength(1);
    expect(marks[0]).toMatchObject({ kind: "changed", sub: false, lineStart: 5, lineEnd: 5 });
  });
});
