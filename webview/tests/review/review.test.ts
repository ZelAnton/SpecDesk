import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { DiffMark } from "../../src/review/diff-marks.js";
import { type DiffSurface, ReviewController, type ReviewDeps } from "../../src/review/review.js";
import type { DiffEntryPayload } from "../../src/wire/protocol.js";

// A fake editor surface: getText is fixed, setDiff/clearDiff are spies, hasPendingChange defaults to
// "settled" (false) so the existing tests below (written before T-077) see no behavior change. No
// DOM — the controller is pure state + delegation, so the surfaces and host callbacks are all that
// need standing in.
function surface(text = "head text", pending = false) {
  const setDiff = vi.fn<(marks: DiffMark[]) => void>();
  const clearDiff = vi.fn<() => void>();
  let isPending = pending;
  const hasPendingChange = vi.fn<() => boolean>(() => isPending);
  const view: DiffSurface = { getText: () => text, setDiff, clearDiff, hasPendingChange };
  return {
    view,
    setDiff,
    clearDiff,
    hasPendingChange,
    setPending: (v: boolean) => {
      isPending = v;
    },
  };
}

// A single changed plain block (no children) → one whole-block mark when expanded.
function changedEntry(): DiffEntryPayload {
  return {
    kind: "changed",
    lineStart: 0,
    lineEnd: 0,
    children: [],
    baseText: "old",
    baseSource: "old",
  };
}

function harness(version = 1) {
  const editor = surface();
  const formatted = surface();
  const setPressed = vi.fn();
  const requestCompare = vi.fn();
  const onEmptyState = vi.fn<(showing: boolean) => void>();
  const onOverflow = vi.fn<(showing: boolean) => void>();
  let current = version;
  const deps: ReviewDeps = {
    surfaces: [editor.view, formatted.view],
    setPressed,
    requestCompare,
    docVersion: () => current,
    onEmptyState,
    onOverflow,
  };
  return {
    review: new ReviewController(deps),
    editor,
    formatted,
    setPressed,
    requestCompare,
    onEmptyState,
    onOverflow,
    setVersion: (v: number) => {
      current = v;
    },
  };
}

describe("ReviewController", () => {
  it("toggle from idle: presses the button and asks the host to compare, but paints nothing yet", () => {
    const h = harness();
    h.review.toggle();
    expect(h.setPressed).toHaveBeenCalledWith(true);
    // The overlay owns the base choice — the local "Show changes" affordance always asks for "lastVersion".
    expect(h.requestCompare).toHaveBeenCalledWith("lastVersion");
    expect(h.requestCompare).toHaveBeenCalledTimes(1);
    // The marks only arrive later via applyResult, so no surface is painted on the click itself.
    expect(h.editor.setDiff).not.toHaveBeenCalled();
    expect(h.formatted.setDiff).not.toHaveBeenCalled();
  });

  it("toggle while showing: clears the overlay (un-presses, clears both surfaces)", () => {
    const h = harness();
    h.review.toggle();
    h.review.toggle();
    expect(h.setPressed).toHaveBeenLastCalledWith(false);
    expect(h.editor.clearDiff).toHaveBeenCalledTimes(1);
    expect(h.formatted.clearDiff).toHaveBeenCalledTimes(1);
    // A second compare was NOT requested — toggling off only clears.
    expect(h.requestCompare).toHaveBeenCalledTimes(1);
  });

  it("re-arming after a clear requests a fresh compare", () => {
    const h = harness();
    h.review.toggle(); // on  → compare #1
    h.review.clear(); // edit/save/reload drops the overlay
    h.review.toggle(); // on again → compare #2, button pressed once more
    expect(h.requestCompare).toHaveBeenCalledTimes(2);
    expect(h.setPressed).toHaveBeenLastCalledWith(true);
    // And a result for the live version now paints again (the overlay is genuinely re-armed).
    h.review.applyResult(1, [changedEntry()]);
    expect(h.editor.setDiff).toHaveBeenCalledTimes(1);
  });

  it("clear is a no-op when nothing is showing", () => {
    const h = harness();
    h.review.clear();
    expect(h.setPressed).not.toHaveBeenCalled();
    expect(h.editor.clearDiff).not.toHaveBeenCalled();
  });

  it("applyResult paints both surfaces when showing and the version matches", () => {
    const h = harness(7);
    h.review.toggle();
    h.review.applyResult(7, [changedEntry()]);
    expect(h.editor.setDiff).toHaveBeenCalledTimes(1);
    expect(h.formatted.setDiff).toHaveBeenCalledTimes(1);
    const marks = h.editor.setDiff.mock.calls[0]?.[0];
    expect(Array.isArray(marks)).toBe(true);
    expect(marks).toHaveLength(1); // one changed block → one whole-block mark
    // Both surfaces get the SAME expanded marks (Split shows both at once).
    expect(h.formatted.setDiff.mock.calls[0]?.[0]).toBe(marks);
  });

  it("applyResult is dropped when the overlay is not showing", () => {
    const h = harness(3);
    h.review.applyResult(3, [changedEntry()]);
    expect(h.editor.setDiff).not.toHaveBeenCalled();
  });

  it("applyResult is dropped when the version is stale (the author edited past the snapshot)", () => {
    const h = harness(5);
    h.review.toggle();
    h.setVersion(6); // a genuine edit advanced the live version after the compare request
    h.review.applyResult(5, [changedEntry()]);
    expect(h.editor.setDiff).not.toHaveBeenCalled();
  });

  it("a result arriving after clear() is dropped", () => {
    const h = harness(2);
    h.review.toggle();
    h.review.clear();
    h.review.applyResult(2, [changedEntry()]);
    expect(h.editor.setDiff).not.toHaveBeenCalled();
  });

  it("empty entries paint an empty mark set (a clean diff shows no washes)", () => {
    const h = harness(1);
    h.review.toggle();
    h.review.applyResult(1, []);
    expect(h.editor.setDiff).toHaveBeenCalledWith([]);
    expect(h.formatted.setDiff).toHaveBeenCalledWith([]);
  });

  it("an empty result raises the no-changes notice", () => {
    const h = harness(1);
    h.review.toggle();
    h.review.applyResult(1, []);
    expect(h.onEmptyState).toHaveBeenLastCalledWith(true);
  });

  it("a result with changes lowers the no-changes notice", () => {
    const h = harness(1);
    h.review.toggle();
    h.review.applyResult(1, [changedEntry()]);
    expect(h.onEmptyState).toHaveBeenLastCalledWith(false);
  });

  it("clearing the overlay lowers the no-changes notice", () => {
    const h = harness(1);
    h.review.toggle();
    h.review.applyResult(1, []); // notice raised
    h.review.clear();
    expect(h.onEmptyState).toHaveBeenLastCalledWith(false);
  });

  it("a dropped result (stale / not showing) never touches the notice", () => {
    const h = harness(4);
    h.review.applyResult(4, []); // not reviewing → dropped
    expect(h.onEmptyState).not.toHaveBeenCalled();
  });

  // T-081: a result overflowing the native node-pair guard swaps `entries` for a compact count-only
  // signal. Painting the fallback's flat Removed+Added listing (thousands of marks) would freeze the
  // editors, so this must wash nothing and raise a distinct notice instead of expanding `entries`.
  describe("overflow signal (T-081)", () => {
    it("washes nothing and raises the overflow notice, not the empty-diff one", () => {
      const h = harness(1);
      h.review.toggle();
      h.review.applyResult(1, [], { removedCount: 5000, addedCount: 5000 });
      expect(h.editor.setDiff).not.toHaveBeenCalled();
      expect(h.formatted.setDiff).not.toHaveBeenCalled();
      expect(h.editor.clearDiff).toHaveBeenCalledTimes(1);
      expect(h.formatted.clearDiff).toHaveBeenCalledTimes(1);
      expect(h.onOverflow).toHaveBeenLastCalledWith(true);
      expect(h.onEmptyState).not.toHaveBeenCalledWith(true);
    });

    it("even a non-empty entries array is ignored once overflow is present (defense in depth)", () => {
      const h = harness(1);
      h.review.toggle();
      h.review.applyResult(1, [changedEntry()], { removedCount: 1, addedCount: 1 });
      expect(h.editor.setDiff).not.toHaveBeenCalled();
    });

    it("a later non-overflowing result lowers the overflow notice", () => {
      const h = harness(1);
      h.review.toggle();
      h.review.applyResult(1, [], { removedCount: 5000, addedCount: 5000 });
      h.review.clear();
      h.review.toggle();
      h.review.applyResult(1, [changedEntry()]);
      expect(h.onOverflow).toHaveBeenLastCalledWith(false);
      expect(h.editor.setDiff).toHaveBeenCalledTimes(1);
    });

    it("clearing the overlay lowers the overflow notice", () => {
      const h = harness(1);
      h.review.toggle();
      h.review.applyResult(1, [], { removedCount: 5000, addedCount: 5000 });
      h.review.clear();
      expect(h.onOverflow).toHaveBeenLastCalledWith(false);
    });

    it("an overflowing result dropped while not showing never touches either notice", () => {
      const h = harness(4);
      h.review.applyResult(4, [], { removedCount: 5000, addedCount: 5000 }); // not reviewing → dropped
      expect(h.onOverflow).not.toHaveBeenCalled();
      expect(h.onEmptyState).not.toHaveBeenCalled();
    });
  });
});

// T-077: toggle() must not ask the host to diff a head one of the surfaces is about to change out
// from under (an unsent edit still waiting out its own 120ms debounce). These use fake timers since
// the deferral polls on a bounded setTimeout chain.
describe("ReviewController: deferred compare while a surface has a pending edit (T-077)", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("defers requestCompare while a surface reports a pending edit, firing once it settles", () => {
    const h = harness();
    h.editor.setPending(true);
    h.review.toggle();
    // Pressed immediately (the button reflects the click right away), but the compare itself waits.
    expect(h.setPressed).toHaveBeenCalledWith(true);
    expect(h.requestCompare).not.toHaveBeenCalled();
    vi.advanceTimersByTime(60);
    expect(h.requestCompare).not.toHaveBeenCalled();
    h.editor.setPending(false); // the debounce fires, the edit is reported
    vi.advanceTimersByTime(20);
    expect(h.requestCompare).toHaveBeenCalledTimes(1);
    expect(h.requestCompare).toHaveBeenCalledWith("lastVersion");
  });

  it("both surfaces pending: compare fires only after both settle", () => {
    const h = harness();
    h.editor.setPending(true);
    h.formatted.setPending(true);
    h.review.toggle();
    expect(h.requestCompare).not.toHaveBeenCalled();
    h.editor.setPending(false); // only one settled so far
    vi.advanceTimersByTime(20);
    expect(h.requestCompare).not.toHaveBeenCalled();
    h.formatted.setPending(false); // both settled now
    vi.advanceTimersByTime(20);
    expect(h.requestCompare).toHaveBeenCalledTimes(1);
  });

  it("no pending change: fires immediately, no regression", () => {
    const h = harness();
    h.review.toggle();
    // No timer needed at all — the first (synchronous) settle check already passes.
    expect(h.requestCompare).toHaveBeenCalledTimes(1);
    expect(h.requestCompare).toHaveBeenCalledWith("lastVersion");
  });

  it("gives up and fires anyway after the bounded retry window if a surface never settles", () => {
    const h = harness();
    h.editor.setPending(true); // never cleared — simulates a surface that never reports settled
    h.review.toggle();
    expect(h.requestCompare).not.toHaveBeenCalled();
    vi.runAllTimers(); // exhausts the bounded poll chain
    expect(h.requestCompare).toHaveBeenCalledTimes(1);
  });

  it("clearing the overlay while a compare is deferred cancels it (no late spurious request)", () => {
    const h = harness();
    h.editor.setPending(true);
    h.review.toggle();
    h.review.clear();
    h.editor.setPending(false);
    vi.runAllTimers();
    expect(h.requestCompare).not.toHaveBeenCalled();
  });

  it("clear-then-re-toggle while the old chain is still polling fires exactly once for the new overlay", () => {
    const h = harness();
    h.editor.setPending(true);
    h.review.toggle(); // chain #1 starts, deferred
    h.review.clear(); // overlay closed before chain #1 ever fired
    h.review.toggle(); // re-armed: chain #2 starts fresh
    h.editor.setPending(false); // settles for both chains' checks
    vi.runAllTimers();
    // Exactly one compare for the live (re-armed) overlay — chain #1 recognizes it was superseded.
    expect(h.requestCompare).toHaveBeenCalledTimes(1);
  });
});
