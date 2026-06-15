/**
 * The rendered-preview pane. It injects the HTML produced natively by Markdig, indexes the
 * `data-line-start`/`data-line-end` attributes into a line map, and uses that map for scroll-sync
 * (docs/design/05-live-preview.md). The container element is the scroll container.
 */

/** A rendered top-level block plus its 0-based, inclusive source line range. */
export interface PreviewBlock {
  el: HTMLElement;
  lineStart: number;
  lineEnd: number;
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
 * blocks) — the last block that starts at or before it. Pure and layout-free, so it is
 * unit-tested without a DOM.
 */
export function blockForLine(blocks: PreviewBlock[], line: number): PreviewBlock | undefined {
  let candidate: PreviewBlock | undefined;
  for (const block of blocks) {
    if (line >= block.lineStart && line <= block.lineEnd) {
      return block;
    }
    if (line >= block.lineStart) {
      candidate = block;
    }
  }
  return candidate ?? blocks[0];
}

export class Preview {
  private readonly el: HTMLElement;
  private lastVersion = -1;
  private blocks: PreviewBlock[] = [];

  constructor(el: HTMLElement) {
    this.el = el;
    // Links in the preview must never navigate the app away (the whole webview would be
    // replaced), and a `javascript:` URL must never execute. Swallow anchor clicks; opening
    // links externally is a later concern. This also blocks the `javascript:` scheme that
    // Markdig does not sanitize. (TODO(PoC-3+): open http(s) links in the OS browser.)
    this.el.addEventListener("click", (event) => {
      if ((event.target as HTMLElement | null)?.closest("a") !== null) {
        event.preventDefault();
      }
    });
  }

  /** Inject a rendered result, dropping it if a newer one was already applied. Returns whether applied. */
  apply(html: string, version: number): boolean {
    if (!isFresh(this.lastVersion, version)) {
      return false;
    }
    this.lastVersion = version;
    this.el.innerHTML = html;
    this.indexBlocks();
    return true;
  }

  /** Scroll the preview so the rendered block for the given 0-based source line aligns at the top. */
  scrollToSourceLine(line: number): void {
    const block = blockForLine(this.blocks, line);
    if (block === undefined) {
      return;
    }
    const span = block.lineEnd - block.lineStart + 1;
    const fraction = span > 1 ? (line - block.lineStart) / span : 0;
    this.el.scrollTop = block.el.offsetTop + fraction * block.el.offsetHeight;
  }

  /** The 0-based source line at the top of the preview viewport (the inverse of the above). */
  topVisibleSourceLine(): number {
    const scrollTop = this.el.scrollTop;
    let current: PreviewBlock | undefined;
    for (const block of this.blocks) {
      if (block.el.offsetTop <= scrollTop) {
        current = block;
      } else {
        break;
      }
    }
    current ??= this.blocks[0];
    if (current === undefined) {
      return 0;
    }
    const height = current.el.offsetHeight;
    const span = current.lineEnd - current.lineStart + 1;
    const fraction = height > 0 ? (scrollTop - current.el.offsetTop) / height : 0;
    return current.lineStart + Math.floor(fraction * span);
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
