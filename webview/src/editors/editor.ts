/**
 * The CodeMirror 6 source editor. Stays deliberately thin: it owns the document text, emits
 * debounced change notifications carrying a monotonic version, reports the source line at the top
 * of its viewport for scroll-sync, and can scroll itself to a given source line. No Markdown
 * logic lives here — that is all native (docs/design/05-live-preview.md).
 *
 * It also hosts the editor side of height-synced scroll: block-widget "spacer" decorations that
 * pad source regions so each rendered block lines up vertically with its source (see height-sync.ts).
 */

import { markdown } from "@codemirror/lang-markdown";
import { HighlightStyle, syntaxHighlighting } from "@codemirror/language";
import {
  Compartment,
  EditorState,
  Prec,
  type Range,
  StateEffect,
  StateField,
} from "@codemirror/state";
import { Decoration, type DecorationSet, EditorView, WidgetType } from "@codemirror/view";
import { tags } from "@lezer/highlight";
import { basicSetup } from "codemirror";
import { applyWordDiff, removedMarkerLabel } from "../review/diff-decoration.js";
import type { DiffMark } from "../review/diff-marks.js";
import type { EditorSpacer } from "../sync/height-sync.js";
import { debounce } from "../util/debounce.js";
import { urlAtColumn } from "../util/links.js";
import { rafThrottle } from "../util/raf.js";
import { isRecord } from "../wire/decoders.js";
import { type FormatCommand, formatMarkdown } from "./md-format.js";

const DEBOUNCE_MS = 120;
/** Idle gap after the last scroll event before we treat scrolling as finished and re-snap. */
const SCROLL_SETTLE_MS = 120;

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

/** A block widget standing in for a removed block (which is absent from the head document). */
class RemovedWidget extends WidgetType {
  constructor(readonly text: string) {
    super();
  }

  override eq(other: RemovedWidget): boolean {
    return other.text === this.text;
  }

  toDOM(): HTMLElement {
    const element = document.createElement("div");
    element.className = "cm-diff-removed-marker";
    element.setAttribute("aria-hidden", "true");
    element.textContent = removedMarkerLabel(this.text);
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

function buildDiffDecorations(state: EditorState, marks: DiffMark[]): DecorationSet {
  const lineCount = state.doc.lines;
  const ranges: Range<Decoration>[] = [];
  for (const mark of marks) {
    if (mark.kind === "removed") {
      const widget = new RemovedWidget(mark.removedText);
      if (mark.anchorLine >= lineCount) {
        // Deleted past the last head line — a marker below the last line.
        ranges.push(
          Decoration.widget({ widget, block: true, side: 1 }).range(state.doc.line(lineCount).to),
        );
      } else {
        // A marker above the line the deleted block sat before.
        const line = state.doc.line(Math.max(mark.anchorLine + 1, 1));
        ranges.push(Decoration.widget({ widget, block: true, side: -1 }).range(line.from));
      }
      continue;
    }
    const cls = `cm-diff-${mark.kind}`;
    const start = Math.min(Math.max(mark.lineStart + 1, 1), lineCount);
    const end = Math.min(Math.max(mark.lineEnd + 1, 1), lineCount);
    for (let n = start; n <= end; n++) {
      ranges.push(Decoration.line({ class: cls }).range(state.doc.line(n).from));
    }
    // On a granular changed paragraph/heading, refine the line wash with inline word highlights. The
    // wash stays as the block-level signal (the Code pane has no annotation pill); too-significant or
    // sub-block (row/item) marks keep just the wash.
    if (mark.kind === "changed" && mark.sub !== true && mark.baseSource !== undefined) {
      pushInlineSourceWords(ranges, state, mark.baseSource, start, end);
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
// via clearMarkersEffect (there is nothing left in the new document for them to mean); mirroring the
// SAME logical content (the Split mirror, a mode-switch hydration) instead restores the pre-transaction
// positions verbatim via restoreMarkersEffect (clamped to the new length) — content is unchanged or only
// locally edited by the sibling pane, so the captured position is still the closest available estimate,
// unlike the blunt mapPos result.
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
      // The editor relaid out because of a real transaction other than a content edit or our own
      // spacer dispatch (i.e. a wrap toggle) → re-equalize. We require a transaction so we ignore
      // CodeMirror's internal re-measure of our just-applied spacers (which fires geometryChanged
      // with no transaction) — that would otherwise loop apply→measure→apply forever (flicker).
      if (
        update.geometryChanged &&
        update.transactions.length > 0 &&
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
          markdown(),
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
   * Replace the whole document. By default triggers a normal change/render and drops any pending
   * image-insert markers — this is the "a genuinely different document is now current" path (a file
   * was opened/loaded), so mapping a marker through the wholesale replace would land it at an
   * arbitrary position in a document it was never captured against. insertAtMarker/discardMarker
   * already no-op gracefully once a marker is gone, so an in-flight round-trip simply drops its insert.
   *
   * Pass `silent` for the OTHER use of a whole-document replace: mirroring the SAME logical content in
   * from the sibling surface (the Split mirror, or hydrating a pane on a mode switch) — not a document
   * change, so pending markers are kept, restored to their pre-transaction positions (clamped to the
   * new length) rather than dropped or blindly mapped through the blunt whole-document change. This
   * also suppresses the change notification so the mirror doesn't echo back out as a new edit.
   */
  setText(text: string, silent = false): void {
    this.suppressChange = silent;
    // Silent = mirroring the same logical content (Split mirror / mode-switch hydration): keep pending
    // image-insert markers, restored verbatim (see restoreMarkersEffect above) rather than dropped.
    // Non-silent = a genuinely different document (a file was opened) — drop any pending markers.
    const markerEffect = silent
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
   */
  topVisibleLineExact(): number {
    const scrollTop = this.view.scrollDOM.scrollTop;
    const rect = this.view.scrollDOM.getBoundingClientRect();
    const pos = this.view.posAtCoords({ x: rect.left + 1, y: rect.top + 1 });
    if (pos === null) {
      return 0;
    }
    const lineNumber = this.view.state.doc.lineAt(pos).number;
    const block = this.view.lineBlockAt(this.view.state.doc.line(lineNumber).from);
    const fraction = block.height > 0 ? (scrollTop - block.top) / block.height : 0;
    return lineNumber - 1 + Math.min(Math.max(fraction, 0), 1);
  }

  /** Scroll so the given 0-based source line is at the top of the viewport. */
  scrollToSourceLine(line: number): void {
    const target = this.view.state.doc.line(this.clampLine(line)).from;
    this.view.dispatch({ effects: EditorView.scrollIntoView(target, { y: "start" }) });
  }

  /**
   * Scroll the editor the minimum amount so the given 0-based source line is visible (no-op if it
   * already is). Used in Split to reveal the synced active-line highlight when accumulated block-height
   * drift pushed it outside this pane's viewport while the user works in the other pane. Unlike
   * {@link scrollToSourceLine} (which snaps the line to the top) this uses "nearest" — the least move.
   */
  revealSourceLine(line: number): void {
    const target = this.view.state.doc.line(this.clampLine(line)).from;
    this.view.dispatch({ effects: EditorView.scrollIntoView(target, { y: "nearest" }) });
  }

  /**
   * Natural top offset (excluding our spacer widgets) of each given 0-based source line. Computed
   * as CodeMirror's actual block top MINUS the spacers currently above that line (read live from
   * the decoration set). This makes "natural" independent of whether spacers are applied — a true
   * fixed point — so reconciling does not oscillate. (A prefix sum of line heights had counted the
   * block widgets, creating a measure→apply→measure feedback loop.)
   */
  naturalLineTops(lines: number[]): number[] {
    return lines.map((line) => {
      const pos = this.view.state.doc.line(this.clampLine(line)).from;
      return this.view.lineBlockAt(pos).top - this.spacerHeightAbove(pos);
    });
  }

  /**
   * Total height of spacer widgets above a document position. A spacer at <c>from</c> counts when
   * <c>from &lt; pos</c>. Crucially the leading spacer (at position 0) is therefore NOT counted for
   * the first anchor (pos 0) — CodeMirror's `lineBlockAt(0).top` does not include it, so counting it
   * there would over-subtract and make the computed lead grow on every edit.
   */
  private spacerHeightAbove(pos: number): number {
    let total = 0;
    const cursor = this.view.state.field(spacerField).iter();
    while (cursor.value !== null) {
      // CodeMirror types Decoration.spec as `any`; read its widget through `unknown` (no cast) and let
      // the `instanceof` below validate it.
      const spec: unknown = cursor.value.spec;
      const widget = isRecord(spec) ? spec.widget : undefined;
      if (widget instanceof SpacerWidget && cursor.from < pos) {
        total += widget.height;
      }
      cursor.next();
    }
    return total;
  }

  /**
   * Replace the spacer decorations (height-sync). Block spacers sit below each block's last source
   * line; the optional leading spacer sits above the first line so the first block aligns.
   */
  setSpacers(spacers: EditorSpacer[], leadingHeight = 0): void {
    const ranges = spacers.map((spacer) =>
      Decoration.widget({
        widget: new SpacerWidget(spacer.height),
        block: true,
        side: 1,
      }).range(this.view.state.doc.line(this.clampLine(spacer.lineEnd)).to),
    );
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
   * `scrollToSourceLine()` are read against fresh layout.
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

  private clampLine(line: number): number {
    return Math.min(Math.max(line + 1, 1), this.view.state.doc.lines);
  }
}
