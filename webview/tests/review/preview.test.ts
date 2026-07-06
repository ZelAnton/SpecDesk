// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { blockForLine, isFresh, type PreviewBlock } from "../../src/review/preview.js";

// instanceof narrowing (no `as`) — mirrors the pattern in dialogs.test.ts.
function stubElement(): HTMLElement {
  const el = document.createElement("div");
  if (!(el instanceof HTMLElement)) {
    throw new Error("expected an HTMLElement");
  }
  return el;
}

/** Build a block list without meaningful DOM content — only the line ranges matter for these pure helpers. */
function blocks(...ranges: Array<[number, number]>): PreviewBlock[] {
  return ranges.map(([lineStart, lineEnd]) => ({
    el: stubElement(),
    lineStart,
    lineEnd,
  }));
}

describe("isFresh", () => {
  it("accepts the same or a newer version", () => {
    expect(isFresh(5, 5)).toBe(true);
    expect(isFresh(5, 6)).toBe(true);
  });

  it("rejects an older version (a superseded render)", () => {
    expect(isFresh(5, 4)).toBe(false);
  });
});

describe("blockForLine", () => {
  const index = blocks([0, 0], [2, 4], [6, 6]);

  it("returns the block whose range contains the line", () => {
    expect(blockForLine(index, 3)).toBe(index[1]);
    expect(blockForLine(index, 0)).toBe(index[0]);
    expect(blockForLine(index, 6)).toBe(index[2]);
  });

  it("falls back to the last block at or before a gap line", () => {
    // Line 5 sits in the blank gap between blocks [2,4] and [6,6].
    expect(blockForLine(index, 5)).toBe(index[1]);
  });

  it("falls back to the first block for a line before everything", () => {
    expect(blockForLine(blocks([3, 5]), 0)).toEqual({
      el: expect.any(HTMLElement),
      lineStart: 3,
      lineEnd: 5,
    });
  });

  it("returns undefined for an empty index", () => {
    expect(blockForLine([], 2)).toBeUndefined();
  });
});
