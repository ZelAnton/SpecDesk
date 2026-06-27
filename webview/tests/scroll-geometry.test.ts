import { describe, expect, it } from "vitest";
import { type BlockBox, lineAtScrollTop, scrollTopForLine } from "../src/scroll-geometry.js";

// A preview-style box (no contentLineEnd → span = lineEnd+1-lineStart = 5) and a formatted-style box
// (contentLineEnd present → span = contentLineEnd-lineStart = 3), both 50px tall starting at y=100.
const preview: BlockBox = {
  lineStart: 10,
  lineEnd: 14,
  contentLineEnd: undefined,
  top: 100,
  height: 50,
};
const formatted: BlockBox = {
  lineStart: 10,
  lineEnd: 14,
  contentLineEnd: 13,
  top: 100,
  height: 50,
};

describe("scrollTopForLine", () => {
  it("maps the block's first line to its top", () => {
    expect(scrollTopForLine(preview, 10)).toBe(100);
  });

  it("interpolates a fractional line across the block height (span via lineEnd+1 fallback)", () => {
    // fraction = (12 - 10) / 5 = 0.4 → 100 + 0.4*50
    expect(scrollTopForLine(preview, 12)).toBe(120);
  });

  it("clamps a line at/after the content span to the block bottom", () => {
    expect(scrollTopForLine(preview, 15)).toBe(150); // fraction clamped to 1
    expect(scrollTopForLine(preview, 99)).toBe(150);
  });

  it("clamps a line before the block to its top", () => {
    expect(scrollTopForLine(preview, 9)).toBe(100); // fraction clamped to 0
  });

  it("uses contentLineEnd as the span when present (formatted pane)", () => {
    // span = 13 - 10 = 3; line 13 → fraction 1 → bottom
    expect(scrollTopForLine(formatted, 13)).toBe(150);
    // line 11.5 → fraction = 1.5/3 = 0.5 → 100 + 25
    expect(scrollTopForLine(formatted, 11.5)).toBe(125);
  });
});

describe("lineAtScrollTop", () => {
  it("maps the block top to its first line", () => {
    expect(lineAtScrollTop(preview, 100)).toBe(10);
  });

  it("interpolates the line from the scroll into the block", () => {
    // fraction = (120-100)/50 = 0.4 → 10 + floor(0.4*5) = 12
    expect(lineAtScrollTop(preview, 120)).toBe(12);
  });

  it("is the inverse of scrollTopForLine at whole-line boundaries", () => {
    expect(lineAtScrollTop(preview, scrollTopForLine(preview, 12))).toBe(12);
  });

  it("is NOT clamped to lineEnd — the caller (formatted) clamps, the preview does not", () => {
    // scroll past the block bottom: fraction 1 → 10 + floor(5) = 15, beyond lineEnd 14
    expect(lineAtScrollTop(preview, 150)).toBe(15);
  });

  it("returns the first line when the block has no height (avoids a divide-by-zero)", () => {
    expect(lineAtScrollTop({ ...preview, height: 0 }, 200)).toBe(10);
  });

  it("uses contentLineEnd as the span when present (formatted pane)", () => {
    // span = 3; scrollTop 150 → fraction 1 → 10 + floor(3) = 13
    expect(lineAtScrollTop(formatted, 150)).toBe(13);
  });
});
