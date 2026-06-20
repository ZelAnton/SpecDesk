/**
 * The formatted (WYSIWYG) editor surface — PoC-12. A ProseMirror view over the Markdown document;
 * edits serialize back to Markdown via block-splice (md-splice.ts) so the file changes only where the
 * author actually edited. Markdig stays canonical for the read-only render, diff and comments
 * (docs/design/05-live-preview.md) — this surface only turns formatted edits into Markdown text, which
 * then flows through the same native pipeline as a source edit.
 *
 * Deliberately thin and shaped like {@link MarkdownEditor}: setText / getText / setEditable plus the
 * scroll helpers mode-switching needs. The schema/parser/serializer (incl. GFM tables) live in
 * pm-markdown.ts, shared with the block-splice serializer so their top-level node counts agree.
 */

import { baseKeymap } from "prosemirror-commands";
import { history, redo, undo } from "prosemirror-history";
import { keymap } from "prosemirror-keymap";
import type { Node as PmNode, ResolvedPos } from "prosemirror-model";
import { EditorState, Plugin, PluginKey } from "prosemirror-state";
import { Decoration, DecorationSet, EditorView } from "prosemirror-view";
import { type MdBlock, splitTopLevelBlocks } from "./md-blocks.js";
import { serializeWithSplice } from "./md-splice.js";
import { parser, resolveImageSrc, schema } from "./pm-markdown.js";
import type { BlockGeometry } from "./preview.js";
import { rafThrottle } from "./raf.js";

const DEBOUNCE_MS = 120;
const emptyDoc = (): PmNode => schema.node("doc", null, [schema.node("paragraph")]);

// Highlight the node holding the caret (active) and the one under the mouse (hover) — the formatted
// equivalent of the source editor's active-/hover-line highlights. Both are driven externally
// (setActiveLine / setHoverLine via index.ts), so in Split they stay in step with the source editor.
// The highlighted node is the top-level block, EXCEPT inside a table or list, where it is the row /
// item the line maps to (so the caret highlights one row, not the whole table). The decorations are
// pushed in as a ready-made set (the editor resolves the node ranges, since it owns the block map)
// and mapped through edits so they never reference a stale position.
const highlightKey = new PluginKey<DecorationSet>("sd-formatted-highlight");

/** [from, to] document positions of the top-level child at `index`. */
function blockRange(doc: PmNode, index: number): [number, number] {
  let pos = 0;
  for (let i = 0; i < index; i++) {
    pos += doc.child(i).nodeSize;
  }
  return [pos, pos + doc.child(index).nodeSize];
}

const highlightPlugin = new Plugin<DecorationSet>({
  key: highlightKey,
  state: {
    init: () => DecorationSet.empty,
    apply: (tr, set) =>
      (tr.getMeta(highlightKey) as DecorationSet | undefined) ?? set.map(tr.mapping, tr.doc),
  },
  props: {
    decorations: (state) => highlightKey.getState(state) ?? DecorationSet.empty,
  },
});

export interface FormattedEditorCallbacks {
  /** Fired ~120 ms after an edit, with the block-spliced Markdown of the current document. */
  onChange: (text: string) => void;
  /** Fired when the author tries to edit while the document is read-only (offer to start a draft). */
  onEditAttempt: () => void;
  /** Fired (rAF-throttled) as the pane scrolls — drives block-level scroll-sync with the source editor. */
  onScroll: () => void;
  /** Fired (rAF-throttled) with the caret block's 0-based source line, for cross-pane highlight sync. */
  onCursor: (line: number | null) => void;
  /** Fired (rAF-throttled) with the 0-based source line under the mouse (null outside), for hover sync. */
  onHover: (line: number | null) => void;
  /** Fired when rendered content changes height (e.g. an image finished decoding) — re-run height-sync. */
  onContentResize: () => void;
}

export class FormattedEditor {
  private readonly view: EditorView;
  private readonly scrollEl: HTMLElement;
  private readonly onChange: (text: string) => void;
  private readonly onEditAttempt: () => void;
  private readonly onScroll: () => void;
  private readonly onCursor: (line: number | null) => void;
  private readonly onHover: (line: number | null) => void;
  private readonly onContentResize: () => void;
  // The Markdown this document was last loaded from — the baseline block-splice diffs against.
  private original = "";
  // The open document's directory relative to the repo root, used to resolve relative image links to
  // `app://repo/…` for display (the image node keeps its original relative src for serialization).
  private docDir = "";
  // The top-level block split of `original`, cached so the per-frame highlight/scroll sync doesn't
  // re-parse the whole document on every caret/mouse move. Refreshed only when `original` changes.
  private blocks: MdBlock[] = [];
  private editable = false;
  private timer: ReturnType<typeof setTimeout> | undefined;
  // The synced active/hover source lines (driven externally), remembered so they can be re-applied
  // after setText rebuilds the document (which resets the highlight plugin's state).
  private activeLine: number | null = null;
  private hoverLine: number | null = null;
  // rAF-throttled caret reporter; assigned in the constructor (needs `this.view`).
  private readonly reportCaret: () => void;

  constructor(parent: HTMLElement, callbacks: FormattedEditorCallbacks) {
    this.scrollEl = parent;
    this.onChange = callbacks.onChange;
    this.onEditAttempt = callbacks.onEditAttempt;
    this.onScroll = callbacks.onScroll;
    this.onCursor = callbacks.onCursor;
    this.onHover = callbacks.onHover;
    this.onContentResize = callbacks.onContentResize;
    this.view = new EditorView(parent, {
      state: this.freshState(emptyDoc()),
      // The view stays contentEditable so the caret/selection work and a typing attempt is detectable
      // even while read-only; document edits themselves are gated by the filterTransaction plugin in
      // freshState (which offers to start a draft). This mirrors the source editor's read-only model.
      // Tag the content element so it shares the rendered-document stylesheet (§5) with the preview.
      attributes: { class: "sd-doc" },
      // Render images with their relative src resolved to `app://repo/…` (the node keeps the original
      // relative src, so serialization is unaffected). Without this the WYSIWYG <img> would resolve
      // against the webview's own app:// base and fail to load — only the native preview rewrote it.
      nodeViews: {
        image: (node) => {
          const dom = document.createElement("img");
          dom.src = resolveImageSrc(this.docDir, String(node.attrs.src ?? ""));
          if (node.attrs.alt) {
            dom.alt = String(node.attrs.alt);
          }
          if (node.attrs.title) {
            dom.title = String(node.attrs.title);
          }
          // An image grows from nothing to its decoded size, shifting the blocks below — re-run
          // height-sync once it loads (same as the preview did).
          dom.addEventListener("load", () => this.onContentResize(), { once: true });
          return { dom };
        },
      },
      dispatchTransaction: (tr) => {
        const next = this.view.state.apply(tr);
        // Use the post-filter doc: a transaction blocked while read-only leaves the doc unchanged.
        const changed = !next.doc.eq(this.view.state.doc);
        const selectionMoved = tr.selectionSet || changed;
        this.view.updateState(next);
        if (changed) {
          this.scheduleChange();
        }
        if (selectionMoved) {
          this.reportCaret();
        }
      },
    });

    // Anchor clicks must never navigate the webview away (it would replace the whole app), and a
    // `javascript:` URL must never run. Swallow them — the same guard the read-only preview uses.
    // Essential in read-only formatted mode, where the content is not editable and a link would
    // otherwise follow its href. (TODO: open http(s) links in the OS browser, like the preview.)
    this.view.dom.addEventListener("click", (event) => {
      if ((event.target as HTMLElement | null)?.closest("a") !== null) {
        event.preventDefault();
      }
    });

    // The pane is the scroll container; report scrolls (throttled) for block-level scroll-sync.
    const reportScroll = rafThrottle(() => this.onScroll());
    this.scrollEl.addEventListener("scroll", reportScroll);

    // Report the caret's block as a source line for cross-pane highlight sync (index.ts pushes the
    // decoration back via setActiveLine). Deferred (rAF) so the resulting dispatch runs outside the
    // originating transaction.
    this.reportCaret = rafThrottle(() => {
      this.onCursor(this.sourceLineForPos(this.view.state.selection.$head));
    });

    // Report the block under the mouse as a source line for cross-pane hover sync (index.ts pushes the
    // decoration back via setHoverLine).
    let hoverX = 0;
    let hoverY = 0;
    const reportHover = rafThrottle(() => {
      const at = this.view.posAtCoords({ left: hoverX, top: hoverY });
      if (at === null) {
        this.onHover(null);
        return;
      }
      this.onHover(this.sourceLineForPos(this.view.state.doc.resolve(at.pos)));
    });
    this.view.dom.addEventListener("mousemove", (event) => {
      hoverX = event.clientX;
      hoverY = event.clientY;
      reportHover();
    });
    this.view.dom.addEventListener("mouseleave", () => this.onHover(null));
  }

  private freshState(doc: PmNode): EditorState {
    return EditorState.create({
      doc,
      plugins: [
        history(),
        keymap({ "Mod-z": undo, "Mod-y": redo, "Shift-Mod-z": redo }),
        keymap(baseKeymap),
        highlightPlugin,
        // Read-only gate: while not in a draft, block document edits and offer to start one — the
        // formatted-mode parity of the source editor's "type in a read-only doc → start a draft".
        // Selection-only transactions pass through, so the caret still works.
        new Plugin({
          filterTransaction: (tr) => {
            if (tr.docChanged && !this.editable) {
              this.onEditAttempt();
              return false;
            }
            return true;
          },
        }),
      ],
    });
  }

  /** Set the document directory (repo-relative) used to resolve relative image links. Call before
   *  {@link setText} so the image node views render with the right `app://repo/…` src. */
  setDocDir(dir: string): void {
    this.docDir = dir;
  }

  /** Replace the document from Markdown (on load and on switching into formatted mode). */
  setText(md: string): void {
    this.original = md;
    this.blocks = splitTopLevelBlocks(md);
    this.view.updateState(this.freshState(parser.parse(md) ?? emptyDoc()));
    // freshState reset the highlight plugin's state; re-apply the synced active/hover blocks.
    this.pushHighlights();
  }

  /** Map a 0-based source line to the index of the top-level block that contains it. */
  private blockIndexForLine(line: number | null): number | null {
    if (line === null) {
      return null;
    }
    let index: number | null = this.blocks.length > 0 ? 0 : null;
    for (let i = 0; i < this.blocks.length; i++) {
      const block = this.blocks[i];
      if (block !== undefined && line >= block.lineStart) {
        index = i;
      }
    }
    return index;
  }

  /**
   * [from, to] of the node to highlight for a source line: the top-level block, or — inside a table /
   * list — the row / item the line falls in (so the caret highlights one row, not the whole table).
   * Positions are computed against the current document so they are always valid.
   */
  private nodeRangeForLine(line: number | null): [number, number] | null {
    if (line === null) {
      return null;
    }
    const topIndex = this.blockIndexForLine(line);
    const doc = this.view.state.doc;
    if (topIndex === null || topIndex < 0 || topIndex >= doc.childCount) {
      return null;
    }
    const [blockFrom, blockTo] = blockRange(doc, topIndex);
    const block = doc.child(topIndex);
    const childStarts = this.blocks[topIndex]?.childLineStarts;
    if (childStarts === undefined || childStarts.length === 0 || block.childCount === 0) {
      return [blockFrom, blockTo];
    }
    let childIndex = 0;
    for (let i = 0; i < childStarts.length; i++) {
      if (line >= (childStarts[i] ?? Number.POSITIVE_INFINITY)) {
        childIndex = i;
      }
    }
    childIndex = Math.min(childIndex, block.childCount - 1);
    let childPos = blockFrom + 1; // step inside the container node
    for (let i = 0; i < childIndex; i++) {
      childPos += block.child(i).nodeSize;
    }
    return [childPos, childPos + block.child(childIndex).nodeSize];
  }

  /** The source line a resolved position maps to: the row/item line inside a table/list, else the
   *  containing top-level block's line. The inverse of {@link nodeRangeForLine}, for caret/hover reports. */
  private sourceLineForPos($pos: ResolvedPos): number | null {
    const block = this.blocks[$pos.index(0)];
    if (block === undefined) {
      return null;
    }
    if (block.childLineStarts !== undefined && $pos.depth >= 1) {
      return block.childLineStarts[$pos.index(1)] ?? block.lineStart;
    }
    return block.lineStart;
  }

  /** Build and push the active/hover decorations from the synced source lines (resolved to nodes). */
  private pushHighlights(): void {
    const doc = this.view.state.doc;
    const decos: Decoration[] = [];
    const active = this.nodeRangeForLine(this.activeLine);
    if (active !== null) {
      decos.push(Decoration.node(active[0], active[1], { class: "sd-active-block" }));
    }
    const hover = this.nodeRangeForLine(this.hoverLine);
    if (hover !== null && !(active !== null && hover[0] === active[0] && hover[1] === active[1])) {
      decos.push(Decoration.node(hover[0], hover[1], { class: "sd-hover-block" }));
    }
    this.view.dispatch(this.view.state.tr.setMeta(highlightKey, DecorationSet.create(doc, decos)));
  }

  /** Highlight the node for the given source line — the table row / list item inside a table/list,
   *  else the top-level block — as the caret line; null clears it. */
  setActiveLine(line: number | null): void {
    this.activeLine = line;
    this.pushHighlights();
  }

  /** Highlight the node for the given hovered source line (row/item inside a table/list, else the
   *  top-level block); null clears it. */
  setHoverLine(line: number | null): void {
    this.hoverLine = line;
    this.pushHighlights();
  }

  /** The current document serialized back to Markdown, minimal-diff against the last {@link setText}. */
  getText(): string {
    return serializeWithSplice(this.original, this.view.state.doc);
  }

  /**
   * Allow or block document edits. Read by the filterTransaction gate; the view stays contentEditable
   * either way (so the caret works and a read-only typing attempt can offer a draft).
   */
  setEditable(enabled: boolean): void {
    this.editable = enabled;
  }

  /** Force a re-render after the pane returns from display:none to a new width. */
  refresh(): void {
    this.view.updateState(this.view.state);
  }

  /** Inner width of the pane (its wrapping width) — for height-sync diagnostics. */
  contentWidth(): number {
    return this.scrollEl.clientWidth;
  }

  /**
   * Per-top-level-block source-line range plus measured pixel geometry, in document order. This is
   * the reference height-sync (height-sync.ts) pads the source editor against, so each source block's
   * top lines up with its rendered block. Granularity is top-level blocks (md-blocks); rows of a
   * table or items of a list are not aligned individually. Tops are container-relative + scrollTop, in
   * one coordinate system, matching {@link Preview.blockGeometry}.
   */
  blockGeometry(): BlockGeometry[] {
    const doc = this.view.state.doc;
    const containerTop = this.scrollEl.getBoundingClientRect().top;
    const scrollTop = this.scrollEl.scrollTop;
    const result: BlockGeometry[] = [];
    let pos = 0;
    for (let i = 0; i < doc.childCount; i++) {
      const dom = this.view.nodeDOM(pos);
      const block = this.blocks[i];
      if (dom instanceof HTMLElement && block !== undefined) {
        const rect = dom.getBoundingClientRect();
        result.push({
          lineStart: block.lineStart,
          lineEnd: block.lineEnd,
          top: rect.top - containerTop + scrollTop,
          height: rect.height,
        });
      }
      pos += doc.child(i).nodeSize;
    }
    return result;
  }

  /**
   * The 0-based source line at the top of the viewport (block granularity), for cross-mode sync.
   * Uses block DOM geometry rather than posAtCoords: the pane has horizontal padding, so a probe near
   * the left edge would fall outside the ProseMirror content and posAtCoords would return null (which
   * made the formatted→source scroll-sync snap to the top). Finds the topmost still-visible block.
   */
  topVisibleSourceLine(): number {
    const top = this.scrollEl.getBoundingClientRect().top + 4;
    const doc = this.view.state.doc;
    let pos = 0;
    for (let i = 0; i < doc.childCount; i++) {
      const dom = this.view.nodeDOM(pos);
      if (dom instanceof HTMLElement && dom.getBoundingClientRect().bottom > top) {
        return this.blocks[i]?.lineStart ?? 0;
      }
      pos += doc.child(i).nodeSize;
    }
    return this.blocks[Math.max(0, doc.childCount - 1)]?.lineStart ?? 0;
  }

  /** Scroll so the block containing the given source line aligns near the top (block granularity). */
  scrollToSourceLine(line: number): void {
    let index = 0;
    for (let i = 0; i < this.blocks.length; i++) {
      const block = this.blocks[i];
      if (block !== undefined && line >= block.lineStart) {
        index = i;
      }
    }
    const doc = this.view.state.doc;
    const clamped = Math.max(0, Math.min(index, doc.childCount - 1));
    let pos = 0;
    for (let i = 0; i < clamped; i++) {
      pos += doc.child(i).nodeSize;
    }
    const dom = this.view.nodeDOM(pos);
    if (dom instanceof HTMLElement) {
      this.scrollEl.scrollTop =
        dom.getBoundingClientRect().top -
        this.scrollEl.getBoundingClientRect().top +
        this.scrollEl.scrollTop;
    }
  }

  private scheduleChange(): void {
    if (this.timer !== undefined) {
      clearTimeout(this.timer);
    }
    this.timer = setTimeout(() => {
      this.timer = undefined;
      this.onChange(this.getText());
    }, DEBOUNCE_MS);
  }
}
