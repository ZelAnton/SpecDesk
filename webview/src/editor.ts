/**
 * The CodeMirror 6 source editor. Stays deliberately thin: it owns the document text, emits
 * debounced change notifications carrying a monotonic version, reports the source line at the top
 * of its viewport for scroll-sync, and can scroll itself to a given source line. No Markdown
 * logic lives here — that is all native (docs/design/05-live-preview.md).
 */

import { markdown } from "@codemirror/lang-markdown";
import { EditorState } from "@codemirror/state";
import { basicSetup, EditorView } from "codemirror";
import { rafThrottle } from "./raf.js";

const DEBOUNCE_MS = 120;

export interface EditorCallbacks {
  /** Fired ~120 ms after the last keystroke, with the full text and its new version. */
  onChange: (text: string, version: number) => void;
  /** Fired as the editor scrolls, with the 0-based source line at the viewport top. */
  onScroll: (topLine: number) => void;
}

export class MarkdownEditor {
  private readonly view: EditorView;
  private readonly onChange: (text: string, version: number) => void;
  private readonly onScroll: (topLine: number) => void;
  private version = 0;
  private timer: ReturnType<typeof setTimeout> | undefined;

  constructor(parent: HTMLElement, callbacks: EditorCallbacks) {
    this.onChange = callbacks.onChange;
    this.onScroll = callbacks.onScroll;

    const updates = EditorView.updateListener.of((update) => {
      if (update.docChanged) {
        this.scheduleChange();
      }
    });

    this.view = new EditorView({
      parent,
      state: EditorState.create({
        doc: "",
        extensions: [basicSetup, markdown(), EditorView.lineWrapping, updates],
      }),
    });

    const reportScroll = rafThrottle(() => this.onScroll(this.topVisibleLine()));
    this.view.scrollDOM.addEventListener("scroll", reportScroll);
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
    const lineNumber = Math.min(Math.max(line + 1, 1), this.view.state.doc.lines);
    const target = this.view.state.doc.line(lineNumber).from;
    this.view.dispatch({ effects: EditorView.scrollIntoView(target, { y: "start" }) });
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
