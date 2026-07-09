/**
 * Height-synced scroll: pad the editor so each source block's top lines up with its rendered
 * counterpart. The rendered preview is the fixed reference — it is never padded, so toggling editor
 * wrap (or any editor-side change) never shifts the preview. Anchors come from the `lineMap`
 * (every leaf rendered element). See docs/ROADMAP.md ("Planned upgrade — height-synced scroll").
 *
 * The math (`computeGapAdjustments`, `computeScrollCompensation`) is pure and unit-tested; `HeightSync`
 * is the DOM orchestration.
 */

import type { MarkdownEditor } from "../editors/editor.js";
import type { BlockGeometry } from "../review/preview.js";

/** Anything that can report per-block rendered geometry to align the editor against (the formatted
 *  WYSIWYG view in Split; formerly the read-only preview). The source is never padded — the editor is. */
export interface GeometrySource {
  /** Per top-level block, its source-line range and measured geometry — the anchors this aligns the
   *  editor against. The formatted pane resolves these through the shared block-map (block-map.ts),
   *  which pairs each source block with its ProseMirror node 1:1; a markdown-it/ProseMirror split
   *  divergence yields NO blocks (rather than mispaired anchors), so {@link HeightSync.reconcile}'s
   *  zero-block path simply clears the spacers until the split re-agrees. */
  blockGeometry(): BlockGeometry[];
  contentWidth(): number;
  /** Whether this pane has an edit typed that hasn't been reported via its own debounce yet — mirrors
   *  {@link MarkdownEditor.hasPendingChange}. Checked by {@link HeightSync.reconcile} before applying
   *  anchors (T-084): a pane with a pending edit is mid-write, so its `blockGeometry()`/text is about to
   *  change underneath the reconcile that's reading it right now. */
  hasPendingChange(): boolean;
  /** This pane's current text — compared against the editor's `getText()` by {@link HeightSync.reconcile}
   *  (T-084) to catch a divergence `hasPendingChange()` alone can miss: the cross-pane mirror in index.ts
   *  is guarded by the DESTINATION's `hasPendingChange()`, not the source's, so a settled pane whose text
   *  hasn't been mirrored into (yet-pending) sibling can still disagree with it for a beat. */
  getText(): string;
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

/** One applied spacer set: the lead plus the per-block spacers, as tracked by {@link HeightSync}. */
interface SpacerSet {
  lead: number;
  spacers: EditorSpacer[];
}

/** Total height of a spacer set's pieces that sit strictly above `viewportLine` (a 0-based source
 *  line, matching {@link EditorSpacer.lineEnd}). A block spacer sits right after its `lineEnd`, so it
 *  counts once the viewport has scrolled past that line (`lineEnd < viewportLine`); the lead sits
 *  before line 0, so it counts for any viewport that has scrolled past the very top of the document
 *  (`viewportLine > 0`) — mirroring the position-based convention in `MarkdownEditor.spacerHeightAbove`
 *  (a leading spacer is never "above" the very first anchor). Pure. */
function spacerHeightAboveLine(set: SpacerSet, viewportLine: number): number {
  let total = viewportLine > 0 ? set.lead : 0;
  for (const spacer of set.spacers) {
    if (spacer.lineEnd < viewportLine) {
      total += spacer.height;
    }
  }
  return total;
}

/**
 * The `scrollTop` delta (px) to apply so the content currently at the viewport top does not visibly
 * shift when the spacer set changes from `previous` to `next` (T-066). Positive when the spacer weight
 * above the viewport grew (scroll further down by that much to keep the same content at the top);
 * negative when it shrank; zero — the common case once a Split layout has settled — when nothing above
 * the viewport changed, so most reconciles apply no compensation at all. Pure — mirrors
 * {@link computeGapAdjustments}; `viewportLine` comes from `MarkdownEditor.topVisibleLine()`.
 */
export function computeScrollCompensation(
  viewportLine: number,
  previous: SpacerSet,
  next: SpacerSet,
): number {
  return spacerHeightAboveLine(next, viewportLine) - spacerHeightAboveLine(previous, viewportLine);
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
    // Compensate the viewport BEFORE swapping the applied set, so the delta reflects exactly what is
    // about to change (T-066): the reference line and the "previous" weight above it must both be read
    // against the still-old spacer set, otherwise the delta would compare against a set that no longer
    // matches what's on screen. Without this, a spacer-weight change above the current viewport shifts
    // the content under a fixed scrollTop — the visible jump while typing in Split that motivated this.
    const viewportLine = this.editor.topVisibleLine();
    const delta = computeScrollCompensation(
      viewportLine,
      { lead: this.appliedLead, spacers: this.appliedSpacers },
      { lead, spacers },
    );
    this.appliedLead = lead;
    this.appliedSpacers = spacers;
    this.hasApplied = true;
    this.editor.setSpacers(spacers, lead);
    // Applied in the same synchronous pass as the spacer dispatch above — same frame, so the two land
    // as one atomic visual update instead of a spacer-jump-then-scroll-catchup flicker.
    if (delta !== 0) {
      this.editor.adjustScrollTop(delta);
    }
    return true;
  }

  /**
   * Measure both panes' natural geometry and apply editor spacers to match the reference (formatted)
   * pane. The reference is measured as-is (we never pad it); editor tops come from a spacer-free
   * prefix sum, so this is a stable fixed point with no tracked state. Idempotent by design: calling
   * it again on an unchanged geometry recomputes the same spacers and skips re-dispatching them (see
   * {@link apply}), so it can be driven every time CodeMirror re-measures without flickering.
   *
   * Gated on pane consistency (T-084): every caller (onContentResize, window resize,
   * onGeometryChange, onEditorChange/onFormattedChange in index.ts) drives this unconditionally, but
   * between an edit and its 120ms debounce-mirror settling the two panes' texts can disagree — the
   * `blockGeometry()` anchors below would then belong to a DIFFERENT document than the one
   * `naturalLineTops` is about to measure against, producing spacers on the wrong lines (duplicated
   * anchors, negative gaps). When either pane has a pending (not-yet-mirrored) edit, or their texts
   * simply disagree, this returns WITHOUT applying anything — the stale spacer set (if any) is left in
   * place rather than replaced with a wrong one. This is not a dead end: once the mirror lands (the
   * pending pane's own debounce fires `onEditorChange`/`onFormattedChange`, which mirrors the text and
   * then unconditionally calls `reconcileHeights()` again — see index.ts), reconcile runs again with
   * consistent panes and catches the geometry up.
   */
  reconcile(): void {
    if (this.editor.hasPendingChange() || this.source.hasPendingChange()) {
      this.onDebug?.("height-sync: deferred — a pane has a pending (unmirrored) edit");
      return;
    }
    if (this.editor.getText() !== this.source.getText()) {
      this.onDebug?.("height-sync: deferred — panes' texts disagree (mirror not settled yet)");
      return;
    }

    const geometry = this.source.blockGeometry();
    if (geometry.length === 0) {
      this.apply(0, []);
      this.onDebug?.("height-sync: 0 blocks");
      return;
    }

    const editorTops = this.editor.naturalLineTops(geometry.map((block) => block.lineStart));
    if (editorTops.some((top) => top === null)) {
      this.onDebug?.(
        "height-sync: deferred — stale anchor line(s) outside the editor's current document",
      );
      return;
    }
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
