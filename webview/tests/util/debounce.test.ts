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

  it("flush delivers a pending call immediately and only once", () => {
    const fn = vi.fn();
    const run = debounce(fn, 100);
    expect(run.flush()).toBe(false);
    run();
    expect(run.flush()).toBe(true);
    expect(run.pending).toBe(false);
    expect(fn).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(100);
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it("cancel retires a pending call without invoking it", () => {
    const fn = vi.fn();
    const run = debounce(fn, 100);
    expect(run.cancel()).toBe(false);
    run();
    expect(run.cancel()).toBe(true);
    expect(run.pending).toBe(false);
    expect(run.pendingOrder).toBeNull();
    vi.advanceTimersByTime(100);
    expect(fn).not.toHaveBeenCalled();
  });

  it("exposes a shared chronological order for pending work", () => {
    const older = debounce(() => {}, 100);
    const newer = debounce(() => {}, 100);

    older();
    newer();

    expect(older.pendingOrder).not.toBeNull();
    expect(newer.pendingOrder).toBeGreaterThan(older.pendingOrder ?? 0);
    older.flush();
    expect(older.pendingOrder).toBeNull();
    expect(newer.pendingOrder).not.toBeNull();
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
