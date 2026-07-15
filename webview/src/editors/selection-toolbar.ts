import type { FormatCommand } from "./format-registry.js";
import type { SourceSelection } from "./selection-comments.js";

interface SelectionToolbarOptions {
  readonly parent: HTMLElement;
  readonly surface: "code" | "formatted";
  readonly hasSelection: () => boolean;
  readonly selection: () => SourceSelection | null;
  readonly anchor: () => DOMRect | null;
  readonly format: (command: FormatCommand) => void;
  readonly addComment: (selection: SourceSelection, body: string) => void;
  readonly active?: () => ReadonlySet<FormatCommand>;
  readonly disabled?: () => ReadonlySet<FormatCommand>;
}

const COMMANDS: readonly {
  id: FormatCommand;
  label: string;
  className?: string;
}[] = [
  { id: "bold", label: "Bold", className: "selection-format-button--bold" },
  { id: "italic", label: "Italic", className: "selection-format-button--italic" },
  { id: "strike", label: "Strike", className: "selection-format-button--strike" },
  { id: "inlineCode", label: "Code", className: "selection-format-button--code" },
  { id: "link", label: "Link" },
  { id: "quote", label: "Quote" },
];

/** Shared stationary selected-text toolbar. Its position is read from selection geometry once when the
 * selection gesture completes; ordinary pointer movement never re-anchors it, so its controls remain
 * reachable. It closes only on a collapsed selection, scroll, Escape, or a deliberate outside click. */
export class SelectionToolbar {
  private readonly options: SelectionToolbarOptions;
  private readonly root: HTMLDivElement;
  private readonly commands: HTMLDivElement;
  private readonly compose: HTMLDivElement;
  private readonly textarea: HTMLTextAreaElement;
  private selectionValue: SourceSelection | null = null;

  constructor(options: SelectionToolbarOptions) {
    this.options = options;
    this.root = document.createElement("div");
    this.root.className = `selection-format-popover selection-format-popover--${options.surface}`;
    this.root.setAttribute("role", "toolbar");
    this.root.setAttribute("aria-label", "Format selected text or add a comment");
    this.root.hidden = true;
    this.root.addEventListener("pointerdown", (event) => event.stopPropagation());

    this.commands = document.createElement("div");
    this.commands.className = "selection-format-commands";
    for (const command of COMMANDS) {
      const button = document.createElement("button");
      button.type = "button";
      button.dataset.format = command.id;
      button.className = command.className ?? "";
      button.textContent = command.label;
      button.title = command.label;
      button.setAttribute("aria-label", command.label);
      button.addEventListener("pointerdown", (event) => event.preventDefault());
      button.addEventListener("click", () => {
        options.format(command.id);
        this.refreshState();
      });
      this.commands.appendChild(button);
    }
    const comment = document.createElement("button");
    comment.type = "button";
    comment.className = "selection-comment-open";
    comment.textContent = "Comment";
    comment.title = "Add comment to selection";
    comment.setAttribute("aria-label", "Add comment to selection");
    comment.addEventListener("pointerdown", (event) => event.preventDefault());
    comment.addEventListener("click", () => this.openComposer());
    this.commands.appendChild(comment);

    this.compose = document.createElement("div");
    this.compose.className = "selection-comment-compose";
    this.compose.hidden = true;
    this.textarea = document.createElement("textarea");
    this.textarea.rows = 3;
    this.textarea.placeholder = "Write a comment…";
    this.textarea.setAttribute("aria-label", "Comment text");
    const note = document.createElement("small");
    note.textContent = "Saved locally in this SpecDesk session; not posted to GitHub.";
    const actions = document.createElement("div");
    const submit = document.createElement("button");
    submit.type = "button";
    submit.textContent = "Add comment";
    submit.addEventListener("click", () => this.submitComment());
    const cancel = document.createElement("button");
    cancel.type = "button";
    cancel.textContent = "Cancel";
    cancel.addEventListener("click", () => this.closeComposer());
    actions.append(submit, cancel);
    this.compose.append(this.textarea, note, actions);
    this.root.append(this.commands, this.compose);
    options.parent.appendChild(this.root);
  }

  show(): void {
    let anchor: DOMRect | null = null;
    try {
      anchor = this.options.anchor();
    } catch {
      // Layout-free document environments may not implement selection client rects. The editor remains
      // usable; a real rendered surface provides geometry on the next user gesture.
    }
    if (!this.options.hasSelection() || anchor === null) {
      this.hide();
      return;
    }
    this.root.hidden = false;
    this.refreshState();
    const parent = this.options.parent.getBoundingClientRect();
    const rect = this.root.getBoundingClientRect();
    const left = Math.min(
      Math.max(8, anchor.left - parent.left),
      Math.max(8, parent.width - rect.width - 8),
    );
    const above = anchor.top - parent.top - rect.height - 8;
    const top = above >= 8 ? above : anchor.bottom - parent.top + 8;
    this.root.style.left = `${left}px`;
    this.root.style.top = `${Math.min(Math.max(8, top), Math.max(8, parent.height - rect.height - 8))}px`;
  }

  selectionChanged(): void {
    if (this.root.hidden) return;
    if (!this.options.hasSelection()) this.hide();
  }

  contains(target: EventTarget | null): boolean {
    return target instanceof Node && this.root.contains(target);
  }

  isVisible(): boolean {
    return !this.root.hidden;
  }

  hide(): void {
    this.root.hidden = true;
    this.selectionValue = null;
    this.closeComposer();
  }

  private refreshState(): void {
    const active = this.options.active?.() ?? new Set<FormatCommand>();
    const disabled = this.options.disabled?.() ?? new Set<FormatCommand>();
    for (const button of this.commands.querySelectorAll<HTMLButtonElement>("[data-format]")) {
      const command = button.dataset.format as FormatCommand;
      button.disabled = disabled.has(command);
      button.setAttribute("aria-pressed", String(active.has(command)));
    }
  }

  private openComposer(): void {
    this.selectionValue = this.options.selection();
    if (this.selectionValue === null) {
      this.hide();
      return;
    }
    this.commands.hidden = true;
    this.compose.hidden = false;
    this.textarea.focus();
  }

  private closeComposer(): void {
    this.commands.hidden = false;
    this.compose.hidden = true;
    this.textarea.value = "";
  }

  private submitComment(): void {
    if (this.selectionValue === null || this.textarea.value.trim().length === 0) return;
    this.options.addComment(this.selectionValue, this.textarea.value);
    this.hide();
  }
}
