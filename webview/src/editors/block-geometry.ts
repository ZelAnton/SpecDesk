/**
 * A scroll-invariant cache of the formatted pane's per-block rendered geometry, so the hot scroll path
 * (FormattedEditor.topVisibleSourceLine / scrollToSourceLine) finds the block at the viewport top by a
 * BINARY SEARCH over cached tops instead of `nodeDOM` + `getBoundingClientRect` on every block up to the
 * viewport every scroll frame — the layout thrashing T-072 removes.
 *
 * Why it can be cached across scroll frames. Each box's `top` is CONTENT-relative (px from the pane's
 * content top: `rect.top − containerTop + scrollTop`), so it is invariant to `scrollTop` — scrolling by
 * Δ lowers `rect.top` by Δ and raises `scrollTop` by Δ, leaving the sum unchanged. A cache built once
 * therefore stays valid until the LAYOUT actually changes (an edit, a resize, an image decode, or a
 * review-overlay marker); FormattedEditor {@link invalidate}s it on exactly those events, so a stale
 * cache never silently misreports geometry (which would quietly corrupt scroll-sync — the T-072 risk).
 *
 * Layout-free: the DOM measurement stays in FormattedEditor (it owns the ProseMirror view); this module
 * only holds the already-measured boxes and searches them, so it is unit-tested without a GUI. It pairs
 * 1:1 with the shared block-map (block-map.ts) — a diverged split yields zero measured blocks, so every
 * search returns null and the caller degrades to its first-line / no-op fallback rather than guessing.
 */

import type { BlockBox } from "../sync/scroll-geometry.js";
import { lastIndexAtOrBefore } from "./block-map.js";

export class BlockGeometryCache {
  // The measured boxes, in document order, or null when stale (never measured, or invalidated since).
  private cached: readonly BlockBox[] | null = null;
  // The boxes' content-relative tops and source-line starts, extracted once on {@link set} so each
  // search is a plain number scan (block-map's binary {@link lastIndexAtOrBefore}) with no per-call map.
  private tops: readonly number[] = [];
  private lineStarts: readonly number[] = [];

  /** Whether the cache needs re-measuring before its next read (never measured, or invalidated since). */
  get isStale(): boolean {
    return this.cached === null;
  }

  /** Mark the cache stale so the next read re-measures — called on every event that relays the pane's
   *  blocks out (edit / resize / image decode / review-overlay marker). Idempotent. */
  invalidate(): void {
    this.cached = null;
    this.tops = [];
    this.lineStarts = [];
  }

  /** Store a freshly measured set of boxes (document order), extracting the ascending search keys once. */
  set(boxes: readonly BlockBox[]): void {
    this.cached = boxes;
    this.tops = boxes.map((box) => box.top);
    this.lineStarts = boxes.map((box) => box.lineStart);
  }

  /** The measured boxes, or an empty list when stale/diverged. */
  get boxes(): readonly BlockBox[] {
    return this.cached ?? [];
  }

  /**
   * The block straddling `scrollTop`: the last cached box whose content-relative top is at or above the
   * viewport top (`top <= scrollTop`), found by binary search over the ascending tops. Null when the
   * cache is empty/diverged, or `scrollTop` sits above every block's top (the viewport is in the pane's
   * leading padding) — the caller then falls back to the first source line.
   */
  blockAtScrollTop(scrollTop: number): BlockBox | null {
    if (this.cached === null || this.cached.length === 0) {
      return null;
    }
    const index = lastIndexAtOrBefore(this.tops, scrollTop);
    return index < 0 ? null : (this.cached[index] ?? null);
  }

  /**
   * The nearest block at or before source `line` (block 0 when `line` precedes every block), found by
   * binary search over the ascending line starts — the scroll TARGET lookup mirroring
   * {@link BlockMap.entryForScroll}, but resolved against the cached geometry so no DOM is measured on
   * the scroll frame. Null only when the cache is empty/diverged (there is no block to scroll to).
   */
  blockForLine(line: number): BlockBox | null {
    if (this.cached === null || this.cached.length === 0) {
      return null;
    }
    const index = lastIndexAtOrBefore(this.lineStarts, line);
    return this.cached[index < 0 ? 0 : index] ?? null;
  }
}
