/**
 * Pure state + persistence codec for the workspace docks (workspace/dock.ts). Kept DOM-free so the size
 * clamping and the tolerant parse/serialize round-trip are unit-tested directly; the localStorage IO lives
 * in dock-store.ts and the DOM wiring in dock.ts. A persisted value is untrusted input (a stale schema, a
 * hand-edited or corrupt entry), so parsing NEVER throws and always yields a complete, in-range state —
 * a bad value can degrade to the defaults but can never brick the layout.
 */

/** The three docks, by the window edge each sits on. A side rail is sized by width, the bottom by height. */
export type DockEdge = "left" | "right" | "bottom";

export const DOCK_EDGES: readonly DockEdge[] = ["left", "right", "bottom"];

/** One dock's persisted state: open/collapsed, its size in px (width for the side rails, height for the
 *  bottom dock), and which mode (tool id) is active. */
export interface DockState {
  open: boolean;
  size: number;
  mode: string;
}

export type WorkspaceDocksState = Record<DockEdge, DockState>;

/** Size bounds per edge (px). The max is a sanity guard, not a limit the user hits often — the effective
 *  cap is that the centre keeps a usable width via its own min-width:0 plus the window size. */
export interface SizeBounds {
  min: number;
  max: number;
}

export const DOCK_SIZE_BOUNDS: Record<DockEdge, SizeBounds> = {
  left: { min: 180, max: 560 },
  right: { min: 220, max: 640 },
  bottom: { min: 120, max: 520 },
};

/** Starting state: all docks collapsed (progressive disclosure — the document owns the room until the
 *  author opens a panel). The default sizes sit within each edge's bounds. The default modes are the id of
 *  each dock's first tool; dock.ts re-validates a persisted mode against the tools it is actually given. */
export const DEFAULT_DOCKS_STATE: WorkspaceDocksState = {
  left: { open: false, size: 260, mode: "navigator" },
  right: { open: false, size: 320, mode: "assistant" },
  bottom: { open: false, size: 200, mode: "log" },
};

/** The localStorage key holding the serialized {@link WorkspaceDocksState}; versioned so a future schema
 *  change can bump it and start clean rather than mis-reading an old shape. */
export const DOCKS_STORAGE_KEY = "specdesk.docks.v1";

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

/** Clamp a size to the edge's bounds, falling back to the default when it is not a finite number. Rounds to
 *  a whole pixel (a drag produces fractional px; the persisted value stays tidy and the applied width is
 *  device-pixel-crisp). */
export function clampDockSize(edge: DockEdge, size: number): number {
  const { min, max } = DOCK_SIZE_BOUNDS[edge];
  if (!Number.isFinite(size)) {
    return DEFAULT_DOCKS_STATE[edge].size;
  }
  return Math.min(max, Math.max(min, Math.round(size)));
}

function readDockState(parsed: unknown, edge: DockEdge): DockState {
  const def = DEFAULT_DOCKS_STATE[edge];
  const raw = isRecord(parsed) ? parsed[edge] : undefined;
  if (!isRecord(raw)) {
    return { ...def };
  }
  return {
    open: typeof raw.open === "boolean" ? raw.open : def.open,
    size: clampDockSize(edge, typeof raw.size === "number" ? raw.size : Number.NaN),
    // The mode is validated for existence against the actual tools in dock.ts (which alone knows them);
    // here it only has to be a non-empty string, else fall back to the edge's default.
    mode: typeof raw.mode === "string" && raw.mode.length > 0 ? raw.mode : def.mode,
  };
}

/** Serialize the docks state for persistence. */
export function serializeDocksState(state: WorkspaceDocksState): string {
  return JSON.stringify(state);
}

/**
 * Parse persisted docks state, tolerating any malformed / partial / out-of-range input by falling back to
 * the per-edge defaults field by field. `null` (nothing stored, or an unreadable store) yields the full
 * defaults. The result always has all three edges with in-range sizes.
 */
export function parseDocksState(raw: string | null): WorkspaceDocksState {
  let parsed: unknown = null;
  if (raw !== null) {
    try {
      parsed = JSON.parse(raw);
    } catch {
      // A corrupt entry (truncated write, hand-edit) is treated as absent — defaults below.
      parsed = null;
    }
  }
  return {
    left: readDockState(parsed, "left"),
    right: readDockState(parsed, "right"),
    bottom: readDockState(parsed, "bottom"),
  };
}
