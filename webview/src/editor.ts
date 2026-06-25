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
import { Compartment, EditorState, Prec, StateEffect, StateField } from "@codemirror/state";
import { Decoration, type DecorationSet, EditorView, WidgetType } from "@codemirror/view";
import { tags } from "@lezer/highlight";
import { basicSetup } from "codemirror";
import type { EditorSpacer } from "./height-sync.js";
import { urlAtColumn } from "./links.js";
import { type FormatCommand, formatMarkdown } from "./md-format.js";
import { rafThrottle } from "./raf.js";

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

  eq(other: SpacerWidget): boolean {
    return other.height === this.height && other.isLead === this.isLead;
  }

  toDOM(): HTMLElement {
    const element = document.createElement("div");
    element.className = "cm-sync-spacer";
    element.style.height = `${this.height}px`;
    element.setAttribute("aria-hidden", "true");
    return element;
  }

  get estimatedHeight(): number {
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
        if (effect.value === null) {
          return Decoration.none;
        }
        const lineNumber = Math.min(Math.max(effect.value + 1, 1), tr.state.doc.lines);
        const line = tr.state.doc.line(lineNumber);
        return Decoration.set([Decoration.line({ class: "cm-active-line" }).range(line.from)]);
      }
    }
    return decorations.map(tr.changes);
  },
  provide: (field) => EditorView.decorations.from(field),
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
  private timer: ReturnType<typeof setTimeout> | undefined;
  // Set by a silent setText (mirror from the formatted editor) to skip the resulting change
  // notification, so a mirrored update doesn't echo back out as an edit.
  private suppressChange = false;
  // The current synced active/hover source lines, remembered so a full-document setText can re-apply
  // them in the same transaction (a whole-doc replace would otherwise collapse/clear the decorations).
  private activeLineValue: number | null = null;
  private hoverLineValue: number | null = null;

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
    let scrollSettleTimer: ReturnType<typeof setTimeout> | undefined;
    this.view.scrollDOM.addEventListener("scroll", () => {
      reportScroll();
      if (scrollSettleTimer !== undefined) {
        clearTimeout(scrollSettleTimer);
      }
      scrollSettleTimer = setTimeout(() => this.onScrollSettle(), SCROLL_SETTLE_MS);
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
   * Replace the whole document. By default triggers a normal change/render (used when a file is
   * opened). Pass `silent` to suppress the change notification — used when mirroring text in from the
   * formatted editor in split, so the mirror doesn't echo back out as a new edit.
   */
  setText(text: string, silent = false): void {
    this.suppressChange = silent;
    // Re-apply the synced highlights in the same transaction: a whole-document replace would otherwise
    // map the active-line decoration to position 0 and clear the hover one (it drops on docChanged).
    this.view.dispatch({
      changes: { from: 0, to: this.view.state.doc.length, insert: text },
      effects: [
        setActiveLineEffect.of(this.activeLineValue),
        setHoverLineEffect.of(this.hoverLineValue),
      ],
    });
    // Clear in case the text was identical and no docChanged fired to consume the flag.
    this.suppressChange = false;
  }

  getText(): string {
    return this.view.state.doc.toString();
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
      const widget = (cursor.value.spec as { widget?: unknown }).widget;
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

  private scheduleChange(): void {
    if (this.timer !== undefined) {
      clearTimeout(this.timer);
    }
    this.timer = setTimeout(() => {
      this.timer = undefined;
      this.version += 1;
      this.onChange(this.getText(), this.version);
    }, DEBOUNCE_MS);
  }
}
