import { describe, expect, it } from "vitest";
import {
  clampDockSize,
  collapsedForStartup,
  DEFAULT_DOCKS_STATE,
  DOCK_SIZE_BOUNDS,
  parseDocksState,
  serializeDocksState,
  type WorkspaceDocksState,
} from "../../src/workspace/dock-state.js";

describe("collapsedForStartup", () => {
  it("collapses all panels without forgetting their preferred modes or sizes", () => {
    const state: WorkspaceDocksState = {
      left: { open: true, size: 311, mode: "repositories" },
      right: { open: true, size: 422, mode: "assistant" },
      bottom: { open: true, size: 233, mode: "comment" },
    };

    expect(collapsedForStartup(state)).toEqual({
      left: { open: false, size: 311, mode: "repositories" },
      right: { open: false, size: 422, mode: "assistant" },
      bottom: { open: false, size: 233, mode: "comment" },
    });
    expect(state.left.open).toBe(true);
  });
});

describe("clampDockSize", () => {
  it("clamps to the edge's bounds and rounds to a whole pixel", () => {
    expect(clampDockSize("left", 10)).toBe(DOCK_SIZE_BOUNDS.left.min);
    expect(clampDockSize("left", 99999)).toBe(DOCK_SIZE_BOUNDS.left.max);
    expect(clampDockSize("left", 260.7)).toBe(261);
    expect(clampDockSize("bottom", 200)).toBe(200);
  });

  it("falls back to the edge default for a non-finite size", () => {
    expect(clampDockSize("right", Number.NaN)).toBe(DEFAULT_DOCKS_STATE.right.size);
    expect(clampDockSize("bottom", Number.POSITIVE_INFINITY)).toBe(DEFAULT_DOCKS_STATE.bottom.size);
  });
});

describe("parseDocksState", () => {
  it("returns the full defaults for null (nothing stored)", () => {
    expect(parseDocksState(null)).toEqual(DEFAULT_DOCKS_STATE);
  });

  it("returns the full defaults for corrupt JSON", () => {
    expect(parseDocksState("{not json")).toEqual(DEFAULT_DOCKS_STATE);
  });

  it("round-trips a valid state", () => {
    const state: WorkspaceDocksState = {
      left: { open: true, size: 300, mode: "outline" },
      right: { open: false, size: 400, mode: "tools" },
      bottom: { open: true, size: 260, mode: "comment" },
    };
    expect(parseDocksState(serializeDocksState(state))).toEqual(state);
  });

  it("fills missing edges and fields from the defaults", () => {
    // Only `left` present, and it omits `size`/`mode`.
    const parsed = parseDocksState(JSON.stringify({ left: { open: true } }));
    expect(parsed.left).toEqual({
      open: true,
      size: DEFAULT_DOCKS_STATE.left.size,
      mode: DEFAULT_DOCKS_STATE.left.mode,
    });
    expect(parsed.right).toEqual(DEFAULT_DOCKS_STATE.right);
    expect(parsed.bottom).toEqual(DEFAULT_DOCKS_STATE.bottom);
  });

  it("clamps an out-of-range persisted size and ignores wrong-typed fields", () => {
    const parsed = parseDocksState(
      JSON.stringify({
        left: { open: "yes", size: 99999, mode: 42 },
        right: { open: false, size: 5 },
      }),
    );
    // open wrong type → default; size clamped to max; mode wrong type → default.
    expect(parsed.left.open).toBe(DEFAULT_DOCKS_STATE.left.open);
    expect(parsed.left.size).toBe(DOCK_SIZE_BOUNDS.left.max);
    expect(parsed.left.mode).toBe(DEFAULT_DOCKS_STATE.left.mode);
    // right size below min → clamped up.
    expect(parsed.right.size).toBe(DOCK_SIZE_BOUNDS.right.min);
  });

  it("treats a non-object top level as empty (defaults)", () => {
    expect(parseDocksState("42")).toEqual(DEFAULT_DOCKS_STATE);
    expect(parseDocksState("null")).toEqual(DEFAULT_DOCKS_STATE);
    expect(parseDocksState('"a string"')).toEqual(DEFAULT_DOCKS_STATE);
  });
});
