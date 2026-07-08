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

  it("resolves a changed list's child to the item's source line range (sub, with baseText)", () => {
    // List items at source lines 0, 1, 2; the second item changed.
    const marks = expandDiffMarks(
      [changedContainer(0, 2, [{ kind: "changed", childIndex: 1, baseText: "two" }])],
      "- one\n- two changed\n- three\n",
    );
    expect(marks).toHaveLength(1);
    expect(marks[0]).toMatchObject({
      kind: "changed",
      lineStart: 1,
      lineEnd: 1,
      sub: true,
      baseText: "two",
      // A sub (row/item) changed mark has no own code-pane source.
      baseSource: null,
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

  it("falls back to a whole-block mark when the container's children can't be resolved", () => {
    // entry.lineStart=5 matches no block in the (single-paragraph) text → no childLineStarts.
    const marks = expandDiffMarks(
      [changedContainer(5, 5, [{ kind: "changed", childIndex: 0, baseText: "x" }])],
      "just a paragraph\n",
    );
    expect(marks).toHaveLength(1);
    expect(marks[0]).toMatchObject({ kind: "changed", sub: false, lineStart: 5, lineEnd: 5 });
  });
});
