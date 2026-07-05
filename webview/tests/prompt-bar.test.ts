// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { PromptBar } from "../src/prompt-bar.js";

function makeBar(): HTMLElement {
  const el = document.createElement("div");
  el.hidden = true; // both prompt bars start hidden, as in index.html
  return el;
}

// A reveal that does the visible work the dialogs callbacks do — here just unhiding the bar.
function revealUnhiding(bar: HTMLElement) {
  return vi.fn((_suggested: string) => {
    bar.hidden = false;
  });
}

describe("PromptBar", () => {
  it("isOpen reflects the bar's hidden state", () => {
    const bar = makeBar();
    const pb = new PromptBar(bar);
    expect(pb.isOpen).toBe(false);
    bar.hidden = false;
    expect(pb.isOpen).toBe(true);
  });

  it("open fetches the suggestion and reveals with it", async () => {
    const bar = makeBar();
    const pb = new PromptBar(bar);
    const reveal = revealUnhiding(bar);
    await pb.open(async () => "hello", reveal);
    expect(reveal).toHaveBeenCalledWith("hello");
    expect(pb.isOpen).toBe(true);
  });

  it("is a no-op when already open (no second request)", async () => {
    const bar = makeBar();
    bar.hidden = false; // already revealed
    const pb = new PromptBar(bar);
    const suggest = vi.fn(async () => "x");
    await pb.open(suggest, vi.fn());
    expect(suggest).not.toHaveBeenCalled();
  });

  it("latches out a second open while the first request is still in flight", async () => {
    const bar = makeBar();
    const pb = new PromptBar(bar);
    const resolvers: Array<(value: string) => void> = [];
    const suggest = vi.fn(() => new Promise<string>((resolve) => resolvers.push(resolve)));
    const reveal = revealUnhiding(bar);
    const first = pb.open(suggest, reveal);
    const second = pb.open(suggest, reveal); // in-flight latch → no-op
    expect(suggest).toHaveBeenCalledTimes(1);
    resolvers[0]?.("v");
    await Promise.all([first, second]);
    expect(reveal).toHaveBeenCalledTimes(1);
  });

  it("a close during the in-flight request supersedes it: reveal never runs, bar stays closed", async () => {
    const bar = makeBar();
    const pb = new PromptBar(bar);
    const resolvers: Array<(value: string) => void> = [];
    const suggest = () => new Promise<string>((resolve) => resolvers.push(resolve));
    const reveal = revealUnhiding(bar);
    const opening = pb.open(suggest, reveal);
    pb.close(); // e.g. a new document loads before the suggestion resolves
    resolvers[0]?.("late");
    await opening;
    expect(reveal).not.toHaveBeenCalled();
    expect(pb.isOpen).toBe(false);
  });

  it("an open() after a close() during the in-flight request is not swallowed by the stale latch", async () => {
    const bar = makeBar();
    const pb = new PromptBar(bar);
    const resolvers: Array<(value: string) => void> = [];
    const suggest = vi.fn(() => new Promise<string>((resolve) => resolvers.push(resolve)));
    const reveal = revealUnhiding(bar);

    const stale = pb.open(suggest, reveal); // request #1, still in flight
    pb.close(); // e.g. a new document loads before it resolves — invalidates #1, drops the latch
    const fresh = pb.open(suggest, reveal); // reopened right away — must not be latched out by #1
    expect(suggest).toHaveBeenCalledTimes(2);

    resolvers[0]?.("stale"); // #1's late reply must not reveal, nor clobber #2's latch
    resolvers[1]?.("fresh");
    await Promise.all([stale, fresh]);

    expect(reveal).toHaveBeenCalledTimes(1);
    expect(reveal).toHaveBeenCalledWith("fresh");
    expect(pb.isOpen).toBe(true);
  });

  it("close hides the bar", () => {
    const bar = makeBar();
    bar.hidden = false;
    const pb = new PromptBar(bar);
    pb.close();
    expect(bar.hidden).toBe(true);
  });

  it("tolerates a null bar — never open, close is a no-op, an un-superseded open still reveals", async () => {
    const pb = new PromptBar(null);
    const reveal = vi.fn();
    await pb.open(async () => "x", reveal);
    expect(reveal).toHaveBeenCalledWith("x");
    expect(pb.isOpen).toBe(false);
    expect(() => pb.close()).not.toThrow();
  });
});
