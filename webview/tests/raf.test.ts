import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { rafThrottle } from "../src/raf.js";

describe("rafThrottle", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    // The test environment is plain Node (no DOM), so stub `requestAnimationFrame` with a
    // timer-based shim the fake clock can drive.
    vi.stubGlobal("requestAnimationFrame", (cb: FrameRequestCallback) =>
      setTimeout(() => cb(0), 16),
    );
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it("defers the call to the next animation frame", () => {
    const fn = vi.fn();
    const run = rafThrottle(fn);
    run();
    expect(fn).not.toHaveBeenCalled();
    vi.runOnlyPendingTimers();
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it("coalesces calls within the same frame into a single invocation", () => {
    const fn = vi.fn();
    const run = rafThrottle(fn);
    run();
    run();
    run();
    vi.runOnlyPendingTimers();
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it("schedules again for a call that arrives after the frame fires", () => {
    const fn = vi.fn();
    const run = rafThrottle(fn);
    run();
    vi.runOnlyPendingTimers();
    expect(fn).toHaveBeenCalledTimes(1);
    run();
    vi.runOnlyPendingTimers();
    expect(fn).toHaveBeenCalledTimes(2);
  });
});
