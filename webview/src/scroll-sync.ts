/**
 * Bidirectional scroll-sync between the editor and the preview. The hard part is avoiding a
 * feedback loop: when we programmatically scroll one pane, that pane fires its own scroll event,
 * which would drive the other pane straight back. We suppress the echo with a per-pane "ignore
 * next" flag, plus a short timeout that clears it in case the programmatic scroll produced no
 * event at all (e.g. the target was already in view). See docs/design/05-live-preview.md.
 */

import type { MarkdownEditor } from "./editor.js";
import type { Preview } from "./preview.js";

const UNLOCK_MS = 150;

export class ScrollSync {
  private readonly editor: MarkdownEditor;
  private readonly preview: Preview;
  private ignoreEditor = false;
  private ignorePreview = false;

  constructor(editor: MarkdownEditor, preview: Preview) {
    this.editor = editor;
    this.preview = preview;
  }

  /** The editor scrolled to `topLine` (0-based); drive the preview unless this is our own echo. */
  fromEditor(topLine: number): void {
    if (this.ignoreEditor) {
      this.ignoreEditor = false;
      return;
    }
    this.ignorePreview = true;
    this.preview.scrollToSourceLine(topLine);
    this.clearSoon("preview");
  }

  /** The preview scrolled; drive the editor unless this is our own echo. */
  fromPreview(): void {
    if (this.ignorePreview) {
      this.ignorePreview = false;
      return;
    }
    this.ignoreEditor = true;
    this.editor.scrollToSourceLine(this.preview.topVisibleSourceLine());
    this.clearSoon("editor");
  }

  private clearSoon(which: "editor" | "preview"): void {
    setTimeout(() => {
      if (which === "editor") {
        this.ignoreEditor = false;
      } else {
        this.ignorePreview = false;
      }
    }, UNLOCK_MS);
  }
}
