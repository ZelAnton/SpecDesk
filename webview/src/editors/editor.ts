/**
 * The CodeMirror 6 source editor. Stays deliberately thin: it owns the document text, emits
 * debounced change notifications carrying a monotonic version, reports the source line at the top
 * of its viewport for scroll-sync, and can scroll itself to a given source line. No Markdown
 * logic lives here — that is all native (docs/design/05-live-preview.md).
 *
 * It also hosts the editor side of height-synced scroll: block-widget "spacer" decorations that
 * pad source regions so each rendered block lines up vertically with its source (see height-sync.ts).
 */

import { markdown, markdownLanguage } from "@codemirror/lang-markdown";
import { HighlightStyle, syntaxHighlighting, syntaxTree } from "@codemirror/language";
import {
  Compartment,
  EditorState,
  Prec,
  type Range,
  StateEffect,
  StateField,
} from "@codemirror/state";
import {
  Decoration,
  type DecorationSet,
  EditorView,
  type KeyBinding,
  keymap,
  WidgetType,
} from "@codemirror/view";
import { tags } from "@lezer/highlight";
import { basicSetup } from "codemirror";
import { applyWordDiff } from "../review/diff-decoration.js";
import type { DiffMark } from "../review/diff-marks.js";
import { buildOverlayPlan, type RemovedAnchor } from "../review/overlay-plan.js";
import type { EditorSpacer } from "../sync/height-sync.js";
import { assertNever } from "../util/assert.js";
import { debounce } from "../util/debounce.js";
import { urlAtColumn } from "../util/links.js";
import { rafThrottle } from "../util/raf.js";
import { isRecord } from "../wire/decoders.js";
import { FORMAT_REGISTRY, type FormatKind } from "./format-registry.js";
import { splitTopLevelBlocks } from "./md-blocks.js";
import { type FormatCommand, formatMarkdown } from "./md-format.js";
import { computeTextPatch } from "./mirror-patch.js";

const DEBOUNCE_MS = 120;
/** Idle gap after the last scroll event before we treat scrolling as finished and re-snap. */
const SCROLL_SETTLE_MS = 120;

/** A lang-markdown syntax node, derived from `syntaxTree()` so this module needs no direct @lezer/common
 *  dep (mirrors md-format.ts's own `MdNode` alias). */
type SyntaxTreeNode = ReturnType<ReturnType<typeof syntaxTree>["resolveInner"]>;

/**
 * The lang-markdown Lezer syntax-node name a toolbar {@link FormatKind} maps to — the source pane's
 * counterpart of pm-commands.ts's per-kind node check, read against the syntax tree instead of a
 * ProseMirror selection: an inline mark's own `node` (StrongEmphasis/Emphasis/Strikethrough, from the
 * registry — the same name CommonMark's grammar produces for the valid wrapped form), a heading's
 * `ATXHeading<level>`, a list's `BulletList`/`OrderedList`, a quote's `Blockquote`, a fence's
 * `FencedCode`. `default: assertNever(kind)` keeps this exhaustive against {@link FormatKind}.
 */
function syntaxNodeNameFor(kind: FormatKind): string {
  switch (kind.type) {
    case "inline":
      return kind.node;
    case "heading":
      return `ATXHeading${kind.level}`;
    case "list":
      return kind.ordered ? "OrderedList" : "BulletList";
    case "quote":
      return "Blockquote";
    case "fence":
      return "FencedCode";
    default:
      return assertNever(kind);
  }
}

function formattingKeymapFor(apply: (command: FormatCommand) => void): readonly KeyBinding[] {
  return FORMAT_REGISTRY.map((command) => ({
    key: command.hotkey,
    run: () => {
      apply(command.id);
      return true;
    },
  }));
}

/** A zero-content block of a fixed pixel height, inserted to match a taller rendered block. */
class SpacerWidget extends WidgetType {
  constructor(
    readonly height: number,
    readonly isLead = false,
  ) {
    super();
  }

  override eq(other: SpacerWidget): boolean {
    return other.height === this.height && other.isLead === this.isLead;
  }

  toDOM(): HTMLElement {
    const element = document.createElement("div");
    element.className = "cm-sync-spacer";
    element.style.height = `${this.height}px`;
    element.setAttribute("aria-hidden", "true");
    return element;
  }

  override get estimatedHeight(): number {
    return this.height;
  }
}

const setSpacersEffect = StateEffect.define<DecorationSet>();

const spacerField = StateField.define<DecorationSet>({
  create: () => Decoration.none,
  update(decorations, tr) {
    let mapped = decorations.map(tr.changes);
    for (const effect of tr.effects) {
      if (effect.is(setSpacersEffect)) {
        mapped = effect.value;
      }
    }
    return mapped;
  },
  provide: (field) => EditorView.decorations.from(field),
});

/** The count of ascending `values` strictly less than `target` — a binary lower bound (the first index
 *  whose value is >= target). Indexes a spacer prefix-sum by a document position, so the total height of
 *  spacers strictly above it is one array read (see {@link MarkdownEditor.naturalLineTops}). */
function countLessThan(values: readonly number[], target: number): number {
  let lo = 0;
  let hi = values.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if ((values[mid] ?? Number.POSITIVE_INFINITY) < target) {
      lo = mid + 1;
    } else {
      hi = mid;
    }
  }
  return lo;
}

// The faint highlight on the source line under the mouse pointer (auxiliary; see index.ts).
const setHoverLineEffect = StateEffect.define<number | null>();

const hoverLineField = StateField.define<DecorationSet>({
  create: () => Decoration.none,
  update(decorations, tr) {
    for (const effect of tr.effects) {
      if (effect.is(setHoverLineEffect)) {
        if (effect.value === null) {
          return Decoration.none;
        }
        const lineNumber = Math.min(Math.max(effect.value + 1, 1), tr.state.doc.lines);
        const line = tr.state.doc.line(lineNumber);
        return Decoration.set([Decoration.line({ class: "cm-hover-line" }).range(line.from)]);
      }
    }
    return tr.docChanged ? Decoration.none : decorations;
  },
  provide: (field) => EditorView.decorations.from(field),
});

// The prominent highlight on the active source line (the caret line). Driven externally (setActiveLine)
// rather than by CodeMirror's built-in active-line plugin, so index.ts can keep it in step with the
// formatted pane: in Split, the line the caret/mouse is on in one pane highlights the matching block in
// the other. Mapped through edits so it stays put until the next caret report re-sets it.
const setActiveLineEffect = StateEffect.define<number | null>();

const activeLineField = StateField.define<DecorationSet>({
  create: () => Decoration.none,
  update(decorations, tr) {
    for (const effect of tr.effects) {
      if (effect.is(setActiveLineEffect)) {
        const lineNumber = effect.value === null ? null : effect.value + 1;
        // A stale line index from a document that has since shrunk (e.g. the Split mirror re-applying
        // the last synced line across a whole-document setText replace) clears the highlight rather
        // than pinning it to the last line — matching the formatted pane (see formatted.ts
        // blockIndexForLine), which resets instead of clamping to its last block.
        if (lineNumber === null || lineNumber < 1 || lineNumber > tr.state.doc.lines) {
          return Decoration.none;
        }
        const line = tr.state.doc.line(lineNumber);
        return Decoration.set([Decoration.line({ class: "cm-active-line" }).range(line.from)]);
      }
    }
    return decorations.map(tr.changes);
  },
  provide: (field) => EditorView.decorations.from(field),
});

/** A block widget standing in for a removed block (which is absent from the head document). `label` is
 *  the already-composed marker text (the single removed-text policy lives in overlay-plan.ts). */
class RemovedWidget extends WidgetType {
  constructor(readonly label: string) {
    super();
  }

  override eq(other: RemovedWidget): boolean {
    return other.label === this.label;
  }

  toDOM(): HTMLElement {
    const element = document.createElement("div");
    element.className = "cm-diff-removed-marker";
    element.setAttribute("aria-hidden", "true");
    element.textContent = this.label;
    return element;
  }
}

/** The inline struck span standing in for source words deleted inside a changed block (Code pane). */
class RemovedWordWidget extends WidgetType {
  constructor(readonly text: string) {
    super();
  }

  override eq(other: RemovedWordWidget): boolean {
    return other.text === this.text;
  }

  toDOM(): HTMLElement {
    const span = document.createElement("span");
    span.className = "cm-diff-word-removed";
    span.setAttribute("aria-hidden", "true");
    span.textContent = this.text;
    return span;
  }
}

// The review/compare overlay (PoC-6): per-line change classes + a removed-block marker. Cleared on any
// real content edit (the snapshot goes stale — index.ts re-runs Compare), but re-applied across a silent
// whole-document setText (the Split mirror / a mode-switch hydration) so it survives those.
const setDiffEffect = StateEffect.define<DiffMark[] | null>();

/**
 * Append inline word-diff decorations for a changed block's SOURCE — a mark over each added/changed
 * source run and a struck widget at each deletion — on top of the line wash. A near-total rewrite (over
 * the ratio) or a too-large block adds nothing, leaving just the wash. `start`/`end` are 1-based inclusive
 * CM line numbers; a source offset `o` maps to the document position `blockStart + o` (sliceDoc chars are
 * 1:1 with positions).
 */
function pushInlineSourceWords(
  ranges: Range<Decoration>[],
  state: EditorState,
  baseSource: string,
  start: number,
  end: number,
): void {
  const blockStart = state.doc.line(start).from;
  const headSource = state.sliceDoc(blockStart, state.doc.line(end).to);
  applyWordDiff(
    baseSource,
    headSource,
    (s, e) =>
      ranges.push(
        Decoration.mark({ class: "cm-diff-word-added" }).range(blockStart + s, blockStart + e),
      ),
    (at, text) =>
      ranges.push(
        Decoration.widget({ widget: new RemovedWordWidget(text), side: -1 }).range(blockStart + at),
      ),
  );
}

/** The 0-based source line a removed-block marker sits at, or null to plant it below the last line —
 *  the Code pane's line-coordinate reading of the single {@link RemovedAnchor}. A top-level anchor's
 *  `line` is a real block start (always in range); a row/item anchor past the document end plants below
 *  the last line, like a deletion after all head content. */
function removedLineFor(anchor: RemovedAnchor, lineCount: number): number | null {
  switch (anchor.at) {
    case "end":
      return null;
    case "block":
      return anchor.line;
    case "child":
      return anchor.line >= lineCount ? null : anchor.line;
    default:
      return assertNever(anchor);
  }
}

// Thin adapter: turn the pane-independent overlay plan (overlay-plan.ts) into CodeMirror decorations.
// The plan owns the removed-marker anchoring + text policy; here we only map its instructions to line
// washes, inline source word marks, and block widgets.
function buildDiffDecorations(state: EditorState, marks: DiffMark[]): DecorationSet {
  const lineCount = state.doc.lines;
  const ranges: Range<Decoration>[] = [];
  // The block start lines are needed only to anchor a removed TOP-LEVEL block; skip the source split
  // entirely when the diff has none (the common case).
  const needsBlocks = marks.some((mark) => mark.kind === "removed" && !mark.sub);
  const blockLineStarts = needsBlocks
    ? splitTopLevelBlocks(state.doc.toString()).map((block) => block.lineStart)
    : [];
  const plan = buildOverlayPlan(marks, blockLineStarts);

  // Wash a whole-block range by kind; returns the clamped 1-based CM line span so a changed block can
  // refine it with inline word highlights.
  const washLines = (
    kind: "added" | "moved" | "changed",
    lineStart: number,
    lineEnd: number,
  ): [number, number] => {
    const cls = `cm-diff-${kind}`;
    const start = Math.min(Math.max(lineStart + 1, 1), lineCount);
    const end = Math.min(Math.max(lineEnd + 1, 1), lineCount);
    for (let n = start; n <= end; n++) {
      ranges.push(Decoration.line({ class: cls }).range(state.doc.line(n).from));
    }
    return [start, end];
  };

  for (const instr of plan) {
    switch (instr.type) {
      case "fill":
        washLines(instr.kind, instr.lineStart, instr.lineEnd);
        break;
      case "inline": {
        // A changed block always keeps its line wash as the block-level signal (the Code pane has no
        // annotation pill), refined with inline word highlights on top — both a whole-block and a row/item
        // (sub) changed mark carry a source to diff against; only an unresolved case keeps just the wash.
        const [start, end] = washLines("changed", instr.lineStart, instr.lineEnd);
        if (instr.baseSource !== null) {
          pushInlineSourceWords(ranges, state, instr.baseSource, start, end);
        }
        break;
      }
      case "removed": {
        const widget = new RemovedWidget(instr.label);
        const line = removedLineFor(instr.anchor, lineCount);
        if (line === null) {
          // Deleted past the last head line — a marker below the last line.
          ranges.push(
            Decoration.widget({ widget, block: true, side: 1 }).range(state.doc.line(lineCount).to),
          );
        } else {
          // A marker above the line the deleted block sat before.
          const at = state.doc.line(Math.max(line + 1, 1));
          ranges.push(Decoration.widget({ widget, block: true, side: -1 }).range(at.from));
        }
        break;
      }
      default:
        assertNever(instr);
    }
  }

  return Decoration.set(ranges, true);
}

const diffField = StateField.define<DecorationSet>({
  create: () => Decoration.none,
  update(decorations, tr) {
    for (const effect of tr.effects) {
      if (effect.is(setDiffEffect)) {
        return effect.value === null
          ? Decoration.none
          : buildDiffDecorations(tr.state, effect.value);
      }
    }
    return decorations.map(tr.changes);
  },
  provide: (field) => EditorView.decorations.from(field),
});

// A pending image-insert marker (T-034/M-21): the position an in-flight paste/drop was captured at is
// registered here and remapped through every subsequent edit — typing, another marker's own resolution,
// … — via ChangeSet.mapPos, so the async host round-trip inserts wherever that position has moved to
// instead of the stale value captured at paste time. `assoc: 1` on the mapping means a marker sitting
// exactly where ANOTHER marker's insert just landed sticks AFTER that inserted text, so several images
// captured at the same original position still resolve into distinct, non-clobbering locations no matter
// which host reply arrives first.
//
// A whole-document setText is a single blunt change (the entire old text deleted, the entire new text
// inserted), so ChangeSet.mapPos through it is meaningless for a marker — every position ends up mapped
// to one edge of the change. setText therefore never lets a marker go through the ordinary docChanged
// mapping above: a genuinely different document (loading another file) drops pending markers outright
// via clearMarkersEffect (there is nothing left in the new document for them to mean); a mode-switch
// hydration re-applying the SAME logical content instead restores the pre-transaction positions verbatim
// via restoreMarkersEffect (clamped to the new length) — content is unchanged, so the captured position
// is still the closest available estimate, unlike the blunt mapPos result. The Split cross-pane mirror
// (T-097) is NOT one of these: it goes through {@link MarkdownEditor.mirror}, a minimal changed-span
// transaction, so its markers ride the ordinary `mapPos` mapping above and need no restore at all.
const setMarkerEffect = StateEffect.define<{ id: number; pos: number | null }>();
const clearMarkersEffect = StateEffect.define<null>();
const restoreMarkersEffect = StateEffect.define<Map<number, number>>();

const markerField = StateField.define<Map<number, number>>({
  create: () => new Map(),
  update(markers, tr) {
    let next = markers;
    if (tr.docChanged && markers.size > 0) {
      next = new Map();
      for (const [id, pos] of markers) {
        next.set(id, tr.changes.mapPos(pos, 1));
      }
    }
    for (const effect of tr.effects) {
      if (effect.is(clearMarkersEffect)) {
        next = new Map();
      } else if (effect.is(restoreMarkersEffect)) {
        const maxPos = tr.state.doc.length;
        next = new Map();
        for (const [id, pos] of effect.value) {
          next.set(id, Math.min(pos, maxPos));
        }
      } else if (effect.is(setMarkerEffect)) {
        if (next === markers) {
          next = new Map(next);
        }
        if (effect.value.pos === null) {
          next.delete(effect.value.id);
        } else {
          next.set(effect.value.id, effect.value.pos);
        }
      }
    }
    return next;
  },
});

/**
 * Editor theme (§6 of the design concept): structural chrome only — background, gutter, active line,
 * cursor and selection — all reading from the design tokens, so the editor follows light/dark with
 * the rest of the UI. Theme values are plain CSS strings, so `var(--token)` resolves against :root.
 */
const editorTheme = EditorView.theme({
  "&": {
    color: "var(--ed-text)",
    backgroundColor: "var(--surface)",
  },
  ".cm-content": {
    fontFamily: "var(--font-mono)",
  },
  ".cm-gutters": {
    color: "var(--ed-gutter)",
    backgroundColor: "var(--surface)",
    border: "none",
  },
  // The built-in active-line highlight is neutralized here: the active line is driven externally
  // (activeLineField / setActiveLine) so it can be synchronized with the formatted pane's active
  // block. The visible style lives in `.cm-active-line` (styles.css). Same for the gutter accent.
  ".cm-activeLineGutter": {
    color: "var(--ed-gutter)",
    backgroundColor: "transparent",
  },
  ".cm-activeLine": {
    backgroundColor: "transparent",
  },
  ".cm-cursor, .cm-dropCursor": {
    borderLeftColor: "var(--text-strong)",
  },
  "&.cm-focused .cm-selectionBackground, .cm-selectionBackground, .cm-content ::selection": {
    backgroundColor: "color-mix(in srgb, var(--accent) 22%, transparent)",
  },
});

/**
 * Markdown syntax colours (§6): headings lift to the heading token, structural marks (#, -, >, `)
 * recede to the marker token, links use the accent. Layered after basicSetup's default highlight so
 * these rules win for the tags they name.
 */
const editorHighlight = HighlightStyle.define([
  { tag: tags.heading, color: "var(--ed-heading)", fontWeight: "600" },
  { tag: tags.processingInstruction, color: "var(--ed-marker)" },
  { tag: tags.quote, color: "var(--ed-marker)" },
  { tag: tags.emphasis, fontStyle: "italic" },
  { tag: tags.strong, fontWeight: "700" },
  { tag: [tags.link, tags.url], color: "var(--accent)" },
  { tag: tags.monospace, color: "var(--ed-text)" },
]);

export interface EditorCallbacks {
  /** Fired ~120 ms after the last keystroke, with the full text and its new version. */
  onChange: (text: string, version: number) => void;
  /** Fired as the editor scrolls; scroll-sync reads the scroll position directly off the editor. */
  onScroll: () => void;
  /** Fired ~120 ms after scrolling stops — used to re-snap the preview precisely to the editor. */
  onScrollSettle: () => void;
  /** Fired when the cursor moves, with the 0-based line it is on (for active-line highlighting) and
   *  whether this was a pure navigation (caret move without a text edit) — used to gate the cross-pane
   *  reveal scroll, which must fire on selecting a line but not on every keystroke while typing. */
  onCursor: (line: number, navigated: boolean) => void;
  /** Fired as the mouse moves, with the 0-based line under the pointer (null when outside). */
  onHover: (line: number | null) => void;
  /** Fired when the editor's own geometry settles (wrap toggle, resize, font load) — re-sync heights. */
  onGeometryChange: () => void;
  /** Fired when the user attempts to modify the document while it is read-only (offer to start editing). */
  onEditAttempt: () => void;
  /** Fired when the editor gains focus — lets the toolbar route formatting to the active pane in Split. */
  onFocus: () => void;
  /** Fired with an http/https URL the author Ctrl/Cmd-clicked in the source, to open in the OS browser. */
  onOpenLink: (url: string) => void;
  /** Diagnostics for the height-sync reconcile path (T-084) — mirrors {@link HeightSync}'s own optional
   *  `onDebug`. Fired when {@link naturalLineTops}/{@link setSpacers} refuse a stale anchor instead of
   *  silently clamping it to the last line (see those methods). Optional: only wired where useful. */
  onDebug?: (summary: string) => void;
}

export class MarkdownEditor {
  private readonly view: EditorView;
  private readonly onChange: (text: string, version: number) => void;
  private readonly onScroll: () => void;
  private readonly onScrollSettle: () => void;
  private readonly onCursor: (line: number, navigated: boolean) => void;
  private readonly onHover: (line: number | null) => void;
  private readonly onGeometryChange: () => void;
  private readonly onEditAttempt: () => void;
  private readonly onFocus: () => void;
  private readonly onOpenLink: (url: string) => void;
  private readonly onDebug: ((summary: string) => void) | undefined;
  private readonly wrap = new Compartment();
  private readonly editable = new Compartment();
  private version = 0;
  // Edit-change notification, debounced: a burst of keystrokes coalesces into one onChange once
  // typing goes quiet (see debounce.ts). A field, so it exists before the EditorView's update
  // listener (wired in the constructor) can call it; the body reads onChange/version at fire time.
  private readonly scheduleChange = debounce(() => {
    this.version += 1;
    this.onChange(this.getText(), this.version);
  }, DEBOUNCE_MS);
  // Set by a silent setText (mirror from the formatted editor) to skip the resulting change
  // notification, so a mirrored update doesn't echo back out as an edit.
  private suppressChange = false;
  // The current synced active/hover source lines, remembered so a full-document setText can re-apply
  // them in the same transaction (a whole-doc replace would otherwise collapse/clear the decorations).
  private activeLineValue: number | null = null;
  private hoverLineValue: number | null = null;
  // The review/compare overlay marks, remembered so a whole-document setText re-applies them.
  private diffValue: DiffMark[] | null = null;
  // Monotonic id source for tracked image-insert markers (see markerField / trackPosition).
  private nextMarkerId = 0;
  // The last value topVisibleLineExact() successfully resolved via posAtCoords. Reused when a probe
  // lands mid-measure (posAtCoords returns null, e.g. during a layout rebuild) so a transient miss
  // does not report line 0 and yank the passive Split pane back to the top of the document.
  private lastTopVisibleLineExact = 0;

  constructor(parent: HTMLElement, callbacks: EditorCallbacks) {
    this.onChange = callbacks.onChange;
    this.onScroll = callbacks.onScroll;
    this.onScrollSettle = callbacks.onScrollSettle;
    this.onCursor = callbacks.onCursor;
    this.onHover = callbacks.onHover;
    this.onGeometryChange = callbacks.onGeometryChange;
    this.onEditAttempt = callbacks.onEditAttempt;
    this.onFocus = callbacks.onFocus;
    this.onOpenLink = callbacks.onOpenLink;
    this.onDebug = callbacks.onDebug;

    // The caret line is reported rAF-deferred so the resulting setActiveLine dispatch (cross-pane
    // sync) runs after this update listener, not re-entrantly within it.
    let cursorLine = 0;
    // Whether the latest caret report is a pure navigation (click / arrow), not a text edit. Only a
    // navigation triggers the passive pane's reveal scroll (see index.ts setActive); typing must not.
    let cursorNavigated = false;
    const reportCursor = rafThrottle(() => this.onCursor(cursorLine, cursorNavigated));

    const updates = EditorView.updateListener.of((update) => {
      const silent = update.docChanged && this.suppressChange;
      if (update.docChanged) {
        if (this.suppressChange) {
          this.suppressChange = false;
        } else {
          this.scheduleChange();
        }
      }
      // Report the caret line for highlight sync — but not for a silent mirror setText, which would
      // otherwise override the active line the originating (formatted) pane just set.
      if ((update.docChanged || update.selectionSet) && !silent) {
        cursorLine = update.state.doc.lineAt(update.state.selection.main.head).number - 1;
        cursorNavigated = !update.docChanged;
        reportCursor();
      }
      // The editor relaid out for a reason other than a content edit or our own spacer dispatch →
      // re-equalize. Two cases matter: a real relayout transaction (a wrap toggle), AND — with no
      // transaction — CodeMirror finishing an async re-measure that turned an *estimated* line height
      // into a measured one (T-062: a block below the viewport, especially a wrapped one, whose top
      // `naturalLineTops` first read as an underestimate, yielding an inflated spacer). We used to gate
      // on `transactions.length > 0` to swallow that transaction-less re-measure, because re-reconciling
      // on it looped apply→measure→apply forever. That loop is now broken at the source — HeightSync only
      // re-dispatches spacers when the computed set actually changed (naturalLineTops is spacer-invariant,
      // so a settled geometry reconciles to the identical set and stops) — so we can safely let the
      // re-measure through and the stale spacer self-corrects. We still ignore the geometryChanged that
      // OUR OWN setSpacers dispatch carries (its transaction holds a setSpacersEffect) and any docChanged
      // (an edit already schedules its own reconcile via onChange).
      if (
        update.geometryChanged &&
        !update.docChanged &&
        !update.transactions.some((tr) => tr.effects.some((effect) => effect.is(setSpacersEffect)))
      ) {
        this.onGeometryChange();
      }
    });

    this.view = new EditorView({
      parent,
      state: EditorState.create({
        doc: "",
        extensions: [
          basicSetup,
          // `markdown()`'s OWN default base is plain CommonMark (`commonmarkLanguage`) — GFM (tables,
          // strikethrough, autolink, task lists) is a SEPARATE exported language (`markdownLanguage`)
          // that must be opted into explicitly. Every other Markdown-aware piece of this app already
          // assumes GFM's grammar (pm-markdown.ts's schema has a strikethrough mark; md-format.ts
          // re-parses with `markdownLanguage` directly to find an enclosing Strikethrough/FencedCode
          // node) — the live editor's own tree must match, both so `~~struck~~` highights and so
          // {@link activeFormats} (T-100, which reads THIS live tree via `syntaxTree()`) can recognize a
          // Strikethrough node at all.
          markdown({ base: markdownLanguage }),
          keymap.of(formattingKeymapFor((command) => this.applyFormat(command))),
          editorTheme,
          syntaxHighlighting(editorHighlight),
          this.wrap.of(EditorView.lineWrapping),
          // Start read-only: a document is only editable after the author clicks Edit (which forks a
          // working branch). We use `readOnly` alone — NOT `editable: false` — so the caret, text
          // selection, keyboard navigation and copy all keep working; only modifications are blocked.
          // Programmatic dispatches (setText, image insert, spacers) still apply under readOnly.
          this.editable.of(EditorState.readOnly.of(true)),
          spacerField,
          hoverLineField,
          activeLineField,
          diffField,
          markerField,
          updates,
          // Ctrl/Cmd-click a link in the source opens it in the OS browser (the host re-validates the
          // scheme), matching the formatted view. Registered as a CodeMirror dom handler (not a
          // bubbling DOM listener on scrollDOM) so returning true consumes the event BEFORE
          // CodeMirror's modifier-click would add a second cursor; a modifier-click that is not on a
          // URL returns false and is left to CodeMirror as usual. Prec.highest so it runs first.
          Prec.highest(
            EditorView.domEventHandlers({
              mousedown: (event, view) => {
                // Primary-button modifier-click only: a middle/right click (even with a modifier) is
                // left to CodeMirror (e.g. so a right-click can raise the context menu).
                if (event.button !== 0 || !(event.metaKey || event.ctrlKey)) {
                  return false;
                }
                const pos = view.posAtCoords({ x: event.clientX, y: event.clientY });
                if (pos === null) {
                  return false;
                }
                const line = view.state.doc.lineAt(pos);
                const url = urlAtColumn(line.text, pos - line.from);
                if (url === null) {
                  return false;
                }
                event.preventDefault();
                this.onOpenLink(url);
                return true;
              },
            }),
          ),
        ],
      }),
    });

    // Live sync runs every frame (sub-line precise). When scrolling stops, fire a settle callback
    // so the preview can be re-snapped exactly to the editor's top — the live frames can lag a
    // momentum scroll's final resting position by a frame.
    const reportScroll = rafThrottle(() => this.onScroll());
    const reportScrollSettle = debounce(() => this.onScrollSettle(), SCROLL_SETTLE_MS);
    this.view.scrollDOM.addEventListener("scroll", () => {
      reportScroll();
      reportScrollSettle();
    });

    let hoverX = 0;
    let hoverY = 0;
    const reportHover = rafThrottle(() => {
      const pos = this.view.posAtCoords({ x: hoverX, y: hoverY });
      this.onHover(pos === null ? null : this.view.state.doc.lineAt(pos).number - 1);
    });
    this.view.scrollDOM.addEventListener("mousemove", (event) => {
      hoverX = event.clientX;
      hoverY = event.clientY;
      reportHover();
    });
    this.view.scrollDOM.addEventListener("mouseleave", () => this.onHover(null));

    // A `beforeinput` only fires for modifying actions (typing, deletion, paste) — never for caret
    // navigation. While read-only the change is blocked anyway, so we use it purely as the signal
    // that the author is trying to write and should be offered the chance to start a draft.
    this.view.contentDOM.addEventListener("beforeinput", () => {
      if (this.view.state.readOnly) {
        this.onEditAttempt();
      }
    });

    // Report focus so the formatting toolbar can route to this pane when it is the active one in Split.
    this.view.contentDOM.addEventListener("focus", () => this.onFocus());
  }

  /**
   * Apply a formatting-toolbar command to the source selection (Markdown text transform), then refocus
   * the editor. While read-only the change is blocked by `readOnly` and the author is offered a draft.
   */
  applyFormat(command: FormatCommand): void {
    if (this.view.state.readOnly) {
      this.onEditAttempt();
      return;
    }
    const { from, to } = this.view.state.selection.main;
    const edit = formatMarkdown(this.view.state.doc.toString(), from, to, command);
    this.view.dispatch({
      changes: { from: edit.from, to: edit.to, insert: edit.insert },
      selection: { anchor: edit.selectionStart, head: edit.selectionEnd },
    });
    this.view.focus();
  }

  /**
   * The toolbar commands active at the caret in the lang-markdown syntax tree — the source pane's
   * counterpart of pm-commands.ts's `activeFormats`, for the toolbar's pressed-button state in Code/
   * Split (lifting the historical "the source editor has no inline-mark notion" limitation). Walks every
   * ancestor of the caret's syntax node (Lezer's incremental reparse makes this cheap even on a large
   * document) and lights up each registry command whose {@link syntaxNodeNameFor} names one of them —
   * mirroring the PM tract's ancestor scan, so a bullet item nested in a blockquote lights up both.
   * Reads the caret HEAD only (not a selection range): unlike the PM tract's mark-coverage question
   * (T-100 Stage 1), the source tract's toolbar commands are per-line/per-node text transforms with no
   * analogous "is the mark on the WHOLE selection" question to answer.
   */
  activeFormats(): Set<FormatCommand> {
    const pos = this.view.state.selection.main.head;
    const names = new Set<string>();
    for (
      let node: SyntaxTreeNode | null = syntaxTree(this.view.state).resolveInner(pos, -1);
      node !== null;
      node = node.parent
    ) {
      names.add(node.name);
    }
    const result = new Set<FormatCommand>();
    for (const { id, kind } of FORMAT_REGISTRY) {
      if (names.has(syntaxNodeNameFor(kind))) {
        result.add(id);
      }
    }
    return result;
  }

  /**
   * Replace the whole document. By default triggers a normal change/render and drops any pending
   * image-insert markers — this is the "a genuinely different document is now current" path (a file
   * was opened/loaded), so mapping a marker through the wholesale replace would land it at an
   * arbitrary position in a document it was never captured against. insertAtMarker/discardMarker
   * already no-op gracefully once a marker is gone, so an in-flight round-trip simply drops its insert.
   *
   * Pass `silent` to suppress the resulting change notification — for any replace whose text the host
   * already knows about (a mode-switch hydration, or the initial `doc.loaded` hydration), so
   * CodeMirror's own onChange doesn't echo it straight back out as a new edit (which would otherwise
   * round-trip through the host as a no-op re-render). The live Split cross-pane sync no longer comes
   * through here at all — it uses {@link mirror}, a minimal changed-span transaction.
   *
   * `sameDocument` is a SEPARATE axis controlling the marker behavior, defaulting to `silent` (the two
   * usually coincide): pass/leave it true for re-hydrating a pane with the SAME logical content on a
   * mode switch — not a document change, so pending markers are kept, restored to their pre-transaction
   * positions (clamped to the new length) rather than dropped or blindly mapped through the blunt
   * whole-document change. `doc.loaded` is silent (the host already has this text) but NOT the same
   * document — pass `sameDocument: false` there so any marker left over from the previous document is
   * dropped rather than restored at a now-meaningless clamped position.
   */
  setText(text: string, silent = false, sameDocument = silent): void {
    this.suppressChange = silent;
    // sameDocument = mirroring the same logical content (Split mirror / mode-switch hydration): keep
    // pending image-insert markers, restored verbatim (see restoreMarkersEffect above) rather than
    // dropped. Not sameDocument = a genuinely different document (a file was opened/loaded) — drop any
    // pending markers, independently of whether the change notification itself is suppressed.
    const markerEffect = sameDocument
      ? restoreMarkersEffect.of(this.view.state.field(markerField))
      : clearMarkersEffect.of(null);
    // Re-apply the synced highlights in the same transaction: a whole-document replace would otherwise
    // map the active-line decoration to position 0 and clear the hover one (it drops on docChanged).
    this.view.dispatch({
      changes: { from: 0, to: this.view.state.doc.length, insert: text },
      effects: [
        setActiveLineEffect.of(this.activeLineValue),
        setHoverLineEffect.of(this.hoverLineValue),
        setDiffEffect.of(this.diffValue),
        markerEffect,
      ],
    });
    // Clear in case the text was identical and no docChanged fired to consume the flag.
    this.suppressChange = false;
  }

  /**
   * Mirror the sibling (formatted) pane's edit in with the SMALLEST change that reconciles the two —
   * the Split cross-pane sync (index.ts onFormattedChange). Unlike {@link setText}'s whole-document
   * replace, this dispatches a single changed-span transaction (common-prefix/suffix diff), so the
   * passive source editor's caret, selection, scroll anchor and tracked image-insert markers all remap
   * naturally through it: the caret no longer collapses to the replace boundary on every keystroke in
   * the other pane, and the marker mapping is ordinary `ChangeSet.mapPos` (the whole-document
   * restore-markers workaround is only for setText's mode-switch hydration now, never this hot path).
   *
   * Silent like the mirror always was — the change is not re-notified out as an edit (it originated in
   * the other pane) — and the synced active/hover/diff overlays are re-asserted, exactly as setText
   * does, since a docChange otherwise drops the hover highlight and the synced line must stay pinned.
   */
  mirror(text: string): void {
    const patch = computeTextPatch(this.getText(), text);
    if (patch === null) {
      return; // already identical — nothing to mirror
    }
    this.suppressChange = true;
    this.view.dispatch({
      changes: { from: patch.from, to: patch.to, insert: patch.insert },
      effects: [
        setActiveLineEffect.of(this.activeLineValue),
        setHoverLineEffect.of(this.hoverLineValue),
        setDiffEffect.of(this.diffValue),
      ],
    });
    // Clear defensively in case an identical-after-clamp change produced no docChanged to consume it.
    this.suppressChange = false;
  }

  getText(): string {
    return this.view.state.doc.toString();
  }

  /** Whether an edit has been typed here that hasn't been reported via `onChange` yet (still waiting
   *  out the debounce). The cross-pane mirror in index.ts checks this on the DESTINATION pane before a
   *  silent `setText`, so a same-instant edit there isn't clobbered by a stale mirror from the sibling
   *  pane's own (earlier-started, now-firing) debounce. */
  hasPendingChange(): boolean {
    return this.scheduleChange.pending;
  }

  /** The editor's editable DOM element — where image paste/drop is captured. */
  get contentDOM(): HTMLElement {
    return this.view.contentDOM;
  }

  /** Current cursor position (used as the insert point for a pasted image). */
  selectionHead(): number {
    return this.view.state.selection.main.head;
  }

  /** Document position at the given client coordinates, or null (used for a drop point). */
  posAtCoords(x: number, y: number): number | null {
    return this.view.posAtCoords({ x, y });
  }

  /** Insert text at a position and place the cursor after it. */
  insertAt(pos: number, text: string): void {
    const clamped = Math.max(0, Math.min(pos, this.view.state.doc.length));
    this.view.dispatch({
      changes: { from: clamped, insert: text },
      selection: { anchor: clamped + text.length },
    });
  }

  /**
   * Register `pos` as a tracked marker (T-034/M-21) and return its id. The position is remapped
   * through every subsequent edit — typing, another marker's resolution, … — until {@link
   * insertAtMarker} (or {@link discardMarker}) consumes it. Use this instead of a raw captured
   * position whenever the eventual insert follows an async round-trip (image paste/drop): by the
   * time the reply arrives, a plain number captured up front may point at the wrong place, or —
   * for several images captured at the same spot — collide with another pending insert.
   */
  trackPosition(pos: number): number {
    const id = this.nextMarkerId++;
    const clamped = Math.max(0, Math.min(pos, this.view.state.doc.length));
    this.view.dispatch({ effects: setMarkerEffect.of({ id, pos: clamped }) });
    return id;
  }

  /**
   * Insert text at the marker's current (remapped) position, place the cursor after it, and clear
   * the marker. A no-op if the marker no longer exists (already resolved/discarded, or dropped by a
   * whole-document {@link setText} in the meantime) — an in-flight round-trip that loses its race
   * with a document switch simply drops its insert rather than landing somewhere meaningless.
   */
  insertAtMarker(id: number, text: string): void {
    const pos = this.view.state.field(markerField).get(id);
    if (pos === undefined) {
      return;
    }
    this.view.dispatch({
      changes: { from: pos, insert: text },
      selection: { anchor: pos + text.length },
      effects: setMarkerEffect.of({ id, pos: null }),
    });
  }

  /** Discard a tracked marker without inserting (e.g. the host round-trip failed or was empty). */
  discardMarker(id: number): void {
    this.view.dispatch({ effects: setMarkerEffect.of({ id, pos: null }) });
  }

  /**
   * The source line at the top of the editor viewport as a fractional 0-based line: the integer
   * part is the line, the fractional part is how far the viewport top has scrolled into that
   * line's block. Used for sub-line-precise scroll-sync so the preview's top edge lines up with
   * the editor's instead of snapping to the nearest whole line (the cause of the residual drift).
   *
   * The probe point is the left edge of the CONTENT area (`contentDOM`, not `scrollDOM`, whose
   * rect includes the line-number gutter) — probing inside the gutter leaves `posAtCoords`'s
   * result unspecified. When `posAtCoords` misses (e.g. mid-measure, during a layout rebuild) the
   * last successfully resolved value is returned instead of 0, so a transient miss doesn't yank
   * the passive Split pane back to the top of the document (T-064).
   */
  topVisibleLineExact(): number {
    const scrollTop = this.view.scrollDOM.scrollTop;
    const rect = this.view.contentDOM.getBoundingClientRect();
    const pos = this.posAtCoords(rect.left + 1, rect.top + 1);
    if (pos === null) {
      return this.lastTopVisibleLineExact;
    }
    const lineNumber = this.view.state.doc.lineAt(pos).number;
    const block = this.view.lineBlockAt(this.view.state.doc.line(lineNumber).from);
    const fraction = block.height > 0 ? (scrollTop - block.top) / block.height : 0;
    const exact = lineNumber - 1 + Math.min(Math.max(fraction, 0), 1);
    this.lastTopVisibleLineExact = exact;
    return exact;
  }

  /**
   * The 0-based source line whose block currently sits at the top of the editor viewport, resolved
   * through CodeMirror's height map (`lineBlockAtHeight`) rather than `posAtCoords`/
   * `getBoundingClientRect` — so, unlike {@link topVisibleLineExact}, this stays accurate even where
   * real layout/painting isn't available (e.g. under jsdom in tests). Used by {@link HeightSync} (T-066)
   * as the fixed reference point for compensating `scrollTop` when the spacer set changes: whichever
   * spacers sit above this line are the ones whose height change must be added to `scrollTop` so the
   * content already at the viewport top does not visibly jump.
   */
  topVisibleLine(): number {
    const block = this.view.lineBlockAtHeight(this.view.scrollDOM.scrollTop);
    return this.view.state.doc.lineAt(block.from).number - 1;
  }

  /**
   * Nudge `scrollTop` by `delta` pixels, synchronously — used right after {@link setSpacers} dispatches
   * a spacer set whose weight above the viewport changed, so the compensation lands in the very same
   * frame as the spacer change and the two are visually indistinguishable from one atomic update.
   */
  adjustScrollTop(delta: number): void {
    this.view.scrollDOM.scrollTop += delta;
  }

  /** This pane's current scroll offset — the coordinator (sync-coordinator.ts) reads it to detect its own
   *  echo (a scroll settling on a value the coordinator just wrote). */
  scrollTop(): number {
    return this.view.scrollDOM.scrollTop;
  }

  /**
   * Set the scroll offset directly and SYNCHRONOUSLY — the coordinator's one write per pane. Direct (not
   * CodeMirror's asynchronous `scrollIntoView`) so the resulting scrollTop can be read straight back for
   * echo detection; the same `scrollDOM.scrollTop` write {@link adjustScrollTop} already relies on.
   */
  setScrollTop(px: number): void {
    this.view.scrollDOM.scrollTop = px;
  }

  /** The (fractional) 0-based source line at the viewport top — the coordinator's per-line-precise read
   *  for coupling (delegates to {@link topVisibleLineExact}). */
  topLine(): number {
    return this.topVisibleLineExact();
  }

  /**
   * The ACTUAL pixel top (including any height-sync spacers above it) of each given 0-based source line —
   * the anchors the coordinator builds this pane's line↔px map from (sync-coordinator.ts). Unlike
   * {@link naturalLineTops} this does NOT subtract the spacers: the map must reflect where each line
   * really sits so scroll coupling accounts for the padded layout. An out-of-range line clamps to the
   * nearest valid line's top (a scroll map is best-effort; a stale anchor just reads the document edge).
   */
  topsForLines(lines: readonly number[]): number[] {
    return lines.map((line) => {
      const cmLine = Math.min(Math.max(line + 1, 1), this.view.state.doc.lines);
      return this.view.lineBlockAt(this.view.state.doc.line(cmLine).from).top;
    });
  }

  /**
   * Scroll so the given (fractional) 0-based source line sits at the viewport top, directly and
   * synchronously (the mode-switch restore; self-contained, so it works while the sibling pane is
   * hidden). The fractional part is interpolated across the line's block — the inverse of
   * {@link topVisibleLineExact}.
   */
  scrollToLine(line: number): void {
    const line0 = Math.floor(line);
    const cmLine = Math.min(Math.max(line0 + 1, 1), this.view.state.doc.lines);
    const block = this.view.lineBlockAt(this.view.state.doc.line(cmLine).from);
    const fraction = Math.max(0, Math.min(1, line - line0));
    this.view.scrollDOM.scrollTop = block.top + fraction * block.height;
  }

  /**
   * Scroll the editor the minimum amount so the given 0-based source line is visible (no-op if it already
   * is), directly and synchronously. Used in Split to reveal the synced active-line highlight when
   * accumulated block-height drift pushed it outside this pane's viewport while the user works in the
   * other pane. Unlike {@link scrollToLine} (which snaps the line to the top) this is "nearest" — the
   * least move — and direct-scrollTop so the coordinator can record it as its own write (echo-free).
   */
  reveal(line: number): void {
    const block = this.view.lineBlockAt(this.view.state.doc.line(this.clampLine(line)).from);
    const scroller = this.view.scrollDOM;
    const margin = 8; // breathing room from the pane edge when we do scroll
    if (block.top < scroller.scrollTop) {
      scroller.scrollTop = block.top - margin;
    } else if (block.bottom > scroller.scrollTop + scroller.clientHeight) {
      scroller.scrollTop = block.bottom - scroller.clientHeight + margin;
    }
    // Already fully visible → no-op.
  }

  /**
   * Natural top offset (excluding our spacer widgets) of each given 0-based source line. Computed
   * as CodeMirror's actual block top MINUS the spacers currently above that line (read live from
   * the decoration set). This makes "natural" independent of whether spacers are applied — a true
   * fixed point — so reconciling does not oscillate. (A prefix sum of line heights had counted the
   * block widgets, creating a measure→apply→measure feedback loop.)
   *
   * This fixed-point property holds for the FIRST anchor too, which is what keeps the height-sync
   * lead stable (T-061): see {@link spacerHeightsAbove} for why the leading spacer is (correctly) not
   * subtracted at pos 0.
   *
   * A `line` outside the CURRENT document (T-084) yields `null` at that index rather than being
   * silently clamped to the last line: on the height-sync reconcile path a line comes from the sibling
   * (formatted) pane's `blockGeometry()`, so an out-of-range line means that anchor was minted against
   * a document this editor no longer has — clamping it would measure the wrong line and produce a
   * spacer on it instead of surfacing the mismatch. {@link HeightSync.reconcile} refuses to apply the
   * whole anchor set when any come back `null` (see its pane-consistency gate) rather than build spacers
   * off a partially-stale set.
   */
  naturalLineTops(lines: number[]): (number | null)[] {
    // Build the spacer prefix sums ONCE for the whole batch, then query per anchor — O(spacers) to build
    // + O(anchors · log spacers) to query, replacing the former per-anchor scan of the entire spacer set
    // (O(anchors × spacers), which made a long-document reconcile quadratic on the hot path — T-072).
    const spacerAbove = this.spacerHeightsAbove();
    return lines.map((line) => {
      if (!this.isValidLine(line)) {
        this.onDebug?.(
          `height-sync: refused stale anchor line ${line} (doc has ${this.view.state.doc.lines} lines)`,
        );
        return null;
      }
      const pos = this.view.state.doc.line(line + 1).from;
      return this.view.lineBlockAt(pos).top - spacerAbove(pos);
    });
  }

  /** Whether 0-based `line` resolves within the CURRENT document — the check {@link naturalLineTops}
   *  and {@link setSpacers} use INSTEAD OF {@link clampLine}'s silent clamp on the reconcile path
   *  (T-084): an anchor line minted against a sibling pane's now-diverged document is a symptom of
   *  staleness, not a value worth relocating to the nearest valid line. */
  private isValidLine(line: number): boolean {
    return line >= 0 && line < this.view.state.doc.lines;
  }

  /**
   * A prefix-sum view of the current spacer widgets, built in ONE pass over the spacer decoration set,
   * returning a query for the total spacer height STRICTLY above a document position (a spacer at `from`
   * counts when `from < pos`). {@link naturalLineTops} builds it once per batch and queries it per anchor
   * — the replacement for the former per-anchor full-set scan (O(anchors × spacers) → O(spacers) build +
   * O(log spacers) per query).
   *
   * The `from < pos` boundary is load-bearing and preserved exactly from the old scan: the leading spacer
   * (a block widget at position 0, side −1) is therefore NOT counted for the first anchor (pos 0), and
   * that is correct: CodeMirror folds a leading block widget into the first line's own block as its
   * `spaceAbove`, and `lineBlockAt(0).top` reports the TOP of that combined region (the widget's top, i.e.
   * the document origin) — NOT the text top below the widget. So `lineBlockAt(0).top` is invariant to the
   * lead height (confirmed by instrumenting `@codemirror/view`: `HeightMapText.spaceAbove` +
   * `BlockInfo.join`, which keeps the joined block's `top` at the space block's top). Counting the lead
   * here would subtract a height the measurement never included, driving `naturalLineTops[0]` negative and
   * the computed lead to grow without bound between reconciles. Every OTHER anchor sits strictly below the
   * lead, so `from < pos` correctly subtracts it for them.
   */
  private spacerHeightsAbove(): (pos: number) => number {
    // Spacer froms ascending (RangeSet.iter yields in position order), with a running height sum:
    // cumulative[i] is the total height of the first i spacers (froms[0..i−1]).
    const froms: number[] = [];
    const cumulative: number[] = [0];
    const cursor = this.view.state.field(spacerField).iter();
    while (cursor.value !== null) {
      // CodeMirror types Decoration.spec as `any`; read its widget through `unknown` (no cast) and let
      // the `instanceof` below validate it.
      const spec: unknown = cursor.value.spec;
      const widget = isRecord(spec) ? spec.widget : undefined;
      if (widget instanceof SpacerWidget) {
        froms.push(cursor.from);
        cumulative.push((cumulative[cumulative.length - 1] ?? 0) + widget.height);
      }
      cursor.next();
    }
    // The number of spacers with `from < pos` indexes straight into the cumulative sum.
    return (pos: number): number => cumulative[countLessThan(froms, pos)] ?? 0;
  }

  /**
   * Replace the spacer decorations (height-sync). Block spacers sit below each block's last source
   * line; the optional leading spacer sits above the first line so the first block aligns.
   *
   * A spacer whose `lineEnd` falls outside the CURRENT document (T-084) is dropped rather than
   * silently clamped to the last line — clamping would plant it under whatever line happens to be
   * last, producing a spacer on the wrong line instead of surfacing the staleness (a symptom of the
   * caller applying anchors from a since-diverged sibling document). {@link HeightSync.reconcile}'s
   * pane-consistency gate is expected to keep this from happening in practice; this is defense in depth
   * for the direct caller of `setSpacers`.
   */
  setSpacers(spacers: EditorSpacer[], leadingHeight = 0): void {
    const ranges: Range<Decoration>[] = [];
    for (const spacer of spacers) {
      if (!this.isValidLine(spacer.lineEnd)) {
        this.onDebug?.(
          `height-sync: refused stale spacer at line ${spacer.lineEnd} (doc has ${this.view.state.doc.lines} lines)`,
        );
        continue;
      }
      ranges.push(
        Decoration.widget({
          widget: new SpacerWidget(spacer.height),
          block: true,
          side: 1,
        }).range(this.view.state.doc.line(spacer.lineEnd + 1).to),
      );
    }
    if (leadingHeight > 0) {
      ranges.push(
        Decoration.widget({
          widget: new SpacerWidget(leadingHeight, true),
          block: true,
          side: -1,
        }).range(0),
      );
    }
    this.view.dispatch({ effects: setSpacersEffect.of(Decoration.set(ranges, true)) });
  }

  /** Faintly highlight the source line under the mouse (null clears it). */
  setHoverLine(line: number | null): void {
    this.hoverLineValue = line;
    this.view.dispatch({ effects: setHoverLineEffect.of(line) });
  }

  /** Highlight the active source line (the caret line); null clears it. Driven externally so the
   *  active line can be synchronized with the formatted pane in Split. */
  setActiveLine(line: number | null): void {
    this.activeLineValue = line;
    this.view.dispatch({ effects: setActiveLineEffect.of(line) });
  }

  /** Show the review/compare overlay: highlight each changed source line by kind and mark removed
   *  blocks. The marks are remembered so a silent whole-document setText (the Split mirror) keeps them. */
  setDiff(marks: DiffMark[]): void {
    this.diffValue = marks;
    this.view.dispatch({ effects: setDiffEffect.of(marks) });
  }

  /** Clear the review/compare overlay. */
  clearDiff(): void {
    this.diffValue = null;
    this.view.dispatch({ effects: setDiffEffect.of(null) });
  }

  /** Wrapping width of the editor (its scroller's client width) — for diagnostics. */
  contentWidth(): number {
    return this.view.scrollDOM.clientWidth;
  }

  /**
   * Force CodeMirror to re-measure its geometry. Needed after the editor returns from `display:none`
   * to a new width (a view-mode switch): wrapping must reflow before `topVisibleLineExact()` and
   * `scrollToLine()` are read against fresh layout.
   */
  refresh(): void {
    this.view.requestMeasure();
  }

  /** Move keyboard focus into the source editor (used by the skip-to-editor link). */
  focus(): void {
    this.view.focus();
  }

  /** Toggle soft line wrapping. Off = long lines stay on one row (horizontal scroll). */
  setLineWrapping(enabled: boolean): void {
    this.view.dispatch({
      effects: this.wrap.reconfigure(enabled ? EditorView.lineWrapping : []),
    });
  }

  /**
   * Allow or block user editing. Read-only until the author starts editing (which forks a working
   * branch), but the caret, selection and navigation stay available the whole time — only document
   * modifications are blocked. Programmatic changes (setText, image insert, spacers) still apply.
   */
  setEditable(enabled: boolean): void {
    this.view.dispatch({
      effects: this.editable.reconfigure(enabled ? [] : EditorState.readOnly.of(true)),
    });
  }

  /** Clamp a (possibly FRACTIONAL) 0-based line to the nearest valid 1-based CM line — appropriate for a
   *  user/programmatic scroll TARGET (`reveal`), where landing on the nearest line is the right degraded
   *  behaviour for an out-of-range request. The input is floored first: the shared scroll-sync contract is
   *  a fractional 0-based line (T-065), and `doc.line()` demands an integer, so a fractional line reveals
   *  the block it falls in rather than throwing a RangeError. NOT used on the height-sync reconcile path
   *  (`naturalLineTops`, `setSpacers`, T-084): there, an out-of-range line means the anchor was minted
   *  against a since-diverged sibling document, and clamping would silently measure/pad the wrong line
   *  instead of refusing it — see {@link isValidLine}. */
  private clampLine(line: number): number {
    return Math.min(Math.max(Math.floor(line) + 1, 1), this.view.state.doc.lines);
  }
}
