import { describe, expect, it } from "vitest";
import { icon } from "../../src/workspace/icons.js";

describe("icon", () => {
  it("returns inline svg markup for a known name", () => {
    const svg = icon("navigator");
    expect(svg.startsWith("<svg")).toBe(true);
    expect(svg).toContain('viewBox="0 0 24 24"');
    // Decorative: the icon carries no accessible name (the button's aria-label does).
    expect(svg).toContain('aria-hidden="true"');
  });

  it("falls back to a neutral dot for an unknown name", () => {
    const svg = icon("does-not-exist");
    expect(svg.startsWith("<svg")).toBe(true);
    expect(svg).toContain("<circle");
  });
});
