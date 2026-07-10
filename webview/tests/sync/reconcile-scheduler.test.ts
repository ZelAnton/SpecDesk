import { describe, expect, it } from "vitest";
import { ReconcileScheduler } from "../../src/sync/reconcile-scheduler.js";

/**
 * A manual animation-frame queue: `invalidate` bookings land here as pending callbacks, and `flush` fires
 * them, so a test drives the scheduler's coalescing frame-by-frame with no real rAF and no wall clock.
 * Callbacks scheduled DURING a flush queue for the NEXT flush (a real rAF fires the next frame), which is
 * what lets a run that re-invalidates converge over successive frames instead of looping within one.
 */
function fakeFrames(): { raf: (cb: () => void) => void; flush: () => void; pending: () => number } {
  let queue: Array<() => void> = [];
  return {
    raf: (cb) => {
      queue.push(cb);
    },
    flush: () => {
      const due = queue;
      queue = [];
      for (const cb of due) {
        cb();
      }
    },
    pending: () => queue.length,
  };
}

describe("ReconcileScheduler (generation-aware coalescing)", () => {
  it("coalesces a burst of invalidations into ONE run against the newest generation", () => {
    const frames = fakeFrames();
    const runs: number[] = [];
    const scheduler = new ReconcileScheduler((generation) => runs.push(generation), frames.raf);

    // Five signals (resize + edit + image + font + geometry) arrive before the next frame.
    scheduler.invalidate();
    scheduler.invalidate();
    scheduler.invalidate();
    scheduler.invalidate();
    scheduler.invalidate();
    expect(frames.pending()).toBe(1); // one frame booked, not five
    expect(runs).toEqual([]); // nothing has run yet

    frames.flush();
    expect(runs).toEqual([5]); // exactly one run, seeing the NEWEST generation (5), not the first (1)
    expect(scheduler.lastRunGeneration).toBe(5);
    expect(scheduler.currentGeneration).toBe(5);
  });

  it("does not apply a stale generation: a later invalidation before the frame wins", () => {
    const frames = fakeFrames();
    const seen: number[] = [];
    const scheduler = new ReconcileScheduler((generation) => seen.push(generation), frames.raf);

    scheduler.invalidate(); // generation 1 (an edit against document A) books the frame
    // Before the frame fires, the document changes again (a width change / another edit) → generation 2.
    scheduler.invalidate();
    frames.flush();

    // The one run applies generation 2 (the current state), never the generation-1 snapshot it was booked on.
    expect(seen).toEqual([2]);
  });

  it("runs once per frame across separate frames (a later invalidation books a fresh frame)", () => {
    const frames = fakeFrames();
    const runs: number[] = [];
    const scheduler = new ReconcileScheduler((generation) => runs.push(generation), frames.raf);

    scheduler.invalidate();
    frames.flush();
    expect(runs).toEqual([1]);

    // A new signal after the run books another frame — it is not swallowed by the completed one.
    scheduler.invalidate();
    scheduler.invalidate();
    frames.flush();
    expect(runs).toEqual([1, 3]); // second run sees generation 3 (two more invalidations)
  });

  it("lets a run that re-invalidates converge over the NEXT frame, not loop within this one", () => {
    const frames = fakeFrames();
    let runCount = 0;
    // The run re-invalidates the first two times (modelling applying spacers → CodeMirror re-measures →
    // another geometryChanged) and settles on the third — a finite-frame fixed point, not an infinite loop.
    const scheduler = new ReconcileScheduler((_generation) => {
      runCount += 1;
      if (runCount < 3) {
        scheduler.invalidate();
      }
    }, frames.raf);

    scheduler.invalidate();
    frames.flush(); // run 1 → re-invalidates for the next frame
    expect(runCount).toBe(1);
    expect(frames.pending()).toBe(1); // the re-invalidation booked exactly one follow-up frame

    frames.flush(); // run 2 → re-invalidates once more
    expect(runCount).toBe(2);

    frames.flush(); // run 3 → settles, no re-invalidation
    expect(runCount).toBe(3);
    expect(frames.pending()).toBe(0); // converged — no further frames booked
  });
});
