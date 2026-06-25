/**
 * Whether an anchor href is one the host can open externally: a web page (http/https) or an email
 * address (mailto). The host re-validates the scheme before opening — this is just the webview-side
 * filter for which anchor clicks to forward rather than silently swallow.
 */
export function isOpenableHref(href: string): boolean {
  return /^(?:https?:\/\/|mailto:)/i.test(href.trim());
}

/**
 * Find the http/https URL the given 0-based column falls within on a line of Markdown source, or
 * null. Used by the source editor's Ctrl/Cmd-click to open links (the host re-validates the scheme).
 *
 * The match runs to the first whitespace or Markdown delimiter (`)`, `<`, `>`, `]`), so it handles
 * inline links `[t](url)`, autolinks `<url>`, reference defs `[id]: url`, and bare URLs alike; a
 * trailing sentence punctuation mark is trimmed. Balanced parens inside a bare URL are not handled
 * (rare); the common inline-link case is exact.
 */
export function urlAtColumn(lineText: string, col: number): string | null {
  const pattern = /https?:\/\/[^\s)<>\]]+/g;
  for (let match = pattern.exec(lineText); match !== null; match = pattern.exec(lineText)) {
    const start = match.index;
    const end = start + match[0].length;
    if (col >= start && col <= end) {
      return match[0].replace(/[.,;:!?]+$/, "");
    }
  }
  return null;
}
