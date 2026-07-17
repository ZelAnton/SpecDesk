/**
 * The inline prompt bars: "name this draft" (branch) on Edit, "save a version" (the commit message) on
 * Save version, and "send for review" (the PR title/body) on Send for review. All follow the same
 * pattern — open with a host-suggested value the author can edit, confirm/cancel, Esc backs out — so they
 * live together here, out of the index.ts integrator. This owns its own DOM and listeners; it talks to the
 * host only through the injected callbacks (the integrator keeps the ipc/Kinds knowledge), which also
 * keeps it unit-testable.
 */

import { SPELLCHECK_ENABLED, SPELLCHECK_LANG } from "../util/spellcheck.js";
import { PromptBar } from "./prompt-bar.js";

/** Keep a draft name a valid git ref as the author types: backslashes become '/', and spaces or any
 *  other disallowed character become '_'. Length is preserved (1:1) so the caret stays put; the host
 *  sanitizes again on submit (collapsing/trimming) as the authority. */
export function sanitizeDraftName(value: string): string {
  return value.replace(/\\/g, "/").replace(/[^A-Za-z0-9._/-]/g, "_");
}

/** The editable pull-request text shown in the send-for-review prompt (host-suggested, author-edited). */
export interface PrText {
  title: string;
  body: string;
}

/** The host's reply to a send-for-review request: the suggested text, or a `blocked` reason the send
 *  can't proceed (not connected / not a GitHub repo / no saved version) — when present the prompt does
 *  NOT open, so the author never composes text into a send that would be rejected. */
export interface PrSuggestion extends PrText {
  blocked?: string;
}

export interface DialogsDeps {
  /** The draft-name (branch) prompt's own elements (each may be absent from the markup). */
  branchNameBar: HTMLElement | null;
  branchNameInput: HTMLInputElement | null;
  branchNameConfirm: HTMLButtonElement | null;
  branchNameCancel: HTMLButtonElement | null;
  /** The version-note (commit message) prompt's own elements. */
  versionNoteBar: HTMLElement | null;
  versionNoteInput: HTMLInputElement | null;
  versionNoteTextarea: HTMLTextAreaElement | null;
  versionNoteExpand: HTMLButtonElement | null;
  versionNoteConfirm: HTMLButtonElement | null;
  versionNoteCancel: HTMLButtonElement | null;
  /** The send-for-review (PR title/body) prompt's own elements. */
  prTextBar: HTMLElement | null;
  prTitleInput: HTMLInputElement | null;
  prBodyTextarea: HTMLTextAreaElement | null;
  prTextConfirm: HTMLButtonElement | null;
  prTextCancel: HTMLButtonElement | null;
  /** Fetch the host's suggested draft (branch) name to prefill the prompt; resolves "" on failure. */
  suggestBranchName: () => Promise<string>;
  /** The author confirmed a draft name (already trimmed) — fork the working branch and begin editing. */
  onBranchName: (name: string) => void;
  /** Fetch the host's suggested version note to prefill the prompt; resolves "" on failure. */
  suggestVersionNote: () => Promise<string>;
  /** The author confirmed a version note (already trimmed) — make the explicit "save a version" commit. */
  onVersionNote: (note: string) => void;
  /** Fetch the host's send readiness + suggested PR title/body; resolves with a `blocked` reason (and the
   *  prompt is not opened) when the send can't proceed, or empty text on transport failure. */
  suggestPrText: () => Promise<PrSuggestion>;
  /** The send can't proceed — show the plain reason to the author (the prompt is not opened). */
  onPrBlocked: (reason: string) => void;
  /** The author confirmed the PR title/body — push the branch and open the review with this text. */
  onPrText: (text: PrText) => void;
}

export class Dialogs {
  private readonly branchNameBar: HTMLElement | null;
  private readonly branchNameInput: HTMLInputElement | null;
  private readonly branchNameConfirm: HTMLButtonElement | null;
  private readonly branchNameCancel: HTMLButtonElement | null;

  private readonly versionNoteBar: HTMLElement | null;
  private readonly versionNoteInput: HTMLInputElement | null;
  private readonly versionNoteTextarea: HTMLTextAreaElement | null;
  private readonly versionNoteExpand: HTMLButtonElement | null;
  private readonly versionNoteConfirm: HTMLButtonElement | null;
  private readonly versionNoteCancel: HTMLButtonElement | null;

  private readonly prTextBar: HTMLElement | null;
  private readonly prTitleInput: HTMLInputElement | null;
  private readonly prBodyTextarea: HTMLTextAreaElement | null;
  private readonly prTextConfirm: HTMLButtonElement | null;
  private readonly prTextCancel: HTMLButtonElement | null;

  // The open/close state machine (re-entrancy latch + supersession token) for each bar lives in
  // PromptBar, so that subtle handling is written once and the bars cannot drift apart.
  private readonly branchBar: PromptBar;
  private readonly versionBar: PromptBar;
  private readonly prBar: PromptBar;

  constructor(private readonly deps: DialogsDeps) {
    this.branchNameBar = deps.branchNameBar;
    this.branchNameInput = deps.branchNameInput;
    this.branchNameConfirm = deps.branchNameConfirm;
    this.branchNameCancel = deps.branchNameCancel;

    this.versionNoteBar = deps.versionNoteBar;
    this.versionNoteInput = deps.versionNoteInput;
    this.versionNoteTextarea = deps.versionNoteTextarea;
    this.versionNoteExpand = deps.versionNoteExpand;
    this.versionNoteConfirm = deps.versionNoteConfirm;
    this.versionNoteCancel = deps.versionNoteCancel;

    this.prTextBar = deps.prTextBar;
    this.prTitleInput = deps.prTitleInput;
    this.prBodyTextarea = deps.prBodyTextarea;
    this.prTextConfirm = deps.prTextConfirm;
    this.prTextCancel = deps.prTextCancel;

    this.branchBar = new PromptBar(this.branchNameBar);
    this.versionBar = new PromptBar(this.versionNoteBar);
    this.prBar = new PromptBar(this.prTextBar);

    // spellcheck/lang enable WebView2/Chromium's built-in spellchecker on the prose fields (the version
    // note and the PR title/body); the draft-name field is deliberately excluded — it is sanitized down
    // to a git-ref-safe string as the author types (see sanitizeDraftName), not prose. setAttribute (not
    // the `.spellcheck`/`.lang` IDL properties) so the attribute lands in the actual DOM markup — jsdom
    // doesn't reflect those properties onto the underlying attribute.
    for (const field of [
      this.versionNoteInput,
      this.versionNoteTextarea,
      this.prTitleInput,
      this.prBodyTextarea,
    ]) {
      if (field === null) continue;
      field.setAttribute("spellcheck", String(SPELLCHECK_ENABLED));
      field.setAttribute("lang", SPELLCHECK_LANG);
    }

    // Escape closes/cancels a bar no matter which of its own elements holds focus (button or text
    // field) — bound on the bar container itself so it fires regardless of the focused descendant,
    // not only from the text-input/textarea listeners below.
    this.branchNameBar?.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        event.preventDefault();
        this.branchBar.close();
      }
    });
    this.versionNoteBar?.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        event.preventDefault();
        this.closeVersionNote();
      }
    });
    this.prTextBar?.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        event.preventDefault();
        this.prBar.close();
      }
    });

    this.branchNameConfirm?.addEventListener("click", () => this.confirmBranchName());
    this.branchNameCancel?.addEventListener("click", () => this.branchBar.close());
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
        this.branchBar.close();
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

    this.prTextConfirm?.addEventListener("click", () => this.confirmPrText());
    this.prTextCancel?.addEventListener("click", () => this.prBar.close());
    // Title: Enter sends (the body is prefilled and optional), Esc cancels.
    this.prTitleInput?.addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        event.preventDefault();
        this.confirmPrText();
      } else if (event.key === "Escape") {
        event.preventDefault();
        this.prBar.close();
      }
    });
    // Body: Enter inserts a newline (default), Ctrl/Cmd+Enter sends, Esc cancels.
    this.prBodyTextarea?.addEventListener("keydown", (event) => {
      if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
        event.preventDefault();
        this.confirmPrText();
      } else if (event.key === "Escape") {
        event.preventDefault();
        this.prBar.close();
      }
    });
  }

  // —— Draft-name (branch) prompt ——————————————————————————————————————————————————————————————————

  /** Reveal the draft-name prompt, prefilled with the host's suggestion. No-op if it is already open
   *  (e.g. repeated keystrokes in the read-only editor) so requests don't stack — see PromptBar. */
  async openBranchName(): Promise<void> {
    await this.branchBar.open(
      () => this.deps.suggestBranchName(),
      (suggested) => {
        if (this.branchNameInput) {
          this.branchNameInput.value = suggested;
        }
        if (this.branchNameBar) {
          this.branchNameBar.hidden = false;
        }
        this.branchNameInput?.focus();
        this.branchNameInput?.select();
      },
    );
  }

  private confirmBranchName(): void {
    const branchName = this.branchNameInput?.value.trim() ?? "";
    this.branchBar.close();
    this.deps.onBranchName(branchName);
  }

  // —— Version-note (commit message) prompt ——————————————————————————————————————————————————————————

  /** Reveal the version-note prompt, prefilled with the host's suggestion, always in the compact
   *  single-line state. No-op if it is already open. The latch in PromptBar closes the in-flight window
   *  the `!hidden` guard misses — without it a late reply would re-run the reset-to-single-line block
   *  below and silently discard a multi-line note the author had expanded into and started writing. */
  async openVersionNote(): Promise<void> {
    // Only one draft prompt at a time — close the send-for-review bar if it's open (both are draft-state).
    this.prBar.close();
    await this.versionBar.open(
      () => this.deps.suggestVersionNote(),
      (suggested) => {
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
      },
    );
  }

  closeVersionNote(): void {
    this.versionBar.close();
  }

  // —— Send-for-review (PR title/body) prompt ————————————————————————————————————————————————————————

  /** Reveal the send-for-review prompt, prefilled with the host's suggested PR title + body, for the
   *  author to confirm/edit the outward-facing text before the review opens. No-op if already open. */
  async openPrText(): Promise<void> {
    // Only one draft prompt at a time — close the version-note bar if it's open (both are draft-state).
    this.versionBar.close();
    await this.prBar.open(
      () => this.deps.suggestPrText(),
      (suggested) => {
        if (suggested.blocked) {
          // The send can't proceed — show the reason and do NOT open the prompt, so the author never
          // composes text into a send that would be rejected.
          this.deps.onPrBlocked(suggested.blocked);
          return;
        }
        if (this.prTitleInput) {
          this.prTitleInput.value = suggested.title;
        }
        if (this.prBodyTextarea) {
          this.prBodyTextarea.value = suggested.body;
        }
        if (this.prTextBar) {
          this.prTextBar.hidden = false;
        }
        this.prTitleInput?.focus();
        this.prTitleInput?.select();
      },
    );
  }

  closePrText(): void {
    this.prBar.close();
  }

  private confirmPrText(): void {
    const title = this.prTitleInput?.value.trim() ?? "";
    const body = this.prBodyTextarea?.value.trim() ?? "";
    this.prBar.close();
    this.deps.onPrText({ title, body });
  }

  /** Close every prompt bar (e.g. when a new document loads). */
  closeAll(): void {
    this.branchBar.close();
    this.versionBar.close();
    this.prBar.close();
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
    this.deps.onVersionNote(raw.trim());
  }
}
