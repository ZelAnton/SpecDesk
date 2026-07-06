import { describe, expect, it } from "vitest";
import { applyWordDiff, diffLabel, removedMarkerLabel } from "../../src/review/diff-decoration.js";

// diff-decoration is the single source of truth for both review panes' overlays (a fix lands in both
// the Code and Formatted panes at once). It is pure and DOM-free but branchy — the change labels, the
// removed-block marker text, and applyWordDiff's three whole-block-wash fallbacks. A silent regression
// here corrupts what the reviewer sees without crashing, so pin the branches directly.

describe("diffLabel", () => {
  it("labels each known change kind as the author's own", () => {
    expect(diffLabel("added")).toBe("Added by you");
    expect(diffLabel("changed")).toBe("Updated by you");
    expect(diffLabel("moved")).toBe("Moved by you");
    expect(diffLabel("removed")).toBe("Deleted by you");
  });

  it("falls back to a generic label for an unknown kind", () => {
    expect(diffLabel("")).toBe("Changed by you");
    expect(diffLabel("something-else")).toBe("Changed by you");
  });
});

describe("removedMarkerLabel", () => {
  it("previews a single removed line", () => {
    expect(removedMarkerLabel("a heading")).toBe("Deleted by you — a heading");
  });

  it("shows a placeholder when the removed block is empty", () => {
    expect(removedMarkerLabel("")).toBe("Deleted by you — (empty block)");
    expect(removedMarkerLabel("   ")).toBe("Deleted by you — (empty block)");
  });

  it("previews the first line and counts the lines for a multi-line block", () => {
    expect(removedMarkerLabel("first\nsecond\nthird")).toBe("Deleted by you — first (… 3 lines)");
  });

  it("trims the previewed first line", () => {
    expect(removedMarkerLabel("  spaced  \nmore")).toBe("Deleted by you — spaced (… 2 lines)");
  });
});

describe("applyWordDiff", () => {
  it("reports add and remove offsets for a small edit and returns true", () => {
    const base = "the quick brown fox jumps over the lazy dog";
    const head = "the quick brown fox leaps over the lazy dog";
    const adds: Array<[number, number]> = [];
    const removes: string[] = [];

    const result = applyWordDiff(
      base,
      head,
      (start, end) => adds.push([start, end]),
      (_at, text) => removes.push(text),
    );

    expect(result).toBe(true);
    // Added offsets are HEAD offsets — slicing head by them recovers the new word.
    expect(adds.map(([s, e]) => head.slice(s, e)).join("")).toContain("leaps");
    expect(removes.join("")).toContain("jumps");
  });

  it("washes the whole block (false, no callbacks) when the head is too large", () => {
    let called = false;
    const result = applyWordDiff(
      "small",
      "a".repeat(4001),
      () => {
        called = true;
      },
      () => {
        called = true;
      },
    );
    expect(result).toBe(false);
    expect(called).toBe(false);
  });

  it("washes the whole block when the base is too large", () => {
    let called = false;
    const result = applyWordDiff(
      "a".repeat(4001),
      "small",
      () => {
        called = true;
      },
      () => {
        called = true;
      },
    );
    expect(result).toBe(false);
    expect(called).toBe(false);
  });

  it("washes the whole block when nothing word-level differs", () => {
    let called = false;
    const result = applyWordDiff(
      "hello world",
      "hello world",
      () => {
        called = true;
      },
      () => {
        called = true;
      },
    );
    expect(result).toBe(false);
    expect(called).toBe(false);
  });

  it("washes the whole block when too much changed (over the ratio)", () => {
    let called = false;
    const result = applyWordDiff(
      "alpha beta gamma",
      "delta epsilon zeta",
      () => {
        called = true;
      },
      () => {
        called = true;
      },
    );
    expect(result).toBe(false);
    expect(called).toBe(false);
  });
});
