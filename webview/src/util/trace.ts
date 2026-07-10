/**
 * Always-on diagnostic trace ring for the webview. Every causal step in the editor's hot paths
 * (formatting, mirror, splice, scroll-sync, height-sync, reconcile, render, review, ipc) records
 * one small flat entry here, so when something misbehaves the ring holds WHY it happened — the
 * question the plain `log` channel could never answer because nothing in those subsystems logged.
 *
 * Design constraints (do not regress):
 * - **IPC-free.** The ring lives entirely in the page. It leaves only via `window.__specdeskTrace`
 *   (Playwright / CDP / interactive devtools) or, once wired, an explicit `trace.dump` wire frame —
 *   never one frame per event. This is why it also works headless (no host bridge required).
 * - **Zero serialization on the record path.** `trace()` does one bounds check, one object literal,
 *   and one slot write. No `JSON.stringify`, no `Date`, no `console` — those happen only at
 *   snapshot/dump time. `performance.now()` (cheap) is the only per-record clock read.
 * - **Structural data only.** Entry `data` carries offsets/lengths/counts/verdicts; the few
 *   genuinely diagnostic text fragments go through {@link clip}. Never put document content here.
 */

import { log } from "./log.js";

export type TraceCategory =
  | "format"
  | "mirror"
  | "splice"
  | "scroll"
  | "height"
  | "reconcile"
  | "render"
  | "review"
  | "ipc"
  | "error";

export interface TraceEntry {
  /** Monotonic sequence number; never resets on wrap, so a consumer detects eviction via {@link TraceSnapshot.firstSeq}. */
  seq: number;
  /** `performance.now()` at record time, ms since navigation start. */
  t: number;
  cat: TraceCategory;
  /** Short dotted name, e.g. `"splice.fallback"`. */
  event: string;
  /** Small flat literal (offsets/lengths/verdicts); text only via {@link clip}. */
  data?: Record<string, unknown>;
}

export interface TraceSnapshot {
  /** `Date.now() - performance.now()` at module init: add to an entry's `t` for wall-clock time. */
  t0Epoch: number;
  /** Sequence number of the oldest retained entry (0 until the ring first wraps). */
  firstSeq: number;
  entries: TraceEntry[];
}

/** The in-page read surface exposed as `window.__specdeskTrace` for Playwright/CDP/devtools. */
export interface SpecDeskTraceApi {
  snapshot(): TraceSnapshot;
  get(n: number): TraceEntry[];
  clear(): void;
  mark(label: string): void;
  setVerbose(v: boolean): void;
}

/** The callable trace singleton plus its toggles and read/dump surface. */
export interface TraceFn {
  (cat: TraceCategory, event: string, data?: Record<string, unknown>): void;
  /** Master switch (default true). When false, {@link trace} is a no-op after one boolean check. */
  enabled: boolean;
  /** Gate for the few genuinely per-frame events (scroll couple-skip evaluations); default false. */
  verbose: boolean;
  /** `Date.now() - performance.now()` captured once at module init. */
  t0Epoch: number;
  snapshot(): TraceSnapshot;
  get(n: number): TraceEntry[];
  clear(): void;
  mark(label: string): void;
  setVerbose(v: boolean): void;
}

declare global {
  interface Window {
    __specdeskTrace?: SpecDeskTraceApi;
  }
}

/** Ring capacity. 2000 entries with the hot paths edge-triggered holds minutes of typical editing. */
const RING_SIZE = 2000;

const ring: (TraceEntry | undefined)[] = new Array<TraceEntry | undefined>(RING_SIZE);
let nextSeq = 0;

let enabled = true;
let verbose = false;

const t0Epoch = Date.now() - performance.now();

// Error-forward rate window (see forwardError). `-Infinity` anchors the first window on the first
// error rather than on navigation start, so the initial tumbling window can't end moments after it.
const ERROR_WINDOW_MS = 10_000;
const ERROR_MAX_FRAMES = 10;
let errorWindowStart = Number.NEGATIVE_INFINITY;
let errorFramesInWindow = 0;

function record(cat: TraceCategory, event: string, data?: Record<string, unknown>): void {
  if (!enabled) {
    return;
  }
  const seq = nextSeq++;
  const entry: TraceEntry = { seq, t: performance.now(), cat, event };
  if (data !== undefined) {
    entry.data = data;
  }
  ring[seq % RING_SIZE] = entry;
}

function collect(fromSeq: number): TraceEntry[] {
  const entries: TraceEntry[] = [];
  for (let s = fromSeq; s < nextSeq; s++) {
    const entry = ring[s % RING_SIZE];
    if (entry) {
      entries.push(entry);
    }
  }
  return entries;
}

function oldestRetainedSeq(): number {
  return Math.max(0, nextSeq - RING_SIZE);
}

function snapshot(): TraceSnapshot {
  const firstSeq = oldestRetainedSeq();
  return { t0Epoch, firstSeq, entries: collect(firstSeq) };
}

/** The last `n` entries (or all retained, if fewer), oldest first. */
function getLast(n: number): TraceEntry[] {
  const want = Math.max(0, Math.floor(n));
  return collect(Math.max(oldestRetainedSeq(), nextSeq - want));
}

/** Full reset — a dev/test affordance; normal operation never resets `seq` (it wraps). Also resets the
 *  error-forward rate window so diagnostics start from a clean slate. */
function clear(): void {
  ring.fill(undefined);
  nextSeq = 0;
  errorWindowStart = Number.NEGATIVE_INFINITY;
  errorFramesInWindow = 0;
}

function mark(label: string): void {
  record("ipc", "mark", { label });
}

function setVerbose(v: boolean): void {
  verbose = v;
}

export const trace: TraceFn = Object.assign(record, {
  // `enabled`/`verbose` are accessor-backed (below) so both `trace.verbose` reads and the module
  // `verbose` let stay one source of truth; the placeholders here satisfy the object shape.
  enabled: true,
  verbose: false,
  t0Epoch,
  snapshot,
  get: getLast,
  clear,
  mark,
  setVerbose,
});

Object.defineProperty(trace, "enabled", {
  get: () => enabled,
  set: (v: boolean) => {
    enabled = v;
  },
});
Object.defineProperty(trace, "verbose", {
  get: () => verbose,
  set: (v: boolean) => {
    verbose = v;
  },
});

/** Truncate a diagnostic text fragment for a trace entry — never store full document content. */
export function clip(s: string | undefined, n = 40): string {
  if (s === undefined) {
    return "";
  }
  return s.length <= n ? s : `${s.slice(0, n)}…`;
}

// Global error capture forwards a bounded number of frames to the `log` channel so an error also
// lands in the Serilog file live; the ring records every occurrence regardless (it self-caps by
// eviction). Without the cap, a render-loop throwing every frame would flood IPC.
function forwardError(message: string, data: Record<string, unknown>): void {
  const now = performance.now();
  if (now - errorWindowStart > ERROR_WINDOW_MS) {
    errorWindowStart = now;
    errorFramesInWindow = 0;
  }
  if (errorFramesInWindow < ERROR_MAX_FRAMES) {
    errorFramesInWindow++;
    log.error(message, data);
  }
}

let installed = false;

/**
 * Idempotently expose `window.__specdeskTrace` and install global error capture. Called first thing
 * in `wire()`, so an error during any of the DOM/editor wiring is recorded (module import-time
 * errors in other modules that ran before this still escape). Safe to call in any environment: it
 * touches only `window`/`globalThis` event targets that jsdom and real browsers both provide.
 */
export function installDiagnostics(): void {
  if (installed) {
    return;
  }
  installed = true;

  const api: SpecDeskTraceApi = { snapshot, get: getLast, clear, mark, setVerbose };
  window.__specdeskTrace = api;

  globalThis.addEventListener("error", (event: ErrorEvent) => {
    const data = {
      message: clip(event.message, 200),
      source: event.filename,
      line: event.lineno,
      col: event.colno,
      stack: clip(event.error instanceof Error ? event.error.stack : undefined, 500),
    };
    record("error", "window.onerror", data);
    forwardError("Unhandled error", data);
  });

  globalThis.addEventListener("unhandledrejection", (event: PromiseRejectionEvent) => {
    const reason: unknown = event.reason;
    const data = {
      reason: clip(String(reason), 200),
      stack: clip(reason instanceof Error ? reason.stack : undefined, 500),
    };
    record("error", "unhandledrejection", data);
    forwardError("Unhandled promise rejection", data);
  });
}
