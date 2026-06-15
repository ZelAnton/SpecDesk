/**
 * Height-synced scroll: pad the editor so each source block's top lines up with its rendered
 * counterpart. The rendered preview is the fixed reference — it is never padded, so toggling editor
 * wrap (or any editor-side change) never shifts the preview. Anchors come from the `lineMap`
 * (every leaf rendered element). See docs/ROADMAP.md ("Planned upgrade — height-synced scroll").
 *
 * The math (`computeGapAdjustments`) is pure and unit-tested; `HeightSync` is the DOM orchestration.
 */

import type { MarkdownEditor } from "./editor.js";
import type { Preview } from "./preview.js";

/** A spacer to insert below a block's last source line, of the given pixel height. */
export interface EditorSpacer {
  lineEnd: number;
  height: number;
}

/** One anchor's measured top in each pane (editor top is spacer-free; preview top is natural). */
export interface AnchorMetrics {
  lineEnd: number;
  editorTop: number;
  previewTop: number;
}

export interface GapAdjustments {
  /** Space above the first source line so the first block reaches its preview top. */
  editorLead: number;
  /** Per-block spacers below each block's last line so the next block reaches its preview top. */
  editorSpacers: EditorSpacer[];
}

/**
 * Pad only the editor so each block lines up with the (fixed) preview. This is computed **per gap**,
 * not cumulatively: each block's spacer depends only on its own two anchor tops. That keeps a
 * block's spacer stable as long as that block's anchors are measured stably (e.g. the visible
 * region) — a cumulative scheme would let one off-screen line whose height CodeMirror re-estimates
 * cascade into every spacer below it (the flicker). The trade-off is that where the editor source
 * is intrinsically taller than the render we add nothing (no negative spacer), so a little vertical
 * drift can accumulate over long distances — acceptable, and the active-line highlight covers it.
 * Pure.
 */
export function computeGapAdjustments(anchors: AnchorMetrics[]): GapAdjustments {
  const editorSpacers: EditorSpacer[] = [];
  const first = anchors[0];
  const editorLead = first ? Math.max(0, Math.round(first.previewTop - first.editorTop)) : 0;

  for (let i = 0; i < anchors.length - 1; i++) {
    const current = anchors[i];
    const next = anchors[i + 1];
    if (!current || !next) {
      continue;
    }
    const editorGap = next.editorTop - current.editorTop;
    const previewGap = next.previewTop - current.previewTop;
    const pad = Math.round(previewGap - editorGap);
    if (pad > 0) {
      editorSpacers.push({ lineEnd: current.lineEnd, height: pad });
    }
  }

  return { editorLead, editorSpacers };
}

export class HeightSync {
  private readonly editor: MarkdownEditor;
  private readonly preview: Preview;
  private readonly onDebug: ((summary: string) => void) | undefined;

  constructor(editor: MarkdownEditor, preview: Preview, onDebug?: (summary: string) => void) {
    this.editor = editor;
    this.preview = preview;
    this.onDebug = onDebug;
  }

  /**
   * Measure both panes' natural geometry and apply editor spacers to match the preview. The preview
   * is measured as-is (we never pad it); editor tops come from a spacer-free prefix sum, so this is
   * a stable fixed point with no tracked state.
   */
  reconcile(): void {
    const geometry = this.preview.blockGeometry();
    if (geometry.length === 0) {
      this.editor.setSpacers([], 0);
      this.onDebug?.("height-sync: 0 blocks");
      return;
    }

    const editorTops = this.editor.naturalLineTops(geometry.map((block) => block.lineStart));
    const anchors: AnchorMetrics[] = geometry.map((block, index) => ({
      lineEnd: block.lineEnd,
      editorTop: editorTops[index] ?? 0,
      previewTop: block.top,
    }));

    const { editorLead, editorSpacers } = computeGapAdjustments(anchors);
    this.editor.setSpacers(editorSpacers, editorLead);

    const last = anchors[anchors.length - 1];
    const round = (value: number) => Math.round(value);
    this.onDebug?.(
      `hs: ${anchors.length} · eN=${round(last?.editorTop ?? 0)} pN=${round(last?.previewTop ?? 0)}` +
        ` · eW=${this.editor.contentWidth()} pW=${this.preview.contentWidth()}` +
        ` · lead ${editorLead} sp ${editorSpacers.length}`,
    );
  }
}
