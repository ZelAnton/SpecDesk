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
import { Compartment, EditorState, StateEffect, StateField } from "@codemirror/state";
import { Decoration, type DecorationSet, EditorView, WidgetType } from "@codemirror/view";
import { basicSetup } from "codemirror";
import type { EditorSpacer } from "./height-sync.js";
import { rafThrottle } from "./raf.js";

const DEBOUNCE_MS = 120;

/** A zero-content block of a fixed pixel height, inserted to match a taller rendered block. */
class SpacerWidget extends WidgetType {
  constructor(readonly height: number) {
    super();
  }

  eq(other: SpacerWidget): boolean {
    return other.height === this.height;
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

export interface EditorCallbacks {
  /** Fired ~120 ms after the last keystroke, with the full text and its new version. */
  onChange: (text: string, version: number) => void;
  /** Fired as the editor scrolls, with the 0-based source line at the viewport top. */
  onScroll: (topLine: number) => void;
  /** Fired when the cursor moves, with the 0-based line it is on (for active-line highlighting). */
  onCursor: (line: number) => void;
  /** Fired as the mouse moves, with the 0-based line under the pointer (null when outside). */
  onHover: (line: number | null) => void;
  /** Fired when the editor's own geometry settles (wrap toggle, resize, font load) — re-sync heights. */
  onGeometryChange: () => void;
}

export class MarkdownEditor {
  private readonly view: EditorView;
  private readonly onChange: (text: string, version: number) => void;
  private readonly onScroll: (topLine: number) => void;
  private readonly onCursor: (line: number) => void;
  private readonly onHover: (line: number | null) => void;
  private readonly onGeometryChange: () => void;
  private readonly wrap = new Compartment();
  private version = 0;
  private timer: ReturnType<typeof setTimeout> | undefined;

  constructor(parent: HTMLElement, callbacks: EditorCallbacks) {
    this.onChange = callbacks.onChange;
    this.onScroll = callbacks.onScroll;
    this.onCursor = callbacks.onCursor;
    this.onHover = callbacks.onHover;
    this.onGeometryChange = callbacks.onGeometryChange;

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
          this.wrap.of(EditorView.lineWrapping),
          spacerField,
          hoverLineField,
          updates,
        ],
      }),
    });

    const reportScroll = rafThrottle(() => this.onScroll(this.topVisibleLine()));
    this.view.scrollDOM.addEventListener("scroll", reportScroll);

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

  /** The 0-based source line at the top of the editor viewport. */
  topVisibleLine(): number {
    const rect = this.view.scrollDOM.getBoundingClientRect();
    const pos = this.view.posAtCoords({ x: rect.left + 1, y: rect.top + 1 });
    if (pos === null) {
      return 0;
    }
    return this.view.state.doc.lineAt(pos).number - 1;
  }

  /** Scroll so the given 0-based source line is at the top of the viewport. */
  scrollToSourceLine(line: number): void {
    const target = this.view.state.doc.line(this.clampLine(line)).from;
    this.view.dispatch({ effects: EditorView.scrollIntoView(target, { y: "start" }) });
  }

  /**
   * Natural top offset (excluding our spacer widgets) of each given 0-based source line, as a
   * prefix sum of text line heights. Because spacers are separate block widgets, a text line's own
   * `height` excludes them, so this is the spacer-free geometry — robust across edits with no
   * tracked state (which is what previously drifted when line numbers shifted).
   */
  naturalLineTops(lines: number[]): number[] {
    const doc = this.view.state.doc;
    const padding = Number.parseFloat(getComputedStyle(this.view.contentDOM).paddingTop) || 0;
    const targets = lines.map((line) => this.clampLine(line));
    const maxLine = targets.length > 0 ? Math.max(...targets) : 0;

    const topByLine = new Map<number, number>();
    let top = padding;
    for (let line = 1; line <= maxLine; line++) {
      topByLine.set(line, top);
      top += this.view.lineBlockAt(doc.line(line).from).height;
    }
    return targets.map((line) => topByLine.get(line) ?? padding);
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
        Decoration.widget({ widget: new SpacerWidget(leadingHeight), block: true, side: -1 }).range(
          0,
        ),
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

  /** Toggle soft line wrapping. Off = long lines stay on one row (horizontal scroll). */
  setLineWrapping(enabled: boolean): void {
    this.view.dispatch({
      effects: this.wrap.reconfigure(enabled ? EditorView.lineWrapping : []),
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
