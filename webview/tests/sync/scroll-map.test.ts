import { describe, expect, it } from "vitest";
import { type ScrollAnchor, ScrollMap } from "../../src/sync/scroll-map.js";

// A three-block layout: block starts at lines 0/5/10, rendered at px 0/100/300, plus a trailing anchor
// pinning the last block's bottom (line 13 → px 360). So the last block interpolates internally too.
const anchors: ScrollAnchor[] = [
  { line: 0, px: 0 },
  { line: 5, px: 100 },
  { line: 10, px: 300 },
  { line: 13, px: 360 },
];

describe("ScrollMap.isEmpty", () => {
  it("is true only with no anchors (a diverged/empty document)", () => {
    expect(new ScrollMap([]).isEmpty).toBe(true);
    expect(new ScrollMap([{ line: 0, px: 0 }]).isEmpty).toBe(false);
  });

  it("maps an empty map to 0 in both directions rather than throwing", () => {
    const map = new ScrollMap([]);
    expect(map.pxForLine(4)).toBe(0);
    expect(map.lineForPx(200)).toBe(0);
  });
});

describe("ScrollMap.pxForLine", () => {
  it("returns the exact px at each anchor line", () => {
    const map = new ScrollMap(anchors);
    expect(map.pxForLine(0)).toBe(0);
    expect(map.pxForLine(5)).toBe(100);
    expect(map.pxForLine(10)).toBe(300);
    expect(map.pxForLine(13)).toBe(360);
  });

  it("interpolates a line within a block linearly across that block's pixel span", () => {
    const map = new ScrollMap(anchors);
    // Segment [0,5] → [0,100]: line 2.5 sits halfway → 50px.
    expect(map.pxForLine(2.5)).toBe(50);
    // Segment [5,10] → [100,300]: line 7.5 sits halfway → 200px.
    expect(map.pxForLine(7.5)).toBe(200);
    // Segment [10,13] → [300,360]: line 11 is one-third in → 320px.
    expect(map.pxForLine(11)).toBe(320);
  });

  it("clamps a line before the first / after the last anchor to the endpoint px", () => {
    const map = new ScrollMap(anchors);
    expect(map.pxForLine(-4)).toBe(0);
    expect(map.pxForLine(99)).toBe(360);
  });

  it("returns the single anchor's px for any line when there is only one anchor", () => {
    const map = new ScrollMap([{ line: 4, px: 120 }]);
    expect(map.pxForLine(0)).toBe(120);
    expect(map.pxForLine(4)).toBe(120);
    expect(map.pxForLine(40)).toBe(120);
  });
});

describe("ScrollMap.lineForPx", () => {
  it("returns the exact line at each anchor px", () => {
    const map = new ScrollMap(anchors);
    expect(map.lineForPx(0)).toBe(0);
    expect(map.lineForPx(100)).toBe(5);
    expect(map.lineForPx(300)).toBe(10);
    expect(map.lineForPx(360)).toBe(13);
  });

  it("interpolates a pixel within a block back to a fractional line", () => {
    const map = new ScrollMap(anchors);
    expect(map.lineForPx(50)).toBe(2.5); // halfway up segment [0,100] → [0,5]
    expect(map.lineForPx(200)).toBe(7.5); // halfway up segment [100,300] → [5,10]
  });

  it("clamps a pixel before / after the mapped range to the endpoint line", () => {
    const map = new ScrollMap(anchors);
    expect(map.lineForPx(-20)).toBe(0);
    expect(map.lineForPx(9999)).toBe(13);
  });

  it("is the inverse of pxForLine at interior points", () => {
    const map = new ScrollMap(anchors);
    for (const line of [1, 2.5, 5, 7.5, 9, 11.25]) {
      expect(map.lineForPx(map.pxForLine(line))).toBeCloseTo(line, 6);
    }
  });
});

describe("ScrollMap degenerate segments", () => {
  it("does not divide by zero on a zero-height block (duplicate px), resolving to the far line", () => {
    // Block 1 renders at zero height: px 100 repeats at lines 5 and 8. lineForPx(100) must resolve
    // without NaN; it lands on the far endpoint of the flat segment.
    const map = new ScrollMap([
      { line: 0, px: 0 },
      { line: 5, px: 100 },
      { line: 8, px: 100 },
      { line: 12, px: 220 },
    ]);
    expect(Number.isNaN(map.lineForPx(100))).toBe(false);
    expect(map.lineForPx(100)).toBe(8);
    // Pixels above the flat still resolve into the next block normally.
    expect(map.lineForPx(160)).toBe(10); // halfway up [100,220] → [8,12]
  });

  it("handles a zero-line-span block (duplicate line) without NaN", () => {
    const map = new ScrollMap([
      { line: 0, px: 0 },
      { line: 5, px: 100 },
      { line: 5, px: 140 },
      { line: 9, px: 260 },
    ]);
    expect(Number.isNaN(map.pxForLine(5))).toBe(false);
    expect(map.pxForLine(7)).toBe(200); // halfway up [5,9] → [140,260]
  });
});

describe("ScrollMap expresses a negative gap without drift (T-073 criterion)", () => {
  it("maps a source-taller-than-render span monotonically, unlike an additive-spacer scheme", () => {
    // The editor (source) is INTRINSICALLY taller than the render across block 0: its own top for line 5
    // is 180px while the render places the matching block at only 100px. A spacer scheme cannot subtract
    // the 80px difference (spacers are non-negative), so it drifts; the map just reads the true tops.
    const editorMap = new ScrollMap([
      { line: 0, px: 0 },
      { line: 5, px: 180 },
      { line: 10, px: 300 },
    ]);
    const renderMap = new ScrollMap([
      { line: 0, px: 0 },
      { line: 5, px: 100 },
      { line: 10, px: 300 },
    ]);
    // Coupling by LINE round-trips exactly at the shared anchors regardless of the per-block px mismatch.
    for (const line of [0, 5, 10]) {
      expect(renderMap.lineForPx(renderMap.pxForLine(line))).toBe(line);
      expect(editorMap.lineForPx(editorMap.pxForLine(line))).toBe(line);
    }
    // The editor pixel that corresponds to render-line 3 is found via the shared line, not a px delta:
    // renderPx 60 → line 3 → editorPx 108 (both interpolated on their own monotone map).
    const line = renderMap.lineForPx(60);
    expect(line).toBe(3);
    expect(editorMap.pxForLine(line)).toBe(108);
  });
});
