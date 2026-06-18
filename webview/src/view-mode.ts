/**
 * The three editor view modes (PoC-11): `code` (source only), `split` (source + rendered preview,
 * the default), and `formatted` (rendered preview only — read-only for now; WYSIWYG editing is
 * PoC-12). The functions here are pure layout policy — which panes a mode shows and which visible
 * pane owns the scroll position — so they are unit-tested directly; the DOM wiring lives in index.ts.
 * `paneVisibility` is the single source of truth; the others derive from it so they cannot disagree.
 * See docs/design/05-live-preview.md and docs/ROADMAP.md (editor track).
 */

export type ViewMode = "code" | "split" | "formatted";

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

/**
 * The visible pane whose scroll position is authoritative when leaving a mode. We capture the top
 * source line from it before a switch and restore that line in the new mode, so the reading
 * position survives the width reflow. The editor wins whenever it is shown (code/split); only
 * formatted reads from the preview.
 */
export function scrollAuthority(mode: ViewMode): "editor" | "preview" {
  return paneVisibility(mode).editor ? "editor" : "preview";
}

/** Split is the only mode where both panes are visible, so height-sync and scroll-sync apply. */
export function isSplit(mode: ViewMode): boolean {
  const { editor, preview } = paneVisibility(mode);
  return editor && preview;
}
