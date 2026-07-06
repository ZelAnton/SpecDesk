/**
 * The formatting toolbar's routing (PoC-12). A format command applies to the pane the author last
 * worked in: Code → the source editor (Markdown text transforms), Formatted → the WYSIWYG editor
 * (ProseMirror commands), Split → whichever pane last had focus (default the source editor). The
 * formatted pane's currently-active formats are reflected on the buttons' aria-pressed. It owns the
 * button listeners and the last-focused state; the integrator feeds it the live mode and the two panes'
 * apply/active operations through callbacks, so it stays free of editor/ipc knowledge and is
 * unit-testable (mirrors the dialogs/review leaf modules).
 */

import { type FormatCommand, isFormatCommand } from "../editors/md-format.js";
import type { ViewMode } from "./view-mode.js";

export interface FormatToolbarDeps {
  /** The toolbar's format buttons (each carries a `data-format` = a FormatCommand). */
  buttons: HTMLButtonElement[];
  /** Apply a Markdown text transform in the source editor (the Code/Split target). */
  applyInSource: (command: FormatCommand) => void;
  /** Apply a ProseMirror command in the formatted editor (the Formatted/Split target). */
  applyInFormatted: (command: FormatCommand) => void;
  /** The formatted editor's currently-active formats, for the buttons' pressed state. */
  activeFormats: () => Set<FormatCommand>;
  /** The live view mode (Code / Split / Formatted). */
  mode: () => ViewMode;
}

export class FormatToolbar {
  // The pane the author last focused — the format target in Split (Code/Formatted are unambiguous).
  private lastFocused: "editor" | "formatted" = "editor";

  constructor(private readonly deps: FormatToolbarDeps) {
    for (const button of this.deps.buttons) {
      const command = button.dataset.format;
      if (!isFormatCommand(command)) {
        continue;
      }
      // mousedown is prevented so the click never steals focus from the editor — the selection it acts
      // on stays intact and lastFocused keeps pointing at the right pane.
      button.addEventListener("mousedown", (event) => event.preventDefault());
      button.addEventListener("click", () => this.run(command));
    }
  }

  /** Record the pane that just gained focus (the Split format target) and refresh the buttons. */
  setFocused(pane: "editor" | "formatted"): void {
    this.lastFocused = pane;
    this.refresh();
  }

  /** Reflect the active formats of the current target on the buttons' aria-pressed state. Active state
   *  is shown only for the formatted pane (the source editor has no inline-mark notion). */
  refresh(): void {
    const active =
      this.target() === "formatted" ? this.deps.activeFormats() : new Set<FormatCommand>();
    for (const button of this.deps.buttons) {
      const command = button.dataset.format;
      if (isFormatCommand(command)) {
        button.setAttribute("aria-pressed", String(active.has(command)));
      }
    }
  }

  /** The pane a format command applies to: Code → source, Formatted → WYSIWYG, Split → last focused. */
  private target(): "editor" | "formatted" {
    const mode = this.deps.mode();
    return mode === "code" ? "editor" : mode === "formatted" ? "formatted" : this.lastFocused;
  }

  private run(command: FormatCommand): void {
    if (this.target() === "formatted") {
      this.deps.applyInFormatted(command);
    } else {
      this.deps.applyInSource(command);
    }
    this.refresh();
  }
}
