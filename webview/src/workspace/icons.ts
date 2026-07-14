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
  // folder — the workspace file navigator
  files: `<path d="M4 6a1 1 0 0 1 1-1h4l2 2h8a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1z"/>`,
  // document — a file row's leading affordance (paired with `files` = folder)
  file: `<path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><polyline points="14 3 14 8 19 8"/>`,
  // clock — recently opened files and folders
  recent: `<circle cx="12" cy="12" r="9"/><polyline points="12 7 12 12 15 14"/>`,
  // star — favorites (fillable via CSS when a row is starred)
  favorites: `<polygon points="12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2"/>`,
  // package/box — registered repositories (deliberately NOT a git-branch glyph; the author never sees git vocabulary)
  repositories: `<path d="M21 8.5v7a1.8 1.8 0 0 1-.9 1.56l-7 4a1.8 1.8 0 0 1-1.8 0l-7-4A1.8 1.8 0 0 1 3 15.5v-7a1.8 1.8 0 0 1 .9-1.56l7-4a1.8 1.8 0 0 1 1.8 0l7 4A1.8 1.8 0 0 1 21 8.5z"/><polyline points="3.3 7.4 12 12.4 20.7 7.4"/><line x1="12" y1="22" x2="12" y2="12.4"/>`,
  // eye/check — reviews waiting for the signed-in user
  review: `<path d="M2.5 12s3.5-6 9.5-6 9.5 6 9.5 6-3.5 6-9.5 6-9.5-6-9.5-6z"/><circle cx="12" cy="12" r="2.5"/><polyline points="15.5 18.5 17.5 20.5 21 17"/>`,
  // converging arrows — pull requests the user authored or joined
  pullRequests: `<circle cx="6" cy="5" r="2"/><circle cx="18" cy="19" r="2"/><path d="M6 7v5a7 7 0 0 0 7 7h3"/><polyline points="13 5 18 5 18 10"/><line x1="18" y1="5" x2="11" y2="12"/>`,
  // sliders — document tools
  tools: `<line x1="4" y1="9" x2="20" y2="9"/><circle cx="8" cy="9" r="2.5" fill="currentColor" stroke="none"/><line x1="4" y1="15" x2="20" y2="15"/><circle cx="16" cy="15" r="2.5" fill="currentColor" stroke="none"/>`,
  // terminal prompt — the log
  log: `<polyline points="6 8 10 12 6 16"/><line x1="12" y1="16" x2="18" y2="16"/>`,
  // speech bubble — a comment
  comment: `<path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>`,
  versions: `<path d="M7 3h10v4H7z"/><path d="M5 7h14v14H5z"/><line x1="8" y1="12" x2="16" y2="12"/><line x1="8" y1="16" x2="14" y2="16"/>`,
  history: `<circle cx="12" cy="12" r="9"/><polyline points="12 7 12 12 16 14"/><path d="M3 5v5h5"/>`,
  createCopy: `<rect x="7" y="7" width="13" height="13" rx="2"/><path d="M4 16H3a1 1 0 0 1-1-1V3a1 1 0 0 1 1-1h12a1 1 0 0 1 1 1v1"/><line x1="13.5" y1="10" x2="13.5" y2="17"/><line x1="10" y1="13.5" x2="17" y2="13.5"/>`,
  createBranch: `<circle cx="6" cy="5" r="2"/><circle cx="6" cy="19" r="2"/><circle cx="18" cy="12" r="2"/><path d="M6 7v10"/><path d="M8 7c6 0 4 5 8 5"/><line x1="18" y1="5" x2="18" y2="9"/><line x1="16" y1="7" x2="20" y2="7"/>`,
  more: `<circle cx="5" cy="12" r="1" fill="currentColor" stroke="none"/><circle cx="12" cy="12" r="1" fill="currentColor" stroke="none"/><circle cx="19" cy="12" r="1" fill="currentColor" stroke="none"/>`,
  delete: `<path d="M3 6h18"/><path d="M8 6V4h8v2"/><path d="M19 6l-1 15H6L5 6"/><line x1="10" y1="10" x2="10" y2="17"/><line x1="14" y1="10" x2="14" y2="17"/>`,
  // dot fallback — an unknown mode
  fallback: `<circle cx="12" cy="12" r="3.5" fill="currentColor" stroke="none"/>`,
};

/** The full inline SVG markup for `name` (falls back to a neutral dot for an unknown name). */
export function icon(name: string): string {
  const body = ICON_BODIES[name] ?? ICON_BODIES.fallback;
  return `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">${body}</svg>`;
}
