/**
 * Height-synced scroll: pad the editor so each source block's top lines up with its rendered
 * counterpart. The rendered preview is the fixed reference — it is never padded, so toggling editor
 * wrap (or any editor-side change) never shifts the preview. Anchors come from the `lineMap`
 * (every leaf rendered element). See docs/ROADMAP.md ("Planned upgrade — height-synced scroll").
 *
 * The math (`computeGapAdjustments`) is pure and unit-tested; `HeightSync` is the DOM orchestration.
 */

import type { MarkdownEditor } from "../editors/editor.js";
import type { BlockGeometry } from "../review/preview.js";

/** Anything that can report per-block rendered geometry to align the editor against (the formatted
 *  WYSIWYG view in Split; formerly the read-only preview). The source is never padded — the editor is. */
export interface GeometrySource {
  blockGeometry(): BlockGeometry[];
  contentWidth(): number;
}

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
 *
 * Coordinate systems (T-061). Both `previewTop` and `editorTop` are measured from their own pane's
 * scroll origin (the reference via `blockGeometry`, the editor via `naturalLineTops`); the two panes
 * are side-by-side, so those origins coincide on screen. Inter-block spacers are differences
 * (`previewGap − editorGap`), so any constant origin offset cancels — only the LEAD crosses panes as
 * an absolute subtraction, so it is the one place a coordinate mismatch would leak in. The lead is the
 * distance from the source's first line to the first rendered block, i.e. it reproduces whatever
 * leading space sits above the first rendered block: the reference pane's structural `padding-top`
 * (its scroll origin to its content box) — a small, genuine offset needed so the first source line
 * sits level with the first rendered block. It does NOT reproduce the first block's own typographic
 * top margin (a heading's `1.6em`): that is reset to 0 in the shared rendered stylesheet
 * (styles.css §5, `.sd-doc > :first-child`), so the first rendered block hugs its pane's content top
 * just as the first source line hugs the editor's — bringing both panes to one leading frame and
 * shrinking the lead from the old ~65px hatched band to the pane's structural inset. The lead stays a
 * stable fixed point because `naturalLineTops[0]` is invariant to the lead we apply (see
 * `MarkdownEditor.spacerHeightAbove`), so a settled geometry recomputes the identical lead and
 * {@link HeightSync.apply} stops re-dispatching — no oscillation.
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

/** Value-equality of two spacer lists (order-sensitive: {@link computeGapAdjustments} emits them
 *  top-to-bottom, so a stable geometry yields an identically ordered list). */
function spacersEqual(a: EditorSpacer[], b: EditorSpacer[]): boolean {
  if (a.length !== b.length) {
    return false;
  }
  for (let i = 0; i < a.length; i++) {
    const x = a[i];
    const y = b[i];
    if (!x || !y || x.lineEnd !== y.lineEnd || x.height !== y.height) {
      return false;
    }
  }
  return true;
}

export class HeightSync {
  private readonly editor: MarkdownEditor;
  private readonly source: GeometrySource;
  private readonly onDebug: ((summary: string) => void) | undefined;
  // The last spacer set actually pushed to the editor. reconcile() skips re-dispatching an identical
  // set — that is what makes repeated reconciles converge instead of flickering (see apply()).
  private appliedLead = 0;
  private appliedSpacers: EditorSpacer[] = [];
  private hasApplied = false;

  constructor(editor: MarkdownEditor, source: GeometrySource, onDebug?: (summary: string) => void) {
    this.editor = editor;
    this.source = source;
    this.onDebug = onDebug;
  }

  /**
   * Push a spacer set to the editor, but ONLY when it differs from the last one applied — returns
   * whether it actually dispatched. This is the loop breaker for the "re-reconcile after CodeMirror
   * re-measures" path (T-062): applying spacers makes CodeMirror re-measure and fire another
   * (transaction-less) `geometryChanged`, which re-enters reconcile. Because `naturalLineTops` is
   * spacer-invariant, a *settled* geometry recomputes the identical set here, so we skip the dispatch
   * and the cascade stops at a fixed point; a geometry that genuinely changed (an estimated height
   * became a measured one) yields a different set, so it dispatches and re-measures until it settles.
   * Without this guard the old code re-dispatched unconditionally and looped apply→measure→apply
   * forever, which is why the update-listener used to swallow every transaction-less geometryChanged.
   */
  private apply(lead: number, spacers: EditorSpacer[]): boolean {
    if (
      this.hasApplied &&
      lead === this.appliedLead &&
      spacersEqual(spacers, this.appliedSpacers)
    ) {
      return false;
    }
    this.appliedLead = lead;
    this.appliedSpacers = spacers;
    this.hasApplied = true;
    this.editor.setSpacers(spacers, lead);
    return true;
  }

  /**
   * Measure both panes' natural geometry and apply editor spacers to match the reference (formatted)
   * pane. The reference is measured as-is (we never pad it); editor tops come from a spacer-free
   * prefix sum, so this is a stable fixed point with no tracked state. Idempotent by design: calling
   * it again on an unchanged geometry recomputes the same spacers and skips re-dispatching them (see
   * {@link apply}), so it can be driven every time CodeMirror re-measures without flickering.
   */
  reconcile(): void {
    const geometry = this.source.blockGeometry();
    if (geometry.length === 0) {
      this.apply(0, []);
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
    const dispatched = this.apply(editorLead, editorSpacers);

    const last = anchors[anchors.length - 1];
    const round = (value: number) => Math.round(value);
    this.onDebug?.(
      `hs: ${anchors.length} · eN=${round(last?.editorTop ?? 0)} pN=${round(last?.previewTop ?? 0)}` +
        ` · eW=${this.editor.contentWidth()} pW=${this.source.contentWidth()}` +
        ` · lead ${editorLead} sp ${editorSpacers.length}${dispatched ? "" : " (settled)"}`,
    );
  }

  /**
   * Drop all editor spacers. Height-sync only makes sense in split, where there are two panes to
   * align; in a single-pane mode (code / formatted) the spacers have nothing to line up with and
   * would just linger as meaningless gaps in the source. {@link reconcile} re-adds them on return.
   */
  clear(): void {
    this.apply(0, []);
  }
}
