/**
 * Pure block↔scroll interpolation shared by the two scrollable panes (FormattedEditor and Preview).
 * Each maps a (possibly fractional) source line to a scroll offset within its rendered block and back,
 * interpolating across the block's measured height over its content-line span. The block's pixel
 * geometry (top, height) and which block is involved are found by the caller from the DOM — only this
 * arithmetic lives here, so it is unit-tested without a GUI. (CodeMirror's own line-block-granular
 * topVisibleLineExact stays in editor.ts; it is not this fractional interpolation.)
 *
 * Both directions are FRACTIONAL and symmetric (T-065): `scrollTopForLine` places a fractional line at a
 * pixel, and `lineAtScrollTop` reports the fractional line at a pixel (no `Math.floor`), so following the
 * WYSIWYG pane's smooth scroll no longer moves the source editor in whole-line steps.
 */

/** A rendered block's source-line span plus its measured pixel geometry, in the pane's scroll
 *  coordinate (px from the content top). */
export interface BlockBox {
  lineStart: number;
  /**
   * The source line where the block's rendered content actually BEGINS, when it differs from
   * {@link lineStart} (only ever the document's first block). Leading blank lines and link reference
   * definitions ride with the block in the source split ({@link MdBlock.contentLineStart}) but produce
   * NO render pixels, so the interpolation must span from HERE, not from `lineStart` — otherwise those
   * leading lines would be (wrongly) attributed a slice of the block's rendered height. `undefined` for
   * every other block, where content starts at `lineStart`.
   */
  contentLineStart: number | undefined;
  lineEnd: number;
  /** Exclusive content end (the formatted pane's markdown-it value); `undefined` for the preview,
   *  where it falls back to `lineEnd + 1` so the span covers the block's lines without the trailing
   *  blank that rides with it in the source split. */
  contentLineEnd: number | undefined;
  top: number;
  height: number;
  /** The container instances (table/list) this leaf is a row/item of, outermost first — the
   *  {@link LeafAnchor.containers} keys carried through the measurement so height-sync can recover each
   *  container's anchor run (its container-tail floor). Absent for panes that don't project per-row
   *  anchors (the preview) and for top-level leaves. */
  containers?: readonly string[];
}

/** The source line the block's rendered pixels START at: its content start (leading blank lines and
 *  ref-definitions excluded), falling back to {@link BlockBox.lineStart} when the block has no such
 *  leading gap (every block but, at most, the document's first). */
function contentStart(box: BlockBox): number {
  return box.contentLineStart ?? box.lineStart;
}

/** The block's rendered source-line span the height is interpolated across: content lines only, from
 *  {@link contentStart} to the exclusive content end (contentLineEnd, or lineEnd+1 for the preview). */
function span(box: BlockBox): number {
  return (box.contentLineEnd ?? box.lineEnd + 1) - contentStart(box);
}

/**
 * The scroll offset that aligns `line` (possibly fractional) at the viewport top, the fractional part
 * interpolated across the block's height so scrolling tracks smoothly within a tall block instead of
 * snapping to its top. The fraction is measured from the block's CONTENT start (see {@link contentStart}),
 * so a first block carrying leading blank lines / ref-definitions maps its rendered top to the content
 * line, not to the (earlier) source line those leading lines sit on.
 */
export function scrollTopForLine(box: BlockBox, line: number): number {
  const blockSpan = span(box);
  const fraction =
    blockSpan > 0 ? Math.min(Math.max((line - contentStart(box)) / blockSpan, 0), 1) : 0;
  return box.top + fraction * box.height;
}

/**
 * The (fractional) source line at `scrollTop` within the block: the fractional scroll into the block
 * interpolated back across its content-line span (the inverse of {@link scrollTopForLine}). Fractional by
 * design (T-065) — the integer part is the line, the fractional part how far the viewport has scrolled
 * into it — so a caller coupling the sibling pane by this line follows a smooth WYSIWYG scroll without the
 * whole-line stepping a `Math.floor` used to impose. Measured from the block's CONTENT start, so leading
 * blank lines / ref-definitions of a first block are not attributed a slice of its rendered height.
 * Deliberately NOT clamped to `lineEnd` — the formatted pane clamps at the call site (so a viewport sitting
 * in the trailing-blank gap still reports the blank line), while the preview does not.
 */
export function lineAtScrollTop(box: BlockBox, scrollTop: number): number {
  const fraction = box.height > 0 ? (scrollTop - box.top) / box.height : 0;
  return contentStart(box) + fraction * span(box);
}
