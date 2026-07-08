import { describe, expect, it } from "vitest";
import type { MarkdownEditor } from "../../src/editors/editor.js";
import type { BlockGeometry } from "../../src/review/preview.js";
import {
  type AnchorMetrics,
  computeGapAdjustments,
  type EditorSpacer,
  type GeometrySource,
  HeightSync,
} from "../../src/sync/height-sync.js";

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

  // T-061 first-block lead edge cases. The lead reproduces the small structural offset from the
  // rendered pane's scroll origin to its first block (its `padding-top`), NOT the first block's own
  // typographic top margin — that margin is reset to 0 in the shared rendered stylesheet, so the first
  // rendered block hugs its pane's content top and the lead stays a small structural inset.
  describe("first-block lead (T-061)", () => {
    it("reproduces only the pane's structural top inset, not a first-block margin", () => {
      // First rendered block sits at the pane's padding-top (20); the source's first line is at 0.
      const result = computeGapAdjustments([anchor(0, 0, 20), anchor(3, 40, 60)]);
      expect(result.editorLead).toBe(20);
    });

    it("is 0 when the first source line and first rendered block already coincide", () => {
      const result = computeGapAdjustments([anchor(0, 0, 0), anchor(3, 40, 40)]);
      expect(result.editorLead).toBe(0);
    });

    it("never emits a negative lead when the first source line starts below the rendered block", () => {
      const result = computeGapAdjustments([anchor(0, 30, 5), anchor(3, 50, 25)]);
      expect(result.editorLead).toBe(0);
    });

    it("leaves inter-block gaps unchanged when the whole rendered document shifts up uniformly", () => {
      // Resetting the first block's top margin lifts every rendered top by the same amount (here 44):
      // only the lead changes; the per-gap spacers, being differences, are identical.
      const withMargin = computeGapAdjustments([
        anchor(0, 0, 64),
        anchor(2, 20, 120),
        anchor(5, 40, 150),
      ]);
      const flush = computeGapAdjustments([
        anchor(0, 0, 20),
        anchor(2, 20, 76),
        anchor(5, 40, 106),
      ]);
      expect(withMargin.editorLead).toBe(64);
      expect(flush.editorLead).toBe(20);
      expect(flush.editorSpacers).toEqual(withMargin.editorSpacers);
    });
  });
});

// A scriptable stand-in for the two collaborators HeightSync drives, so reconcile()'s dispatch
// behaviour can be tested without a live CodeMirror / DOM. `naturalLineTops` is keyed by the source
// line so a test can flip a block's editor top from an estimate to its measured value between
// reconciles (what CodeMirror does when it finishes measuring a below-viewport block).
class FakeEditor {
  readonly calls: { spacers: EditorSpacer[]; lead: number }[] = [];
  private tops = new Map<number, number>();

  setTops(pairs: Array<[line: number, top: number]>): void {
    this.tops = new Map(pairs);
  }

  naturalLineTops(lines: number[]): number[] {
    return lines.map((lineStart) => this.tops.get(lineStart) ?? 0);
  }

  setSpacers(spacers: EditorSpacer[], leadingHeight = 0): void {
    this.calls.push({ spacers, lead: leadingHeight });
  }

  contentWidth(): number {
    return 800;
  }
}

describe("HeightSync.reconcile (T-062: self-heal after re-measure, no flicker loop)", () => {
  function make(geometry: BlockGeometry[]): { editor: FakeEditor; sync: HeightSync } {
    const editor = new FakeEditor();
    const source: GeometrySource = { blockGeometry: () => geometry, contentWidth: () => 800 };
    const sync = new HeightSync(editor as unknown as MarkdownEditor, source);
    return { editor, sync };
  }

  // The description's scenario: a `### A code block` after a table, below the viewport. The formatted
  // pane places it 200px down; the editor first only ESTIMATED that source region, reading its top as
  // 40 (a wrapped table row counted as one line), so the gap looks like 40 and the spacer inflates to
  // 160. When CodeMirror later measures the real height the top corrects to 90 and the spacer must
  // shrink to 110 on its own — the whole point of the fix (no edit, no resize triggered it).
  const geometry: BlockGeometry[] = [
    { lineStart: 0, lineEnd: 5, top: 0, height: 200 },
    { lineStart: 7, lineEnd: 7, top: 200, height: 40 },
  ];

  it("re-pads with the corrected (smaller) spacer once an estimated editor top is measured", () => {
    const { editor, sync } = make(geometry);

    editor.setTops([
      [0, 0],
      [7, 40],
    ]);
    sync.reconcile();
    expect(editor.calls).toHaveLength(1);
    expect(editor.calls[0]?.spacers).toEqual([{ lineEnd: 5, height: 160 }]);

    // CodeMirror finished measuring → the below-viewport top corrects. The re-reconcile (now driven by
    // the transaction-less geometryChanged the update-listener no longer swallows) shrinks the spacer.
    editor.setTops([
      [0, 0],
      [7, 90],
    ]);
    sync.reconcile();
    expect(editor.calls).toHaveLength(2);
    expect(editor.calls[1]?.spacers).toEqual([{ lineEnd: 5, height: 110 }]);
  });

  it("does not re-dispatch while the geometry stays put — a fixed point, so no apply→measure→apply flicker", () => {
    const { editor, sync } = make(geometry);
    editor.setTops([
      [0, 0],
      [7, 90],
    ]);

    sync.reconcile();
    sync.reconcile();
    sync.reconcile();

    // Only the first reconcile touches the editor; the settled ones recompute the identical spacer set
    // and skip the dispatch, so CodeMirror is never nudged into the loop the old blanket guard prevented.
    expect(editor.calls).toHaveLength(1);
    expect(editor.calls[0]?.spacers).toEqual([{ lineEnd: 5, height: 110 }]);
  });

  // T-061: a lead (first rendered block below the first source line) must settle to a fixed point and
  // NOT oscillate. `naturalLineTops` is invariant to the lead we apply (CodeMirror folds the leading
  // block widget into line 0's block, so `lineBlockAt(0).top` doesn't move — see editor.ts
  // spacerHeightAbove), which the FakeEditor models by returning line-keyed tops that don't change when
  // a lead is applied. So repeated reconciles recompute the identical lead and stop re-dispatching.
  it("applies a lead once and settles — no lead flicker between reconciles", () => {
    const leadGeometry: BlockGeometry[] = [
      { lineStart: 0, lineEnd: 0, top: 20, height: 40 }, // first rendered block 20px down (pane inset)
      { lineStart: 2, lineEnd: 2, top: 60, height: 40 },
    ];
    const editor = new FakeEditor();
    const source: GeometrySource = { blockGeometry: () => leadGeometry, contentWidth: () => 800 };
    const sync = new HeightSync(editor as unknown as MarkdownEditor, source);
    editor.setTops([
      [0, 0],
      [2, 40],
    ]);

    sync.reconcile();
    sync.reconcile();
    sync.reconcile();

    // The lead is dispatched exactly once; the settled reconciles recompute the same lead and skip it.
    expect(editor.calls).toHaveLength(1);
    expect(editor.calls[0]?.lead).toBe(20);
  });

  it("clear() drops the spacers and a later reconcile re-applies them (leaving/returning to Split)", () => {
    const { editor, sync } = make(geometry);
    editor.setTops([
      [0, 0],
      [7, 90],
    ]);

    sync.reconcile();
    expect(editor.calls).toHaveLength(1);

    sync.clear();
    expect(editor.calls).toHaveLength(2);
    expect(editor.calls[1]).toEqual({ spacers: [], lead: 0 });

    sync.reconcile();
    expect(editor.calls).toHaveLength(3);
    expect(editor.calls[2]?.spacers).toEqual([{ lineEnd: 5, height: 110 }]);
  });
});
