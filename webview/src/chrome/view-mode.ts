/**
 * The three editor view modes (PoC-11): `code` (source only), `split` (source editor + formatted
 * WYSIWYG, the default), and `formatted` (WYSIWYG only). The functions here are pure layout policy —
 * which panes a mode shows — so they are unit-tested directly; the DOM wiring lives in index.ts.
 * `paneVisibility` is the single source of truth; `isSplit` derives from it so they cannot disagree.
 * See docs/design/05-live-preview.md and docs/ROADMAP.md (editor track).
 */

export type ViewMode = "code" | "split" | "formatted";

const VIEW_MODES: readonly ViewMode[] = ["code", "split", "formatted"];

/**
 * Type guard for a `ViewMode` literal, e.g. a `data-mode` DOM attribute read back at startup. Lets the
 * DOM stay the single declared source of truth for the initial mode instead of a matching TS literal.
 */
export function isViewMode(value: string | null | undefined): value is ViewMode {
  return value !== null && value !== undefined && (VIEW_MODES as readonly string[]).includes(value);
}

/**
 * Which panes are visible in a mode. The CSS (`#panes[data-mode=…]`) hides the rest; the panes are
 * never destroyed. This is the authoritative policy the two helpers below build on.
 */
export function paneVisibility(mode: ViewMode): { editor: boolean; preview: boolean } {
  switch (mode) {
    case "code":
      return { editor: true, preview: false };
    case "formatted":
      return { editor: false, preview: true };
    default:
      return { editor: true, preview: true };
  }
}

/** Split is the only mode where both panes are visible, so height-sync and scroll-sync apply. */
export function isSplit(mode: ViewMode): boolean {
  const { editor, preview } = paneVisibility(mode);
  return editor && preview;
}
