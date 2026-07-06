import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { debounce } from "../../src/util/debounce.js";

describe("debounce", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("defers the call until the delay elapses", () => {
    const fn = vi.fn();
    const run = debounce(fn, 100);
    run();
    expect(fn).not.toHaveBeenCalled();
    vi.advanceTimersByTime(99);
    expect(fn).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it("coalesces a burst into a single trailing call", () => {
    const fn = vi.fn();
    const run = debounce(fn, 100);
    run();
    run();
    run();
    vi.advanceTimersByTime(100);
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it("restarts the delay on each call, so it never fires mid-burst", () => {
    const fn = vi.fn();
    const run = debounce(fn, 100);
    run();
    vi.advanceTimersByTime(60);
    run(); // resets the clock — the earlier 60ms is discarded
    vi.advanceTimersByTime(60); // 120ms since the first call, but only 60ms since the last
    expect(fn).not.toHaveBeenCalled();
    vi.advanceTimersByTime(40); // now 100ms since the last call
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it("fires again for a fresh burst after settling", () => {
    const fn = vi.fn();
    const run = debounce(fn, 100);
    run();
    vi.advanceTimersByTime(100);
    expect(fn).toHaveBeenCalledTimes(1);
    run();
    vi.advanceTimersByTime(100);
    expect(fn).toHaveBeenCalledTimes(2);
  });

  it("pending reflects whether a deferred call is still outstanding", () => {
    const fn = vi.fn();
    const run = debounce(fn, 100);
    expect(run.pending).toBe(false);
    run();
    expect(run.pending).toBe(true);
    vi.advanceTimersByTime(99);
    expect(run.pending).toBe(true);
    vi.advanceTimersByTime(1);
    expect(run.pending).toBe(false);
    expect(fn).toHaveBeenCalledTimes(1);
  });
});
