/**
 * The single owner of both Split panes' scroll position AND of which pane is active. It replaces the
 * former trio of mutually-suppressing mechanisms — the per-frame line-based scroll-sync with its former
 * driver lock, the caret-reveal, and the mode-switch restore — and their glue heuristics (suppress/drive/
 * syncedRecently, the double rAF in applyMode, plus a `leadingPane` bookkeeping variable that used to live
 * in index.ts) with ONE coordinator that alone writes each pane's scrollTop and alone decides which pane
 * leads.
 *
 * Active/passive as an explicit state machine. The ACTIVE pane is the one the author last genuinely
 * scrolled ({@link onEditorScroll}/{@link onFormattedScroll}, echoes excluded) or focused ({@link onFocus})
 * or edited ({@link syncFrom}); the PASSIVE pane is the other. Every reactive coupling write targets ONLY
 * the passive pane — coupling never drives the active pane back, so the pane the author is reading never
 * jumps under them. (The two non-reactive exceptions are the fresh-load {@link reset} and the mode-switch
 * {@link restore}, which establish a baseline on named panes rather than couple; and height-sync's own
 * spacer compensation, which may nudge the editor's scrollTop but only as a viewport-preserving move that
 * keeps the content at the viewport top exactly where it is — no visible motion of active content.)
 *
 * Echo is excluded by construction. Every write the coordinator makes records the resulting scrollTop
 * ({@link lastWritten}); a scroll event whose scrollTop still equals that value is the pane settling where
 * WE put it — its own echo — not a user scroll, so it never drives the sibling back and never re-declares
 * active. That is a deterministic value check, so there is no timing window to tune and no driver lock: the
 * two panes cannot ping-pong. A genuine user scroll moves scrollTop off the recorded value, becomes the new
 * active, and drives the passive once.
 *
 * ONE reversible map, both directions. The couple goes through the line↔px {@link ScrollMap} of each pane,
 * both built from the SAME semantic sync anchors height-sync measures — one per rendered leaf unit, so a
 * tall table couples row-by-row (scroll-map.ts, sync-anchors.ts). Crucially, BOTH the read and the write go
 * through those maps: the active pane's viewport-top line is read as `activeMap.lineForPx(active.scrollTop)`
 * and written as `passiveMap.pxForLine(line)`. Because a pane's own `lineForPx`/`pxForLine` are exact
 * inverses of one piecewise-linear map, and both panes' maps share one line axis (the same anchor lines),
 * the round-trip of one unchanging geometry is the identity — so INTERCEPTING the active pane (the author
 * grabs the pane that was following) couples the sibling straight back to where it already is, with no jump.
 * Reading through the map (not each pane's own height-map read) is what makes that inverse exact: the two
 * were not mutually inverse before (Code read CodeMirror's real per-line heights while its map smeared the
 * spacer between sparse anchors; Formatted read `BlockBox.height` while its map interpolated between block
 * tops), so intercepting active reinterpreted the same vertical point and the viewports drifted. Coupling by
 * LINE is also why height-sync's non-negative-spacer drift never leaks into where the viewports track each
 * other (T-073): the map reads the two panes' actual tops, so a "negative" gap is expressed, not accumulated.
 *
 * The one timing fallback that remains is the reveal-vs-couple guard: after a scroll has just coupled the
 * passive pane, a coincident caret reveal must stand down for a beat, or the two fight over the passive
 * scrollTop and it judders (holding an arrow key that also scrolls the active pane). This is the sole
 * explicit heuristic left, and it gates only the reveal — never the deterministic echo suppression. Its
 * default clock is `performance.now()`, which is monotonic and unaffected by system time adjustments.
 */

import { trace } from "../util/trace.js";
import { type GeometrySnapshot, type ScrollAnchor, ScrollMap } from "./scroll-map.js";

/** Which Split pane. `editor` is the CodeMirror source; `formatted` is the WYSIWYG reference. */
export type Pane = "editor" | "formatted";

/** A scroll event is treated as this pane's own echo when its scrollTop is within this many pixels of the
 *  value the coordinator last wrote — enough slack for the browser rounding scrollTop to a device pixel. */
export const ECHO_EPSILON = 0.5;

/** A couple leaves the passive pane alone when its target lands within this many pixels of where it already
 *  sits — so a settled reconcile (the geometry recomputed the same maps and the passive is already tracking)
 *  makes ZERO scrollTop writes instead of re-writing the same value, and a target clamped at a document
 *  boundary is not re-written every frame. Matches {@link ECHO_EPSILON}: a sub-pixel gap is below what the
 *  browser can even represent, so writing it would only risk a spurious echo, never move anything visible. */
export const WRITE_EPSILON = 0.5;

/** After a scroll couples the passive pane, a caret reveal into it stands down for this long — the one
 *  explicit fallback that keeps a reveal from fighting an active-scroll couple over the passive scrollTop
 *  (matches the former ScrollSync window). */
export const REVEAL_GUARD_MS = 120;

/** The source editor as the coordinator drives it. Reads are CodeMirror-native (per-line precise); the
 *  scroll write is a direct, SYNCHRONOUS scrollTop set so the resulting value can be read straight back
 *  for echo detection (the async `scrollIntoView` the old path used could not be). */
export interface EditorScrollTarget {
  /** The (fractional) 0-based source line at the viewport top — used ONLY for the mode-switch reading
   *  position (its inverse is {@link scrollToLine}); coupling reads the line through the pane's map. */
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
  // The last drive verdict recorded per pane, so the hot scroll path traces only the TRANSITIONS
  // (echo↔drive) rather than one entry per frame — a steady run of echoes records nothing after the first.
  private readonly lastDriveVerdict: Record<Pane, string> = { editor: "", formatted: "" };
  // Which pane leads: the one the author last genuinely scrolled, focused, or edited. The other is the
  // passive pane every coupling write targets. Defaults to the source editor (the first pane shown on a
  // fresh load); every subsequent user interaction re-seats it (see onScroll/onFocus/syncFrom/reset).
  private active: Pane = "editor";

  constructor(
    private readonly editor: EditorScrollTarget,
    private readonly formatted: FormattedScrollTarget,
    /** Monotonic clock using `performance.now()` by default; injectable for deterministic fake-time tests. */
    private readonly now: () => number = () => performance.now(),
  ) {}

  /** The pane that currently leads (last genuine scroll / focus / edit). The passive pane is its sibling. */
  activePane(): Pane {
    return this.active;
  }

  /** Mark the maps stale (a geometry change: an edit, a resize, a height-sync spacer reconcile, a mode
   *  switch, a load). The next couple/restore rebuilds them from fresh anchors. */
  invalidate(): void {
    this.dirty = true;
  }

  /** The editor scrolled — if this is a genuine (non-echo) scroll it becomes active and drives the
   *  formatted pane; its own echo is ignored deterministically. */
  onEditorScroll(): void {
    this.drive("editor");
  }

  /** The formatted pane scrolled — same as {@link onEditorScroll} for the other side. */
  onFormattedScroll(): void {
    this.drive("formatted");
  }

  /**
   * A pane's scroll has SETTLED (the debounced final position after rAF-coalesced frames trailed a
   * momentum/trackpad scroll). Re-run the exact same couple as a live scroll frame from the pane's final
   * scrollTop, so both directions catch up identically to where the scroll actually stopped — an echo
   * (the passive pane settling on our write) is suppressed, so neither pane makes a return write to the
   * active one and there is no oscillation around {@link ECHO_EPSILON}. Symmetric: index.ts wires it for
   * BOTH panes through this one path (the editor had it before; the formatted pane now does too).
   *
   * A settle is STALE once the author has taken over the OTHER pane. The settle debounce is armed on
   * every scroll event and fires ~120 ms after the pane's last one, but a trackpad/momentum scroll of pane
   * A can still have that timer pending when the author grabs pane B: B's own (rAF-throttled) scroll makes
   * B the active pane, and then A's late settle would — through {@link drive} — re-declare A active and
   * couple B straight back to A's line, yanking the pane the author is now scrolling. So a settle only
   * re-couples while its pane is STILL the active one; once the sibling has taken over it stands down (the
   * sibling's own live scroll and settle already own the coupling). This does not weaken the intended
   * re-snap: the pane the author actually finished scrolling IS the active pane, so its settle still runs.
   */
  settle(pane: Pane): void {
    if (pane !== this.active) {
      trace("scroll", "scroll.settle", { pane, active: this.active, verdict: "stale" });
      return;
    }
    trace("scroll", "scroll.settle", { pane, active: this.active, verdict: "run" });
    this.drive(pane);
  }

  /**
   * A pane gained focus — it becomes active immediately and the passive pane is best-effort synced from
   * its first visible semantic line. Since the maps are exact inverses, coupling the newly-passive pane
   * lands it right back where it already sits when the panes were already tracking (no jump on a focus
   * change); it only corrects genuine residual drift. Bows out of the couple when the maps are empty (a
   * diverged split), still re-seating active so the next reconcile/mode-switch leads from the focused pane.
   */
  onFocus(pane: Pane): void {
    this.active = pane;
    this.couple(pane);
  }

  /**
   * Drive the passive pane from `source`'s viewport-top line and MAKE `source` active — the settled
   * programmatic sync after a cross-pane edit mirror re-aligns the sibling that was just mirrored into.
   * Same path as a user scroll, minus the echo check (the caller knows it just changed the source). The
   * mirror just changed a pane's content, so the maps are rebuilt from the fresh geometry before coupling.
   */
  syncFrom(source: Pane): void {
    trace("scroll", "scroll.syncFrom", { source });
    this.active = source;
    this.invalidate();
    this.couple(source);
  }

  /**
   * Adopt a height-sync reconcile's {@link GeometrySnapshot} and re-align the passive pane — the frame-atomic
   * end of one reconcile generation. The snapshot IS the read phase's result: both maps are rebuilt from it
   * directly, so no second Formatted DOM measure or CodeMirror tops read runs after the spacer write that
   * produced it (the forced-layout read→write→read this fixes — T-104). The spacer pass may have nudged the
   * editor's scrollTop as viewport-preserving compensation, so that programmatic move is claimed as our write
   * ({@link absorb}) before coupling. The passive pane is then re-aligned from whichever pane the author last
   * led with, writing ONLY the passive and ONLY when it is more than {@link WRITE_EPSILON} off target — so a
   * settled reconcile makes zero writes and the active pane the author is reading never moves.
   *
   * A `null` snapshot means height-sync gated this pass (a pending / mismatched / stale-anchor pane): nothing
   * measured, so the coordinator keeps its current maps and both panes' scroll exactly as they are until an
   * explicit {@link invalidate}, rather than couple against geometry that never formed.
   */
  reconciled(snapshot: GeometrySnapshot | null): void {
    if (snapshot === null) {
      trace("reconcile", "reconcile.gated", {});
      return;
    }
    trace("reconcile", "reconcile.adopted", {
      anchors: snapshot.formatted.length,
      changed: snapshot.changed,
    });
    // Adopt the one snapshot as both maps — pure, no re-measure — and mark them current so the next scroll
    // frame reuses them without rebuilding (see ensureMaps).
    this.formattedMap = new ScrollMap(snapshot.formatted);
    this.editorMap = new ScrollMap(snapshot.editor);
    this.dirty = false;
    this.absorb("editor");
    this.coupleThrough(this.active);
  }

  /**
   * Reveal the synced line in the PASSIVE pane after a deliberate caret move in `caretPane` (a click/arrow),
   * scrolling it the minimum amount if accumulated drift pushed the line out of view. Skipped while a
   * scroll just coupled the panes — the one explicit timing fallback (see the class comment). The pane's
   * own reveal is precise (row/line level) and synchronous, so its resulting scroll is recorded as our
   * write and suppressed as an echo. Does not re-seat active: a deliberate caret move already focused the
   * pane (a click) or happened inside the already-active one (an arrow), so active is set via {@link onFocus}.
   */
  reveal(line: number, caretPane: Pane): void {
    if (this.now() - this.lastCoupledAt < REVEAL_GUARD_MS) {
      trace("scroll", "scroll.reveal", { line, caretPane, verdict: "guarded", moved: 0 });
      return;
    }
    const passive = this.other(caretPane);
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
    trace("scroll", "scroll.reveal", {
      line,
      caretPane,
      verdict: "revealed",
      moved: Math.round(after - before),
    });
  }

  /**
   * The fractional reading line to carry across a mode switch, read from the pane that owns the reading
   * position: in Split (both panes visible) the ACTIVE pane, else the sole visible pane — so exiting Split
   * keeps the reading coordinate of the pane the author was actually reading, not unconditionally the
   * source editor's. Re-seats active on that pane so the mode just entered leads from there. Called with
   * the PREVIOUS mode's pane visibilities (before the DOM changes) so it reads live geometry.
   */
  readingLine(visibleEditor: boolean, visibleFormatted: boolean): number {
    const from: Pane =
      visibleEditor && visibleFormatted ? this.active : visibleEditor ? "editor" : "formatted";
    this.active = from;
    return from === "editor" ? this.editor.topLine() : this.formatted.topLine();
  }

  /**
   * Restore the reading position after a view-mode switch: scroll each newly-visible `pane` so `line`
   * sits at its viewport top. Uses each pane's self-contained scroll-to-line (not the cross-pane map, so
   * it works while the sibling is still hidden), recording the result as our write so no echo drives back.
   */
  restore(line: number, panes: readonly Pane[]): void {
    trace("scroll", "scroll.restore", { line, panes: panes.length });
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
   * programmatic reset never drives a cross-pane sync. Re-seats active on the source editor — the first
   * pane a freshly loaded document presents.
   */
  reset(): void {
    trace("scroll", "scroll.reset", {});
    this.active = "editor";
    this.write("editor", 0);
    this.write("formatted", 0);
  }

  /**
   * Claim a pane's CURRENT scrollTop as our own write, so its next scroll event is suppressed as an echo.
   * Used (via {@link reconciled}) after height-sync nudges the editor's scrollTop (its spacer-weight
   * compensation) — that programmatic move must not be read as a user scroll and drive the formatted pane.
   */
  absorb(pane: Pane): void {
    this.lastWritten[pane] = this.scrollTopOf(pane);
  }

  /** Whether the pane's current scroll event is a coordinator-written echo, not a user scroll. */
  isEcho(pane: Pane): boolean {
    const written = this.lastWritten[pane];
    return Number.isFinite(written) && Math.abs(this.scrollTopOf(pane) - written) <= ECHO_EPSILON;
  }

  /** A genuine (non-echo) scroll of `pane`: it becomes active and drives the passive once. An echo is
   *  ignored, so it neither re-declares active nor drives back — the shared path for a live scroll frame
   *  and a settle. */
  private drive(pane: Pane): void {
    const echo = this.isEcho(pane);
    const verdict = echo ? "echo" : "drive";
    // Edge-triggered: only a change in verdict is recorded, so a run of echo frames adds one entry, not one
    // per frame. (A genuine scroll's per-frame coupling writes are the always-on `scroll.write` below.)
    if (verdict !== this.lastDriveVerdict[pane]) {
      this.lastDriveVerdict[pane] = verdict;
      trace("scroll", "scroll.drive", {
        pane,
        scrollTop: Math.round(this.scrollTopOf(pane)),
        verdict,
      });
    }
    if (echo) {
      return;
    }
    this.active = pane;
    this.couple(pane);
  }

  /** Couple the PASSIVE pane to `source`'s viewport-top line, (re)building the maps first if a geometry
   *  change marked them stale — the scroll / focus / mirror path. The reconcile path uses
   *  {@link coupleThrough} instead, whose maps were already adopted from the reconcile snapshot. */
  private couple(source: Pane): void {
    this.ensureMaps();
    this.coupleThrough(source);
  }

  /** Couple the PASSIVE pane to `source`'s viewport-top line, reading and writing through the two panes'
   *  CURRENT maps so the round-trip of one geometry is the identity (see the class comment) — no rebuild.
   *  Bows out when either map is empty (a diverged/empty split), leaving both panes where they are, and
   *  leaves the passive alone when it is already within {@link WRITE_EPSILON} of its target so a settled
   *  couple makes no redundant write (and a boundary-clamped target does not re-write every frame). */
  private coupleThrough(source: Pane): void {
    const target = this.other(source);
    const sourceMap = this.mapOf(source);
    const targetMap = this.mapOf(target);
    // A diverged/empty split yields no anchors — leave both panes where they are rather than snapping the
    // target to 0 (matches the pane methods' own zero-block fallbacks).
    if (sourceMap.isEmpty || targetMap.isEmpty) {
      trace.v("scroll", "scroll.skip", { source, verdict: "empty-map" });
      return;
    }
    const line = sourceMap.lineForPx(this.scrollTopOf(source));
    const targetPx = targetMap.pxForLine(line);
    const hadPx = this.scrollTopOf(target);
    if (Math.abs(targetPx - hadPx) <= WRITE_EPSILON) {
      trace.v("scroll", "scroll.skip", {
        source,
        verdict: "epsilon",
        line,
        targetPx: Math.round(targetPx),
      });
      return;
    }
    trace("scroll", "scroll.write", {
      source,
      line,
      targetPx: Math.round(targetPx),
      hadPx: Math.round(hadPx),
    });
    this.write(target, targetPx);
    this.lastCoupledAt = this.now();
  }

  private ensureMaps(): void {
    if (!this.dirty) {
      return;
    }
    const anchors = this.formatted.blockAnchors();
    trace("scroll", "scroll.rebuild", { anchors: anchors.length });
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
    // will read the same clamped value, so the comparison stays exact — a target at the document boundary
    // clamps to one stable best-effort position and its echo is still suppressed (no ping-pong).
    this.lastWritten[pane] = this.scrollTopOf(pane);
  }

  private mapOf(pane: Pane): ScrollMap {
    return pane === "editor" ? this.editorMap : this.formattedMap;
  }

  private scrollTopOf(pane: Pane): number {
    return pane === "editor" ? this.editor.scrollTop() : this.formatted.scrollTop();
  }

  private other(pane: Pane): Pane {
    return pane === "editor" ? "formatted" : "editor";
  }
}
