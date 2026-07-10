/**
 * The single coalescing, generation-aware entry point for the Split geometry reconcile. Every source
 * that can change either pane's layout — an edit / cross-pane mirror, a width or wrap change, an image
 * decode, a font load, a diff overlay marker, a mode-visibility flip, and CodeMirror finishing an async
 * re-measure (an estimated line height becoming a measured one) — funnels its "geometry changed" signal
 * through {@link invalidate}. A burst of those signals arriving before the next animation frame collapses
 * into ONE reconcile run, and that run reads and applies the geometry that is current AT RUN TIME, never a
 * snapshot captured when the run was scheduled.
 *
 * Why a generation, not a bare rAF throttle. Each invalidation bumps a monotonically increasing generation
 * and the run captures the newest generation at the moment it fires, so several invalidations coalesced
 * into one frame are one run against the newest state — no older callback can clobber a newer generation,
 * and a run that itself triggers a follow-up relayout (applying spacers makes CodeMirror re-measure) simply
 * schedules the NEXT generation's frame, converging to a fixed point over a finite number of frames rather
 * than looping within one. The generation is observable ({@link currentGeneration}/{@link lastRunGeneration})
 * so a fake-rAF test can pin the coalescing and staleness budget structurally, without a wall-clock threshold.
 *
 * Pure scheduling: it owns no geometry. The injected {@link run} does the actual read → compute → write
 * pass (height-sync measures once, the coordinator adopts the one snapshot and writes at most once), and the
 * injected clock defaults to `requestAnimationFrame` but is replaceable for deterministic tests.
 */
export class ReconcileScheduler {
  // Bumped by every invalidation signal. The newest value at run time is the generation the run applies.
  private generation = 0;
  // The generation the most recent run captured — lets a test assert a burst collapsed to the newest one.
  private ranGeneration = -1;
  // Whether a frame is already booked; a second invalidation before it fires is folded into that one frame.
  private scheduled = false;

  constructor(
    private readonly run: (generation: number) => void,
    /** Frame clock; defaults to `requestAnimationFrame`, injectable for deterministic fake-frame tests. */
    private readonly raf: (callback: () => void) => void = (callback) => {
      requestAnimationFrame(callback);
    },
  ) {}

  /** The newest generation — advances on every {@link invalidate}, whether or not a run has fired yet. */
  get currentGeneration(): number {
    return this.generation;
  }

  /** The generation the last run captured (−1 before the first run) — a run is stale only if a newer
   *  generation has since been recorded, which the next frame's run then picks up. */
  get lastRunGeneration(): number {
    return this.ranGeneration;
  }

  /**
   * Record that some source changed the layout and coalesce a reconcile into the next frame. Bumps the
   * generation every call (so bursts are ordered and the newest wins), but books at most one frame — the
   * second and later signals before that frame fires ride the same run.
   */
  invalidate(): void {
    this.generation += 1;
    if (this.scheduled) {
      return;
    }
    this.scheduled = true;
    this.raf(() => {
      this.scheduled = false;
      // Capture the newest generation NOW: every signal since scheduling is already folded in, so the run
      // applies the current layout, never the (possibly stale) one that first booked this frame.
      const generation = this.generation;
      this.ranGeneration = generation;
      this.run(generation);
    });
  }
}
