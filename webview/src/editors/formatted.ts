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
import { applyWordDiff, diffLabel } from "../review/diff-decoration.js";
import type { DiffMark } from "../review/diff-marks.js";
import { buildOverlayPlan, type RemovedAnchor } from "../review/overlay-plan.js";
import type { BlockGeometry } from "../review/preview.js";
import { type BlockBox, lineAtScrollTop, scrollTopForLine } from "../sync/scroll-geometry.js";
import { assertNever } from "../util/assert.js";
import { debounce } from "../util/debounce.js";
import { closestElement } from "../util/dom.js";
import { isOpenableHref } from "../util/links.js";
import { log } from "../util/log.js";
import { rafThrottle } from "../util/raf.js";
import { BlockGeometryCache } from "./block-geometry.js";
import { BlockMap, startOfChild } from "./block-map.js";
import { type MdBlock, splitTopLevelBlocks } from "./md-blocks.js";
import type { FormatCommand } from "./md-format.js";
import { serializeWithSplice } from "./md-splice.js";
import { commonEnds } from "./mirror-patch.js";
import {
  commandFor,
  activeFormats as computeActiveFormats,
  disabledFormats as computeDisabledFormats,
} from "./pm-commands.js";
import { parser, resolveImageSrc, schema } from "./pm-markdown.js";

const DEBOUNCE_MS = 120;
const emptyDoc = (): PmNode => schema.node("doc", null, [schema.node("paragraph")]);

// A link reference definition (`[id]: url`) is the one CommonMark construct whose meaning crosses
// top-level block boundaries: a `[text][id]` link in one block resolves against a definition that may
// live in another. {@link FormattedEditor.mirror} re-parses ONLY the changed blocks in isolation, which
// can't see a definition kept verbatim in an unchanged block, so any document that has one falls back to
// a full {@link FormattedEditor.setText} rebuild (in-context parse). Deliberately loose (a `[x]:`-shaped
// line at up to three-space indent) — over-matching only costs an occasional full rebuild, never
// correctness. Such definitions are rare in these specs, so the minimal-patch fast path still covers the
// overwhelming majority of edits.
const REFERENCE_DEFINITION_RE = /^ {0,3}\[[^\]]+\]:/m;

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

/** The block-widget DOM standing in for a removed block (absent from the head document). `label` is the
 *  already-composed marker text (the single removed-text policy lives in overlay-plan.ts). */
function removedMarkerDOM(label: string): HTMLElement {
  const element = document.createElement("div");
  element.className = "sd-diff-removed-marker";
  element.setAttribute("aria-hidden", "true");
  element.textContent = label;
  return element;
}

/** The flattened text an inline word-diff runs against, plus the mapping back from an offset in it to a
 *  document position — the shared return shape of {@link flattenLeaf} and {@link flattenRowOrItem}. */
interface FlattenedText {
  text: string;
  toPos: (offset: number) => number;
}

/** Flatten a pure-text node (a top-level paragraph/heading) to its own `textContent`, for the non-sub
 *  inline word-diff. `null` when the node isn't pure text (an offset couldn't map to a position safely —
 *  e.g. an image or hard break sits between characters, so content size and text length diverge). A
 *  pure-text node's text offset `o` maps to the document position `blockFrom + 1 + o`. */
function flattenLeaf(node: PmNode, blockFrom: number): FlattenedText | null {
  const text = node.textContent;
  if (node.content.size !== text.length) {
    return null;
  }
  const contentStart = blockFrom + 1;
  return { text, toPos: (offset) => contentStart + offset };
}

/** Flatten a table row / list item's CHILDREN (cells / blocks) into one joined text, with the same
 *  joiner the native side uses to flatten the whole row/item (DiffWire.fs `tableRowText`: cells joined
 *  with `" | "`; `listItemText`: blocks joined with `" "`) — so the inline word-diff for a sub (row/item)
 *  mark compares the SAME unit `baseText` was flattened from, not just the row/item's first child.
 *
 *  `null` when the row/item has no children, or any child isn't pure text (same guard as
 *  {@link flattenLeaf}, applied per child) — the caller then falls back to washing the whole row/item.
 *
 *  The returned `toPos` maps an offset in the joined text back to a document position: inside a child's
 *  own span it is that child's exact character position; inside the JOINER between two children (which
 *  has no document counterpart of its own — it exists only in this synthetic joined string) it lands at
 *  the start of the following child, a reasonable anchor for the rare add/del run that touches it (the
 *  joiner is a literal shared by both sides, so it word-diffs as equal almost always). */
function flattenRowOrItem(node: PmNode, rowFrom: number): FlattenedText | null {
  const joiner = node.type.name === "table_row" ? " | " : " ";
  const segments: { from: number; to: number; docFrom: number }[] = [];
  let text = "";
  let pure = true;
  node.forEach((child, offset) => {
    if (!pure) {
      return;
    }
    const childText = child.textContent;
    if (child.content.size !== childText.length) {
      pure = false;
      return;
    }
    if (segments.length > 0) {
      text += joiner;
    }
    const from = text.length;
    text += childText;
    // `offset` is child's position within the row/item's OWN content (0 at the position right after the
    // row/item's opening token, i.e. document position `rowFrom + 1`); its content then starts one step
    // further in, past the child's own opening token.
    segments.push({ from, to: text.length, docFrom: rowFrom + 1 + offset + 1 });
  });
  const firstSegment = segments[0];
  if (!pure || firstSegment === undefined) {
    return null;
  }
  const lastSegment = segments[segments.length - 1] ?? firstSegment;
  const toPos = (offset: number): number => {
    for (const seg of segments) {
      if (offset <= seg.to) {
        // Clamp below `seg.from` too: an offset that lands in the joiner gap BEFORE this segment (no
        // document position of its own) resolves to this segment's start.
        return seg.docFrom + Math.max(0, Math.min(offset, seg.to) - seg.from);
      }
    }
    // Past the last segment (offset === text.length, or a stray overshoot) — its content end.
    return lastSegment.docFrom + (lastSegment.to - lastSegment.from);
  };
  return { text, toPos };
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
  // Paired with the live ProseMirror doc into a {@link BlockMap} on demand (see {@link blockMap}).
  private blocks: MdBlock[] = [];
  // Edge-triggered guard so a markdown-it/ProseMirror split divergence (which {@link blockMap} then
  // falls back on) is logged once when it starts, not on every per-frame map build, and re-armed once
  // the split re-agrees. See {@link blockMap}.
  private divergenceLogged = false;
  // Per-block rendered geometry (content-relative top + height), cached so the scroll hot path
  // ({@link topVisibleSourceLine}/{@link scrollToSourceLine}) binary-searches it instead of measuring
  // every block's DOM per frame. Scroll-invariant, so it survives across scroll frames; invalidated on
  // every event that relays the blocks out — a document edit ({@link dispatchTransaction}), a whole-doc
  // {@link setText}, a review-overlay marker ({@link pushDiff}), an image decode, or a {@link refresh}
  // resize. {@link blockGeometry} (the reconcile path) always re-measures and refreshes it. See
  // block-geometry.ts for why a content-relative top is scroll-invariant.
  private readonly geometryCache = new BlockGeometryCache();
  // The last getText() result, keyed by the (original, doc) pair it was serialized from. The Split
  // cross-pane mirror (index.ts) calls getText() on every SOURCE-side debounce tick just to compare this
  // pane's text against the incoming edit — but this pane's document is untouched between those ticks, so
  // re-running the block-splice (a full re-parse of `original` + a ProseMirror serialize) each time is
  // pure waste. ProseMirror gives every edit a fresh immutable doc (and reuses the doc reference across a
  // selection-only change), so an identity check on both keys is an O(1), staleness-free guard: a real
  // edit (new doc) or a setText (new original) misses and recomputes; anything else hits. Sits above
  // md-splice's own original-parse memo, which the recompute path still benefits from.
  private cachedText: { original: string; doc: PmNode; text: string } | null = null;
  private editable = false;
  // Set for the duration of a {@link mirror} dispatch: the Split cross-pane sync applies a minimal
  // transaction that must NOT re-notify out as an edit (it originated in the source pane) and must
  // bypass the read-only edit gate (a programmatic sync, like the source editor's silent setText).
  private mirroring = false;
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
          // An image grows from nothing to its decoded size, shifting the blocks below — invalidate the
          // cached geometry and re-run height-sync once it loads (same as the preview did).
          dom.addEventListener(
            "load",
            () => {
              this.geometryCache.invalidate();
              this.onContentResize();
            },
            { once: true },
          );
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
          // A document edit (in-pane typing, a format command, or a mirror splice) relaid the blocks
          // out — drop the cached block geometry so the next scroll/reconcile re-measures. Before the
          // mirroring early-return below so a cross-pane mirror dispatch invalidates too.
          this.geometryCache.invalidate();
        }
        // A cross-pane mirror is a programmatic sync of an edit made in the OTHER pane: apply it (the
        // history, caret and selection all mapped through the minimal change), but do not re-notify it
        // out as this pane's own edit or re-report its caret — mirror() drives the highlights itself.
        if (this.mirroring) {
          return;
        }
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
            // A cross-pane mirror is a programmatic sync, not the author typing — it applies regardless
            // of the read-only gate (mirroring the source editor's setText, which bypasses this too).
            if (tr.docChanged && !this.editable && !this.mirroring) {
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
    const parsed = parser.parse(md);
    const doc = parsed ?? emptyDoc();
    this.view.updateState(this.freshState(doc));
    // A whole-document replace goes through updateState (not dispatchTransaction), so invalidate the
    // cached block geometry here explicitly — the new document's blocks are laid out afresh.
    this.geometryCache.invalidate();
    // Prime the getText() cache for the common no-op round-trip: when the parse's top-level nodes line up
    // 1:1 with the source-block split, serializeWithSplice returns `md` verbatim (md-splice), so the Split
    // mirror's per-tick equality check hits this instead of re-serializing the just-set document — the
    // hot path this whole change targets (each source-side edit mirrors in via setText, so without the
    // prime the cache would miss every tick). A fallback (null parse, or counts that differ) leaves it
    // empty so getText() computes the real result on demand. `doc` is exactly the new state's doc, so the
    // identity key matches until the next edit/setText.
    this.cachedText =
      parsed !== null && parsed.childCount === this.blocks.length
        ? { original: md, doc, text: md }
        : null;
    // freshState reset the highlight + diff plugin state; re-apply the synced active/hover blocks and,
    // if a review overlay is showing, its diff marks (so a mode-switch re-hydration doesn't drop them).
    this.pushHighlights();
    this.pushDiff();
  }

  /**
   * Reflect a source-pane edit into this view with the SMALLEST possible transaction — the Split
   * cross-pane sync (index.ts onEditorChange). Where {@link setText} rebuilds the whole document from a
   * fresh parse (resetting the undo history, caret and selection, and re-parsing every block on every
   * mirror tick), this keeps every unchanged leading/trailing top-level block's existing node and
   * re-parses ONLY the changed middle span, splicing it in as one ProseMirror transaction. So the
   * passive pane's history, caret and selection all survive (mapped through the small change), and the
   * per-tick whole-document re-parse is gone.
   *
   * Falls back to a full {@link setText} rebuild when a block-level splice can't safely be trusted: the
   * live doc isn't 1:1 with the cached block split (a parser divergence), the changed slice doesn't
   * re-parse back into the blocks the splitter found for it, or the document uses a link reference
   * definition (whose cross-block resolution an isolated slice parse can't reproduce — see
   * {@link REFERENCE_DEFINITION_RE}). Each fallback is correct, just not minimal.
   */
  mirror(text: string): void {
    const oldBlocks = this.blocks;
    const doc = this.view.state.doc;
    // A block-level splice needs the live doc's top-level nodes to line up 1:1 with the cached source
    // blocks (the same invariant serializeWithSplice guards). A reference definition anywhere means an
    // isolated slice parse could misresolve a link — rebuild in context instead.
    if (doc.childCount !== oldBlocks.length || REFERENCE_DEFINITION_RE.test(text)) {
      this.setText(text);
      return;
    }
    const newBlocks = splitTopLevelBlocks(text);
    const oldTexts = oldBlocks.map((block) => block.text);
    const newTexts = newBlocks.map((block) => block.text);
    const { prefix, suffix } = commonEnds(oldTexts, newTexts);
    const oldEnd = oldBlocks.length - suffix; // exclusive index of the changed old span
    const newEnd = newBlocks.length - suffix; // exclusive index of the changed new span
    const middleCount = newEnd - prefix;
    const middleNodes: PmNode[] = [];
    if (middleCount > 0) {
      // Re-parse just the changed blocks' source (the per-tick whole-document parse this replaces).
      const changedSource = newBlocks
        .slice(prefix, newEnd)
        .map((block) => block.text)
        .join("\n");
      const parsedMiddle = changedSource === "" ? null : parser.parse(changedSource);
      // The changed slice must parse back into exactly the blocks the splitter found for it, or the
      // node↔block 1:1 invariant would break — rebuild wholesale instead. (Also catches an empty-source
      // middle, which a top-level parse can't turn into the paragraph the splitter still counts.)
      if (parsedMiddle === null || parsedMiddle.childCount !== middleCount) {
        this.setText(text);
        return;
      }
      for (let i = 0; i < parsedMiddle.childCount; i++) {
        middleNodes.push(parsedMiddle.child(i));
      }
    }
    const from = startOfChild(doc, prefix);
    const to = startOfChild(doc, oldEnd);
    if (from === to && middleNodes.length === 0) {
      // No structural change reached the document (the diff was inside a block already kept verbatim,
      // or nothing changed at all) — still re-base below so the splice baseline tracks `text`.
      this.rebaseTo(text, newBlocks);
      return;
    }
    const tr = this.view.state.tr;
    tr.replaceWith(from, to, middleNodes);
    this.mirroring = true;
    this.view.dispatch(tr);
    this.mirroring = false;
    this.rebaseTo(text, newBlocks);
    // The active/hover/diff decorations already mapped through the change, but the synced active/hover
    // line may now resolve to a different node against the refreshed block map — re-assert them.
    this.pushHighlights();
    this.pushDiff();
  }

  /** Re-base the block-splice baseline + cached block map onto the just-mirrored source, and prime the
   *  getText() cache so the Split mirror's per-tick equality check stays O(1) (parity with setText's own
   *  prime). `text` is exactly what this pane now shows, so it is the correct cached serialization
   *  whether or not every block round-trips byte-identically. */
  private rebaseTo(text: string, blocks: MdBlock[]): void {
    this.original = text;
    this.blocks = blocks;
    this.cachedText = { original: text, doc: this.view.state.doc, text };
  }

  /**
   * The single line↔block↔PM-node↔DOM correspondence for the current frame — the source-block split
   * ({@link blocks}) paired 1:1 with the live ProseMirror doc (see block-map.ts). Built on demand
   * (O(n), the cost the inline pairing loops it replaced already paid) so every hot path reads the
   * SAME structure instead of re-deriving `blocks[i]`/`doc.child(i)` by a bare index.
   *
   * If the two sides diverge — a markdown-it/ProseMirror parse mismatch (T-083); {@link setText}
   * already declines to prime its round-trip cache on it — the map is empty, so callers degrade to a
   * safe no-op (no highlight, no spacers, no scroll) rather than pairing a block with the wrong node.
   * The divergence is logged ONCE per occurrence (edge-triggered) as a diagnostic instead of silently.
   */
  private blockMap(): BlockMap {
    const map = BlockMap.build(this.view.state.doc, this.blocks);
    if (map.divergence !== null) {
      if (!this.divergenceLogged) {
        log.warn(
          "formatted block-map: markdown-it/ProseMirror split diverged — falling back to no-op",
          map.divergence,
        );
        this.divergenceLogged = true;
      }
    } else {
      this.divergenceLogged = false;
    }
    return map;
  }

  /** The source line a resolved position maps to: the row/item line inside a table/list, else the
   *  containing top-level block's line. The inverse of {@link BlockMap.nodeRange}, for caret/hover
   *  reports. Reads {@link blocks} directly by the position's own top-level index — an inherently
   *  consistent pairing (the index comes from the live doc), so it needs no block-map build. */
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
    const map = this.blockMap();
    const decos: Decoration[] = [];
    const active = map.nodeRange(this.activeLine, true);
    this.activeNodeRange = active;
    if (active !== null) {
      decos.push(Decoration.node(active[0], active[1], { class: "sd-active-block" }));
    }
    const hover = map.nodeRange(this.hoverLine, true);
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

  /** The document position a removed-block marker anchors before — the Formatted pane's node-coordinate
   *  reading of the single {@link RemovedAnchor} (overlay-plan.ts resolves WHICH block/child; here we turn
   *  that into a ProseMirror position, via the shared {@link BlockMap}). A top-level anchor sits before
   *  the whole block; a row/item anchor before the resolved row/item node; an out-of-range anchor (or a
   *  diverged map) at the document end. */
  private removedAnchorPos(map: BlockMap, anchor: RemovedAnchor): number {
    const doc = this.view.state.doc;
    switch (anchor.at) {
      case "end":
        return doc.content.size;
      case "block":
        return map.entryAt(anchor.blockIndex)?.from ?? doc.content.size;
      case "child":
        return map.nodeRange(anchor.line, true)?.[0] ?? doc.content.size;
      default:
        return assertNever(anchor);
    }
  }

  /** Build and push the review/compare decorations from the current diff marks. Thin adapter: the
   *  pane-independent overlay plan (overlay-plan.ts) decides what to wash, what to word-diff inline, and
   *  where the removed markers go; here we only resolve those instructions to ProseMirror node/widget
   *  decorations. */
  private pushDiff(): void {
    const doc = this.view.state.doc;
    // A removed-block marker is a block widget in the document flow (and inline removed-word widgets can
    // rewrap a paragraph), so showing/clearing the review overlay shifts block tops — unlike the
    // active/hover highlights, which only paint (background/box-shadow) and leave geometry untouched.
    // Invalidate the cached geometry so the next scroll/reconcile re-measures the marker-shifted layout.
    this.geometryCache.invalidate();
    if (this.diffMarks === null) {
      this.view.dispatch(this.view.state.tr.setMeta(diffKey, DecorationSet.empty));
      return;
    }
    // The overlay plan's block anchors are SOURCE-side line starts (matching the Code pane, which
    // derives them from the same head split), so it reads `blocks` directly and stays valid even if the
    // PM doc diverges; only resolving an instruction to a node position needs the paired map below.
    const map = this.blockMap();
    const plan = buildOverlayPlan(
      this.diffMarks,
      this.blocks.map((block) => block.lineStart),
    );
    const decos: Decoration[] = [];
    // Distinct keys so adjacent deletions (which share an anchor position) stay separate widgets that
    // ProseMirror can tell apart across redraws rather than collapsing into one.
    let removedSeq = 0;

    // Whole-block (or row/item) wash by kind. `data-diff-label` drives the CSS ::before annotation pill —
    // for whole-block changes only; a row/item (sub) skips it (it would clutter, and a <tr> can't anchor
    // a label).
    const washBlock = (
      kind: "added" | "moved" | "changed",
      sub: boolean,
      range: [number, number],
    ): void => {
      const attrs: { class: string; "data-diff-label"?: string } = { class: `sd-diff-${kind}` };
      if (!sub) {
        attrs["data-diff-label"] = diffLabel(kind);
      }
      decos.push(Decoration.node(range[0], range[1], attrs));
    };

    for (const instr of plan) {
      switch (instr.type) {
        case "fill": {
          // Resolve the node to wash: for a sub-block instruction (a table row / list item),
          // BlockMap.nodeRange narrows to that row/item inside its table/list; for a whole-container
          // instruction (added/moved container, sub === false) it must NOT narrow — lineStart is the
          // container's own first line, and narrowing would wash only its first row/item instead of the
          // whole table/list (T-075).
          const range = map.nodeRange(instr.lineStart, instr.sub);
          if (range !== null) {
            washBlock(instr.kind, instr.sub, range);
          }
          break;
        }
        case "inline": {
          // Same narrow-only-for-sub rule as "fill" above: a whole-container changed mark (sub === false)
          // must resolve to the whole table/list, both for the inline word-diff attempt (which then bows
          // out on a container, since its text isn't pure) and for the whole-block wash fallback below.
          const range = map.nodeRange(instr.lineStart, instr.sub);
          if (range === null) {
            break;
          }
          // A changed block tries an inline word-diff first (highlight the changed words rather than wash
          // the whole thing): a top-level paragraph/heading diffs its own text; a row/item (sub) diffs the
          // WHOLE row/item's text (all its cells/blocks joined — see pushInlineWordDiff) against
          // `instr.baseText`, which the native side flattens the same way (DiffWire.fs tableRowText /
          // listItemText) — comparing the same unit on both sides, not just the row/item's first child. A
          // sub gets no annotation pill. pushInlineWordDiff returns true when it applied (→ skip the wash).
          if (this.pushInlineWordDiff(decos, range, instr.baseText, instr.sub)) {
            break;
          }
          washBlock("changed", instr.sub, range);
          break;
        }
        case "removed":
          decos.push(
            Decoration.widget(
              this.removedAnchorPos(map, instr.anchor),
              removedMarkerDOM(instr.label),
              {
                side: -1,
                key: `sd-removed-${removedSeq++}`,
              },
            ),
          );
          break;
        default:
          assertNever(instr);
      }
    }
    this.view.dispatch(this.view.state.tr.setMeta(diffKey, DecorationSet.create(doc, decos)));
  }

  /**
   * Try to highlight the changed words inside a changed paragraph/heading/row/item instead of washing the
   * whole thing. Returns false — leaving the caller to wash — when the node (or, for a row/item, one of
   * its cells/blocks) isn't pure text (an offset can't map to a position safely), too much changed (word
   * confetti), or nothing word-level differs (a markup-only edit).
   *
   * `range[0]` is the node's own position (a top-level paragraph/heading when `sub` is false; a table row /
   * list item when `sub` is true). A non-sub node diffs its own `textContent` directly. A sub node diffs
   * the WHOLE row/item — {@link flattenRowOrItem} joins its cells/blocks the same way the native side does
   * (DiffWire.fs `tableRowText`/`listItemText`: cells with `" | "`, item blocks with `" "`) — against
   * `baseText`, which the native side flattens identically; comparing anything narrower (e.g. just the
   * first cell/paragraph) would diff against the wrong unit and raise `changeRatio` on every other
   * cell/paragraph's untouched text.
   */
  private pushInlineWordDiff(
    decos: Decoration[],
    range: [number, number],
    baseText: string,
    sub: boolean,
  ): boolean {
    const node = this.view.state.doc.nodeAt(range[0]);
    if (node === null) {
      return false;
    }
    const flattened = sub ? flattenRowOrItem(node, range[0]) : flattenLeaf(node, range[0]);
    if (flattened === null) {
      return false; // images / hard breaks / an impure cell — wash the whole row/item or block instead
    }
    const { text: headText, toPos } = flattened;
    let delSeq = 0;
    const applied = applyWordDiff(
      baseText,
      headText,
      (start, end) =>
        decos.push(
          Decoration.inline(toPos(start), toPos(end), {
            class: "sd-diff-word-added",
          }),
        ),
      (at, text) =>
        decos.push(
          Decoration.widget(toPos(at), removedWordDOM(text), {
            side: -1,
            key: `sd-delword-${range[0]}-${delSeq++}`,
          }),
        ),
    );
    if (!applied) {
      return false;
    }
    // The annotation pill only (sd-diff-inline = position:relative + ::before label), with no block wash.
    // A row/item (no pill) skips this — the inline word highlights are signal enough inside it.
    if (!sub) {
      decos.push(
        Decoration.node(range[0], range[0] + node.nodeSize, {
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

  /** The current document serialized back to Markdown, minimal-diff against the last {@link setText}.
   *  Memoized on the (original, doc) pair — see {@link cachedText} — so the cross-pane mirror's per-tick
   *  equality check doesn't re-parse/serialize an unchanged document. */
  getText(): string {
    const doc = this.view.state.doc;
    if (
      this.cachedText !== null &&
      this.cachedText.original === this.original &&
      this.cachedText.doc === doc
    ) {
      return this.cachedText.text;
    }
    const text = serializeWithSplice(this.original, doc);
    this.cachedText = { original: this.original, doc, text };
    return text;
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

  /** The toolbar commands NOT applicable at the selection (for the disabled-button state) — see
   *  pm-commands.ts's `disabledFormats`. */
  disabledFormats(): Set<FormatCommand> {
    return computeDisabledFormats(this.view.state);
  }

  /** Force a re-render after the pane returns from display:none to a new width. */
  refresh(): void {
    // The new width reflows every block, so the cached geometry is stale — drop it before re-rendering.
    this.geometryCache.invalidate();
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
   * Hard-reset the viewport to the very start of the document. Unlike {@link scrollToSourceLine}
   * this does no block-geometry math (which needs a laid-out DOM the caller may not have yet) — used
   * when hydrating a freshly loaded document, whose scrollTop otherwise still reflects wherever the
   * PREVIOUS document happened to leave it.
   */
  scrollToTop(): void {
    this.scrollEl.scrollTop = 0;
  }

  /**
   * Per-top-level-block source-line range plus measured pixel geometry, in document order. This is
   * the reference height-sync (height-sync.ts) pads the source editor against, so each source block's
   * top lines up with its rendered block. Granularity is top-level blocks (md-blocks); rows of a
   * table or items of a list are not aligned individually. Tops are container-relative + scrollTop, in
   * one coordinate system, matching {@link Preview.blockGeometry}.
   */
  blockGeometry(): BlockGeometry[] {
    // The reconcile path (height-sync.ts) runs right after the blocks relaid out and needs the freshest
    // possible geometry, so it always RE-MEASURES rather than trusting the cache — and stores the fresh
    // boxes so the next scroll frame reads them cache-hit. (Measuring here also keeps height-sync correct
    // on a window resize, which relays the pane out but reaches this pane only through reconcile.)
    const boxes = this.measureBlocks();
    this.geometryCache.set(boxes);
    return boxes.map((box) => ({
      lineStart: box.lineStart,
      lineEnd: box.lineEnd,
      top: box.top,
      height: box.height,
    }));
  }

  /**
   * Measure each top-level block's rendered geometry into a scroll-invariant {@link BlockBox} (its
   * content-relative top + height), in document order. The single forced-layout pass the geometry cache
   * amortizes: {@link blockGeometry} calls it every reconcile, {@link ensureGeometry} once per
   * invalidation for the scroll path. One block↔node pairing for the whole scan (block-map.ts); a
   * diverged split yields no entries, so height-sync/scroll see zero blocks (clearing spacers, falling
   * back to the first line) rather than aligning against mispaired anchors.
   */
  private measureBlocks(): BlockBox[] {
    const map = this.blockMap();
    const containerTop = this.scrollEl.getBoundingClientRect().top;
    const scrollTop = this.scrollEl.scrollTop;
    const boxes: BlockBox[] = [];
    for (const entry of map.entries) {
      const dom = this.view.nodeDOM(entry.from);
      if (dom instanceof HTMLElement) {
        const rect = dom.getBoundingClientRect();
        boxes.push({
          lineStart: entry.block.lineStart,
          lineEnd: entry.block.lineEnd,
          contentLineEnd: entry.block.contentLineEnd,
          // Content-relative (px from the content top), hence invariant to scrollTop — see
          // block-geometry.ts — so a box measured now stays valid across later scroll frames.
          top: rect.top - containerTop + scrollTop,
          height: rect.height,
        });
      }
    }
    return boxes;
  }

  /** The geometry cache, re-measured (once) only when stale — the scroll hot path's cache-or-build gate,
   *  so a run of scroll frames shares one measurement instead of forcing layout each frame. */
  private ensureGeometry(): BlockGeometryCache {
    if (this.geometryCache.isStale) {
      this.geometryCache.set(this.measureBlocks());
    }
    return this.geometryCache;
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
    const scrollTop = this.scrollEl.scrollTop;
    // Binary-search the cached geometry for the block straddling the viewport top — no per-block layout
    // measurement on the scroll frame (the cache is built once per invalidation, then reused). A
    // diverged split / empty cache, or a viewport above every block, yields null → the first-line
    // fallback below, exactly as the former linear scan's "no block straddles" branch did.
    const box = this.ensureGeometry().blockAtScrollTop(scrollTop);
    if (box === null) {
      return this.blocks[0]?.lineStart ?? 0;
    }
    // Span the CONTENT lines only (contentLineEnd = markdown-it's exclusive content end): the rendered
    // block's pixels cover its content, while the trailing blank lines ride with the block in the source
    // split but belong to the inter-block gap — mirrors preview.ts, which uses Markdig's blank-free
    // data-line-end. The lineEnd clamp still lets a viewport sitting in that gap report the blank line.
    return Math.min(lineAtScrollTop(box, scrollTop), box.lineEnd);
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
    // The nearest block at or before the line, resolved by binary search over the CACHED geometry
    // (block-geometry.ts, mirroring BlockMap.entryForScroll) instead of a block-map scan plus a fresh
    // per-block DOM measure — so following the sibling pane's scroll forces no layout on the scroll
    // frame. Null only when the cache is empty (an empty/diverged map): no safe block to scroll to.
    const box = this.ensureGeometry().blockForLine(line);
    if (box === null) {
      return;
    }
    // Content-line span only (see topVisibleSourceLine): the rendered block's height covers its content
    // lines, not the trailing blank lines that ride with the block in the source split.
    this.scrollEl.scrollTop = scrollTopForLine(box, line);
  }
}
