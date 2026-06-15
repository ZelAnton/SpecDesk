/**
 * Coalesce rapid calls into at most one per animation frame. Scroll events fire far more often
 * than the screen repaints; throttling the scroll-sync handlers to a frame keeps a large
 * document smooth (docs/design/09-ipc-protocol.md: scroll.sync is throttled to an animation frame).
 */
export function rafThrottle(fn: () => void): () => void {
  let scheduled = false;
  return () => {
    if (scheduled) {
      return;
    }
    scheduled = true;
    requestAnimationFrame(() => {
      scheduled = false;
      fn();
    });
  };
}
