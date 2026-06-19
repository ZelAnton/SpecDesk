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
import { Compartment, EditorState, StateEffect, StateField } from "@codemirror/state";
import { Decoration, type DecorationSet, EditorView, WidgetType } from "@codemirror/view";
import { tags } from "@lezer/highlight";
import { basicSetup } from "codemirror";
import type { EditorSpacer } from "./height-sync.js";
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
  ".cm-activeLineGutter": {
    color: "var(--accent)",
    backgroundColor: "transparent",
  },
  ".cm-activeLine": {
    backgroundColor: "var(--ed-active)",
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
  /** Fired when the cursor moves, with the 0-based line it is on (for active-line highlighting). */
  onCursor: (line: number) => void;
  /** Fired as the mouse moves, with the 0-based line under the pointer (null when outside). */
  onHover: (line: number | null) => void;
  /** Fired when the editor's own geometry settles (wrap toggle, resize, font load) — re-sync heights. */
  onGeometryChange: () => void;
  /** Fired when the user attempts to modify the document while it is read-only (offer to start editing). */
  onEditAttempt: () => void;
}

export class MarkdownEditor {
  private readonly view: EditorView;
  private readonly onChange: (text: string, version: number) => void;
  private readonly onScroll: () => void;
  private readonly onScrollSettle: () => void;
  private readonly onCursor: (line: number) => void;
  private readonly onHover: (line: number | null) => void;
  private readonly onGeometryChange: () => void;
  private readonly onEditAttempt: () => void;
  private readonly wrap = new Compartment();
  private readonly editable = new Compartment();
  private version = 0;
  private timer: ReturnType<typeof setTimeout> | undefined;

  constructor(parent: HTMLElement, callbacks: EditorCallbacks) {
    this.onChange = callbacks.onChange;
    this.onScroll = callbacks.onScroll;
    this.onScrollSettle = callbacks.onScrollSettle;
    this.onCursor = callbacks.onCursor;
    this.onHover = callbacks.onHover;
    this.onGeometryChange = callbacks.onGeometryChange;
    this.onEditAttempt = callbacks.onEditAttempt;

    const updates = EditorView.updateListener.of((update) => {
      if (update.docChanged) {
        this.scheduleChange();
      }
      if (update.docChanged || update.selectionSet) {
        const head = update.state.selection.main.head;
        this.onCursor(update.state.doc.lineAt(head).number - 1);
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
          updates,
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
  }

  /** Replace the whole document (used when a file is opened). Triggers a normal change/render. */
  setText(text: string): void {
    this.view.dispatch({
      changes: { from: 0, to: this.view.state.doc.length, insert: text },
    });
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

  /** Current vertical scroll offset (pixels from content top) — the scroll-map's editor coordinate. */
  scrollTopValue(): number {
    return this.view.scrollDOM.scrollTop;
  }

  /**
   * Set the vertical scroll offset directly (pixels). A fractional value is kept (not rounded): the
   * scroll map is deterministic so there is no shimmer, and letting the browser snap to device
   * pixels is smoother than quantizing to whole CSS pixels on HiDPI displays.
   */
  setScrollTop(px: number): void {
    this.view.scrollDOM.scrollTop = px;
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
    this.view.dispatch({ effects: setHoverLineEffect.of(line) });
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
