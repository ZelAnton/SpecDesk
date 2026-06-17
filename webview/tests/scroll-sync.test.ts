import { describe, expect, it } from "vitest";
import type { ScrollAnchor } from "../src/height-sync.js";
import { mapByAnchors } from "../src/scroll-sync.js";

const anchors: ScrollAnchor[] = [
  { editorTop: 0, previewTop: 0 },
  { editorTop: 100, previewTop: 50 }, // a tall source block: 100 editor px ↔ 50 preview px
  { editorTop: 160, previewTop: 150 }, // a tall rendered block: 60 editor px ↔ 100 preview px
];

describe("mapByAnchors", () => {
  it("returns null with no anchors so the caller can fall back", () => {
    expect(mapByAnchors([], 42, "editor")).toBeNull();
    expect(mapByAnchors([], 42, "preview")).toBeNull();
  });

  it("interpolates editor → preview inside a segment", () => {
    // Halfway through the first segment (editor 0..100 ↔ preview 0..50).
    expect(mapByAnchors(anchors, 50, "editor")).toBe(25);
    // Halfway through the second segment (editor 100..160 ↔ preview 50..150).
    expect(mapByAnchors(anchors, 130, "editor")).toBe(100);
  });

  it("interpolates preview → editor inside a segment (inverse)", () => {
    expect(mapByAnchors(anchors, 25, "preview")).toBe(50);
    expect(mapByAnchors(anchors, 100, "preview")).toBe(130);
  });

  it("maps anchor points exactly", () => {
    expect(mapByAnchors(anchors, 100, "editor")).toBe(50);
    expect(mapByAnchors(anchors, 150, "preview")).toBe(160);
  });

  it("extrapolates 1:1 above the first anchor", () => {
    const offset: ScrollAnchor[] = [
      { editorTop: 20, previewTop: 20 },
      { editorTop: 60, previewTop: 60 },
    ];
    expect(mapByAnchors(offset, 0, "editor")).toBe(0);
    expect(mapByAnchors(offset, 10, "editor")).toBe(10);
  });

  it("extrapolates 1:1 below the last anchor", () => {
    expect(mapByAnchors(anchors, 200, "editor")).toBe(190); // 150 + (200 - 160)
    expect(mapByAnchors(anchors, 200, "preview")).toBe(210); // 160 + (200 - 150)
  });

  it("is monotonic across the whole range (editor → preview)", () => {
    let previous = Number.NEGATIVE_INFINITY;
    for (let s = -20; s <= 220; s += 5) {
      const mapped = mapByAnchors(anchors, s, "editor");
      expect(mapped).not.toBeNull();
      expect(mapped as number).toBeGreaterThanOrEqual(previous);
      previous = mapped as number;
    }
  });
});
