/**
 * The deterministic piecewise-linear map between a pane's source lines and its scroll pixels — the one
 * line↔px correspondence the Split coordinator (sync-coordinator.ts) drives both panes through. It is
 * built from the SAME semantic sync anchors height-sync already measures (a top per rendered leaf unit —
 * each table row, each list item, each heading/paragraph/quote/code block — plus a trailing anchor for
 * the last unit's bottom), so scroll-sync and height-sync read one geometry instead of three competing
 * mechanisms re-deriving it.
 *
 * Why a map, and why absolute anchors. Each anchor pins a source line to an ABSOLUTE pixel offset
 * (content-relative scroll top), so the map is a fixed function of the measured layout — not a running
 * sum of per-gap deltas. That is what lets it express a "negative" gap (a source block intrinsically
 * TALLER than its rendered counterpart) without drift: height-sync's spacers can only ADD height (they
 * cannot be negative), so `computeGapAdjustments` accumulates a documented downward drift where the
 * source outgrows the render; this map, reading the two panes' actual tops, simply interpolates the true
 * relationship at every line, and the coordinator couples by LINE — so that visual-alignment drift never
 * leaks into where the two viewports track each other.
 *
 * Pure and layout-free: it holds already-measured (line, px) anchors and interpolates, so it is
 * unit-tested without a GUI. An empty map (a diverged markdown-it/ProseMirror split yields zero blocks —
 * see block-map.ts) reports {@link isEmpty}; the coordinator then bows out of scrolling rather than
 * snapping a pane to 0.
 */

/** One breakpoint of the map: a 0-based source line paired with its content-relative scroll pixel offset
 *  in a pane. Anchors are built in document order, i.e. ascending line (and, for real geometry, ascending
 *  px); the interpolation clamps and degrades safely if that ever fails to hold. */
export interface ScrollAnchor {
  readonly line: number;
  readonly px: number;
}

/**
 * Piecewise-linear interpolation of `ys` as a function of `xs` at `x`, clamped to the endpoints. `xs`
 * must be ascending (the axis we search); a zero-or-negative-width segment (a duplicate or out-of-order
 * key — a zero-height block, say) degrades to the segment's far endpoint rather than dividing by zero.
 * Shared by both map directions: line→px searches on the line axis, px→line on the pixel axis.
 */
function interpolate(xs: readonly number[], ys: readonly number[], x: number): number {
  const n = xs.length;
  if (n === 0) {
    return 0;
  }
  const first = xs[0] ?? 0;
  const last = xs[n - 1] ?? 0;
  if (n === 1 || x <= first) {
    return ys[0] ?? 0;
  }
  if (x >= last) {
    return ys[n - 1] ?? 0;
  }
  // The last index whose key is at or before `x` (binary search); `x` is strictly inside the range here,
  // so this lands in [0, n-2] and the next key is strictly greater — the segment [i, i+1] contains `x`.
  let lo = 0;
  let hi = n - 1;
  while (lo < hi) {
    const mid = (lo + hi + 1) >>> 1;
    if ((xs[mid] ?? Number.POSITIVE_INFINITY) <= x) {
      lo = mid;
    } else {
      hi = mid - 1;
    }
  }
  const x0 = xs[lo] ?? 0;
  const x1 = xs[lo + 1] ?? 0;
  const y0 = ys[lo] ?? 0;
  const y1 = ys[lo + 1] ?? 0;
  const span = x1 - x0;
  if (span <= 0) {
    return y1;
  }
  return y0 + ((x - x0) / span) * (y1 - y0);
}

export class ScrollMap {
  // The anchors' two axes, extracted once so each lookup is a plain number scan (no per-call mapping).
  private readonly lines: readonly number[];
  private readonly pxs: readonly number[];

  /** Build the map from anchors in document order (ascending line). The two axes are stored as-is; the
   *  interpolation clamps out-of-range queries and tolerates a degenerate segment, so a caller never has
   *  to pre-validate the anchors. */
  constructor(anchors: readonly ScrollAnchor[]) {
    this.lines = anchors.map((anchor) => anchor.line);
    this.pxs = anchors.map((anchor) => anchor.px);
  }

  /** No anchors — a diverged/empty document. The coordinator treats this as "cannot map" and leaves the
   *  panes' scroll untouched rather than snapping them to 0. */
  get isEmpty(): boolean {
    return this.lines.length === 0;
  }

  /** The content-relative scroll pixel that puts (possibly fractional) source `line` at the viewport top,
   *  the fractional part interpolated across the containing block. Clamped to the mapped pixel range. */
  pxForLine(line: number): number {
    return interpolate(this.lines, this.pxs, line);
  }

  /** The (possibly fractional) source line at content-relative scroll pixel `px` — the inverse of
   *  {@link pxForLine}, interpolated the same way and clamped to the mapped line range. */
  lineForPx(px: number): number {
    return interpolate(this.pxs, this.lines, px);
  }
}
