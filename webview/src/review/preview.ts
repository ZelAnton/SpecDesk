/**
 * The native Markdig render sink. Since PoC-12 made Split's right pane the editable WYSIWYG, this
 * pane is **no longer shown** (hidden via CSS) and the host no longer renders/sends `preview.html` on
 * every edit either (see HostController.Session.cs' `OnEditorChanged`) — there is currently no
 * consumer, visible or otherwise. `apply` (inject the HTML, index the `data-line-*` line map) and the
 * constructor's link handling are kept as the ready sink for the day a real consumer — diff (PoC-6) or
 * comments (PoC-8) — needs it; a prior scroll/highlight/geometry scaffold that duplicated what the live
 * editors (MarkdownEditor/FormattedEditor) already do for the visible panes was removed as dead code
 * rather than kept unwired — see T-089. (docs/design/05-live-preview.md.)
 */

import { lastIndexAtOrBefore } from "../editors/block-map.js";
import { closestElement } from "../util/dom.js";
import { isOpenableHref } from "../util/links.js";

/** A rendered top-level block plus its 0-based, inclusive source line range. */
export interface PreviewBlock {
  el: HTMLElement;
  lineStart: number;
  lineEnd: number;
}

/** A rendered block's source line range plus its measured pixel geometry (for height-sync). */
export interface BlockGeometry {
  lineStart: number;
  lineEnd: number;
  top: number;
  height: number;
  /** The container instances (table/list) this leaf belongs to, outermost first (see
   *  {@link LeafAnchor.containers} in sync-anchors.ts) — lets height-sync group a container's rows/items
   *  for its container-tail floor. Absent for a source with no per-row anchor projection (this preview)
   *  and for top-level leaves. */
  containers?: readonly string[];
}

/**
 * Whether a `preview.html` result is fresh enough to apply: a result whose version is older than
 * the last one applied is stale and must be dropped (pure, so it is unit-tested directly).
 */
export function isFresh(lastAppliedVersion: number, version: number): boolean {
  return version >= lastAppliedVersion;
}

/**
 * The block whose source range contains `line`, or — when none does (e.g. a blank line between
 * blocks) — the last block that starts at or before it; the first block for a line before them all.
 * Pure and layout-free, so it is unit-tested without a DOM. Uses the shared block-by-line search
 * ({@link lastIndexAtOrBefore}) so this pane resolves a line to a block the same single way the
 * formatted editor's block-map does — for an ascending, ordered partition the last block starting at
 * or before the line IS the containing block when the line falls in its range.
 */
export function blockForLine(blocks: PreviewBlock[], line: number): PreviewBlock | undefined {
  const index = lastIndexAtOrBefore(
    blocks.map((block) => block.lineStart),
    line,
  );
  return index < 0 ? blocks[0] : blocks[index];
}

export class Preview {
  private readonly el: HTMLElement;
  private lastVersion = -1;
  private blocks: PreviewBlock[] = [];
  private onContentResize: (() => void) | undefined;
  private onHover: ((line: number | null) => void) | undefined;
  private onOpenLink: ((url: string) => void) | undefined;

  constructor(el: HTMLElement) {
    this.el = el;
    // Report the source line of the rendered block under the mouse (for the faint hover highlight).
    this.el.addEventListener("mousemove", (event) => {
      const block = closestElement(event.target, "[data-line-start]");
      const line = block ? Number(block.getAttribute("data-line-start")) : Number.NaN;
      this.onHover?.(Number.isNaN(line) ? null : line);
    });
    this.el.addEventListener("mouseleave", () => this.onHover?.(null));
    // Links in the preview must never navigate the app away (the whole webview would be replaced),
    // and a `javascript:` URL must never execute. Swallow the in-webview navigation; a real web link
    // (http/https) is instead handed to the host to open in the OS browser. Any other scheme (incl.
    // the `javascript:` Markdig does not sanitize, or repo-relative links) is simply ignored.
    this.el.addEventListener("click", (event) => {
      const anchor = closestElement(event.target, "a");
      if (!anchor) {
        return;
      }
      event.preventDefault();
      const href = anchor.getAttribute("href")?.trim() ?? "";
      if (isOpenableHref(href)) {
        this.onOpenLink?.(href);
      }
    });
  }

  /** Register a callback fired when rendered content changes height (e.g. an image finished loading). */
  setOnContentResize(callback: () => void): void {
    this.onContentResize = callback;
  }

  /** Register a callback fired with the 0-based source line of the block under the mouse (or null). */
  setOnHover(callback: (line: number | null) => void): void {
    this.onHover = callback;
  }

  /** Register a callback fired with an http/https URL the author clicked, to open in the OS browser. */
  setOnOpenLink(callback: (url: string) => void): void {
    this.onOpenLink = callback;
  }

  /** Inject a rendered result, dropping it if a newer one was already applied. Returns whether applied. */
  apply(html: string, version: number): boolean {
    if (!isFresh(this.lastVersion, version)) {
      return false;
    }
    this.lastVersion = version;
    this.el.innerHTML = html;
    this.indexBlocks();
    // Images change block heights only once they decode; re-sync heights on each load.
    for (const img of this.el.querySelectorAll("img")) {
      img.addEventListener("load", () => this.onContentResize?.(), { once: true });
    }
    return true;
  }

  private indexBlocks(): void {
    this.blocks = [];
    const elements = this.el.querySelectorAll<HTMLElement>("[data-line-start]");
    for (const el of elements) {
      const lineStart = Number(el.getAttribute("data-line-start"));
      const lineEnd = Number(el.getAttribute("data-line-end"));
      if (!Number.isNaN(lineStart) && !Number.isNaN(lineEnd)) {
        this.blocks.push({ el, lineStart, lineEnd });
      }
    }
  }
}
