import { describe, expect, it } from "vitest";
import { BlockGeometryCache } from "../../src/editors/block-geometry.js";
import type { BlockBox } from "../../src/sync/scroll-geometry.js";

// The geometry cache is layout-free: it holds already-measured boxes and searches them, so it is
// unit-tested here with hand-built boxes (no GUI). The FormattedEditor jsdom suite covers the wiring
// (measure → cache → invalidate) against a real ProseMirror view.
function box(lineStart: number, top: number, height: number): BlockBox {
  return { lineStart, lineEnd: lineStart, contentLineEnd: lineStart + 1, top, height };
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
