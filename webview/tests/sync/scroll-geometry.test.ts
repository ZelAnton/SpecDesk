import { describe, expect, it } from "vitest";
import {
  type BlockBox,
  lineAtScrollTop,
  scrollTopForLine,
} from "../../src/sync/scroll-geometry.js";

// A preview-style box (no contentLineEnd → span = lineEnd+1-lineStart = 5) and a formatted-style box
// (contentLineEnd present → span = contentLineEnd-lineStart = 3), both 50px tall starting at y=100.
const preview: BlockBox = {
  lineStart: 10,
  contentLineStart: undefined,
  lineEnd: 14,
  contentLineEnd: undefined,
  top: 100,
  height: 50,
};
const formatted: BlockBox = {
  lineStart: 10,
  contentLineStart: undefined,
  lineEnd: 14,
  contentLineEnd: 13,
  top: 100,
  height: 50,
};
// A first block carrying two leading blank lines / ref-defs: its source slice starts at line 0, but its
// rendered content starts at line 2 and ends (exclusive) at line 5 → span = 3, 60px tall from y=100.
const firstBlock: BlockBox = {
  lineStart: 0,
  contentLineStart: 2,
  lineEnd: 5,
  contentLineEnd: 5,
  top: 100,
  height: 60,
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

  it("returns the block top when the span is zero (avoids a divide-by-zero → NaN)", () => {
    // contentLineEnd === lineStart → span = 0; a zero-content-line block must not yield NaN. In
    // practice markdown-it never emits `token.map[1] === token.map[0]` for a top-level block token
    // (md-blocks.ts's contentEndByLine source): every block-level token — paragraph, heading, hr,
    // table, list, html_block, etc. — spans at least one source line by construction, so `map[1]` is
    // always `> map[0]`. This guard is therefore defensive (protects against a future markdown-it
    // change or a hand-built BlockBox in a test/caller), not a fix for an observed real input.
    const zeroSpan: BlockBox = {
      lineStart: 10,
      contentLineStart: undefined,
      lineEnd: 14,
      contentLineEnd: 10,
      top: 100,
      height: 50,
    };
    expect(scrollTopForLine(zeroSpan, 10)).toBe(100);
    expect(scrollTopForLine(zeroSpan, 12)).toBe(100);
  });

  it("spans from contentLineStart, so a first block's leading blanks/ref-defs map to its top (T-065)", () => {
    // The rendered content starts at line 2, so line 2 sits at the block top — and the leading source
    // lines 0/1 (blank lines, ref-defs: no render) clamp to the top too, not a slice of the height.
    expect(scrollTopForLine(firstBlock, 2)).toBe(100);
    expect(scrollTopForLine(firstBlock, 0)).toBe(100);
    expect(scrollTopForLine(firstBlock, 1)).toBe(100);
    // line 3.5 → fraction = (3.5-2)/3 = 0.5 → 100 + 30
    expect(scrollTopForLine(firstBlock, 3.5)).toBe(130);
    expect(scrollTopForLine(firstBlock, 5)).toBe(160); // fraction 1 → bottom
  });
});

describe("lineAtScrollTop", () => {
  it("maps the block top to its first line", () => {
    expect(lineAtScrollTop(preview, 100)).toBe(10);
  });

  it("interpolates a FRACTIONAL line from the scroll into the block (no Math.floor — T-065)", () => {
    // fraction = (115-100)/50 = 0.3 → 10 + 0.3*5 = 11.5 (a floor would have snapped this to 11)
    expect(lineAtScrollTop(preview, 115)).toBe(11.5);
    // fraction = (120-100)/50 = 0.4 → 10 + 0.4*5 = 12 (lands on a whole line here)
    expect(lineAtScrollTop(preview, 120)).toBe(12);
  });

  it("is the exact inverse of scrollTopForLine at fractional lines too", () => {
    expect(lineAtScrollTop(preview, scrollTopForLine(preview, 12))).toBe(12);
    expect(lineAtScrollTop(preview, scrollTopForLine(preview, 12.6))).toBeCloseTo(12.6, 10);
    expect(lineAtScrollTop(formatted, scrollTopForLine(formatted, 11.5))).toBeCloseTo(11.5, 10);
  });

  it("is NOT clamped to lineEnd — the caller (formatted) clamps, the preview does not", () => {
    // scroll past the block bottom: fraction 1 → 10 + 1*5 = 15, beyond lineEnd 14
    expect(lineAtScrollTop(preview, 150)).toBe(15);
  });

  it("returns the first line when the block has no height (avoids a divide-by-zero)", () => {
    expect(lineAtScrollTop({ ...preview, height: 0 }, 200)).toBe(10);
  });

  it("uses contentLineEnd as the span when present (formatted pane), fractional", () => {
    // span = 3; scrollTop 150 → fraction 1 → 10 + 3 = 13
    expect(lineAtScrollTop(formatted, 150)).toBe(13);
    // scrollTop 125 → fraction 0.5 → 10 + 0.5*3 = 11.5
    expect(lineAtScrollTop(formatted, 125)).toBe(11.5);
  });

  it("reports contentLineStart at the block top, so leading blanks aren't attributed pixels (T-065)", () => {
    // The block top is the rendered CONTENT top (line 2), not the block's source slice start (line 0).
    expect(lineAtScrollTop(firstBlock, 100)).toBe(2);
    // Halfway down → 2 + 0.5*3 = 3.5; the bottom → 2 + 3 = 5.
    expect(lineAtScrollTop(firstBlock, 130)).toBe(3.5);
    expect(lineAtScrollTop(firstBlock, 160)).toBe(5);
  });
});
