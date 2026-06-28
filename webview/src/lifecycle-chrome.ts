/**
 * The author-facing lifecycle chrome: the action buttons (Open / Edit / Save version / Discard /
 * Save) plus the formatting toolbar's visibility, driven by one editing flag. The "which controls
 * show in which lifecycle state" policy lives here in one tested place — a published document offers
 * Edit; once a draft is in progress the panes become editable and the format bar + the draft-only
 * actions (Save version / Discard) take over while Edit hides. No IPC/protocol knowledge: index.ts
 * supplies each action as a callback (it owns the wire kinds) and the pane-editable coordination.
 */
export interface LifecycleChromeDeps {
  openBtn: HTMLButtonElement | null;
  editBtn: HTMLButtonElement | null;
  saveVersionBtn: HTMLButtonElement | null;
  discardBtn: HTMLButtonElement | null;
  saveBtn: HTMLButtonElement | null;
  formatBar: HTMLElement | null;
  /** Make both editor panes editable (a draft is in progress) or read-only. */
  setPaneEditable: (editable: boolean) => void;
  onOpen: () => void;
  onEdit: () => void;
  onSaveVersion: () => void;
  onDiscard: () => void;
  onSave: () => void;
}

export class LifecycleChrome {
  private readonly deps: LifecycleChromeDeps;

  constructor(deps: LifecycleChromeDeps) {
    this.deps = deps;
    deps.openBtn?.addEventListener("click", () => deps.onOpen());
    deps.editBtn?.addEventListener("click", () => deps.onEdit());
    deps.saveVersionBtn?.addEventListener("click", () => deps.onSaveVersion());
    deps.discardBtn?.addEventListener("click", () => deps.onDiscard());
    deps.saveBtn?.addEventListener("click", () => deps.onSave());
  }

  /**
   * Apply the chrome for an editing / read-only state: both panes editable while editing; the format
   * bar and the draft-only actions (Save version / Discard) shown only while a draft is in progress;
   * Edit shown only when it is not.
   */
  setEditing(editing: boolean): void {
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
      this.deps.discardBtn.hidden = !editing;
    }
  }
}
