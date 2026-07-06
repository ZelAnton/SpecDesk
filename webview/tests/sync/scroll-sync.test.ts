import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { SCROLL_SYNC_MS, ScrollSync } from "../../src/sync/scroll-sync.js";

// Pure timing logic — drive Date.now() with fake timers (a realistic base, so the initial
// lastSyncAt=0 reads as "long ago", not "just now").
describe("ScrollSync", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(100_000);
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("claim: the first pane drives and the other is locked out within the window", () => {
    const s = new ScrollSync();
    expect(s.claim("editor")).toBe(true);
    expect(s.claim("formatted")).toBe(false); // editor is driving
    expect(s.claim("editor")).toBe(true); // the driver re-claims freely
  });

  it("claim: the lock expires after the window, letting the other pane take over", () => {
    const s = new ScrollSync();
    expect(s.claim("editor")).toBe(true);
    vi.advanceTimersByTime(SCROLL_SYNC_MS); // now == driverUntil → no longer strictly before it
    expect(s.claim("formatted")).toBe(true);
  });

  it("claim: the driver re-claiming extends the window", () => {
    const s = new ScrollSync();
    s.claim("editor");
    vi.advanceTimersByTime(SCROLL_SYNC_MS - 1);
    expect(s.claim("editor")).toBe(true); // extends the window to now + SCROLL_SYNC_MS
    vi.advanceTimersByTime(SCROLL_SYNC_MS - 1);
    expect(s.claim("formatted")).toBe(false); // still inside the extended window
  });

  it("suppress: mutes both panes for the window, then frees them", () => {
    const s = new ScrollSync();
    s.suppress();
    expect(s.claim("editor")).toBe(false);
    expect(s.claim("formatted")).toBe(false);
    vi.advanceTimersByTime(SCROLL_SYNC_MS);
    expect(s.claim("editor")).toBe(true);
  });

  it("suppress: clears an active driver, so even the prior driver is muted", () => {
    const s = new ScrollSync();
    s.claim("editor"); // editor is now the active driver
    s.suppress(); // resets the driver to "none" (not just extends the window)
    expect(s.claim("editor")).toBe(false);
    expect(s.claim("formatted")).toBe(false);
  });

  it("drive: overrides an already-active opposite driver without muting the new one", () => {
    const s = new ScrollSync();
    s.claim("editor"); // editor driving
    s.drive("formatted"); // hand authority to formatted mid-window
    expect(s.claim("formatted")).toBe(true); // the driven pane scrolls normally
    expect(s.claim("editor")).toBe(false); // the former driver is now locked out
  });

  it("syncedRecently: true only within the window after markSynced", () => {
    const s = new ScrollSync();
    expect(s.syncedRecently()).toBe(false); // nothing synced yet
    s.markSynced();
    expect(s.syncedRecently()).toBe(true);
    vi.advanceTimersByTime(SCROLL_SYNC_MS - 1);
    expect(s.syncedRecently()).toBe(true);
    vi.advanceTimersByTime(1); // exactly SCROLL_SYNC_MS since markSynced
    expect(s.syncedRecently()).toBe(false);
  });
});
