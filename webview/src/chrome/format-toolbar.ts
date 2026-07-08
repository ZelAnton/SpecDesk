/**
 * The formatting toolbar's routing (PoC-12). A format command applies to the pane the author last
 * worked in: Code → the source editor (Markdown text transforms), Formatted → the WYSIWYG editor
 * (ProseMirror commands), Split → whichever pane last had focus (default the source editor). The
 * CURRENT target's active formats are reflected on the buttons' aria-pressed, and (formatted target
 * only — see {@link FormatToolbarDeps.disabledInFormatted}) its inapplicable commands as `disabled`
 * (T-100): one `refresh()` contract serves both targets, each supplying its own reading through the
 * deps below, so the loop over buttons is written once. It owns the button listeners and the
 * last-focused state; the integrator feeds it the live mode and the two panes' apply/active/disabled
 * operations through callbacks, so it stays free of editor/ipc knowledge and is unit-testable (mirrors
 * the dialogs/review leaf modules).
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
  /** The source editor's active formats at the caret (its lang-markdown syntax tree), for the buttons'
   *  pressed state when the source pane is the target. */
  activeInSource: () => Set<FormatCommand>;
  /** The formatted editor's active formats at the selection, for the buttons' pressed state when the
   *  formatted pane is the target. */
  activeInFormatted: () => Set<FormatCommand>;
  /** The formatted editor's commands NOT applicable at the current selection, for the buttons' disabled
   *  state when the formatted pane is the target. The source tract has no such notion — a Markdown text
   *  transform is always well-formed regardless of context — so only the formatted target disables. */
  disabledInFormatted: () => Set<FormatCommand>;
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

  /** Reflect the current target's active formats (aria-pressed) and inapplicable commands (disabled) on
   *  the buttons — Code/Split reads the source pane's syntax-tree state, Formatted reads the WYSIWYG
   *  pane's selection state (both active AND disabled; see {@link FormatToolbarDeps.disabledInFormatted}). */
  refresh(): void {
    const disabled = this.currentDisabled();
    const active =
      this.target() === "formatted" ? this.deps.activeInFormatted() : this.deps.activeInSource();
    for (const button of this.deps.buttons) {
      const command = button.dataset.format;
      if (isFormatCommand(command)) {
        button.setAttribute("aria-pressed", String(active.has(command)));
        button.disabled = disabled.has(command);
      }
    }
  }

  /** The pane a format command applies to: Code → source, Formatted → WYSIWYG, Split → last focused. */
  private target(): "editor" | "formatted" {
    const mode = this.deps.mode();
    return mode === "code" ? "editor" : mode === "formatted" ? "formatted" : this.lastFocused;
  }

  /** The current target's inapplicable commands — empty for the source target (see
   *  {@link FormatToolbarDeps.disabledInFormatted}). */
  private currentDisabled(): Set<FormatCommand> {
    return this.target() === "formatted"
      ? this.deps.disabledInFormatted()
      : new Set<FormatCommand>();
  }

  private run(command: FormatCommand): void {
    // Defense in depth against a click that bypasses the native `disabled` gate (e.g. a synthetic click
    // dispatched in a test, or a future keyboard-shortcut path that doesn't go through the button at
    // all) — never apply a command the current target reports as inapplicable.
    if (this.currentDisabled().has(command)) {
      return;
    }
    if (this.target() === "formatted") {
      this.deps.applyInFormatted(command);
    } else {
      this.deps.applyInSource(command);
    }
    this.refresh();
  }
}
