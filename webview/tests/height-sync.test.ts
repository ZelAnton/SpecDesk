import { describe, expect, it } from "vitest";
import {
  type AnchorMetrics,
  computeGapAdjustments,
  computeScrollAnchors,
} from "../src/height-sync.js";

function anchor(lineEnd: number, editorTop: number, previewTop: number): AnchorMetrics {
  return { lineEnd, editorTop, previewTop };
}

describe("computeGapAdjustments", () => {
  it("pads the editor when the rendered block is taller", () => {
    const result = computeGapAdjustments([anchor(2, 0, 0), anchor(5, 20, 120)]);
    expect(result.editorLead).toBe(0);
    expect(result.editorSpacers).toEqual([{ lineEnd: 2, height: 100 }]);
  });

  it("does NOT move the preview when the source is taller (editor-only padding)", () => {
    const result = computeGapAdjustments([anchor(2, 0, 0), anchor(5, 120, 20)]);
    expect(result.editorLead).toBe(0);
    expect(result.editorSpacers).toEqual([]);
  });

  it("adds nothing when the tops already line up", () => {
    const result = computeGapAdjustments([anchor(2, 0, 0), anchor(5, 50, 50)]);
    expect(result).toEqual({ editorLead: 0, editorSpacers: [] });
  });

  it("pads the editor lead when the first preview block starts lower", () => {
    // editor first line at 4px, preview first block at 21px → 17px lead in the editor.
    const result = computeGapAdjustments([anchor(0, 4, 21), anchor(3, 24, 61)]);
    expect(result.editorLead).toBe(17);
    // remaining: cumulative target = previewTop(37 needed) clamped monotonic → spacer of 20 below block 0
    expect(result.editorSpacers).toEqual([{ lineEnd: 0, height: 20 }]);
  });

  it("never pads the preview lead when the first editor line starts lower", () => {
    const result = computeGapAdjustments([anchor(0, 30, 5), anchor(3, 50, 25)]);
    expect(result).toEqual({ editorLead: 0, editorSpacers: [] });
  });

  it("returns empty for no anchors", () => {
    expect(computeGapAdjustments([])).toEqual({ editorLead: 0, editorSpacers: [] });
  });

  it("keeps the cumulative pad monotonic across a taller-then-shorter run", () => {
    const result = computeGapAdjustments([
      anchor(1, 0, 0), // lead 0
      anchor(3, 10, 60), // needs +50 → spacer 50 below block 0
      anchor(6, 90, 80), // editor now taller (90 > 80) → no extra pad (preview fixed)
    ]);
    expect(result.editorLead).toBe(0);
    expect(result.editorSpacers).toEqual([{ lineEnd: 1, height: 50 }]);
  });
});

describe("computeScrollAnchors", () => {
  it("returns empty for no anchors", () => {
    expect(computeScrollAnchors([])).toEqual([]);
  });

  it("aligns editor (spacer-inclusive) and preview tops when the render is taller", () => {
    // 100px spacer below block 0 makes the editor span 120px to block 1, matching the preview.
    expect(computeScrollAnchors([anchor(2, 0, 0), anchor(5, 20, 120)])).toEqual([
      { editorTop: 0, previewTop: 0 },
      { editorTop: 120, previewTop: 120 },
    ]);
  });

  it("compresses the map where the source is taller (no negative spacer)", () => {
    // Editor spans 120px through a tall source block; the preview block is only 20px.
    expect(computeScrollAnchors([anchor(2, 0, 0), anchor(5, 120, 20)])).toEqual([
      { editorTop: 0, previewTop: 0 },
      { editorTop: 120, previewTop: 20 },
    ]);
  });

  it("accounts for the editor lead", () => {
    expect(computeScrollAnchors([anchor(0, 4, 21), anchor(3, 24, 61)])).toEqual([
      { editorTop: 21, previewTop: 21 },
      { editorTop: 61, previewTop: 61 },
    ]);
  });

  it("stays monotonic across a taller-then-shorter run (matches the gap adjustments)", () => {
    expect(computeScrollAnchors([anchor(1, 0, 0), anchor(3, 10, 60), anchor(6, 90, 80)])).toEqual([
      { editorTop: 0, previewTop: 0 },
      { editorTop: 60, previewTop: 60 },
      { editorTop: 140, previewTop: 80 },
    ]);
  });
});
