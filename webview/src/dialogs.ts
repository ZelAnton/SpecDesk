/**
 * The two inline prompt bars: "name this draft" (branch) shown on Edit, and "save a version" (the
 * commit message) shown on Save version. Both follow the same pattern — open with a host-suggested
 * value the author can edit, confirm/cancel, Esc backs out — so they live together here, out of the
 * index.ts integrator. This owns its own DOM and listeners; it talks to the host only through the
 * injected callbacks (the integrator keeps the ipc/Kinds knowledge), which also keeps it unit-testable.
 */

/** Keep a draft name a valid git ref as the author types: backslashes become '/', and spaces or any
 *  other disallowed character become '_'. Length is preserved (1:1) so the caret stays put; the host
 *  sanitizes again on submit (collapsing/trimming) as the authority. */
export function sanitizeDraftName(value: string): string {
  return value.replace(/\\/g, "/").replace(/[^A-Za-z0-9._/-]/g, "_");
}

export interface DialogsCallbacks {
  /** Fetch the host's suggested draft (branch) name to prefill the prompt; resolves "" on failure. */
  suggestBranchName: () => Promise<string>;
  /** The author confirmed a draft name (already trimmed) — fork the working branch and begin editing. */
  onBranchName: (name: string) => void;
  /** Fetch the host's suggested version note to prefill the prompt; resolves "" on failure. */
  suggestVersionNote: () => Promise<string>;
  /** The author confirmed a version note (already trimmed) — make the explicit "save a version" commit. */
  onVersionNote: (note: string) => void;
}

export class Dialogs {
  private readonly branchNameBar = document.querySelector<HTMLElement>("#branch-name-bar");
  private readonly branchNameInput = document.querySelector<HTMLInputElement>("#branch-name-input");
  private readonly branchNameConfirm =
    document.querySelector<HTMLButtonElement>("#branch-name-confirm");
  private readonly branchNameCancel =
    document.querySelector<HTMLButtonElement>("#branch-name-cancel");

  private readonly versionNoteBar = document.querySelector<HTMLElement>("#version-note-bar");
  private readonly versionNoteInput =
    document.querySelector<HTMLInputElement>("#version-note-input");
  private readonly versionNoteTextarea =
    document.querySelector<HTMLTextAreaElement>("#version-note-textarea");
  private readonly versionNoteExpand =
    document.querySelector<HTMLButtonElement>("#version-note-expand");
  private readonly versionNoteConfirm =
    document.querySelector<HTMLButtonElement>("#version-note-confirm");
  private readonly versionNoteCancel =
    document.querySelector<HTMLButtonElement>("#version-note-cancel");

  // Synchronous re-entrancy latches: the `hidden` flip happens only after the suggestion request
  // awaited in open*(), so the `!hidden` guard alone can't stop a second open during that in-flight
  // window (see open* below).
  private branchOpening = false;
  private versionOpening = false;
  // Bumped on every close so a suggestion reply that lands AFTER the bar was closed (e.g. a new
  // document loaded mid-request) can tell it was superseded and not re-reveal a stale bar.
  private branchOpenToken = 0;
  private versionOpenToken = 0;

  constructor(private readonly callbacks: DialogsCallbacks) {
    this.branchNameConfirm?.addEventListener("click", () => this.confirmBranchName());
    this.branchNameCancel?.addEventListener("click", () => this.closeBranchName());
    // Live-clean the draft name to a valid ref as it is typed, keeping the caret in place.
    this.branchNameInput?.addEventListener("input", () => {
      if (!this.branchNameInput) {
        return;
      }
      const caret = this.branchNameInput.selectionStart;
      const cleaned = sanitizeDraftName(this.branchNameInput.value);
      if (cleaned !== this.branchNameInput.value) {
        this.branchNameInput.value = cleaned;
        if (caret !== null) {
          this.branchNameInput.setSelectionRange(caret, caret);
        }
      }
    });
    this.branchNameInput?.addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        event.preventDefault();
        this.confirmBranchName();
      } else if (event.key === "Escape") {
        event.preventDefault();
        this.closeBranchName();
      }
    });

    this.versionNoteConfirm?.addEventListener("click", () => this.confirmVersionNote());
    this.versionNoteCancel?.addEventListener("click", () => this.closeVersionNote());
    this.versionNoteExpand?.addEventListener("click", () => this.expandVersionNote());
    // Single-line: Enter saves, Down arrow expands to the multi-line editor, Esc cancels.
    this.versionNoteInput?.addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        event.preventDefault();
        this.confirmVersionNote();
      } else if (event.key === "ArrowDown") {
        event.preventDefault();
        this.expandVersionNote();
      } else if (event.key === "Escape") {
        event.preventDefault();
        this.closeVersionNote();
      }
    });
    // Multi-line: Enter inserts a newline (default), Ctrl/Cmd+Enter saves, Esc cancels.
    this.versionNoteTextarea?.addEventListener("keydown", (event) => {
      if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
        event.preventDefault();
        this.confirmVersionNote();
      } else if (event.key === "Escape") {
        event.preventDefault();
        this.closeVersionNote();
      }
    });
  }

  // —— Draft-name (branch) prompt ——————————————————————————————————————————————————————————————————

  /** Reveal the draft-name prompt, prefilled with the host's suggestion. No-op if it is already open
   *  (e.g. repeated keystrokes in the read-only editor) so requests don't stack. */
  async openBranchName(): Promise<void> {
    // Guard on the latch as well as `!hidden`: a second Edit click (or a key-mash through
    // onEditAttempt) during the in-flight suggestion request would otherwise stack requests, and a
    // late reply would overwrite the name the author has already started typing.
    if (this.branchOpening || (this.branchNameBar && !this.branchNameBar.hidden)) {
      return;
    }
    this.branchOpening = true;
    const token = ++this.branchOpenToken;
    try {
      const suggested = await this.callbacks.suggestBranchName();
      if (token !== this.branchOpenToken) {
        return; // closed (or superseded) while the request was in flight — don't re-reveal a stale bar
      }
      if (this.branchNameInput) {
        this.branchNameInput.value = suggested;
      }
      if (this.branchNameBar) {
        this.branchNameBar.hidden = false;
      }
      this.branchNameInput?.focus();
      this.branchNameInput?.select();
    } finally {
      this.branchOpening = false;
    }
  }

  private closeBranchName(): void {
    this.branchOpenToken++;
    if (this.branchNameBar) {
      this.branchNameBar.hidden = true;
    }
  }

  private confirmBranchName(): void {
    const branchName = this.branchNameInput?.value.trim() ?? "";
    this.closeBranchName();
    this.callbacks.onBranchName(branchName);
  }

  // —— Version-note (commit message) prompt ——————————————————————————————————————————————————————————

  /** Reveal the version-note prompt, prefilled with the host's suggestion, always in the compact
   *  single-line state. No-op if it is already open. */
  async openVersionNote(): Promise<void> {
    // See openBranchName: the latch closes the in-flight window the `!hidden` guard misses. Without it
    // a late reply would re-run the reset-to-single-line block below and silently discard a multi-line
    // note the author had expanded into and started writing.
    if (this.versionOpening || (this.versionNoteBar && !this.versionNoteBar.hidden)) {
      return;
    }
    this.versionOpening = true;
    const token = ++this.versionOpenToken;
    try {
      const suggested = await this.callbacks.suggestVersionNote();
      if (token !== this.versionOpenToken) {
        return; // closed (or superseded) while in flight — don't re-reveal / reset a stale bar
      }
      // Always reopen in the compact single-line state.
      if (this.versionNoteTextarea) {
        this.versionNoteTextarea.hidden = true;
      }
      if (this.versionNoteExpand) {
        this.versionNoteExpand.hidden = false;
      }
      if (this.versionNoteInput) {
        this.versionNoteInput.hidden = false;
        this.versionNoteInput.value = suggested;
      }
      if (this.versionNoteBar) {
        this.versionNoteBar.hidden = false;
      }
      this.versionNoteInput?.focus();
      this.versionNoteInput?.select();
    } finally {
      this.versionOpening = false;
    }
  }

  closeVersionNote(): void {
    this.versionOpenToken++;
    if (this.versionNoteBar) {
      this.versionNoteBar.hidden = true;
    }
  }

  /** Close both prompt bars (e.g. when a new document loads). */
  closeAll(): void {
    this.closeBranchName();
    this.closeVersionNote();
  }

  private versionNoteMultiline(): boolean {
    return this.versionNoteTextarea !== null && !this.versionNoteTextarea.hidden;
  }

  /** Swap the single-line input for the multi-line textarea, carrying the text and caret over. */
  private expandVersionNote(): void {
    if (!this.versionNoteTextarea || !this.versionNoteInput || this.versionNoteMultiline()) {
      return;
    }
    this.versionNoteTextarea.value = this.versionNoteInput.value;
    this.versionNoteInput.hidden = true;
    if (this.versionNoteExpand) {
      this.versionNoteExpand.hidden = true;
    }
    this.versionNoteTextarea.hidden = false;
    this.versionNoteTextarea.focus();
    const end = this.versionNoteTextarea.value.length;
    this.versionNoteTextarea.setSelectionRange(end, end);
  }

  private confirmVersionNote(): void {
    const raw = this.versionNoteMultiline()
      ? (this.versionNoteTextarea?.value ?? "")
      : (this.versionNoteInput?.value ?? "");
    this.closeVersionNote();
    this.callbacks.onVersionNote(raw.trim());
  }
}
