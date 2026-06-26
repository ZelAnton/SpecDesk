/** Small DOM-boundary helpers that replace unsafe `EventTarget`→`HTMLElement` casts with runtime checks. */

/**
 * The nearest ancestor `HTMLElement` matching `selector`, starting at an event target — `null` when the
 * target isn't an element or no ancestor matches. `instanceof` does the narrowing, so no cast is needed
 * where an event handler only has an `EventTarget | null`.
 */
export function closestElement(target: EventTarget | null, selector: string): HTMLElement | null {
  if (!(target instanceof Element)) {
    return null;
  }
  const match = target.closest(selector);
  return match instanceof HTMLElement ? match : null;
}
