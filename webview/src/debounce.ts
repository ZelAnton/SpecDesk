/** A debounced trigger function, with `pending` exposing whether a deferred call is still outstanding
 *  (a caller edited since the last flush, and the timer hasn't fired yet) — see e.g. the cross-pane
 *  mirror guard in index.ts, which checks this before silently overwriting a pane's content. */
export interface Debounced {
  (): void;
  readonly pending: boolean;
}

/**
 * Trailing-edge debounce: coalesce a burst of calls into a single deferred one that runs `ms` after
 * the last call. Each call cancels the previous pending timer and starts a fresh one, so the wrapped
 * `fn` runs once the calls go quiet — used for edit-change notifications and the scroll-settle snap,
 * where only the final state matters, not the intermediate keystrokes/frames. (For per-frame
 * coalescing of high-frequency events, see {@link rafThrottle} in raf.ts instead.)
 */
export function debounce(fn: () => void, ms: number): Debounced {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const trigger = (): void => {
    if (timer !== undefined) {
      clearTimeout(timer);
    }
    timer = setTimeout(() => {
      timer = undefined;
      fn();
    }, ms);
  };
  return Object.defineProperty(trigger, "pending", {
    get: () => timer !== undefined,
    enumerable: true,
  }) as Debounced;
}
