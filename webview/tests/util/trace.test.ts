// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { log } from "../../src/util/log.js";
import { clip, installDiagnostics, trace } from "../../src/util/trace.js";

// The error-capture path forwards to the `log` channel; mock it so the test asserts forwarding
// without dragging in the IPC transport (and so the record path stays the subject under test).
vi.mock("../../src/util/log.js", () => ({
  log: { debug: vi.fn(), info: vi.fn(), warn: vi.fn(), error: vi.fn() },
}));

const RING_SIZE = 2000;

beforeEach(() => {
  trace.clear();
  trace.enabled = true;
  trace.verbose = false;
});

describe("trace ring buffer", () => {
  it("records entries with monotonic seq and structural data", () => {
    trace("format", "format.inline", { decision: "unwrap-node", from: 3, to: 9 });
    trace("scroll", "scroll.write", { line: 12, targetPx: 240 });

    const { entries } = trace.snapshot();
    expect(entries).toHaveLength(2);
    const [first, second] = entries;
    expect(first).toMatchObject({
      seq: 0,
      cat: "format",
      event: "format.inline",
      data: { decision: "unwrap-node", from: 3, to: 9 },
    });
    expect(second?.seq).toBe(1);
    expect(second?.t ?? 0).toBeGreaterThanOrEqual(first?.t ?? 0);
  });

  it("wraps at capacity, keeps seq monotonic, and reports the oldest retained seq", () => {
    const total = RING_SIZE + 500;
    for (let i = 0; i < total; i++) {
      trace("render", "render.setText", { i });
    }

    const snap = trace.snapshot();
    expect(snap.entries).toHaveLength(RING_SIZE);
    expect(snap.firstSeq).toBe(total - RING_SIZE); // 500 oldest entries evicted
    expect(snap.entries.at(0)?.seq).toBe(total - RING_SIZE);
    expect(snap.entries.at(-1)?.seq).toBe(total - 1);
  });

  it("get(n) returns the last n entries, oldest first, capped at what is retained", () => {
    for (let i = 0; i < 5; i++) {
      trace("mirror", "mirror.change", { i });
    }
    const last3 = trace.get(3);
    expect(last3.map((e) => e.seq)).toEqual([2, 3, 4]);
    // Asking for more than exist returns all retained, never throws.
    expect(trace.get(100)).toHaveLength(5);
    expect(trace.get(0)).toHaveLength(0);
  });

  it("mark() records an ipc/mark entry for harness delimiting", () => {
    trace.mark("open-doc");
    const [entry] = trace.snapshot().entries;
    expect(entry).toMatchObject({ cat: "ipc", event: "mark", data: { label: "open-doc" } });
  });

  it("is a no-op when disabled", () => {
    trace.enabled = false;
    trace("format", "format.source", { command: "bold" });
    expect(trace.snapshot().entries).toHaveLength(0);
  });

  it("setVerbose toggles the per-frame gate", () => {
    expect(trace.verbose).toBe(false);
    trace.setVerbose(true);
    expect(trace.verbose).toBe(true);
  });

  it("carries a t0Epoch usable for wall-clock reconstruction", () => {
    // t0Epoch + performance.now() should land near Date.now() (same machine clock).
    const wall = trace.t0Epoch + performance.now();
    expect(Math.abs(wall - Date.now())).toBeLessThan(1000);
    expect(trace.snapshot().t0Epoch).toBe(trace.t0Epoch);
  });
});

describe("clip", () => {
  it("passes short strings through and truncates long ones with an ellipsis", () => {
    expect(clip("short")).toBe("short");
    expect(clip(undefined)).toBe("");
    const long = "x".repeat(60);
    const clipped = clip(long, 40);
    expect(clipped).toHaveLength(41); // 40 chars + ellipsis
    expect(clipped.endsWith("…")).toBe(true);
  });
});

describe("snapshotPayload (trace.dump wire shape)", () => {
  it("stringifies entry data, omits it when absent, and carries t0Epoch/firstSeq", () => {
    trace("format", "format.source", { command: "bold", from: 0, to: 4 });
    trace("scroll", "scroll.reset");
    const payload = trace.snapshotPayload();

    expect(payload.t0Epoch).toBe(trace.t0Epoch);
    expect(payload.firstSeq).toBe(0);
    expect(payload.entries).toHaveLength(2);
    const [withData, noData] = payload.entries;
    expect(withData?.data).toContain('"command":"bold"');
    expect(noData?.data).toBeUndefined();
  });

  it("caps huge data at 500 chars and survives circular data", () => {
    trace("render", "render.setText", { big: "x".repeat(2000) });
    const circular: Record<string, unknown> = {};
    circular.self = circular;
    trace("render", "render.divergence", circular);
    const [big, circ] = trace.snapshotPayload().entries;

    expect((big?.data ?? "").length).toBeLessThanOrEqual(500);
    expect(circ?.data).toBe("[unserializable trace data]");
  });
});

describe("installDiagnostics", () => {
  it("exposes the read API on window and captures global errors, rate-limiting the log channel", () => {
    installDiagnostics();
    expect(window.__specdeskTrace).toBeDefined();

    trace.clear();
    vi.mocked(log.error).mockClear();

    // Dispatch more errors than the per-window forward cap (10). The ring must record every one;
    // the log channel must be capped.
    const dispatched = 12;
    for (let i = 0; i < dispatched; i++) {
      window.dispatchEvent(
        new ErrorEvent("error", {
          message: `boom ${i}`,
          error: new Error(`boom ${i}`),
          filename: "webview.js",
          lineno: i,
          colno: 1,
        }),
      );
    }
    // An unhandled rejection routes through the same capture (jsdom lacks PromiseRejectionEvent,
    // so synthesize the shape the handler reads).
    const rejection = new Event("unhandledrejection") as Event & { reason: unknown };
    rejection.reason = new Error("nope");
    window.dispatchEvent(rejection);

    const errorEntries = trace.snapshot().entries.filter((e) => e.cat === "error");
    expect(errorEntries).toHaveLength(dispatched + 1);
    expect(errorEntries.some((e) => e.event === "window.onerror")).toBe(true);
    expect(errorEntries.some((e) => e.event === "unhandledrejection")).toBe(true);
    // 13 events dispatched in one window, forwarding capped at 10.
    expect(vi.mocked(log.error).mock.calls.length).toBe(10);
  });

  it("registers the error listeners exactly once across repeated installs", () => {
    // Calling install several times must NOT stack duplicate listeners — otherwise one error would
    // record several ring entries and forward several log frames. Dispatch one error after repeated
    // installs and assert exactly one of each; a removed `installed` guard makes this see more.
    installDiagnostics();
    installDiagnostics();
    installDiagnostics();
    expect(window.__specdeskTrace).toBeDefined();

    trace.clear();
    vi.mocked(log.error).mockClear();

    window.dispatchEvent(new ErrorEvent("error", { message: "once", error: new Error("once") }));

    const errorEntries = trace.snapshot().entries.filter((e) => e.cat === "error");
    expect(errorEntries).toHaveLength(1);
    expect(vi.mocked(log.error).mock.calls.length).toBe(1);
  });
});
