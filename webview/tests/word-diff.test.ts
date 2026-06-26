import { describe, expect, it } from "vitest";
import { wordDiff } from "../src/word-diff.js";

describe("wordDiff", () => {
  it("reports no change for identical text", () => {
    const d = wordDiff("the quick brown fox", "the quick brown fox");
    expect(d.changeRatio).toBe(0);
    expect(d.ops.every((op) => op.type === "equal")).toBe(true);
  });

  it("equal + add ops reconstruct the head text exactly", () => {
    const head = "the quick brown fox leaps high";
    const d = wordDiff("the quick brown fox jumps", head);
    const rebuilt = d.ops
      .filter((op) => op.type !== "del")
      .map((op) => head.slice(op.start, op.end))
      .join("");
    expect(rebuilt).toBe(head);
  });

  it("flags an added word with its head range", () => {
    const head = "alpha beta gamma";
    const d = wordDiff("alpha gamma", head);
    const adds = d.ops.filter((op) => op.type === "add");
    expect(adds.length).toBeGreaterThan(0);
    expect(adds.some((op) => head.slice(op.start, op.end).includes("beta"))).toBe(true);
  });

  it("flags a deleted word as a zero-width del op carrying the text", () => {
    const d = wordDiff("alpha beta gamma", "alpha gamma");
    const dels = d.ops.filter((op) => op.type === "del");
    expect(dels.length).toBeGreaterThan(0);
    expect(dels[0]?.start).toBe(dels[0]?.end);
    expect(dels.map((op) => op.text).join("")).toContain("beta");
  });

  it("a full rewrite has a high change ratio (> 0.5)", () => {
    const d = wordDiff("alpha beta gamma delta", "totally different wording here now");
    expect(d.changeRatio).toBeGreaterThan(0.5);
  });

  it("a small edit in a long sentence has a low change ratio (< 0.5)", () => {
    const d = wordDiff(
      "the quick brown fox jumps over the lazy dog today",
      "the quick brown fox leaps over the lazy dog today",
    );
    expect(d.changeRatio).toBeLessThan(0.5);
  });
});
