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
