/**
 * Pure block↔scroll interpolation shared by the two scrollable panes (FormattedEditor and Preview).
 * Each maps a (possibly fractional) source line to a scroll offset within its rendered block and back,
 * interpolating across the block's measured height over its content-line span. The block's pixel
 * geometry (top, height) and which block is involved are found by the caller from the DOM — only this
 * arithmetic lives here, so it is unit-tested without a GUI. (CodeMirror's own line-block-granular
 * topVisibleLineExact stays in editor.ts; it is not this fractional interpolation.)
 */

/** A rendered block's source-line span plus its measured pixel geometry, in the pane's scroll
 *  coordinate (px from the content top). */
export interface BlockBox {
  lineStart: number;
  lineEnd: number;
  /** Exclusive content end (the formatted pane's markdown-it value); `undefined` for the preview,
   *  where it falls back to `lineEnd + 1` so the span covers the block's lines without the trailing
   *  blank that rides with it in the source split. */
  contentLineEnd: number | undefined;
  top: number;
  height: number;
}

/** The block's source-line span the height is interpolated across (content lines only). */
function span(box: BlockBox): number {
  return (box.contentLineEnd ?? box.lineEnd + 1) - box.lineStart;
}

/**
 * The scroll offset that aligns `line` (possibly fractional) at the viewport top, the fractional part
 * interpolated across the block's height so scrolling tracks smoothly within a tall block instead of
 * snapping to its top.
 */
export function scrollTopForLine(box: BlockBox, line: number): number {
  const blockSpan = span(box);
  const fraction = blockSpan > 0 ? Math.min(Math.max((line - box.lineStart) / blockSpan, 0), 1) : 0;
  return box.top + fraction * box.height;
}

/**
 * The source line at `scrollTop` within the block: the fractional scroll into the block interpolated
 * back across its content-line span (the inverse of {@link scrollTopForLine}). Deliberately NOT clamped
 * to `lineEnd` — the formatted pane clamps at the call site (so a viewport sitting in the trailing-blank
 * gap still reports the blank line), while the preview does not.
 */
export function lineAtScrollTop(box: BlockBox, scrollTop: number): number {
  const fraction = box.height > 0 ? (scrollTop - box.top) / box.height : 0;
  return box.lineStart + Math.floor(fraction * span(box));
}
