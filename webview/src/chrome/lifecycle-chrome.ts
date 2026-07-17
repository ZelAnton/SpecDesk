import { isReviewState, type StatusState } from "../wire/protocol.js";

/**
 * The author-facing lifecycle chrome: the action buttons (Open / Edit / Save version / Send for review /
 * Discard / Save) plus the formatting toolbar's enabled state, driven by the document's lifecycle state.
 * The "which controls show in which lifecycle state" policy lives here in one tested place — a published
 * document offers Edit and a visible disabled format bar; once a draft is in progress the panes and bar
 * become editable and the
 * draft actions (Save version / Discard / Send for review) take over while Edit hides. Discard and Send
 * for review are Draft-only (Discard isn't a legal move once In review; a sent draft is already
 * submitted); once the draft is under review, Update review replaces Send for review (push the
 * newly-saved versions to the open PR). Once the review is Approved, Publish becomes available — but only
 * when the repo permits the author to publish (see setPublishAllowed) AND GitHub is configured. Both Send
 * for review and Update review (and Publish) additionally need GitHub to be configured — without it there
 * is no Connect affordance, so the button would be a dead end. No IPC/protocol knowledge: index.ts
 * supplies each action as a callback (it owns the wire kinds) and the pane-editable coordination.
 */
export interface LifecycleChromeDeps {
  openBtn: HTMLButtonElement | null;
  editBtn: HTMLButtonElement | null;
  saveVersionBtn: HTMLButtonElement | null;
  sendForReviewBtn: HTMLButtonElement | null;
  updateReviewBtn: HTMLButtonElement | null;
  publishBtn: HTMLButtonElement | null;
  discardBtn: HTMLButtonElement | null;
  saveBtn: HTMLButtonElement | null;
  formatBar: HTMLFieldSetElement | null;
  /** Make both editor panes editable (a draft is in progress) or read-only. */
  setPaneEditable: (editable: boolean) => void;
  onOpen: () => void;
  onEdit: () => void;
  onSaveVersion: () => void;
  onSendForReview: () => void;
  onUpdateReview: () => void;
  onPublish: () => void;
  onDiscard: () => void;
  onSave: () => void;
}

export class LifecycleChrome {
  private readonly deps: LifecycleChromeDeps;
  private state: StatusState = "published";
  private githubAvailable = false;
  private publishAllowed = false;
  private documentReadOnly = false;

  constructor(deps: LifecycleChromeDeps) {
    this.deps = deps;
    deps.openBtn?.addEventListener("click", () => deps.onOpen());
    deps.editBtn?.addEventListener("click", () => deps.onEdit());
    deps.saveVersionBtn?.addEventListener("click", () => deps.onSaveVersion());
    deps.sendForReviewBtn?.addEventListener("click", () => deps.onSendForReview());
    deps.updateReviewBtn?.addEventListener("click", () => deps.onUpdateReview());
    deps.publishBtn?.addEventListener("click", () => deps.onPublish());
    deps.discardBtn?.addEventListener("click", () => deps.onDiscard());
    deps.saveBtn?.addEventListener("click", () => deps.onSave());
  }

  /**
   * Apply the chrome for a lifecycle state: both panes editable in any non-published state; the format
   * bar enabled and Save version shown while a draft is in progress; Edit shown only when published; Discard and
   * Send for review shown only in the Draft state (Discard isn't legal once In review; a sent draft is
   * already submitted); Update review shown only while a review is open; Publish shown only once Approved.
   * Send for review, Update review, and Publish additionally require GitHub to be configured (see
   * setGitHubAvailable), and Publish also requires the repo to permit it (see setPublishAllowed).
   */
  setLifecycle(state: StatusState): void {
    this.state = state;
    const editing = state !== "published" && !this.documentReadOnly;
    this.deps.setPaneEditable(editing);
    if (this.deps.formatBar) {
      this.deps.formatBar.disabled = !editing;
    }
    if (this.deps.editBtn) {
      this.deps.editBtn.hidden = editing || this.documentReadOnly;
    }
    if (this.deps.saveVersionBtn) {
      this.deps.saveVersionBtn.hidden = !editing;
    }
    if (this.deps.discardBtn) {
      this.deps.discardBtn.hidden = this.documentReadOnly || state !== "draft";
    }
    if (this.deps.saveBtn) {
      this.deps.saveBtn.hidden = this.documentReadOnly;
    }
    this.applyReviewButtons();
  }

  setDocumentReadOnly(readOnly: boolean): void {
    this.documentReadOnly = readOnly;
    this.setLifecycle(this.state);
  }

  /**
   * Whether GitHub sign-in is configured for this host (mirrors the account affordance). When it isn't,
   * "Send for review" / "Update review" / "Publish" stay hidden — there is no Connect button to act on
   * their "connect first" message.
   */
  setGitHubAvailable(available: boolean): void {
    this.githubAvailable = available;
    this.applyReviewButtons();
  }

  /**
   * Whether the open document's repository permits the author to publish it themselves
   * (`[review] allow-author-publish`, carried on the workspace context). When it doesn't, "Publish" stays
   * hidden even on an approved document. This is only a UX gate — the host re-checks the same policy before
   * it merges — so it defaults to false (fail closed) until the context confirms it is allowed.
   */
  setPublishAllowed(allowed: boolean): void {
    this.publishAllowed = allowed;
    this.applyReviewButtons();
  }

  private applyReviewButtons(): void {
    if (this.deps.sendForReviewBtn) {
      this.deps.sendForReviewBtn.hidden =
        this.documentReadOnly || !(this.state === "draft" && this.githubAvailable);
    }
    if (this.deps.updateReviewBtn) {
      this.deps.updateReviewBtn.hidden =
        this.documentReadOnly || !(isReviewState(this.state) && this.githubAvailable);
    }
    if (this.deps.publishBtn) {
      // Publish appears only on an approved document, and only where GitHub is configured AND the repo
      // permits the author to publish (a destructive, irreversible merge — kept fail-closed).
      this.deps.publishBtn.hidden =
        this.documentReadOnly ||
        !(this.state === "approved" && this.githubAvailable && this.publishAllowed);
    }
  }
}
