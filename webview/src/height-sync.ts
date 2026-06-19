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
 * One aligned anchor: a block's top in each pane's own scroll coordinate AFTER the editor spacers
 * are applied. `editorTop` is spacer-INCLUSIVE (the value of `scrollDOM.scrollTop` at which that
 * block reaches the viewport top); `previewTop` is the preview block's natural top. Scroll-sync
 * interpolates between consecutive anchors to map one pane's scroll position to the other's.
 */
export interface ScrollAnchor {
  editorTop: number;
  previewTop: number;
}

/**
 * The aligned (editorTop, previewTop) anchors for the scroll map, derived from the same per-gap
 * lead/pad as {@link computeGapAdjustments} so the anchors exactly match the applied spacers:
 * `editorTop` accumulates each gap plus its spacer; `previewTop` is the measured preview top. Pure.
 * Keep in lockstep with computeGapAdjustments — both encode the same padding scheme.
 */
export function computeScrollAnchors(anchors: AnchorMetrics[]): ScrollAnchor[] {
  const first = anchors[0];
  if (!first) {
    return [];
  }
  const lead = Math.max(0, Math.round(first.previewTop - first.editorTop));
  const result: ScrollAnchor[] = [
    { editorTop: first.editorTop + lead, previewTop: first.previewTop },
  ];
  let inclusive = first.editorTop + lead;
  for (let i = 1; i < anchors.length; i++) {
    const current = anchors[i];
    const previous = anchors[i - 1];
    if (!current || !previous) {
      continue;
    }
    const editorGap = current.editorTop - previous.editorTop;
    const previewGap = current.previewTop - previous.previewTop;
    const pad = Math.max(0, Math.round(previewGap - editorGap));
    inclusive += editorGap + pad;
    result.push({ editorTop: inclusive, previewTop: current.previewTop });
  }
  return result;
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
  private readonly onAnchors: ((anchors: ScrollAnchor[]) => void) | undefined;

  constructor(
    editor: MarkdownEditor,
    preview: Preview,
    onDebug?: (summary: string) => void,
    onAnchors?: (anchors: ScrollAnchor[]) => void,
  ) {
    this.editor = editor;
    this.preview = preview;
    this.onDebug = onDebug;
    this.onAnchors = onAnchors;
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
      this.onAnchors?.([]);
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
    // Publish the aligned anchors so scroll-sync can map pane→pane by interpolating between them.
    this.onAnchors?.(computeScrollAnchors(anchors));

    const last = anchors[anchors.length - 1];
    const round = (value: number) => Math.round(value);
    this.onDebug?.(
      `hs: ${anchors.length} · eN=${round(last?.editorTop ?? 0)} pN=${round(last?.previewTop ?? 0)}` +
        ` · eW=${this.editor.contentWidth()} pW=${this.preview.contentWidth()}` +
        ` · lead ${editorLead} sp ${editorSpacers.length}`,
    );
  }

  /**
   * Drop all editor spacers (and the aligned anchors). Height-sync only makes sense in split, where
   * there are two panes to align; in a single-pane mode (code / formatted) the spacers have nothing
   * to line up with and would just linger as meaningless gaps in the source. {@link reconcile}
   * re-adds them on return to split.
   */
  clear(): void {
    this.editor.setSpacers([], 0);
    this.onAnchors?.([]);
  }
}
