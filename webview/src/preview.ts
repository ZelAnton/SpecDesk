/**
 * The native Markdig render sink. Since PoC-12 made Split's right pane the editable WYSIWYG, this
 * pane is **no longer shown** (hidden via CSS); it is kept as the canonical Markdig render — `apply`
 * injects the HTML and indexes the `data-line-*` line map — to back the upcoming diff (PoC-6) and
 * comments (PoC-8). The geometry / scroll / highlight methods below are retained scaffolding for
 * those overlays and are not currently wired (the live editors handle scroll-sync and highlights).
 * (docs/design/05-live-preview.md.)
 */

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
  private onContentResize: (() => void) | undefined;
  private onHover: ((line: number | null) => void) | undefined;

  constructor(el: HTMLElement) {
    this.el = el;
    // Report the source line of the rendered block under the mouse (for the faint hover highlight).
    this.el.addEventListener("mousemove", (event) => {
      const block = (event.target as HTMLElement | null)?.closest<HTMLElement>("[data-line-start]");
      const line = block ? Number(block.getAttribute("data-line-start")) : Number.NaN;
      this.onHover?.(Number.isNaN(line) ? null : line);
    });
    this.el.addEventListener("mouseleave", () => this.onHover?.(null));
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

  /** Register a callback fired when rendered content changes height (e.g. an image finished loading). */
  setOnContentResize(callback: () => void): void {
    this.onContentResize = callback;
  }

  /** Register a callback fired with the 0-based source line of the block under the mouse (or null). */
  setOnHover(callback: (line: number | null) => void): void {
    this.onHover = callback;
  }

  /** Inner width of the preview (its wrapping width) — for diagnostics. */
  contentWidth(): number {
    return this.el.clientWidth;
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

  /** Per-block source-line range plus measured pixel geometry, in document order (for height-sync). */
  blockGeometry(): BlockGeometry[] {
    return this.blocks.map((block) => ({
      lineStart: block.lineStart,
      lineEnd: block.lineEnd,
      top: this.blockTop(block.el),
      height: block.el.getBoundingClientRect().height,
    }));
  }

  /**
   * Distance from the top of the scrolled content to an element's top, in a single coordinate
   * system. `offsetTop` is relative to the offset parent (which differs for table rows vs. block
   * elements), so we use bounding rects relative to the container instead.
   */
  private blockTop(el: HTMLElement): number {
    return el.getBoundingClientRect().top - this.el.getBoundingClientRect().top + this.el.scrollTop;
  }

  /** Highlight the rendered block matching the editor's cursor line (and clear the others). */
  highlightSourceLine(line: number): void {
    const target = blockForLine(this.blocks, line);
    for (const block of this.blocks) {
      block.el.classList.toggle("sd-active-block", block === target);
    }
  }

  /** Faintly highlight the rendered block under the mouse (null clears it). */
  highlightHoverLine(line: number | null): void {
    const target = line === null ? undefined : blockForLine(this.blocks, line);
    for (const block of this.blocks) {
      block.el.classList.toggle("sd-hover-block", target !== undefined && block === target);
    }
  }

  /**
   * Scroll the preview so the rendered block for the given source line aligns at the top. `line`
   * may be fractional (see {@link MarkdownEditor.topVisibleLineExact}); the fractional part is
   * interpolated across the block's height so a partly-scrolled source line maps to the matching
   * point inside its rendered block rather than snapping to the block's top.
   */
  scrollToSourceLine(line: number): void {
    const block = blockForLine(this.blocks, line);
    if (block === undefined) {
      return;
    }
    const span = block.lineEnd - block.lineStart + 1;
    const fraction = Math.min(Math.max((line - block.lineStart) / span, 0), 1);
    this.el.scrollTop =
      this.blockTop(block.el) + fraction * block.el.getBoundingClientRect().height;
  }

  /** Current vertical scroll offset (pixels from content top) — the scroll-map's preview coordinate. */
  scrollTopValue(): number {
    return this.el.scrollTop;
  }

  /**
   * Set the vertical scroll offset directly (pixels). A fractional value is kept (not rounded): the
   * scroll map is deterministic so there is no shimmer, and letting the browser snap to device
   * pixels is smoother than quantizing to whole CSS pixels on HiDPI displays.
   */
  setScrollTop(px: number): void {
    this.el.scrollTop = px;
  }

  /** The 0-based source line at the top of the preview viewport (the inverse of the above). */
  topVisibleSourceLine(): number {
    const scrollTop = this.el.scrollTop;
    let current: PreviewBlock | undefined;
    for (const block of this.blocks) {
      if (this.blockTop(block.el) <= scrollTop) {
        current = block;
      } else {
        break;
      }
    }
    current ??= this.blocks[0];
    if (current === undefined) {
      return 0;
    }
    const height = current.el.getBoundingClientRect().height;
    const span = current.lineEnd - current.lineStart + 1;
    const fraction = height > 0 ? (scrollTop - this.blockTop(current.el)) / height : 0;
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
