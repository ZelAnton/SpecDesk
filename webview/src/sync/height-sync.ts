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
import type { GeometrySnapshot, ScrollAnchor } from "./scroll-map.js";

/** Anything that can report per-block rendered geometry to align the editor against (the formatted
 *  WYSIWYG view in Split; formerly the read-only preview). The source is never padded — the editor is. */
export interface GeometrySource {
  /** Per rendered LEAF unit (a heading/paragraph/blockquote/code/thematic-break, each table row, each
   *  list item — nested included), its source-line range and measured geometry — the ordered anchors
   *  this aligns the editor against, so individual rows/items line up, not just whole containers. The
   *  formatted pane resolves these through the shared semantic-anchor projection (sync-anchors.ts) built
   *  on the block-map (block-map.ts); a markdown-it/ProseMirror split divergence yields NO blocks (rather
   *  than mispaired anchors), so {@link HeightSync.reconcile}'s zero-block path simply clears the spacers
   *  until the split re-agrees. */
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

/** A spacer to insert at a block's placement slot, of the given pixel height. `lineEnd` is the semantic
 *  placement slot from the T-101 anchor projection (the source line just before the NEXT anchor), NOT a
 *  container node's own last line — so a spacer between two rows of one table / two items of one list
 *  lands between them rather than after the whole container. */
export interface EditorSpacer {
  lineEnd: number;
  height: number;
}

/** One anchor's measured top in each pane (editor top is spacer-free; preview top is natural). `lineEnd`
 *  is this anchor's placement slot (see {@link EditorSpacer}) — where its spacer is planted. */
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
 * Pad only the editor so each anchor lines up with the (fixed) preview, using the MINIMAL cumulative
 * padding. For anchor `i` the shift needed to bring its natural (spacer-free) editor top down to its
 * preview top is `required[i] = previewTop[i] − editorTop[i]`. Spacers can only ADD height (never
 * remove it) and stack cumulatively down the document, so the padding actually applied above anchor `i`
 * is the RUNNING MAXIMUM of `max(0, required[j])` over `j ≤ i` — the smallest non-decreasing,
 * non-negative sequence that meets each anchor's need where it is reachable and holds the accumulated
 * maximum where it is not (Code already sits at or below its target and height cannot be removed). Each
 * emitted spacer is just the INCREMENT of that running maximum across one placement slot. So where Code
 * ran ahead and Formatted only later catches back up to the already-accumulated maximum, NO new spacer
 * is added and the anchors realign — unlike the former per-gap scheme, which re-added every local
 * positive gap difference and locked that transient lead in as permanent over-padding (the bug). Where
 * alignment is genuinely unreachable (the source is intrinsically taller) the result stays monotonic
 * and never negative; the residual is bounded by the running maximum, not the sum of every local jump.
 * Pure.
 *
 * Subpixel stability. `required` and the running maximum are carried in fractional CSS px; only the
 * emitted CUMULATIVE boundaries are rounded — each spacer is `round(applied[i+1]) − round(applied[i])`,
 * the lead is `round(applied[0])` — so the total padding above any anchor `k` is exactly
 * `round(applied[k])` and per-gap rounding error can never accumulate into drift. That single rounding
 * step is the one epsilon (a half-pixel dead zone on each cumulative boundary), so a subpixel reflow
 * that does not cross a boundary recomputes the identical spacer set and {@link HeightSync.apply} stops
 * re-dispatching — no decoration flicker back and forth.
 *
 * Coordinate systems (T-061). `previewTop` and `editorTop` are each measured from their own pane's
 * scroll origin (the reference via `blockGeometry`, the editor via `naturalLineTops`); the two panes are
 * side-by-side, so those origins coincide on screen — which is what makes the per-anchor absolute
 * subtraction `previewTop − editorTop` a valid on-screen shift. The lead is
 * `applied[0] = max(0, previewTop[0] − editorTop[0])`, the distance from the source's first line to the
 * first rendered block: it reproduces whatever leading space sits above the first rendered block — the
 * reference pane's structural `padding-top` (its scroll origin to its content box), a small, genuine
 * offset needed so the first source line sits level with the first rendered block. It does NOT reproduce
 * the first block's own typographic top margin (a heading's `1.6em`): that is reset to 0 in the shared
 * rendered stylesheet (styles.css §5, `.sd-doc > :first-child`), so the first rendered block hugs its
 * pane's content top just as the first source line hugs the editor's — bringing both panes to one
 * leading frame and shrinking the lead to the pane's structural inset. A uniform shift of the whole
 * rendered document (resetting that first margin lifts every rendered top by the same amount) moves only
 * the lead; the inter-anchor spacers, being increments of the running maximum, are unchanged. The lead
 * stays a stable fixed point because `naturalLineTops[0]` is invariant to the lead we apply (see
 * `MarkdownEditor.spacerHeightsAbove`), so a settled geometry recomputes the identical lead and
 * {@link HeightSync.apply} stops re-dispatching — no oscillation.
 */
export function computeGapAdjustments(anchors: AnchorMetrics[]): GapAdjustments {
  const first = anchors[0];
  if (!first) {
    return { editorLead: 0, editorSpacers: [] };
  }

  // Running maximum of max(0, required) over the anchors seen so far, in fractional px. Seeded with the
  // first anchor's own need so the lead IS applied[0]; never drops below 0, so no anchor's required
  // (which may be negative where Code already sits below its target) can pull it down.
  let runningMax = Math.max(0, first.previewTop - first.editorTop);
  const editorLead = Math.round(runningMax);
  // Rounded cumulative padding applied above the current placement slot. Each spacer is the difference
  // of two rounded cumulative values, so the total above anchor k is exactly round(applied[k]) — the
  // rounding is normalized once per boundary and cannot accumulate across gaps.
  let appliedBelow = editorLead;

  const editorSpacers: EditorSpacer[] = [];
  for (let i = 0; i < anchors.length - 1; i++) {
    const current = anchors[i];
    const next = anchors[i + 1];
    if (!current || !next) {
      continue;
    }
    runningMax = Math.max(runningMax, next.previewTop - next.editorTop);
    const appliedAtNext = Math.round(runningMax);
    const height = appliedAtNext - appliedBelow;
    if (height > 0) {
      editorSpacers.push({ lineEnd: current.lineEnd, height });
    }
    appliedBelow = appliedAtNext;
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

/**
 * Build the immutable {@link GeometrySnapshot} the Split coordinator rebuilds both scroll maps from, PURELY
 * from the one read phase's inputs — the reference blocks, the editor's spacer-free natural line tops, and
 * the spacer set this pass applies. The formatted anchors are each block's natural top plus a trailing
 * anchor at the last block's bottom (matching {@link FormattedEditor.blockAnchors}); the editor anchors are
 * each block's PADDED top — its natural top plus {@link spacerHeightAboveLine} of the applied set at that
 * line, which equals what `MarkdownEditor.topsForLines` would re-read after the write (a block spacer at
 * `lineEnd < line` and the lead for any `line > 0`, side for side — see {@link spacerHeightAboveLine} and
 * `MarkdownEditor.spacerHeightsAbove`). The trailing editor anchor extends the last block by the same
 * pixel span the formatted trailing anchor uses (the block's rendered height the editor is padded to
 * match), keeping both maps' final segment monotonic without re-reading the padded layout. Pure.
 */
function snapshotFromGeometry(
  geometry: BlockGeometry[],
  naturalTops: number[],
  applied: SpacerSet,
  changed: boolean,
): GeometrySnapshot {
  const last = geometry[geometry.length - 1];
  if (!last) {
    return { formatted: [], editor: [], changed };
  }
  const formatted: ScrollAnchor[] = geometry.map((block) => ({
    line: block.lineStart,
    px: block.top,
  }));
  const editor: ScrollAnchor[] = geometry.map((block, index) => ({
    line: block.lineStart,
    px: (naturalTops[index] ?? 0) + spacerHeightAboveLine(applied, block.lineStart),
  }));
  const trailingLine = last.lineEnd + 1;
  formatted.push({ line: trailingLine, px: last.top + last.height });
  const editorLastPx = editor[editor.length - 1]?.px ?? 0;
  editor.push({ line: trailingLine, px: editorLastPx + last.height });
  return { formatted, editor, changed };
}

export class HeightSync {
  private readonly editor: MarkdownEditor;
  private readonly source: GeometrySource;
  private readonly onDebug: ((summary: () => string, perFrame?: boolean) => void) | undefined;
  // The last spacer set actually pushed to the editor. reconcile() skips re-dispatching an identical
  // set — that is what makes repeated reconciles converge instead of flickering (see apply()).
  private appliedLead = 0;
  private appliedSpacers: EditorSpacer[] = [];
  private hasApplied = false;

  constructor(
    editor: MarkdownEditor,
    source: GeometrySource,
    onDebug?: (summary: () => string, perFrame?: boolean) => void,
  ) {
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
   *
   * The one read phase of a Split reconcile generation. It measures the reference pane's block geometry
   * ONCE and reads the editor's spacer-free line tops ONCE, then returns the {@link GeometrySnapshot} the
   * coordinator rebuilds BOTH scroll maps from — so no second DOM / CodeMirror measure runs after the
   * spacer write. The editor map's anchors are the post-write PADDED tops computed arithmetically here
   * (natural top + the applied spacer weight above each line), which is exactly what a re-read would return
   * because spacer widgets report their exact height; deriving them avoids the forced layout a read-back
   * after the write would trigger (T-104). Returns `null` when the pass is gated (a pending / mismatched /
   * stale-anchor pane, the T-084 conditions) — the coordinator then keeps its current maps untouched rather
   * than couple against a snapshot that never formed.
   */
  reconcile(): GeometrySnapshot | null {
    if (this.editor.hasPendingChange() || this.source.hasPendingChange()) {
      this.onDebug?.(() => "height-sync: deferred — a pane has a pending (unmirrored) edit");
      return null;
    }
    if (this.editor.getText() !== this.source.getText()) {
      this.onDebug?.(
        () => "height-sync: deferred — panes' texts disagree (mirror not settled yet)",
      );
      return null;
    }

    const geometry = this.source.blockGeometry();
    if (geometry.length === 0) {
      const changed = this.apply(0, []);
      this.onDebug?.(() => "height-sync: 0 blocks");
      // A diverged split has no anchors — the coordinator adopts the empty maps and bows out of coupling.
      return { formatted: [], editor: [], changed };
    }

    const editorTops = this.editor.naturalLineTops(geometry.map((block) => block.lineStart));
    if (editorTops.some((top) => top === null)) {
      this.onDebug?.(
        () => "height-sync: deferred — stale anchor line(s) outside the editor's current document",
      );
      return null;
    }
    const anchors: AnchorMetrics[] = geometry.map((block, index) => ({
      lineEnd: block.lineEnd,
      editorTop: editorTops[index] ?? 0,
      previewTop: block.top,
    }));

    const { editorLead, editorSpacers } = computeGapAdjustments(anchors);
    const changed = this.apply(editorLead, editorSpacers);

    // A per-reconcile summary (built lazily via the thunk, and marked perFrame so a sink can drop it
    // by default): building it interpolates and reads both panes' content widths — a layout touch we
    // must not pay every reconcile unless a diagnostic sink actually wants it.
    const last = anchors[anchors.length - 1];
    const round = (value: number) => Math.round(value);
    this.onDebug?.(
      () =>
        `hs: ${anchors.length} · eN=${round(last?.editorTop ?? 0)} pN=${round(last?.previewTop ?? 0)}` +
        ` · eW=${this.editor.contentWidth()} pW=${this.source.contentWidth()}` +
        ` · lead ${editorLead} sp ${editorSpacers.length}${changed ? "" : " (settled)"}`,
      true,
    );

    return snapshotFromGeometry(
      geometry,
      editorTops.map((top) => top ?? 0),
      { lead: editorLead, spacers: editorSpacers },
      changed,
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
