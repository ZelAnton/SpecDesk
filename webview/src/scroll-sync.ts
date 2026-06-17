/**
 * Bidirectional scroll-sync between the editor and the preview.
 *
 * Two problems to solve:
 *
 * 1. **Echo feedback.** Driving one pane to follow the other makes the driven pane fire its own
 *    scroll event, which would drive the original back. We suppress that with a **direction lock**:
 *    the actively-scrolled pane stays authoritative for a short rolling window (refreshed on each of
 *    its events), and every event from the other (driven) pane in that window is ignored as an echo.
 *
 * 2. **Smoothness.** The follow must track the driver pixel-for-pixel. We map one pane's
 *    `scrollTop` straight to the other's by interpolating between the **aligned anchors** published
 *    by height-sync (each block's top in both panes, after the editor spacers are applied). This is
 *    a pure piecewise-linear pixel→pixel function over cached numbers — no `posAtCoords`, no
 *    per-frame layout reads, and an integer result — so the driven pane moves as smoothly as the
 *    driver. Until the first anchors arrive (or with no blocks) it falls back to the line-based map.
 *
 * See docs/design/05-live-preview.md.
 */

import type { MarkdownEditor } from "./editor.js";
import type { ScrollAnchor } from "./height-sync.js";
import type { Preview } from "./preview.js";

/**
 * How long, in ms, a pane stays the authoritative scroller after its last scroll event. It must
 * comfortably outlast the gap between consecutive scroll frames (~16 ms) and the settle delay, so
 * the driven pane's echoes always fall inside the window; kept short enough that handing control to
 * the other pane after a gesture feels immediate.
 */
const LOCK_MS = 200;

/**
 * Map a scroll position from one pane to the other by linear interpolation between aligned anchors.
 * `from` selects the source coordinate (`editor` reads `editorTop` and returns `previewTop`, and
 * vice versa). Positions outside the anchored range extrapolate 1:1. Returns `null` when there are
 * no anchors, so the caller can fall back. Pure — unit-tested directly.
 */
export function mapByAnchors(
  anchors: ScrollAnchor[],
  value: number,
  from: "editor" | "preview",
): number | null {
  if (anchors.length === 0) {
    return null;
  }
  const inKey = from === "editor" ? "editorTop" : "previewTop";
  const outKey = from === "editor" ? "previewTop" : "editorTop";

  const firstAnchor = anchors[0];
  if (firstAnchor === undefined || value <= firstAnchor[inKey]) {
    // Above the first anchor (the leading region) — extrapolate 1:1.
    return firstAnchor === undefined ? null : firstAnchor[outKey] + (value - firstAnchor[inKey]);
  }

  // Largest index whose input top is <= value (binary search; anchors are sorted ascending).
  let lo = 0;
  let hi = anchors.length - 1;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    const midAnchor = anchors[mid];
    if (midAnchor !== undefined && midAnchor[inKey] <= value) {
      lo = mid;
    } else {
      hi = mid - 1;
    }
  }

  const low = anchors[lo];
  const high = anchors[lo + 1];
  if (low === undefined) {
    return null;
  }
  if (high === undefined) {
    // Below the last anchor (the trailing region) — extrapolate 1:1.
    return low[outKey] + (value - low[inKey]);
  }
  const span = high[inKey] - low[inKey];
  const fraction = span > 0 ? (value - low[inKey]) / span : 0;
  return low[outKey] + fraction * (high[outKey] - low[outKey]);
}

export class ScrollSync {
  private readonly editor: MarkdownEditor;
  private readonly preview: Preview;
  // Wall-clock (monotonic) timestamps until which each pane is the authoritative scroller. An event
  // from one pane is an echo (ignored) while the other pane's window is still open.
  private editorActiveUntil = 0;
  private previewActiveUntil = 0;
  // Aligned per-block anchors from height-sync; empty until the first reconcile.
  private anchors: ScrollAnchor[] = [];

  constructor(editor: MarkdownEditor, preview: Preview) {
    this.editor = editor;
    this.preview = preview;
  }

  /** Receive the latest aligned anchors (called by height-sync after each reconcile). */
  setAnchors(anchors: ScrollAnchor[]): void {
    this.anchors = anchors;
  }

  /** The editor scrolled; drive the preview unless this is the preview's echo. */
  fromEditor(): void {
    const now = performance.now();
    if (now < this.previewActiveUntil) {
      return;
    }
    this.editorActiveUntil = now + LOCK_MS;
    this.drivePreviewFromEditor();
  }

  /** The preview scrolled; drive the editor unless this is the editor's echo. */
  fromPreview(): void {
    const now = performance.now();
    if (now < this.editorActiveUntil) {
      return;
    }
    this.previewActiveUntil = now + LOCK_MS;
    const mapped = mapByAnchors(this.anchors, this.preview.scrollTopValue(), "preview");
    if (mapped === null) {
      this.editor.scrollToSourceLine(this.preview.topVisibleSourceLine());
    } else {
      this.editor.setScrollTop(mapped);
    }
  }

  /**
   * Re-snap the preview to the editor once scrolling has stopped — a cheap correctness backstop that
   * also realigns after any reconcile that landed mid-gesture. With the pixel map the live follow is
   * already exact, so this is normally a no-op (no visible jump). The editor stays authoritative so
   * the preview's resulting echo is ignored rather than bouncing back.
   */
  snapPreviewToEditor(): void {
    this.editorActiveUntil = performance.now() + LOCK_MS;
    this.drivePreviewFromEditor();
  }

  /**
   * Suppress scroll handling on both panes briefly. Used around a height-sync reconcile, whose
   * spacer/margin changes shift scroll positions and would otherwise be read as a user scroll.
   */
  suppress(): void {
    const until = performance.now() + LOCK_MS;
    this.editorActiveUntil = until;
    this.previewActiveUntil = until;
  }

  private drivePreviewFromEditor(): void {
    const mapped = mapByAnchors(this.anchors, this.editor.scrollTopValue(), "editor");
    if (mapped === null) {
      this.preview.scrollToSourceLine(this.editor.topVisibleLineExact());
    } else {
      this.preview.setScrollTop(mapped);
    }
  }
}
