import { describe, expect, it } from "vitest";
import { BlockGeometryCache } from "../../src/editors/block-geometry.js";
import type { BlockBox } from "../../src/sync/scroll-geometry.js";

// The geometry cache is layout-free: it holds already-measured boxes and searches them, so it is
// unit-tested here with hand-built boxes (no GUI). The FormattedEditor jsdom suite covers the wiring
// (measure → cache → invalidate) against a real ProseMirror view.
function box(lineStart: number, top: number, height: number): BlockBox {
  return {
    lineStart,
    contentLineStart: undefined,
    lineEnd: lineStart,
    contentLineEnd: lineStart + 1,
    top,
    height,
  };
}

// Tops ascending 0, 100, 150, 350; line starts ascending 0, 2, 4, 8.
const BOXES: readonly BlockBox[] = [
  box(0, 0, 100),
  box(2, 100, 50),
  box(4, 150, 200),
  box(8, 350, 80),
];

describe("BlockGeometryCache staleness", () => {
  it("starts stale, reports empty boxes, and resolves nothing", () => {
    const cache = new BlockGeometryCache();
    expect(cache.isStale).toBe(true);
    expect(cache.boxes).toEqual([]);
    // A stale cache guesses nothing rather than searching empty keys.
    expect(cache.blockAtScrollTop(120)).toBeNull();
    expect(cache.blockForLine(3)).toBeNull();
  });

  it("becomes fresh on set and stale again on invalidate", () => {
    const cache = new BlockGeometryCache();
    cache.set(BOXES);
    expect(cache.isStale).toBe(false);
    expect(cache.boxes).toEqual(BOXES);

    cache.invalidate();
    expect(cache.isStale).toBe(true);
    expect(cache.boxes).toEqual([]);
    expect(cache.blockAtScrollTop(120)).toBeNull();
    expect(cache.blockForLine(3)).toBeNull();
  });

  it("treats an empty (diverged) box set as resolving nothing, though not stale", () => {
    const cache = new BlockGeometryCache();
    cache.set([]); // a diverged split yields zero measured blocks
    expect(cache.isStale).toBe(false);
    expect(cache.blockAtScrollTop(0)).toBeNull();
    expect(cache.blockForLine(0)).toBeNull();
  });
});

describe("BlockGeometryCache.blockAtScrollTop (binary search over tops)", () => {
  const cache = new BlockGeometryCache();
  cache.set(BOXES);

  it("finds the last block whose top is at or above the viewport top", () => {
    expect(cache.blockAtScrollTop(0)?.lineStart).toBe(0);
    expect(cache.blockAtScrollTop(99)?.lineStart).toBe(0);
    expect(cache.blockAtScrollTop(100)?.lineStart).toBe(2); // exact top match resolves to that block
    expect(cache.blockAtScrollTop(149)?.lineStart).toBe(2);
    expect(cache.blockAtScrollTop(150)?.lineStart).toBe(4);
    expect(cache.blockAtScrollTop(349)?.lineStart).toBe(4);
    expect(cache.blockAtScrollTop(350)?.lineStart).toBe(8);
    expect(cache.blockAtScrollTop(10000)?.lineStart).toBe(8);
  });

  it("returns null when the viewport sits above every block's top (leading padding)", () => {
    const padded = new BlockGeometryCache();
    padded.set([box(0, 8, 100), box(2, 108, 100)]); // first block's top is 8, not 0
    expect(padded.blockAtScrollTop(0)).toBeNull();
    expect(padded.blockAtScrollTop(7)).toBeNull();
    expect(padded.blockAtScrollTop(8)?.lineStart).toBe(0);
  });
});

describe("BlockGeometryCache.blockForLine (binary search over line starts)", () => {
  const cache = new BlockGeometryCache();
  cache.set(BOXES);

  it("lands on the nearest block at or before the line", () => {
    expect(cache.blockForLine(0)?.lineStart).toBe(0);
    expect(cache.blockForLine(1)?.lineStart).toBe(0); // a blank inter-block line rides with block 0
    expect(cache.blockForLine(2)?.lineStart).toBe(2);
    expect(cache.blockForLine(5)?.lineStart).toBe(4);
    expect(cache.blockForLine(8)?.lineStart).toBe(8);
    expect(cache.blockForLine(999)?.lineStart).toBe(8); // a scroll target clamps to the last block
  });

  it("folds a line before the first block onto block 0 (a scroll target never clears)", () => {
    expect(cache.blockForLine(-1)?.lineStart).toBe(0);
  });
});

// T-101: the cache now holds LEAF boxes (one per rendered table row / list item / block), not one per
// top-level container. Boxes still arrive in document order with ascending tops and ascending line
// starts, so the two binary searches resolve them the same way — even where a nested item's box sits
// vertically INSIDE its parent's (the tops still ascend). These leaf boxes are hand-built (the cache is
// layout-free); the FormattedEditor jsdom suite covers measuring the real rows/items into them.
describe("BlockGeometryCache with leaf-granular boxes (T-101)", () => {
  // A tiling leaf box: it owns pixels [top, top+height) and clamps a straddling line to `lineEnd`.
  function leaf(lineStart: number, lineEnd: number, top: number, height: number): BlockBox {
    return {
      lineStart,
      contentLineStart: undefined,
      lineEnd,
      contentLineEnd: lineStart + 1,
      top,
      height,
    };
  }

  // A table's header (line 10) + two body rows (12, 13); the delimiter (line 11) has no box of its own.
  // Then a bullet list whose second item (line 16) nests two sub-items (17, 18) — the parent's box is
  // clipped to its first child's top so every box tiles, and the nested boxes sit inside the parent's
  // pixel span while their tops still ascend.
  const LEAVES: readonly BlockBox[] = [
    leaf(10, 11, 0, 20), // table header row
    leaf(12, 12, 20, 20), // body row 1
    leaf(13, 15, 40, 20), // body row 2 (rides the trailing blank up to the list)
    leaf(16, 16, 60, 15), // list item, clipped to its first nested child's top
    leaf(17, 17, 75, 15), // nested sub-item a (inside the parent item's pixels)
    leaf(18, 18, 90, 15), // nested sub-item b
  ];

  const cache = new BlockGeometryCache();
  cache.set(LEAVES);

  it("resolves the row/item straddling a scroll offset, nested boxes included", () => {
    expect(cache.blockAtScrollTop(0)?.lineStart).toBe(10); // header row
    expect(cache.blockAtScrollTop(25)?.lineStart).toBe(12); // body row 1
    expect(cache.blockAtScrollTop(65)?.lineStart).toBe(16); // parent item's own pixels
    expect(cache.blockAtScrollTop(80)?.lineStart).toBe(17); // scrolled into the nested sub-item
    expect(cache.blockAtScrollTop(90)?.lineStart).toBe(18);
  });

  it("maps a source line to its row/item, folding the delimiter onto the header row", () => {
    expect(cache.blockForLine(10)?.lineStart).toBe(10);
    expect(cache.blockForLine(11)?.lineStart).toBe(10); // the delimiter line rides with the header row
    expect(cache.blockForLine(12)?.lineStart).toBe(12);
    expect(cache.blockForLine(17)?.lineStart).toBe(17); // a nested sub-item is addressable on its own
    expect(cache.blockForLine(18)?.lineStart).toBe(18);
  });
});
