/**
 * Trailing-edge debounce: coalesce a burst of calls into a single deferred one that runs `ms` after
 * the last call. Each call cancels the previous pending timer and starts a fresh one, so the wrapped
 * `fn` runs once the calls go quiet — used for edit-change notifications and the scroll-settle snap,
 * where only the final state matters, not the intermediate keystrokes/frames. (For per-frame
 * coalescing of high-frequency events, see {@link rafThrottle} in raf.ts instead.)
 */
export function debounce(fn: () => void, ms: number): () => void {
  let timer: ReturnType<typeof setTimeout> | undefined;
  return () => {
    if (timer !== undefined) {
      clearTimeout(timer);
    }
    timer = setTimeout(() => {
      timer = undefined;
      fn();
    }, ms);
  };
}
