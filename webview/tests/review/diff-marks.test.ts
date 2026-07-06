import { describe, expect, it } from "vitest";
import { expandDiffMarks } from "../../src/review/diff-marks.js";
import type { ChildDiffPayload, DiffEntryPayload } from "../../src/wire/protocol.js";

/** A whole-block diff entry with the given overrides (children default to none). */
function entry(over: Partial<DiffEntryPayload>): DiffEntryPayload {
  return {
    kind: "changed",
    lineStart: 0,
    lineEnd: 0,
    anchorLine: -1,
    removedText: "",
    children: [],
    baseText: "",
    baseSource: "",
    ...over,
  };
}

function child(over: Partial<ChildDiffPayload>): ChildDiffPayload {
  return {
    kind: "changed",
    childIndex: 0,
    anchorIndex: -1,
    removedText: "",
    baseText: "",
    ...over,
  };
}

describe("expandDiffMarks", () => {
  it("passes a plain (childless) block through as a whole-block mark", () => {
    const marks = expandDiffMarks(
      [
        entry({
          kind: "changed",
          lineStart: 2,
          lineEnd: 2,
          baseText: "old",
          baseSource: "old src",
        }),
      ],
      "# H\n\nnew\n",
    );
    expect(marks).toHaveLength(1);
    expect(marks[0]).toMatchObject({
      kind: "changed",
      lineStart: 2,
      lineEnd: 2,
      baseText: "old",
      baseSource: "old src",
    });
    expect(marks[0]?.sub).toBeUndefined();
  });

  it("resolves a changed list's child to the item's source line range (sub, with baseText)", () => {
    // List items at source lines 0, 1, 2; the second item changed.
    const marks = expandDiffMarks(
      [
        entry({
          kind: "changed",
          lineStart: 0,
          lineEnd: 2,
          children: [child({ kind: "changed", childIndex: 1, baseText: "two" })],
        }),
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
    });
  });

  it("anchors a removed child at the following item's line (sub removed)", () => {
    const marks = expandDiffMarks(
      [
        entry({
          kind: "changed",
          lineStart: 0,
          lineEnd: 2,
          children: [
            child({ kind: "removed", childIndex: -1, anchorIndex: 1, removedText: "gone" }),
          ],
        }),
      ],
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
      [entry({ kind: "changed", lineStart: 5, lineEnd: 5, children: [child({ childIndex: 0 })] })],
      "just a paragraph\n",
    );
    expect(marks).toHaveLength(1);
    expect(marks[0]).toMatchObject({ kind: "changed", lineStart: 5, lineEnd: 5 });
    expect(marks[0]?.sub).toBeUndefined();
  });
});
