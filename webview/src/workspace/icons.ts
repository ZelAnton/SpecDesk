/**
 * A small inline SVG icon set (Lucide-style hand-authored glyphs: a 24-unit viewBox, round caps/joins,
 * currentColor — so icons inherit text colour and theme automatically). Used by the dock mode rail. The
 * 1.75 stroke in the 24 grid renders ≈1.5px at the rail's 20px display size, matching design concept §11
 * (Lucide, ~1.5px stroke, currentColor, tooltip + aria-label on the button). Kept as trusted in-repo markup
 * so it can be set via innerHTML on the icon buttons; never interpolate untrusted input here.
 */

const ICON_BODIES: Record<string, string> = {
  // compass — the navigator (moving between views)
  navigator: `<circle cx="12" cy="12" r="9"/><polygon points="15.6 8.4 13.2 13.2 8.4 15.6 10.8 10.8"/>`,
  // stacked text lines — a document outline
  outline: `<line x1="5" y1="6" x2="17" y2="6"/><line x1="5" y1="12" x2="19" y2="12"/><line x1="5" y1="18" x2="14" y2="18"/>`,
  // sparkle — the AI assistant
  assistant: `<path d="M12 4 L13.5 10.5 L20 12 L13.5 13.5 L12 20 L10.5 13.5 L4 12 L10.5 10.5 Z"/>`,
  // sliders — document tools
  tools: `<line x1="4" y1="9" x2="20" y2="9"/><circle cx="8" cy="9" r="2.5" fill="currentColor" stroke="none"/><line x1="4" y1="15" x2="20" y2="15"/><circle cx="16" cy="15" r="2.5" fill="currentColor" stroke="none"/>`,
  // terminal prompt — the log
  log: `<polyline points="6 8 10 12 6 16"/><line x1="12" y1="16" x2="18" y2="16"/>`,
  // speech bubble — a comment
  comment: `<path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>`,
  // dot fallback — an unknown mode
  fallback: `<circle cx="12" cy="12" r="3.5" fill="currentColor" stroke="none"/>`,
};

/** The full inline SVG markup for `name` (falls back to a neutral dot for an unknown name). */
export function icon(name: string): string {
  const body = ICON_BODIES[name] ?? ICON_BODIES.fallback;
  return `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">${body}</svg>`;
}
