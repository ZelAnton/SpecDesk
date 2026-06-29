import type { StatusState } from "./protocol.js";

/**
 * The author-facing lifecycle chrome: the action buttons (Open / Edit / Save version / Send for review /
 * Discard / Save) plus the formatting toolbar's visibility, driven by the document's lifecycle state.
 * The "which controls show in which lifecycle state" policy lives here in one tested place — a published
 * document offers Edit; once a draft is in progress the panes become editable and the format bar + the
 * draft actions (Save version / Discard / Send for review) take over while Edit hides. Discard and Send
 * for review are Draft-only (Discard isn't a legal move once In review; a sent draft is already
 * submitted), and Send for review additionally needs GitHub to be configured — without it there is no
 * Connect affordance, so the button would be a dead end. No IPC/protocol knowledge: index.ts supplies
 * each action as a callback (it owns the wire kinds) and the pane-editable coordination.
 */
export interface LifecycleChromeDeps {
  openBtn: HTMLButtonElement | null;
  editBtn: HTMLButtonElement | null;
  saveVersionBtn: HTMLButtonElement | null;
  sendForReviewBtn: HTMLButtonElement | null;
  discardBtn: HTMLButtonElement | null;
  saveBtn: HTMLButtonElement | null;
  formatBar: HTMLElement | null;
  /** Make both editor panes editable (a draft is in progress) or read-only. */
  setPaneEditable: (editable: boolean) => void;
  onOpen: () => void;
  onEdit: () => void;
  onSaveVersion: () => void;
  onSendForReview: () => void;
  onDiscard: () => void;
  onSave: () => void;
}

export class LifecycleChrome {
  private readonly deps: LifecycleChromeDeps;
  private state: StatusState = "published";
  private githubAvailable = false;

  constructor(deps: LifecycleChromeDeps) {
    this.deps = deps;
    deps.openBtn?.addEventListener("click", () => deps.onOpen());
    deps.editBtn?.addEventListener("click", () => deps.onEdit());
    deps.saveVersionBtn?.addEventListener("click", () => deps.onSaveVersion());
    deps.sendForReviewBtn?.addEventListener("click", () => deps.onSendForReview());
    deps.discardBtn?.addEventListener("click", () => deps.onDiscard());
    deps.saveBtn?.addEventListener("click", () => deps.onSave());
  }

  /**
   * Apply the chrome for a lifecycle state: both panes editable in any non-published state; the format
   * bar and Save version shown while a draft is in progress; Edit shown only when published; Discard and
   * Send for review shown only in the Draft state (Discard isn't legal once In review; a sent draft is
   * already submitted). Send for review additionally requires GitHub to be configured (see setGitHubAvailable).
   */
  setLifecycle(state: StatusState): void {
    this.state = state;
    const editing = state !== "published";
    this.deps.setPaneEditable(editing);
    if (this.deps.formatBar) {
      this.deps.formatBar.hidden = !editing;
    }
    if (this.deps.editBtn) {
      this.deps.editBtn.hidden = editing;
    }
    if (this.deps.saveVersionBtn) {
      this.deps.saveVersionBtn.hidden = !editing;
    }
    if (this.deps.discardBtn) {
      this.deps.discardBtn.hidden = state !== "draft";
    }
    this.applySendForReview();
  }

  /**
   * Whether GitHub sign-in is configured for this host (mirrors the account affordance). When it isn't,
   * "Send for review" stays hidden — there is no Connect button to act on its "connect first" message.
   */
  setGitHubAvailable(available: boolean): void {
    this.githubAvailable = available;
    this.applySendForReview();
  }

  private applySendForReview(): void {
    if (this.deps.sendForReviewBtn) {
      this.deps.sendForReviewBtn.hidden = !(this.state === "draft" && this.githubAvailable);
    }
  }
}
