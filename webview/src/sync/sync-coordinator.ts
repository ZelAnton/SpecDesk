/**
 * The single owner of both Split panes' scroll position. It replaces the former trio of mutually-
 * suppressing mechanisms — the per-frame line-based scroll-sync with its former driver lock, the
 * caret-reveal, and the mode-switch restore — and their glue heuristics (suppress/drive/
 * syncedRecently, the double rAF in applyMode) with ONE coordinator that alone writes each pane's
 * scrollTop.
 *
 * Echo is excluded by construction. Every write the coordinator makes records the resulting scrollTop
 * ({@link lastWritten}); a scroll event whose scrollTop still equals that value is the pane settling where
 * WE put it — its own echo — not a user scroll, so it never drives the sibling back. That is a
 * deterministic value check, so there is no timing window to tune and no driver lock: the two panes cannot
 * ping-pong. A genuine user scroll moves scrollTop off the recorded value and drives the sibling once.
 *
 * The couple itself goes through the line↔px {@link ScrollMap} of each pane, built from the SAME semantic
 * sync anchors height-sync measures — one per rendered leaf unit, so a tall table couples row-by-row
 * (scroll-map.ts, sync-anchors.ts). Coupling by LINE — read the source pane's viewport-top
 * line, map it to the sibling's pixels — is why height-sync's non-negative-spacer drift never leaks into
 * where the viewports track each other (T-073): the map reads the two panes' actual tops, so a "negative"
 * gap is expressed, not accumulated.
 *
 * The one timing fallback that remains is the reveal-vs-couple guard: after a scroll has just coupled the
 * passive pane, a coincident caret reveal must stand down for a beat, or the two fight over the passive
 * scrollTop and it judders (holding an arrow key that also scrolls the active pane). This is the sole
 * explicit heuristic left, and it gates only the reveal — never the deterministic echo suppression. Its
 * default clock is `performance.now()`, which is monotonic and unaffected by system time adjustments.
 */

import { type ScrollAnchor, ScrollMap } from "./scroll-map.js";

/** Which Split pane. `editor` is the CodeMirror source; `formatted` is the WYSIWYG reference. */
export type Pane = "editor" | "formatted";

/** A scroll event is treated as this pane's own echo when its scrollTop is within this many pixels of the
 *  value the coordinator last wrote — enough slack for the browser rounding scrollTop to a device pixel. */
export const ECHO_EPSILON = 0.5;

/** After a scroll couples the passive pane, a caret reveal into it stands down for this long — the one
 *  explicit fallback that keeps a reveal from fighting an active-scroll couple over the passive scrollTop
 *  (matches the former ScrollSync window). */
export const REVEAL_GUARD_MS = 120;

/** The source editor as the coordinator drives it. Reads are CodeMirror-native (per-line precise); the
 *  scroll write is a direct, SYNCHRONOUS scrollTop set so the resulting value can be read straight back
 *  for echo detection (the async `scrollIntoView` the old path used could not be). */
export interface EditorScrollTarget {
  /** The (fractional) 0-based source line at the viewport top. */
  topLine(): number;
  /** The actual pixel top of each given 0-based source line (with height-sync spacers), for the map. */
  topsForLines(lines: readonly number[]): number[];
  scrollTop(): number;
  setScrollTop(px: number): void;
  /** Scroll the minimum amount to bring `line` into view (no-op if already visible), synchronously. */
  reveal(line: number): void;
  /** Scroll so `line` sits at the viewport top, synchronously — the mode-switch restore (self-contained,
   *  so it works while the sibling pane is hidden). */
  scrollToLine(line: number): void;
}

/** The formatted (reference) pane. It owns the shared block-map, so it is the source of the block anchors
 *  both maps are built from. */
export interface FormattedScrollTarget {
  topLine(): number;
  /** The semantic sync anchors (a top per rendered leaf unit — each table row / list item / block —
   *  plus the last unit's bottom) in this pane's pixels. */
  blockAnchors(): readonly ScrollAnchor[];
  scrollTop(): number;
  setScrollTop(px: number): void;
  reveal(line: number): void;
  scrollToLine(line: number): void;
}

export class SplitSync {
  private editorMap = new ScrollMap([]);
  private formattedMap = new ScrollMap([]);
  // The maps are rebuilt lazily on the next couple/restore after any geometry change, not per scroll
  // frame — a scroll does not move blocks, so a run of scroll frames reuses one measurement (T-072).
  private dirty = true;
  // The scrollTop the coordinator last wrote to each pane; a scroll settling on it is that pane's own
  // echo. NaN = never written by us, so any scroll of the pane is genuine.
  private readonly lastWritten: Record<Pane, number> = {
    editor: Number.NaN,
    formatted: Number.NaN,
  };
  // When a scroll last coupled the panes — the reveal-vs-couple guard reads it (see reveal()).
  private lastCoupledAt = Number.NEGATIVE_INFINITY;

  constructor(
    private readonly editor: EditorScrollTarget,
    private readonly formatted: FormattedScrollTarget,
    /** Monotonic clock using `performance.now()` by default; injectable for deterministic fake-time tests. */
    private readonly now: () => number = () => performance.now(),
  ) {}

  /** Mark the maps stale (a geometry change: an edit, a resize, a height-sync spacer reconcile, a mode
   *  switch, a load). The next couple/restore rebuilds them from fresh anchors. */
  invalidate(): void {
    this.dirty = true;
  }

  /** The editor scrolled — drive the formatted pane to match, unless this scroll is our own echo. */
  onEditorScroll(): void {
    this.onScroll("editor");
  }

  /** The formatted pane scrolled — drive the editor to match, unless this scroll is our own echo. */
  onFormattedScroll(): void {
    this.onScroll("formatted");
  }

  /**
   * Drive the sibling to the source pane's viewport-top line. Used for a settled programmatic sync (the
   * cross-pane edit mirror re-aligns the sibling that was just mirrored into) — same path as a user
   * scroll, minus the echo check (the caller knows it just changed the source). The mirror just changed a
   * pane's content, so the maps are rebuilt from the fresh geometry before coupling (matching the old
   * path, which scrolled through the pane's freshly-invalidated geometry cache).
   */
  syncFrom(source: Pane): void {
    this.invalidate();
    this.couple(source);
  }

  /**
   * Reveal the synced line in the PASSIVE pane after a deliberate caret move in `active` (a click/arrow),
   * scrolling it the minimum amount if accumulated drift pushed the line out of view. Skipped while a
   * scroll just coupled the panes — the one explicit timing fallback (see the class comment). The pane's
   * own reveal is precise (row/line level) and synchronous, so its resulting scroll is recorded as our
   * write and suppressed as an echo.
   */
  reveal(line: number, active: Pane): void {
    if (this.now() - this.lastCoupledAt < REVEAL_GUARD_MS) {
      return;
    }
    const passive = this.other(active);
    const before = this.scrollTopOf(passive);
    if (passive === "editor") {
      this.editor.reveal(line);
    } else {
      this.formatted.reveal(line);
    }
    const after = this.scrollTopOf(passive);
    if (Math.abs(after - before) > ECHO_EPSILON) {
      this.lastWritten[passive] = after;
    }
  }

  /**
   * Restore the reading position after a view-mode switch: scroll each newly-visible `pane` so `line`
   * sits at its viewport top. Uses each pane's self-contained scroll-to-line (not the cross-pane map, so
   * it works while the sibling is still hidden), recording the result as our write so no echo drives back.
   */
  restore(line: number, panes: readonly Pane[]): void {
    for (const pane of panes) {
      if (pane === "editor") {
        this.editor.scrollToLine(line);
      } else {
        this.formatted.scrollToLine(line);
      }
      this.lastWritten[pane] = this.scrollTopOf(pane);
    }
  }

  /**
   * Reset both panes to the document top (a fresh load) and claim the result as our write, so the
   * programmatic reset never drives a cross-pane sync.
   */
  reset(): void {
    this.write("editor", 0);
    this.write("formatted", 0);
  }

  /**
   * Claim a pane's CURRENT scrollTop as our own write, so its next scroll event is suppressed as an echo.
   * Used after height-sync nudges the editor's scrollTop (its spacer-weight compensation) — that
   * programmatic move must not be read as a user scroll and drive the formatted pane.
   */
  absorb(pane: Pane): void {
    this.lastWritten[pane] = this.scrollTopOf(pane);
  }

  /** Whether the pane's current scroll event is a coordinator-written echo, not a user scroll. */
  isEcho(pane: Pane): boolean {
    const written = this.lastWritten[pane];
    return Number.isFinite(written) && Math.abs(this.scrollTopOf(pane) - written) <= ECHO_EPSILON;
  }

  private onScroll(source: Pane): void {
    if (this.isEcho(source)) {
      return;
    }
    this.couple(source);
  }

  private couple(source: Pane): void {
    this.ensureMaps();
    const target = this.other(source);
    const targetMap = target === "editor" ? this.editorMap : this.formattedMap;
    // A diverged/empty split yields no anchors — leave both panes where they are rather than snapping the
    // target to 0 (matches the pane methods' own zero-block fallbacks).
    if (targetMap.isEmpty) {
      return;
    }
    const line = source === "editor" ? this.editor.topLine() : this.formatted.topLine();
    this.write(target, targetMap.pxForLine(line));
    this.lastCoupledAt = this.now();
  }

  private ensureMaps(): void {
    if (!this.dirty) {
      return;
    }
    const anchors = this.formatted.blockAnchors();
    this.formattedMap = new ScrollMap(anchors);
    const lines = anchors.map((anchor) => anchor.line);
    const tops = this.editor.topsForLines(lines);
    this.editorMap = new ScrollMap(lines.map((line, index) => ({ line, px: tops[index] ?? 0 })));
    this.dirty = false;
  }

  private write(pane: Pane, px: number): void {
    if (pane === "editor") {
      this.editor.setScrollTop(px);
    } else {
      this.formatted.setScrollTop(px);
    }
    // Record the READ-BACK value (not `px`): if the browser clamped or rounded the write, the echo event
    // will read the same clamped value, so the comparison stays exact.
    this.lastWritten[pane] = this.scrollTopOf(pane);
  }

  private scrollTopOf(pane: Pane): number {
    return pane === "editor" ? this.editor.scrollTop() : this.formatted.scrollTop();
  }

  private other(pane: Pane): Pane {
    return pane === "editor" ? "formatted" : "editor";
  }
}
