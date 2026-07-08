import { describe, expect, it } from "vitest";
import { commonEnds, computeTextPatch } from "../../src/editors/mirror-patch.js";

// The shared minimal-patch primitive behind the Split cross-pane mirror (index.ts): a character-level
// single-range diff for the CodeMirror source editor, and a block-level common-ends count for the
// ProseMirror formatted editor. Both are pure functions — exhaustively unit-testable here, so the
// editor suites only need to check they are wired in correctly.

describe("computeTextPatch (character-level minimal patch)", () => {
  it("returns null for identical text (nothing to mirror)", () => {
    expect(computeTextPatch("same", "same")).toBeNull();
    expect(computeTextPatch("", "")).toBeNull();
  });

  it("captures a mid-string insertion as a zero-width replacement at the insert point", () => {
    // "hello world" → "hello brave world": common prefix "hello ", common suffix "world".
    expect(computeTextPatch("hello world", "hello brave world")).toEqual({
      from: 6,
      to: 6,
      insert: "brave ",
    });
  });

  it("captures a mid-string deletion as an empty-insert replacement", () => {
    expect(computeTextPatch("hello brave world", "hello world")).toEqual({
      from: 6,
      to: 12,
      insert: "",
    });
  });

  it("captures a word replacement as the smallest changed span", () => {
    expect(computeTextPatch("the cat sat", "the dog sat")).toEqual({
      from: 4,
      to: 7,
      insert: "dog",
    });
  });

  it("does not let the suffix overlap the prefix on a repeated run (append)", () => {
    // "aa" → "aaa": the prefix already claims both a's, so the suffix must not double-count them —
    // the patch is a single 'a' appended at the end, not a bogus overlapping range.
    expect(computeTextPatch("aa", "aaa")).toEqual({ from: 2, to: 2, insert: "a" });
  });

  it("handles a full replacement (no common prefix or suffix)", () => {
    expect(computeTextPatch("abc", "xyz")).toEqual({ from: 0, to: 3, insert: "xyz" });
  });

  it("handles an edit that only changes the leading characters", () => {
    expect(computeTextPatch("Xtail", "Ytail")).toEqual({ from: 0, to: 1, insert: "Y" });
  });

  it("applying the patch reproduces the new text", () => {
    const cases: [string, string][] = [
      ["one\ntwo\nthree\n", "one\ntwo!\nthree\n"],
      ["", "fresh"],
      ["drop me", ""],
      ["a b c d e", "a b X d e"],
    ];
    for (const [oldText, newText] of cases) {
      const patch = computeTextPatch(oldText, newText);
      if (patch === null) {
        expect(oldText).toBe(newText);
        continue;
      }
      const applied = oldText.slice(0, patch.from) + patch.insert + oldText.slice(patch.to);
      expect(applied).toBe(newText);
    }
  });
});

describe("commonEnds (block-level common leading/trailing count)", () => {
  it("counts a changed middle element (equal ends)", () => {
    expect(commonEnds(["A", "B", "C"], ["A", "X", "C"])).toEqual({ prefix: 1, suffix: 1 });
  });

  it("counts a removed middle element", () => {
    expect(commonEnds(["A", "B", "C"], ["A", "C"])).toEqual({ prefix: 1, suffix: 1 });
  });

  it("counts an inserted middle element", () => {
    expect(commonEnds(["A", "C"], ["A", "B", "C"])).toEqual({ prefix: 1, suffix: 1 });
  });

  it("never overlaps prefix and suffix for identical sequences", () => {
    // All three match as a prefix; the suffix must not re-count them (prefix + suffix ≤ length).
    expect(commonEnds(["A", "B", "C"], ["A", "B", "C"])).toEqual({ prefix: 3, suffix: 0 });
    expect(commonEnds(["A"], ["A"])).toEqual({ prefix: 1, suffix: 0 });
  });

  it("reports zero common ends when nothing matches", () => {
    expect(commonEnds(["A", "B"], ["X", "Y"])).toEqual({ prefix: 0, suffix: 0 });
  });

  it("handles a changed first block (common suffix only)", () => {
    expect(commonEnds(["A", "B", "C"], ["Z", "B", "C"])).toEqual({ prefix: 0, suffix: 2 });
  });
});
