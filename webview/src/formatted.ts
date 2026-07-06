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
import { EditorState, Plugin, PluginKey, type Transaction } from "prosemirror-state";
import { Decoration, DecorationSet, EditorView } from "prosemirror-view";
import { debounce } from "./debounce.js";
import { applyWordDiff, diffLabel, removedMarkerLabel } from "./diff-decoration.js";
import type { DiffMark } from "./diff-marks.js";
import { closestElement } from "./dom.js";
import { isOpenableHref } from "./links.js";
import { type MdBlock, splitTopLevelBlocks } from "./md-blocks.js";
import type { FormatCommand } from "./md-format.js";
import { serializeWithSplice } from "./md-splice.js";
import { commandFor, activeFormats as computeActiveFormats } from "./pm-commands.js";
import { parser, resolveImageSrc, schema } from "./pm-markdown.js";
import type { BlockGeometry } from "./preview.js";
import { rafThrottle } from "./raf.js";
import { lineAtScrollTop, scrollTopForLine } from "./scroll-geometry.js";

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

/** The DecorationSet we stashed on a transaction via setMeta. ProseMirror types `getMeta` as `any`, so
 *  this is the single isolated narrowing — the value is one we set ourselves, never external. */
function decorationMeta(tr: Transaction, key: PluginKey<DecorationSet>): DecorationSet | undefined {
  return tr.getMeta(key) as DecorationSet | undefined;
}

/** Document position where the top-level child at `index` starts — the sum of every prior child's
 *  size. O(index); a scan that visits every block in order should accumulate the position inline
 *  (`pos += doc.child(i).nodeSize` per step) rather than call this per block, which would be O(n²). */
function startOfChild(doc: PmNode, index: number): number {
  let pos = 0;
  for (let i = 0; i < index; i++) {
    pos += doc.child(i).nodeSize;
  }
  return pos;
}

/** [from, to] document positions of the top-level child at `index`. */
function blockRange(doc: PmNode, index: number): [number, number] {
  const from = startOfChild(doc, index);
  return [from, from + doc.child(index).nodeSize];
}

const highlightPlugin = new Plugin<DecorationSet>({
  key: highlightKey,
  state: {
    init: () => DecorationSet.empty,
    apply: (tr, set) => decorationMeta(tr, highlightKey) ?? set.map(tr.mapping, tr.doc),
  },
  props: {
    decorations: (state) => highlightKey.getState(state) ?? DecorationSet.empty,
  },
});

// The review/compare overlay (PoC-6): washes each changed top-level block by kind and marks removed
// blocks with a widget at their anchor. A SEPARATE plugin from the highlight one — the highlight set is
// overwritten on every caret move, while the diff must persist until the next Compare / a real edit. The
// set is pushed in ready-made (the editor owns the line↔block map) and mapped through edits like the
// highlights, so it survives the Split text-mirror and intervening edits until index.ts clears it.
const diffKey = new PluginKey<DecorationSet>("sd-formatted-diff");

const diffPlugin = new Plugin<DecorationSet>({
  key: diffKey,
  state: {
    init: () => DecorationSet.empty,
    apply: (tr, set) => decorationMeta(tr, diffKey) ?? set.map(tr.mapping, tr.doc),
  },
  props: {
    decorations: (state) => diffKey.getState(state) ?? DecorationSet.empty,
  },
});

/** The inline strikethrough span standing in for words deleted inside a changed paragraph/heading. */
function removedWordDOM(text: string): HTMLElement {
  const span = document.createElement("span");
  span.className = "sd-diff-word-removed";
  span.setAttribute("aria-hidden", "true");
  span.textContent = text;
  return span;
}

/** The block-widget DOM standing in for a removed block (absent from the head document). */
function removedMarkerDOM(text: string): HTMLElement {
  const element = document.createElement("div");
  element.className = "sd-diff-removed-marker";
  element.setAttribute("aria-hidden", "true");
  element.textContent = removedMarkerLabel(text);
  return element;
}

export interface FormattedEditorCallbacks {
  /** Fired ~120 ms after an edit, with the block-spliced Markdown of the current document. */
  onChange: (text: string) => void;
  /** Fired when the author tries to edit while the document is read-only (offer to start a draft). */
  onEditAttempt: () => void;
  /** Fired (rAF-throttled) as the pane scrolls — drives block-level scroll-sync with the source editor. */
  onScroll: () => void;
  /** Fired (rAF-throttled) with the caret block's 0-based source line, for cross-pane highlight sync,
   *  and whether this was a pure navigation (caret move without a text edit) — used to gate the
   *  cross-pane reveal scroll, which must fire on selecting a line but not on every keystroke. */
  onCursor: (line: number | null, navigated: boolean) => void;
  /** Fired (rAF-throttled) with the 0-based source line under the mouse (null outside), for hover sync. */
  onHover: (line: number | null) => void;
  /** Fired when rendered content changes height (e.g. an image finished decoding) — re-run height-sync. */
  onContentResize: () => void;
  /** Fired when this view gains focus — lets the toolbar route formatting to the active pane in Split. */
  onFocus: () => void;
  /** Fired when the selection/marks change — lets the toolbar refresh its active-button state. */
  onActiveChange: () => void;
  /** Fired with an http/https URL the author clicked, to open in the OS browser (the webview must
   *  never navigate itself). In read mode a plain click opens it; while editing it takes Ctrl/Cmd-click
   *  so a plain click can still place the caret. */
  onOpenLink: (url: string) => void;
}

export class FormattedEditor {
  private readonly view: EditorView;
  private readonly scrollEl: HTMLElement;
  private readonly onChange: (text: string) => void;
  private readonly onEditAttempt: () => void;
  private readonly onScroll: () => void;
  private readonly onCursor: (line: number | null, navigated: boolean) => void;
  private readonly onHover: (line: number | null) => void;
  private readonly onContentResize: () => void;
  private readonly onFocus: () => void;
  private readonly onActiveChange: () => void;
  private readonly onOpenLink: (url: string) => void;
  // The Markdown this document was last loaded from — the baseline block-splice diffs against.
  private original = "";
  // The open document's directory relative to the repo root, used to resolve relative image links to
  // `app://repo/…` for display (the image node keeps its original relative src for serialization).
  private docDir = "";
  // The top-level block split of the CURRENT document source (= `original` at load, then refreshed
  // after each in-pane edit), cached so the per-frame highlight/scroll line↔node mapping doesn't
  // re-parse on every caret/mouse move. Distinct from `original`, which stays the splice baseline.
  private blocks: MdBlock[] = [];
  private editable = false;
  // Edit-change notification, debounced: a burst of in-pane edits coalesces into one onChange once
  // typing goes quiet (see debounce.ts). A field, so it exists before the update listener can call
  // it; the body reads blocks/getText/onChange/reportCaret at fire time, all set by then.
  private readonly scheduleChange = debounce(() => {
    const text = this.getText();
    // Refresh the block map from the edited source so the highlight + scroll line↔node mapping
    // tracks the live document instead of drifting from the last setText after in-pane edits. Only
    // `blocks` is refreshed — NOT `original` (the splice baseline `getText` diffs against, which must
    // stay the loaded/mirrored source). Then re-report the caret so the highlight uses the fresh map.
    this.blocks = splitTopLevelBlocks(text);
    this.onChange(text);
    this.reportCaret();
  }, DEBOUNCE_MS);
  // The synced active/hover source lines (driven externally), remembered so they can be re-applied
  // after setText rebuilds the document (which resets the highlight plugin's state).
  private activeLine: number | null = null;
  private hoverLine: number | null = null;
  // The review/compare overlay marks (null = no overlay), remembered so a setText rebuild re-applies them.
  private diffMarks: DiffMark[] | null = null;
  // The active node range resolved by the last pushHighlights, cached so revealSourceLine reuses it
  // instead of recomputing the line→node mapping on the caret hot path.
  private activeNodeRange: [number, number] | null = null;
  // Whether the latest caret report is a pure navigation (click / arrow), not a text edit. Only a
  // navigation triggers the passive pane's reveal scroll (see index.ts setActive); typing must not.
  private caretNavigated = false;
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
    this.onFocus = callbacks.onFocus;
    this.onActiveChange = callbacks.onActiveChange;
    this.onOpenLink = callbacks.onOpenLink;
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
          // A transaction that changed the doc is a text edit, not a navigation — gate out the reveal.
          this.caretNavigated = !changed;
          this.reportCaret();
          this.onActiveChange();
        }
      },
    });

    // Anchor clicks must never navigate the webview away (it would replace the whole app), and a
    // `javascript:` URL must never run. Swallow the in-webview navigation; hand a real web link
    // (http/https) to the host to open in the OS browser instead. While editing, a plain click should
    // place the caret (for editing the link text), so opening then needs Ctrl/Cmd-click; in read mode
    // a plain click opens. Other schemes / repo-relative links are ignored (just the navigation guard).
    this.view.dom.addEventListener("click", (event) => {
      const anchor = closestElement(event.target, "a");
      if (!anchor) {
        return;
      }
      event.preventDefault();
      const href = anchor.getAttribute("href")?.trim() ?? "";
      const modifier = event.metaKey || event.ctrlKey;
      if (isOpenableHref(href) && (!this.editable || modifier)) {
        this.onOpenLink(href);
      }
    });

    // Report focus so the formatting toolbar can route to this pane when it is the active one in Split.
    this.view.dom.addEventListener("focus", () => this.onFocus());

    // The pane is the scroll container; report scrolls (throttled) for block-level scroll-sync.
    const reportScroll = rafThrottle(() => this.onScroll());
    this.scrollEl.addEventListener("scroll", reportScroll);

    // Report the caret's block as a source line for cross-pane highlight sync (index.ts pushes the
    // decoration back via setActiveLine). Deferred (rAF) so the resulting dispatch runs outside the
    // originating transaction.
    this.reportCaret = rafThrottle(() => {
      this.onCursor(this.sourceLineForPos(this.view.state.selection.$head), this.caretNavigated);
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
        diffPlugin,
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
    // freshState reset the highlight + diff plugin state; re-apply the synced active/hover blocks and,
    // if a review overlay is showing, its diff marks (so a Split text-mirror doesn't drop them).
    this.pushHighlights();
    this.pushDiff();
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
    // A line past the last block (a stale synced line from a now-shorter doc) clears the highlight
    // rather than pinning the final block.
    if (index !== null && line > (this.blocks[index]?.lineEnd ?? Number.POSITIVE_INFINITY)) {
      return null;
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
    this.activeNodeRange = active;
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

  /** Build and push the review/compare decorations from the current diff marks (resolved to blocks). */
  private pushDiff(): void {
    const doc = this.view.state.doc;
    if (this.diffMarks === null) {
      this.view.dispatch(this.view.state.tr.setMeta(diffKey, DecorationSet.empty));
      return;
    }
    const decos: Decoration[] = [];
    // Distinct keys so adjacent deletions (which share an anchor position) stay separate widgets that
    // ProseMirror can tell apart across redraws rather than collapsing into one.
    let removedSeq = 0;
    for (const mark of this.diffMarks) {
      if (mark.kind === "removed") {
        // A removed ROW/ITEM (mark.sub) anchors at the following row/item inside its container, so the
        // marker sits between the surrounding rows/items rather than at the container's edge.
        const childRange = mark.sub === true ? this.nodeRangeForLine(mark.anchorLine) : null;
        let pos: number;
        if (childRange !== null) {
          pos = childRange[0];
        } else {
          // A removed top-level BLOCK: before the FIRST head block starting at/after the anchor line.
          // Keying on block STARTS (lineStart) is robust to where trailing blank lines land (block ENDS
          // can differ between the native AST that set the anchor and this pane's splitter). anchorLine 0
          // targets the first block (top); nothing at/after the anchor (deleted at the end) → doc end.
          let following = -1;
          for (let i = 0; i < this.blocks.length; i++) {
            const block = this.blocks[i];
            if (block !== undefined && block.lineStart >= mark.anchorLine) {
              following = i;
              break;
            }
          }
          pos =
            following < 0 || following >= doc.childCount
              ? doc.content.size
              : blockRange(doc, following)[0];
        }
        decos.push(
          Decoration.widget(pos, removedMarkerDOM(mark.removedText), {
            side: -1,
            key: `sd-removed-${removedSeq++}`,
          }),
        );
        continue;
      }
      // Resolve the node to wash: a whole top-level block, or — for a sub-block mark (a changed table
      // row / list item) — the individual row/item that nodeRangeForLine narrows to inside a table/list.
      const range = this.nodeRangeForLine(mark.lineStart);
      if (range === null) {
        continue;
      }
      // A changed block tries an inline word-diff first (highlight the changed words rather than wash
      // the whole thing): a top-level paragraph/heading on its own node, or a row/item (sub) on its
      // inner text node (range[0] + 1 steps inside the row/item to its first child). A sub mark gets no
      // annotation pill. pushInlineWordDiff returns true when it applied (→ skip the wash).
      if (mark.kind === "changed" && mark.baseText !== undefined) {
        const inlineFrom = mark.sub === true ? range[0] + 1 : range[0];
        if (this.pushInlineWordDiff(decos, inlineFrom, mark.baseText, mark.sub !== true)) {
          continue;
        }
      }
      // Whole-block wash. `data-diff-label` drives the CSS ::before annotation pill — for whole-block
      // changes only; a row/item (mark.sub) skips it (it would clutter, and a <tr> can't anchor a label).
      const attrs: { class: string; "data-diff-label"?: string } = {
        class: `sd-diff-${mark.kind}`,
      };
      if (mark.sub !== true) {
        attrs["data-diff-label"] = diffLabel(mark.kind);
      }
      decos.push(Decoration.node(range[0], range[1], attrs));
    }
    this.view.dispatch(this.view.state.tr.setMeta(diffKey, DecorationSet.create(doc, decos)));
  }

  /**
   * Try to highlight the changed words inside a changed paragraph/heading instead of washing the whole
   * block. Returns false — leaving the caller to wash — when the node isn't pure text (an offset can't
   * map to a position safely), too much changed (word confetti), or nothing word-level differs (a
   * markup-only edit). A pure-text block's content size equals its text length (no leaf nodes between
   * characters), so a text offset `o` maps to the document position `blockFrom + 1 + o`.
   */
  private pushInlineWordDiff(
    decos: Decoration[],
    blockFrom: number,
    baseText: string,
    withLabel: boolean,
  ): boolean {
    const node = this.view.state.doc.nodeAt(blockFrom);
    if (node === null) {
      return false;
    }
    const headText = node.textContent;
    if (node.content.size !== headText.length) {
      return false; // images / hard breaks break the offset→position identity — wash the whole block
    }
    // A pure-text block's text offset `o` maps to the document position `blockFrom + 1 + o`.
    const contentStart = blockFrom + 1;
    let delSeq = 0;
    const applied = applyWordDiff(
      baseText,
      headText,
      (start, end) =>
        decos.push(
          Decoration.inline(contentStart + start, contentStart + end, {
            class: "sd-diff-word-added",
          }),
        ),
      (at, text) =>
        decos.push(
          Decoration.widget(contentStart + at, removedWordDOM(text), {
            side: -1,
            key: `sd-delword-${blockFrom}-${delSeq++}`,
          }),
        ),
    );
    if (!applied) {
      return false;
    }
    // The annotation pill only (sd-diff-inline = position:relative + ::before label), with no block wash.
    // A row/item (no pill) skips this — the inline word highlights are signal enough inside it.
    if (withLabel) {
      decos.push(
        Decoration.node(blockFrom, blockFrom + node.nodeSize, {
          class: "sd-diff-inline",
          "data-diff-label": diffLabel("changed"),
        }),
      );
    }
    return true;
  }

  /** Show the review/compare overlay: wash each changed top-level block by kind and mark removed blocks.
   *  The marks are remembered so a setText document rebuild (the Split mirror) re-applies them. */
  setDiff(marks: DiffMark[]): void {
    this.diffMarks = marks;
    this.pushDiff();
  }

  /** Clear the review/compare overlay. */
  clearDiff(): void {
    this.diffMarks = null;
    this.pushDiff();
  }

  /** The current document serialized back to Markdown, minimal-diff against the last {@link setText}. */
  getText(): string {
    return serializeWithSplice(this.original, this.view.state.doc);
  }

  /** Whether an edit has been typed here that hasn't been reported via `onChange` yet (still waiting
   *  out the debounce). The cross-pane mirror in index.ts checks this on the DESTINATION pane before a
   *  silent `setText`, so a same-instant edit there isn't clobbered by a stale mirror from the sibling
   *  pane's own (earlier-started, now-firing) debounce. */
  hasPendingChange(): boolean {
    return this.scheduleChange.pending;
  }

  /**
   * Allow or block document edits. Read by the filterTransaction gate; the view stays contentEditable
   * either way (so the caret works and a read-only typing attempt can offer a draft).
   */
  setEditable(enabled: boolean): void {
    this.editable = enabled;
  }

  /** Apply a formatting-toolbar command as a ProseMirror command, then refocus the view. */
  format(command: FormatCommand): void {
    if (!this.editable) {
      this.onEditAttempt();
      return;
    }
    commandFor(command)(this.view.state, this.view.dispatch.bind(this.view));
    this.view.focus();
  }

  /** The toolbar commands currently active at the selection (for the pressed-button state). */
  activeFormats(): Set<FormatCommand> {
    return computeActiveFormats(this.view.state);
  }

  /** Force a re-render after the pane returns from display:none to a new width. */
  refresh(): void {
    this.view.updateState(this.view.state);
  }

  /** Move keyboard focus into the formatted editor (used by the skip-to-editor link). */
  focus(): void {
    this.view.focus();
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
   * The 0-based source line at the top of the viewport, sub-block: the topmost block straddling the
   * viewport top, plus how far the viewport has scrolled into it (its height interpolated back across
   * the block's source-line span). So a partly-scrolled tall block reports the line actually at the
   * top, not the block's first line — the inverse of {@link scrollToSourceLine}. Uses block DOM
   * geometry rather than posAtCoords: the pane has horizontal padding, so a left-edge probe would fall
   * outside the ProseMirror content and posAtCoords would return null.
   */
  topVisibleSourceLine(): number {
    const containerTop = this.scrollEl.getBoundingClientRect().top;
    const scrollTop = this.scrollEl.scrollTop;
    const doc = this.view.state.doc;
    // `blocks` and the PM doc are parallel-indexed and normally 1:1; bound the scan by the shorter so
    // that on a divergence we stop at the last matched block rather than skipping past a hole and
    // pinning an earlier one. (A deeper index↔node misalignment is a pre-existing module-wide concern
    // shared with blockGeometry/nodeRangeForLine, not specific to scroll-sync.)
    const limit = Math.min(doc.childCount, this.blocks.length);
    let pos = 0;
    let current: { top: number; height: number; block: MdBlock } | undefined;
    for (let i = 0; i < limit; i++) {
      const dom = this.view.nodeDOM(pos);
      const block = this.blocks[i];
      if (dom instanceof HTMLElement && block !== undefined) {
        const rect = dom.getBoundingClientRect();
        const top = rect.top - containerTop + scrollTop;
        if (top > scrollTop) {
          break; // first block below the viewport top — the previous one straddles it
        }
        current = { top, height: rect.height, block };
      }
      pos += doc.child(i).nodeSize;
    }
    if (current === undefined) {
      return this.blocks[0]?.lineStart ?? 0;
    }
    // Span the CONTENT lines only (contentLineEnd = markdown-it's exclusive content end): the rendered
    // block's pixels cover its content, while the trailing blank lines ride with the block in the source
    // split but belong to the inter-block gap — mirrors preview.ts, which uses Markdig's blank-free
    // data-line-end. The lineEnd clamp still lets a viewport sitting in that gap report the blank line.
    return Math.min(
      lineAtScrollTop(
        {
          lineStart: current.block.lineStart,
          lineEnd: current.block.lineEnd,
          contentLineEnd: current.block.contentLineEnd,
          top: current.top,
          height: current.height,
        },
        scrollTop,
      ),
      current.block.lineEnd,
    );
  }

  /**
   * Scroll the formatted pane the minimum amount so the active-block highlight is visible (no-op if it
   * already is). Mirrors {@link MarkdownEditor.revealSourceLine}: reveals the synced active-block
   * highlight when accumulated block-height drift pushed it outside this pane's viewport while the user
   * works in the other pane. Targets the node range {@link pushHighlights} last resolved for the active
   * line — a table row / list item inside a container, else the whole top-level block.
   */
  revealActiveBlock(): void {
    const range = this.activeNodeRange;
    if (range === null) {
      return;
    }
    const dom = this.view.nodeDOM(range[0]);
    if (!(dom instanceof HTMLElement)) {
      return;
    }
    const elRect = dom.getBoundingClientRect();
    const viewRect = this.scrollEl.getBoundingClientRect();
    const margin = 8; // breathing room from the pane edge when we do scroll
    const above = elRect.top < viewRect.top;
    const below = elRect.bottom > viewRect.bottom;
    if (above && !below) {
      this.scrollEl.scrollTop += elRect.top - viewRect.top - margin;
    } else if (below && !above) {
      this.scrollEl.scrollTop += elRect.bottom - viewRect.bottom + margin;
    }
    // above && below → the node spans the whole viewport (already visible) → no-op.
    // !above && !below → fully visible → no-op.
  }

  /**
   * Scroll so the (possibly fractional) source line aligns at the top, the fractional part
   * interpolated across the matching block's height — sub-block precision matching the source editor's
   * fractional top line ({@link MarkdownEditor.topVisibleLineExact}), so scrolling tracks smoothly
   * within a tall block instead of snapping to block tops.
   */
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
    const block = this.blocks[clamped];
    const pos = startOfChild(doc, clamped);
    const dom = this.view.nodeDOM(pos);
    if (block === undefined || !(dom instanceof HTMLElement)) {
      return;
    }
    const rect = dom.getBoundingClientRect();
    const blockTop = rect.top - this.scrollEl.getBoundingClientRect().top + this.scrollEl.scrollTop;
    // Content-line span only (see topVisibleSourceLine): the rendered block's height covers its content
    // lines, not the trailing blank lines that ride with the block in the source split.
    this.scrollEl.scrollTop = scrollTopForLine(
      {
        lineStart: block.lineStart,
        lineEnd: block.lineEnd,
        contentLineEnd: block.contentLineEnd,
        top: blockTop,
        height: rect.height,
      },
      line,
    );
  }
}
