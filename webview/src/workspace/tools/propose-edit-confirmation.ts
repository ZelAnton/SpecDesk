/**
 * The confirmation surface for the assistant's gated `proposeEdit` (design §08-ai-agent / §10.5). The host
 * stages a proposed full-document replacement and sends `confirm.request`; this surface shows the author the
 * difference from the current document, lets them edit the proposal, and confirm or discard it. Only a
 * confirmed proposal is sent back (`confirm.result`, accepted) and applied by the host through the ordinary
 * editing path; discarding — or editing then discarding — leaves no trace in the document.
 *
 * It owns its own DOM and listeners and talks to the integrator only through the injected callbacks (index.ts
 * keeps the ipc/Kinds knowledge), which keeps it unit-testable. It reuses the existing word-diff surface
 * ({@link wordDiff}) to render the difference — no new diff algorithm — falling back to a whole-block
 * before/after view when the change is too large for readable inline highlighting.
 */

import { INLINE_DIFF_MAX_RATIO, wordDiff } from "../../review/word-diff.js";

export interface ProposeEditRequest {
  id: string;
  currentText: string;
  proposedText: string;
  summary: string | null;
}

export interface ProposeEditConfirmationDeps {
  /** Where to mount the surface (the assistant experiences it in the right-rail chat area). */
  container: HTMLElement;
  /** The author confirmed the (possibly edited) proposal — apply it. `text` is the final confirmed text. */
  onAccept(id: string, text: string): void;
  /** The author discarded the proposal — nothing is applied and nothing is left behind. */
  onReject(id: string): void;
}

let confirmationSequence = 0;

export class ProposeEditConfirmation {
  private readonly deps: ProposeEditConfirmationDeps;

  private root: HTMLElement | null = null;
  private request: ProposeEditRequest | null = null;
  private diffRegion: HTMLElement | null = null;
  private editor: HTMLTextAreaElement | null = null;
  private editing = false;

  constructor(deps: ProposeEditConfirmationDeps) {
    this.deps = deps;
  }

  /** Whether a proposal is currently open for confirmation. */
  get isOpen(): boolean {
    return this.root !== null;
  }

  /** Reveal the confirmation surface for one staged proposal. A newer request supersedes any open one
   *  (the host single-flights, so an earlier proposal's reply no longer matches by id and is dropped). */
  open(request: ProposeEditRequest): void {
    this.close();
    this.request = request;
    this.editing = false;

    const id = `propose-edit-${++confirmationSequence}`;
    const titleId = `${id}-title`;
    const noteId = `${id}-note`;

    const root = document.createElement("section");
    root.className = "propose-edit";
    root.id = id;
    root.setAttribute("role", "group");
    root.setAttribute("aria-labelledby", titleId);
    root.setAttribute("aria-describedby", noteId);

    const title = document.createElement("strong");
    title.className = "propose-edit-title";
    title.id = titleId;
    title.textContent = "Suggested edit";

    const note = document.createElement("p");
    note.className = "propose-edit-note";
    note.id = noteId;
    note.textContent = request.summary
      ? `${request.summary} Nothing changes until you apply it.`
      : "Review the suggested change. Nothing changes until you apply it.";

    this.diffRegion = document.createElement("div");
    this.diffRegion.className = "propose-edit-diff";
    this.diffRegion.setAttribute("role", "region");
    this.diffRegion.setAttribute("aria-label", "Suggested change");

    this.editor = document.createElement("textarea");
    this.editor.className = "propose-edit-editor";
    this.editor.setAttribute("aria-label", "Edit the suggested text");
    this.editor.value = request.proposedText;
    this.editor.hidden = true;
    this.editor.rows = 8;
    this.editor.addEventListener("input", () => this.renderDiff());

    const actions = document.createElement("div");
    actions.className = "propose-edit-actions";

    const editButton = document.createElement("button");
    editButton.type = "button";
    editButton.className = "propose-edit-btn propose-edit-edit";
    editButton.textContent = "Edit";
    editButton.setAttribute("aria-pressed", "false");
    editButton.addEventListener("click", () => this.toggleEditing(editButton));

    const rejectButton = document.createElement("button");
    rejectButton.type = "button";
    rejectButton.className = "propose-edit-btn propose-edit-reject";
    rejectButton.textContent = "Discard";
    rejectButton.addEventListener("click", () => this.reject());

    const acceptButton = document.createElement("button");
    acceptButton.type = "button";
    acceptButton.className = "propose-edit-btn propose-edit-accept";
    acceptButton.textContent = "Apply edit";
    acceptButton.addEventListener("click", () => this.accept());

    actions.append(editButton, rejectButton, acceptButton);
    root.append(title, note, this.diffRegion, this.editor, actions);
    root.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        event.preventDefault();
        this.reject();
      }
    });

    this.deps.container.appendChild(root);
    this.root = root;
    this.renderDiff();
    acceptButton.focus();
  }

  /** Dismiss the surface without sending a decision (e.g. when a new document loads). */
  close(): void {
    this.root?.remove();
    this.root = null;
    this.request = null;
    this.diffRegion = null;
    this.editor = null;
    this.editing = false;
  }

  private finalText(): string {
    return this.editing && this.editor !== null
      ? this.editor.value
      : (this.request?.proposedText ?? "");
  }

  private toggleEditing(button: HTMLButtonElement): void {
    if (this.editor === null) {
      return;
    }
    this.editing = !this.editing;
    this.editor.hidden = !this.editing;
    button.setAttribute("aria-pressed", String(this.editing));
    if (this.editing) {
      this.editor.focus();
    }
    this.renderDiff();
  }

  private renderDiff(): void {
    if (this.request === null || this.diffRegion === null) {
      return;
    }
    renderWordDiff(this.diffRegion, this.request.currentText, this.finalText());
  }

  private accept(): void {
    const request = this.request;
    if (request === null) {
      return;
    }
    const text = this.finalText();
    this.close();
    this.deps.onAccept(request.id, text);
  }

  private reject(): void {
    const request = this.request;
    if (request === null) {
      return;
    }
    this.close();
    this.deps.onReject(request.id);
  }
}

/** Render the word-level difference `base`→`head` into `region`, reusing {@link wordDiff}. Above the
 *  existing inline-diff ratio threshold the inline highlighting becomes confetti, so it falls back to a
 *  whole-block before/after view — the same threshold the editor's inline diff uses. */
function renderWordDiff(region: HTMLElement, base: string, head: string): void {
  region.replaceChildren();
  const diff = wordDiff(base, head);
  if (diff.changeRatio > INLINE_DIFF_MAX_RATIO) {
    region.appendChild(wholeBlock("propose-edit-block-removed", "Current", base));
    region.appendChild(wholeBlock("propose-edit-block-added", "Suggested", head));
    return;
  }

  const inline = document.createElement("pre");
  inline.className = "propose-edit-inline";
  for (const op of diff.ops) {
    if (op.type === "equal") {
      inline.appendChild(document.createTextNode(head.slice(op.start, op.end)));
    } else if (op.type === "add") {
      const added = document.createElement("span");
      added.className = "propose-edit-word-added";
      added.textContent = head.slice(op.start, op.end);
      inline.appendChild(added);
    } else {
      const removed = document.createElement("span");
      removed.className = "propose-edit-word-removed";
      removed.textContent = op.text;
      inline.appendChild(removed);
    }
  }
  region.appendChild(inline);
}

function wholeBlock(className: string, label: string, text: string): HTMLElement {
  const block = document.createElement("div");
  block.className = `propose-edit-block ${className}`;
  const heading = document.createElement("span");
  heading.className = "propose-edit-block-label";
  heading.textContent = label;
  const body = document.createElement("pre");
  body.className = "propose-edit-block-text";
  body.textContent = text;
  block.append(heading, body);
  return block;
}
