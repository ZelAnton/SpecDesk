/** A debounced trigger function, with `pending` exposing whether a deferred call is still outstanding
 *  (a caller edited since the last flush, and the timer hasn't fired yet) — see e.g. the cross-pane
 *  mirror guard in index.ts, which checks this before silently overwriting a pane's content. */
export interface Debounced {
  (): void;
  readonly pending: boolean;
  readonly pendingOrder: number | null;
  flush(): boolean;
  cancel(): boolean;
}

let nextPendingOrder = 0;

/**
 * Trailing-edge debounce: coalesce a burst of calls into a single deferred one that runs `ms` after
 * the last call. Each call cancels the previous pending timer and starts a fresh one, so the wrapped
 * `fn` runs once the calls go quiet — used for edit-change notifications and the scroll-settle snap,
 * where only the final state matters, not the intermediate keystrokes/frames. (For per-frame
 * coalescing of high-frequency events, see {@link rafThrottle} in raf.ts instead.)
 */
export function debounce(fn: () => void, ms: number): Debounced {
  let timer: ReturnType<typeof setTimeout> | undefined;
  let pendingOrder: number | null = null;
  const trigger = (): void => {
    if (timer !== undefined) {
      clearTimeout(timer);
    }
    pendingOrder = ++nextPendingOrder;
    timer = setTimeout(() => {
      timer = undefined;
      pendingOrder = null;
      fn();
    }, ms);
  };
  return Object.defineProperties(trigger, {
    pending: {
      get: () => timer !== undefined,
      enumerable: true,
    },
    pendingOrder: {
      get: () => pendingOrder,
      enumerable: true,
    },
    flush: {
      value: (): boolean => {
        if (timer === undefined) {
          return false;
        }
        clearTimeout(timer);
        timer = undefined;
        pendingOrder = null;
        fn();
        return true;
      },
      enumerable: true,
    },
    cancel: {
      value: (): boolean => {
        if (timer === undefined) {
          return false;
        }
        clearTimeout(timer);
        timer = undefined;
        pendingOrder = null;
        return true;
      },
      enumerable: true,
    },
  }) as Debounced;
}
