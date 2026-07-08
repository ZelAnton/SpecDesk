/**
 * The review/compare overlay state machine (PoC-6). "Show changes" diffs the working copy against the
 * last saved version and washes the changed lines/blocks in BOTH editors. The overlay is a snapshot
 * taken at the docVersion of the click: any genuine edit (or a version save / reload) invalidates it,
 * so {@link ReviewController.clear} drops it and un-presses the button; the author clicks Show changes
 * again to recompute. It owns only the `reviewing` flag and the editors' diff marks — the integrator
 * keeps all ipc/Kinds/decoder knowledge and feeds parsed results in (see index.ts), mirroring how the
 * inline prompt bars reach the host only through callbacks (dialogs.ts).
 */

import type { DiffBaseKind, DiffEntryPayload, DiffOverflowPayload } from "../wire/protocol.js";
import { type DiffMark, expandDiffMarks } from "./diff-marks.js";

/** The slice of an editor the overlay paints — both MarkdownEditor and FormattedEditor satisfy it. */
export interface DiffSurface {
  getText(): string;
  setDiff(marks: DiffMark[]): void;
  clearDiff(): void;
  /** Whether an edit has been typed here that hasn't been reported via `onChange` yet (still waiting
   *  out the editor's own debounce) — see the identical member on `MarkdownEditor`/`FormattedEditor`.
   *  {@link ReviewController.toggle} polls this before asking the host to compare, so the request is
   *  never taken against a stale head (see the class doc comment). */
  hasPendingChange(): boolean;
}

/** How often {@link ReviewController.toggle} re-checks the surfaces while a compare is deferred. */
const SETTLE_POLL_MS = 20;
/** Upper bound on deferral: `MAX_SETTLE_POLLS * SETTLE_POLL_MS` (300ms) comfortably exceeds the
 *  editors' own 120ms edit debounce, so a genuinely in-flight edit always settles within the window.
 *  If a surface still reports pending past this bound (a caller in a state this class doesn't expect,
 *  rather than an ordinary in-flight debounce), the request fires anyway — deferring indefinitely
 *  would risk the overlay never opening at all, which is worse than the rare stale-head race this
 *  guards against. */
const MAX_SETTLE_POLLS = 15;

export interface ReviewDeps {
  /** The editable panes the overlay washes (Split shows both at once); at least one. The first is the
   *  canonical head whose text the diff is expanded against — in Split the panes are kept mirrored, so
   *  either reads the same source. */
  surfaces: readonly [DiffSurface, ...DiffSurface[]];
  /** Reflect the toggle's pressed state on the "Show changes" button. */
  setPressed: (on: boolean) => void;
  /** Ask the host to diff the working copy against the given base. The overlay owns which base to ask
   *  for (today always `"lastVersion"`; PoC-7 wires other affordances that pass `"published"`/`"pr"`).
   *  The integrator stamps the current docVersion on the request so a stale reply is dropped by
   *  {@link ReviewController.applyResult}. */
  requestCompare: (base: DiffBaseKind) => void;
  /** The live monotonic document version, for the result's version-gate. */
  docVersion: () => number;
  /** Toggle the "no changes to show" notice: fired with true when a compare returns an empty diff
   *  while the overlay is on (so the author isn't left wondering why nothing is highlighted), and with
   *  false when there are changes to show or the overlay is cleared. */
  onEmptyState: (showing: boolean) => void;
  /** Toggle the "too many changes to show" notice: fired with true when a compare overflows the native
   *  node-pair guard (see {@link DiffOverflowPayload}) while the overlay is on — a distinct notice from
   *  {@link onEmptyState}'s, since nothing is washed for a reason opposite to "no changes" (there ARE
   *  changes, just too many to diff in detail). Fired with false when a later result doesn't overflow, or
   *  the overlay is cleared. */
  onOverflow: (showing: boolean) => void;
}

export class ReviewController {
  private reviewing = false;
  // Bumped by every toggle() (a fresh deferred-compare chain) and by clear() (nothing should still be
  // waiting to fire). requestCompareOnceSettled captures the token its own chain started with and
  // compares it back before ever calling requestCompare, so a chain outlived by a clear()-then-toggle()
  // re-arm recognizes it's stale and aborts instead of firing a second, spurious compare for the NEW
  // (unrelated) toggle — reviewing alone can't tell the two apart, since a re-arm sets it back to true.
  private settleToken = 0;

  constructor(private readonly deps: ReviewDeps) {}

  /** Toggle the overlay: clear a showing one, or start a fresh compare (press the button and ask the
   *  host to diff — the marks arrive later via {@link applyResult}). The local "Show changes" affordance
   *  always compares against the last saved version; PoC-7's PR/published affordances will call a
   *  variant that passes a different base. The compare request itself is deferred — see
   *  {@link requestCompareOnceSettled} — until every surface reports no pending, not-yet-reported edit,
   *  so the host is never asked to diff a head that's about to change out from under the reply. */
  toggle(): void {
    if (this.reviewing) {
      this.clear();
      return;
    }
    this.reviewing = true;
    this.deps.setPressed(true);
    this.settleToken += 1;
    this.requestCompareOnceSettled(this.settleToken, 0);
  }

  /** Poll every surface's {@link DiffSurface.hasPendingChange}; fire the compare request once none are
   *  pending (immediately, on the first check, when nothing was in flight — the common case), or after
   *  {@link MAX_SETTLE_POLLS} bounded retries if one never settles. Aborts silently once `token` no
   *  longer matches {@link settleToken} — this chain's own toggle() call was superseded by a clear() or
   *  a fresh re-arm since it started. */
  private requestCompareOnceSettled(token: number, attempt: number): void {
    if (token !== this.settleToken) {
      return;
    }
    const settled = this.deps.surfaces.every((surface) => !surface.hasPendingChange());
    if (settled || attempt >= MAX_SETTLE_POLLS) {
      this.deps.requestCompare("lastVersion");
      return;
    }
    setTimeout(() => this.requestCompareOnceSettled(token, attempt + 1), SETTLE_POLL_MS);
  }

  /** Drop the overlay: un-press the button and clear the marks in every surface. A genuine edit, a
   *  version save, a discard, or a document reload calls this; a no-op when nothing is showing. */
  clear(): void {
    if (!this.reviewing) {
      return;
    }
    this.reviewing = false;
    this.settleToken += 1; // invalidate any deferred compare chain still polling for this overlay
    this.deps.setPressed(false);
    for (const surface of this.deps.surfaces) {
      surface.clearDiff();
    }
    this.deps.onEmptyState(false);
    this.deps.onOverflow(false);
  }

  /** Apply a `diff.result`. Dropped unless the overlay is still showing and the result matches the live
   *  version — i.e. the author hasn't edited past the snapshot the request was taken at (the same
   *  version-gate the preview uses). Marks are expanded against the current head. The version-gate alone
   *  does NOT guarantee that head matches the diff's — a `docVersion` bump only happens once an edit's
   *  debounce reports it, so a result computed against a head with a still-pending, not-yet-reported edit
   *  would pass the gate (the version hasn't moved yet) while `getText()` already reflects that edit. What
   *  closes the gap is {@link toggle} deferring the request itself until every surface is settled (see
   *  {@link requestCompareOnceSettled}), so by the time a request is ever sent, the head it's taken
   *  against is the one this expansion later reads.
   *
   *  `overflow`, when present, means the native side's node-pair guard fired and swapped a compact
   *  count-only signal in for `entries` (empty in that case) — expanding it would otherwise mean nothing
   *  to expand anyway, but the point is `entries` was NEVER the full flat Removed+Added listing on the
   *  wire in the first place. Painting that listing's thousands of marks would freeze the editors, which
   *  is exactly the pathological case the guard exists for, so this washes nothing and raises a distinct
   *  notice instead of the ordinary "no changes" one. */
  applyResult(version: number, entries: DiffEntryPayload[], overflow?: DiffOverflowPayload): void {
    if (!this.reviewing || version !== this.deps.docVersion()) {
      return;
    }
    if (overflow) {
      for (const surface of this.deps.surfaces) {
        surface.clearDiff();
      }
      this.deps.onEmptyState(false);
      this.deps.onOverflow(true);
      return;
    }
    this.deps.onOverflow(false);
    const [head] = this.deps.surfaces;
    const marks = expandDiffMarks(entries, head.getText());
    for (const surface of this.deps.surfaces) {
      surface.setDiff(marks);
    }
    // An empty diff (nothing changed, or no saved version yet) washes nothing, so surface a plain
    // "no changes" notice; a non-empty diff hides it (the highlights speak for themselves).
    this.deps.onEmptyState(marks.length === 0);
  }
}
