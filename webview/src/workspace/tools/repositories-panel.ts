/**
 * The left-rail Repositories panel (design concept §9): the GitHub repositories the author registered, so
 * they're at hand. A small form at the top registers a new one from an `owner/name` or a GitHub URL — the
 * host validates and stores it, or emits an `error` the app already surfaces; each listed repo can be
 * opened or removed.
 *
 * Clicking a registered repository browses its remote tree without requiring a local copy. Explicit copy
 * actions remain separate. Like FileTree, this keeps NO IPC/Kinds knowledge — the integrator (index.ts)
 * passes plain callbacks, so the panel is unit-testable without a host bridge.
 */

import type {
  GitHubRepositoryOptionPayload,
  RegisteredBranch,
  RegisteredClone,
  RegisteredRepo,
  RepoCloneConflictPayload,
  RepoCloneDestinationPayload,
  RepoConfirmationPayload,
  RepoDescriptionPayload,
  RepositoryStatusPayload,
  WorkspaceItem,
  WorkspaceStatePayload,
} from "../../wire/protocol.js";
import { DestructiveConfirmation } from "../destructive-confirmation.js";
import { icon } from "../icons.js";
import type { PanelTool } from "../panel-tool.js";

export interface RepositoriesCallbacks {
  /** Clone into SpecDesk-managed storage. */
  onCloneManaged(url: string, localName: string, destinationPath: string): void;
  /** Ask the host for a parent folder, then clone there. */
  onCloneToFolder(url: string, localName: string): void;
  /** Resolve the exact managed path shown before Clone is allowed. */
  onDestinationRequest(url: string, localName: string, requestId: number): void;
  /** Resolve the current repository's description and visibility before Clone is allowed. */
  onDescriptionRequest(url: string, requestId: number): void;
  /** Remove the registered repository whose id is `id`. */
  onUnregister(id: string): void;
  /** Open the repository as the workspace — the host clones it into a managed folder (if needed) and opens it. */
  onBrowseRepo(repo: RegisteredRepo): void;
  onOpenClone(repo: RegisteredRepo, clonePath: string): void;
  onSwitchBranch(repo: RegisteredRepo, clonePath: string, branch: string): void;
  onCreateBranch(repo: RegisteredRepo, clonePath: string, branch: string): void;
  onRenameClone(repo: RegisteredRepo, clonePath: string, localName: string): void;
  onRenameBranch(repo: RegisteredRepo, clonePath: string, branch: string, newBranch: string): void;
  onOpenExistingClone(url: string, clonePath: string): void;
  onToggleFavorite?(repo: RegisteredRepo, favorite: boolean): void;
  onToggleCloneFavorite?(repo: RegisteredRepo, clonePath: string, favorite: boolean): void;
  onToggleBranchFavorite?(
    repo: RegisteredRepo,
    clonePath: string,
    branch: string,
    favorite: boolean,
  ): void;
  onDeleteClone(repo: RegisteredRepo, clonePath: string, confirmationToken?: string): void;
  onDeleteBranch(
    repo: RegisteredRepo,
    clonePath: string,
    branch: string,
    confirmationToken?: string,
  ): void;
  onRefresh(requestId: number): void;
  /** Ask the host to check every registered local copy for upstream updates in the background. Sent on a
   *  focus-gated cadence (no request id, no user action); the host throttles, applies any safe fast-forward,
   *  and pushes back a fresh workspace state the panel reacts to. */
  onAutoSync?(): void;
  onPull(repo: RegisteredRepo, clonePath: string, branch: string): void;
  onPush(repo: RegisteredRepo, clonePath: string, branch: string): void;
}

let suggestionListSequence = 0;
let repositoryOperationRequestSequence = 0;
const SKIP_CLONE_CONFIRMATION_KEY = "specdesk.clone.skip-confirmation.v1";
// Background auto-sync cadence: while the window has focus, ask the host to check every registered local copy
// for upstream updates on this interval (a change can land while the window stays focused, so a focus event
// alone would miss it). The host throttles far more aggressively, so this only needs to be loose enough to feel
// live without being chatty.
const AUTO_SYNC_POLL_INTERVAL_MS = 180_000;

/** One process-wide request-id namespace prevents concurrent Refresh and editor transitions from
 * accepting each other's completion event. */
export function nextRepositoryOperationRequestId(): number {
  repositoryOperationRequestSequence += 1;
  return repositoryOperationRequestSequence;
}

interface PendingCloneConfirmation {
  readonly url: string;
  readonly summary: string;
  readonly run: () => void;
}

interface PendingManagedClone {
  readonly url: string;
  readonly localName: string;
  readonly destination: string;
}

interface RepositoryMenuItem {
  readonly label: string;
  readonly danger?: boolean;
  readonly destructiveDescription?: string;
  readonly run: () => void;
}

export class RepositoriesPanel implements PanelTool {
  readonly id = "repositories";
  readonly label = "Repositories";
  readonly icon = icon("repositories");

  private input: HTMLInputElement | null = null;
  private localNameInput: HTMLInputElement | null = null;
  private suggestionsEl: HTMLUListElement | null = null;
  private publicHintEl: HTMLElement | null = null;
  private cloneMenuEl: HTMLElement | null = null;
  private clonePrimaryEl: HTMLButtonElement | null = null;
  private cloneToggleEl: HTMLButtonElement | null = null;
  private managedActionEl: HTMLButtonElement | null = null;
  private folderActionEl: HTMLButtonElement | null = null;
  private destinationEl: HTMLElement | null = null;
  private descriptionEl: HTMLElement | null = null;
  private confirmationEl: HTMLElement | null = null;
  private confirmationSummaryEl: HTMLElement | null = null;
  private confirmationSkipEl: HTMLInputElement | null = null;
  private confirmationYesEl: HTMLButtonElement | null = null;
  private cloneFormEl: HTMLFormElement | null = null;
  private listEl: HTMLElement | null = null;
  private operationConfirmationEl: HTMLElement | null = null;
  private operationMessageEl: HTMLElement | null = null;
  private operationWarningsEl: HTMLUListElement | null = null;
  private refreshEl: HTMLButtonElement | null = null;
  private repositorySummaryEl: HTMLElement | null = null;
  private refreshRequestId: number | null = null;
  private pendingOperation: RepoConfirmationPayload | null = null;
  private operationReturnFocus: HTMLButtonElement | null = null;
  private readonly operationFocusTargets = new Map<string, HTMLButtonElement>();
  private emptyEl: HTMLElement | null = null;
  private repos: readonly RegisteredRepo[] = [];
  private favorites: readonly WorkspaceItem[] = [];
  private highlightedRepoId: string | null = null;
  private suggestions: readonly GitHubRepositoryOptionPayload[] = [];
  private filteredSuggestions: readonly GitHubRepositoryOptionPayload[] = [];
  private activeSuggestion = -1;
  private cloneActionPending = false;
  private destinationRequestId = 0;
  private destinationTimer: number | null = null;
  private managedDestination: string | null = null;
  private managedDestinationOccupied = false;
  private localNameCustomized = false;
  private descriptionRequestId = 0;
  private descriptionTimer: number | null = null;
  private descriptionReady = false;
  private skipCloneConfirmation = false;
  private pendingConfirmation: PendingCloneConfirmation | null = null;
  private pendingManagedClone: PendingManagedClone | null = null;
  private confirmationReturnFocus: HTMLElement | null = null;
  private contextMenuEl: HTMLElement | null = null;
  private contextMenuReturnFocus: HTMLElement | null = null;
  private nameDialogEl: HTMLDialogElement | null = null;
  private nameDialogInputEl: HTMLInputElement | null = null;
  private nameDialogErrorEl: HTMLElement | null = null;
  private pendingNameAction: ((value: string) => void) | null = null;
  private readonly destructiveConfirmation = new DestructiveConfirmation();
  private readonly requestAutoSyncOnFocus = (): void => this.requestAutoSync();

  constructor(private readonly callbacks: RepositoriesCallbacks) {}

  mount(body: HTMLElement): void {
    const root = document.createElement("div");
    root.className = "repositories";

    const actions = document.createElement("div");
    actions.className = "repo-panel-actions";
    const actionsHint = document.createElement("span");
    actionsHint.textContent = "No repositories yet";
    const refresh = document.createElement("button");
    refresh.type = "button";
    refresh.className = "repo-refresh";
    refresh.textContent = "Refresh";
    refresh.title = "Check every local copy for updates";
    refresh.disabled = true;
    refresh.addEventListener("click", () => {
      if (this.refreshRequestId !== null) {
        return;
      }
      const requestId = nextRepositoryOperationRequestId();
      this.refreshRequestId = requestId;
      this.updateRefreshAction();
      this.callbacks.onRefresh(requestId);
    });
    actions.append(actionsHint, refresh);

    // The register form: a text field + Add. Submitting registers the typed repo and clears the field.
    const form = document.createElement("form");
    form.className = "repo-register";

    const input = document.createElement("input");
    input.type = "text";
    input.className = "repo-register-input";
    input.setAttribute("aria-label", "Repository owner/name or GitHub URL");
    input.placeholder = "e.g. acme/specs or a GitHub link";
    input.setAttribute("role", "combobox");
    input.setAttribute("aria-autocomplete", "list");
    input.setAttribute("aria-expanded", "false");

    const suggestions = document.createElement("ul");
    suggestions.id = `repo-suggestions-${++suggestionListSequence}`;
    suggestions.className = "repo-suggestions";
    suggestions.setAttribute("role", "listbox");
    suggestions.setAttribute("aria-label", "Available GitHub repositories");
    suggestions.hidden = true;
    input.setAttribute("aria-controls", suggestions.id);
    input.addEventListener("input", () => {
      this.cloneActionPending = false;
      this.syncSuggestedLocalName();
      this.updateSuggestions();
      this.scheduleDestination();
      this.scheduleDescription();
    });
    input.addEventListener("keydown", (event) => this.onSuggestionKeydown(event));
    input.addEventListener("blur", () => this.closeSuggestions());

    const localNameField = document.createElement("label");
    localNameField.className = "repo-local-name-field";
    const localNameLabel = document.createElement("span");
    localNameLabel.textContent = "Local copy name";
    const localNameInput = document.createElement("input");
    localNameInput.type = "text";
    localNameInput.className = "repo-local-name-input";
    localNameInput.placeholder = "specs";
    localNameInput.autocomplete = "off";
    localNameInput.addEventListener("input", () => {
      this.localNameCustomized = true;
      this.scheduleDestination();
    });
    localNameField.append(localNameLabel, localNameInput);

    const publicHint = document.createElement("span");
    publicHint.className = "repo-public-hint";
    publicHint.setAttribute("role", "status");
    publicHint.textContent =
      "Not in your suggestions — you can still use a public owner/repository.";
    publicHint.hidden = true;

    const cloneSplit = document.createElement("div");
    cloneSplit.className = "repo-clone-split";
    const clonePrimary = document.createElement("button");
    clonePrimary.type = "submit";
    clonePrimary.className = "repo-register-add repo-clone-primary";
    clonePrimary.textContent = "Clone…";
    clonePrimary.disabled = true;
    const cloneToggle = document.createElement("button");
    cloneToggle.type = "button";
    cloneToggle.className = "repo-register-add repo-clone-toggle";
    cloneToggle.textContent = "▾";
    cloneToggle.setAttribute("aria-label", "More clone options");
    cloneToggle.setAttribute("aria-haspopup", "menu");
    cloneToggle.setAttribute("aria-expanded", "false");
    cloneToggle.disabled = true;
    cloneSplit.append(clonePrimary, cloneToggle);

    const cloneMenu = document.createElement("div");
    cloneMenu.className = "repo-clone-menu";
    cloneMenu.setAttribute("role", "menu");
    cloneMenu.hidden = true;
    const managed = document.createElement("button");
    managed.type = "button";
    managed.className = "repo-clone-menu-action";
    managed.setAttribute("role", "menuitem");
    managed.textContent = "Clone…";
    managed.disabled = true;
    managed.addEventListener("click", () => this.requestManagedClone());
    const toFolder = document.createElement("button");
    toFolder.type = "button";
    toFolder.className = "repo-clone-menu-action";
    toFolder.setAttribute("role", "menuitem");
    toFolder.textContent = "Clone to folder…";
    toFolder.disabled = true;
    toFolder.addEventListener("click", () => this.requestFolderClone());
    cloneMenu.append(managed, toFolder);
    cloneToggle.addEventListener("click", () => this.toggleCloneMenu());

    const destination = document.createElement("div");
    destination.className = "repo-managed-destination";
    destination.setAttribute("role", "status");
    destination.setAttribute("aria-live", "polite");
    destination.hidden = true;

    const description = document.createElement("output");
    description.className = "repo-description";
    description.setAttribute("role", "status");
    description.setAttribute("aria-live", "polite");
    description.hidden = true;

    const confirmation = document.createElement("div");
    confirmation.className = "repo-clone-confirmation";
    confirmation.setAttribute("role", "dialog");
    confirmation.setAttribute("aria-labelledby", "repo-clone-confirm-title");
    confirmation.hidden = true;
    const confirmationTitle = document.createElement("strong");
    confirmationTitle.id = "repo-clone-confirm-title";
    confirmationTitle.textContent = "Clone repository?";
    const confirmationSummary = document.createElement("p");
    confirmationSummary.className = "repo-clone-confirm-summary";
    const confirmationSkipLabel = document.createElement("label");
    confirmationSkipLabel.className = "repo-clone-confirm-skip";
    const confirmationSkip = document.createElement("input");
    confirmationSkip.type = "checkbox";
    confirmationSkipLabel.append(confirmationSkip, " Do not show this confirmation again");
    const confirmationActions = document.createElement("div");
    confirmationActions.className = "repo-clone-confirm-actions";
    const confirmationNo = document.createElement("button");
    confirmationNo.type = "button";
    confirmationNo.textContent = "No";
    confirmationNo.addEventListener("click", () => this.cancelCloneConfirmation());
    const confirmationYes = document.createElement("button");
    confirmationYes.type = "button";
    confirmationYes.textContent = "Yes";
    confirmationYes.className = "repo-clone-confirm-yes";
    confirmationYes.addEventListener("click", () => this.acceptCloneConfirmation());
    confirmationActions.append(confirmationNo, confirmationYes);
    confirmation.append(
      confirmationTitle,
      confirmationSummary,
      confirmationSkipLabel,
      confirmationActions,
    );
    confirmation.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        event.preventDefault();
        this.cancelCloneConfirmation();
      }
    });

    form.append(
      input,
      cloneSplit,
      suggestions,
      cloneMenu,
      publicHint,
      description,
      localNameField,
      destination,
    );
    form.addEventListener("submit", (event) => {
      event.preventDefault();
      this.requestManagedClone();
    });

    const addRepository = document.createElement("section");
    addRepository.className = "repo-add";
    addRepository.setAttribute("aria-label", "Add or copy a repository");
    addRepository.append(form);

    const empty = document.createElement("p");
    empty.className = "repo-empty";
    empty.textContent = "Add a repository to keep it ready.";

    const list = document.createElement("ul");
    list.className = "repo-list";
    list.setAttribute("aria-label", "Registered repositories");

    const operationConfirmation = document.createElement("section");
    operationConfirmation.className = "repo-operation-confirmation";
    operationConfirmation.setAttribute("role", "alertdialog");
    operationConfirmation.setAttribute("aria-labelledby", "repo-operation-title");
    operationConfirmation.hidden = true;
    const operationTitle = document.createElement("strong");
    operationTitle.id = "repo-operation-title";
    operationTitle.textContent = "Delete local work?";
    const operationMessage = document.createElement("p");
    operationMessage.className = "repo-operation-message";
    const operationWarnings = document.createElement("ul");
    operationWarnings.className = "repo-operation-warnings";
    const operationActions = document.createElement("div");
    operationActions.className = "repo-clone-confirm-actions";
    const operationCancel = document.createElement("button");
    operationCancel.type = "button";
    operationCancel.textContent = "Keep it";
    operationCancel.addEventListener("click", () => this.closeOperationConfirmation());
    const operationDelete = document.createElement("button");
    operationDelete.type = "button";
    operationDelete.className = "repo-operation-delete";
    operationDelete.textContent = "Confirm deletion";
    operationDelete.addEventListener("click", () => this.confirmOperation());
    operationActions.append(operationCancel, operationDelete);
    operationConfirmation.append(
      operationTitle,
      operationMessage,
      operationWarnings,
      operationActions,
    );
    operationConfirmation.addEventListener("keydown", (event) => {
      if (event.key === "Escape") {
        event.preventDefault();
        this.closeOperationConfirmation();
      }
    });

    const contextMenu = document.createElement("div");
    contextMenu.className = "repo-context-menu";
    contextMenu.setAttribute("role", "menu");
    contextMenu.hidden = true;

    const nameDialog = document.createElement("dialog");
    nameDialog.className = "repo-name-dialog";
    const nameForm = document.createElement("form");
    nameForm.method = "dialog";
    const nameTitle = document.createElement("strong");
    nameTitle.className = "repo-name-dialog-title";
    const nameLabel = document.createElement("label");
    nameLabel.textContent = "Name";
    const nameInput = document.createElement("input");
    nameInput.className = "repo-name-dialog-input";
    nameInput.autocomplete = "off";
    nameLabel.append(nameInput);
    const nameError = document.createElement("p");
    nameError.className = "repo-name-dialog-error";
    nameError.setAttribute("role", "alert");
    nameError.hidden = true;
    const nameActions = document.createElement("div");
    nameActions.className = "repo-name-dialog-actions";
    const nameCancel = document.createElement("button");
    nameCancel.type = "button";
    nameCancel.textContent = "Cancel";
    nameCancel.addEventListener("click", () => this.closeNameDialog());
    const nameSubmit = document.createElement("button");
    nameSubmit.type = "submit";
    nameSubmit.textContent = "Continue";
    nameActions.append(nameCancel, nameSubmit);
    nameForm.append(nameTitle, nameLabel, nameError, nameActions);
    nameForm.addEventListener("submit", (event) => {
      event.preventDefault();
      const value = nameInput.value.trim();
      const valid =
        nameDialog.dataset.kind === "clone" ? isValidLocalName(value) : isValidBranchName(value);
      if (!valid) {
        nameError.textContent =
          nameDialog.dataset.kind === "clone"
            ? "Use a Windows folder name without path characters."
            : "Use a valid working-line name without spaces or reserved sequences.";
        nameError.hidden = false;
        return;
      }
      const action = this.pendingNameAction;
      this.closeNameDialog();
      action?.(value);
    });
    nameDialog.addEventListener("close", () => {
      this.pendingNameAction = null;
      this.contextMenuReturnFocus?.focus();
    });
    nameDialog.append(nameForm);

    root.append(
      actions,
      addRepository,
      confirmation,
      operationConfirmation,
      empty,
      list,
      contextMenu,
      nameDialog,
    );
    body.appendChild(root);
    this.input = input;
    this.localNameInput = localNameInput;
    this.suggestionsEl = suggestions;
    this.publicHintEl = publicHint;
    this.cloneMenuEl = cloneMenu;
    this.clonePrimaryEl = clonePrimary;
    this.cloneToggleEl = cloneToggle;
    this.managedActionEl = managed;
    this.folderActionEl = toFolder;
    this.destinationEl = destination;
    this.descriptionEl = description;
    this.confirmationEl = confirmation;
    this.confirmationSummaryEl = confirmationSummary;
    this.confirmationSkipEl = confirmationSkip;
    this.confirmationYesEl = confirmationYes;
    this.cloneFormEl = form;
    this.skipCloneConfirmation = readSkipCloneConfirmation();
    this.emptyEl = empty;
    this.listEl = list;
    this.operationConfirmationEl = operationConfirmation;
    this.operationMessageEl = operationMessage;
    this.operationWarningsEl = operationWarnings;
    this.refreshEl = refresh;
    this.repositorySummaryEl = actionsHint;
    this.contextMenuEl = contextMenu;
    this.nameDialogEl = nameDialog;
    this.nameDialogInputEl = nameInput;
    this.nameDialogErrorEl = nameError;
    document.addEventListener("pointerdown", (event) => {
      if (contextMenu.hidden || contextMenu.contains(event.target as Node)) return;
      this.closeContextMenu();
    });
    // Background auto-sync (T-076): while the window is in use, keep every registered local copy's
    // "updates available"/"conflict" indicators current — and let the host safely fast-forward a clean main line
    // — without a manual Refresh. Mirrors the review-status poll: a focus-gated interval (a change can land while
    // the window stays focused) plus a refresh whenever the window regains focus ("check GitHub, come back").
    // The host throttles, single-flights, and only touches upstream when there is a local copy to sync; the panel
    // just reacts to the workspace state it pushes back.
    // A singleton panel that lives for the app's lifetime (like the review-status poll in index.ts), so the
    // interval and focus listener are never torn down.
    window.setInterval(() => {
      if (document.hasFocus()) {
        this.requestAutoSync();
      }
    }, AUTO_SYNC_POLL_INTERVAL_MS);
    window.addEventListener("focus", this.requestAutoSyncOnFocus);
    this.render();
  }

  /** Trigger a background auto-sync, but only when there is at least one local copy to check — an empty list has
   *  nothing to fetch, and the callback stays optional so the panel is testable without a host bridge. */
  private requestAutoSync(): void {
    if (!this.repos.some((repo) => repo.clones.length > 0)) {
      return;
    }
    this.callbacks.onAutoSync?.();
  }

  setOperationConfirmation(payload: RepoConfirmationPayload): void {
    this.pendingOperation = payload;
    this.operationReturnFocus =
      this.operationFocusTargets.get(
        this.operationKey(payload.operation, payload.id, payload.clonePath, payload.branch),
      ) ?? null;
    if (this.operationMessageEl !== null) {
      this.operationMessageEl.textContent = payload.message;
    }
    if (this.operationWarningsEl !== null) {
      this.operationWarningsEl.replaceChildren(
        ...payload.warnings.map((warning) => {
          const item = document.createElement("li");
          item.textContent = warning;
          return item;
        }),
      );
    }
    if (this.operationConfirmationEl !== null) {
      const localKind = payload.operation === "deleteBranch" ? "branch" : "copy";
      const title = this.operationConfirmationEl.querySelector<HTMLElement>("strong");
      if (title !== null) {
        title.textContent = `Delete local ${localKind}?`;
      }
      const action =
        this.operationConfirmationEl.querySelector<HTMLButtonElement>(".repo-operation-delete");
      if (action !== null) {
        action.textContent = "Confirm deletion";
        action.setAttribute("aria-label", `Confirm deletion of local ${localKind}`);
      }
      this.operationConfirmationEl.hidden = false;
      this.operationConfirmationEl
        .querySelector<HTMLButtonElement>(".repo-operation-delete")
        ?.focus();
    }
    this.cloneFormEl?.toggleAttribute("inert", true);
    this.listEl?.toggleAttribute("inert", true);
  }

  /** Replace the repository list with the host's latest workspace state. */
  setState(state: WorkspaceStatePayload): void {
    this.destructiveConfirmation.close(false);
    this.repos = state.repositories;
    this.favorites = state.favorites;
    const cloneCount = state.repositories.reduce((count, repo) => count + repo.clones.length, 0);
    if (this.repositorySummaryEl !== null) {
      this.repositorySummaryEl.textContent =
        state.repositories.length === 0
          ? "No repositories yet"
          : `${state.repositories.length} ${state.repositories.length === 1 ? "repository" : "repositories"} · ${cloneCount} local ${cloneCount === 1 ? "copy" : "copies"}`;
    }
    this.clearManagedCloneInputAfterSuccess();
    this.render();
    this.updateRefreshAction();
  }

  /** Finish Refresh only for the request this panel actually started. Other repository-operation
   * completions share the same event kind and must not make Refresh look idle. */
  operationCompleted(requestId: number): void {
    if (this.refreshRequestId !== requestId) {
      return;
    }
    this.refreshRequestId = null;
    this.updateRefreshAction();
  }

  private updateRefreshAction(): void {
    if (this.refreshEl === null) {
      return;
    }
    this.refreshEl.disabled =
      this.refreshRequestId !== null || !this.repos.some((repo) => repo.clones.length > 0);
    this.refreshEl.textContent = this.refreshRequestId !== null ? "Refreshing…" : "Refresh";
  }

  /** Reveal and focus a registered repository selected from another surface, such as Favorites. */
  revealRepository(id: string): void {
    const repo = this.repos.find((candidate) => candidate.id.toLowerCase() === id.toLowerCase());
    if (repo === undefined) {
      return;
    }
    this.highlightedRepoId = repo.id;
    this.render();
    const button = Array.from(
      this.listEl?.querySelectorAll<HTMLButtonElement>(".repo-open") ?? [],
    ).find((candidate) => candidate.dataset.id?.toLowerCase() === repo.id.toLowerCase());
    button?.focus();
    button?.scrollIntoView?.({ block: "nearest" });
  }

  focusPrimary(): void {
    this.input?.focus();
    this.input?.select();
  }

  /** Replace the connected account's repository autocomplete choices. */
  setSuggestions(suggestions: readonly GitHubRepositoryOptionPayload[]): void {
    const unique = new Map<string, GitHubRepositoryOptionPayload>();
    for (const suggestion of suggestions) {
      const key = suggestion.fullName.toLocaleLowerCase();
      if (!unique.has(key)) {
        unique.set(key, suggestion);
      }
    }
    this.suggestions = [...unique.values()].sort((left, right) =>
      left.fullName.localeCompare(right.fullName, undefined, { sensitivity: "base" }),
    );
    this.updateSuggestions();
  }

  /** Drop repository discovery state at a GitHub account boundary. The input may name a private repository,
   *  and a resolved description/confirmation is authorization-specific, so none of it may survive sign-out
   *  or an account replacement. Incrementing both request ids makes already-queued host replies stale. */
  clearAccountState(): void {
    this.destructiveConfirmation.close(false);
    this.closeOperationConfirmation(false);
    if (this.destinationTimer !== null) {
      window.clearTimeout(this.destinationTimer);
      this.destinationTimer = null;
    }
    if (this.descriptionTimer !== null) {
      window.clearTimeout(this.descriptionTimer);
      this.descriptionTimer = null;
    }
    this.destinationRequestId++;
    this.descriptionRequestId++;
    this.managedDestination = null;
    this.managedDestinationOccupied = false;
    this.localNameCustomized = false;
    this.descriptionReady = false;
    this.cloneActionPending = false;
    this.pendingManagedClone = null;
    this.suggestions = [];

    const focusWasInConfirmation = this.confirmationEl?.contains(document.activeElement) === true;
    this.pendingConfirmation = null;
    this.confirmationReturnFocus = null;
    if (this.confirmationEl !== null) {
      this.confirmationEl.hidden = true;
    }
    this.setCloneContentInert(false);

    if (this.input !== null) {
      this.input.value = "";
    }
    if (this.localNameInput !== null) {
      this.localNameInput.value = "";
    }
    this.closeSuggestions();
    this.closeCloneMenu();
    if (this.publicHintEl !== null) {
      this.publicHintEl.hidden = true;
    }
    if (this.destinationEl !== null) {
      this.destinationEl.hidden = true;
      this.destinationEl.textContent = "";
      this.destinationEl.title = "";
    }
    if (this.descriptionEl !== null) {
      this.descriptionEl.hidden = true;
      this.descriptionEl.textContent = "";
    }
    this.updateCloneAvailability();
    if (focusWasInConfirmation) {
      this.input?.focus();
    }
  }

  setManagedDestination(payload: RepoCloneDestinationPayload): void {
    this.syncSuggestedLocalName();
    if (
      payload.requestId !== this.destinationRequestId ||
      this.input?.value.trim() !== payload.url ||
      this.localNameInput?.value.trim() !== payload.localName
    ) {
      return;
    }
    this.managedDestination = payload.path ?? null;
    this.managedDestinationOccupied = payload.exists;
    if (this.destinationEl !== null) {
      this.destinationEl.hidden = false;
      this.destinationEl.replaceChildren();
      const existingClonePath = payload.existingClonePath;
      if (payload.exists && existingClonePath) {
        const warning = document.createElement("span");
        warning.className = "repo-destination-warning";
        warning.textContent = `A local copy named “${payload.localName}” already exists.`;
        const open = document.createElement("button");
        open.type = "button";
        open.className = "repo-open-existing";
        open.textContent = "Open existing copy";
        open.addEventListener("click", () => this.openExistingClone(existingClonePath));
        this.destinationEl.append(warning, open);
      } else if (payload.exists) {
        const warning = document.createElement("span");
        warning.className = "repo-destination-warning";
        warning.textContent = `A file or folder named “${payload.localName}” already exists. Choose another local copy name.`;
        this.destinationEl.append(warning);
      } else {
        this.destinationEl.textContent = payload.path
          ? `Managed destination: ${payload.path}`
          : "Managed destination unavailable for this entry.";
      }
      this.destinationEl.title = payload.path ?? "";
    }
    this.updateCloneAvailability();
  }

  setCloneConflict(payload: RepoCloneConflictPayload): void {
    if (
      this.input?.value.trim() !== payload.url ||
      this.localNameInput?.value.trim() !== payload.localName
    ) {
      return;
    }
    this.managedDestination = null;
    this.managedDestinationOccupied = true;
    if (this.destinationEl !== null) {
      this.destinationEl.hidden = false;
      this.destinationEl.replaceChildren();
      const warning = document.createElement("span");
      warning.className = "repo-destination-warning";
      warning.textContent = payload.message;
      const open = document.createElement("button");
      open.type = "button";
      open.className = "repo-open-existing";
      open.textContent = "Open existing copy";
      open.addEventListener("click", () => this.openExistingClone(payload.existingClonePath));
      this.destinationEl.append(warning, open);
    }
    this.updateCloneAvailability();
  }

  setDescription(payload: RepoDescriptionPayload): void {
    if (
      payload.requestId !== this.descriptionRequestId ||
      this.input?.value.trim() !== payload.url
    ) {
      return;
    }
    this.descriptionReady = payload.state === "found" || payload.state === "private";
    const previousName = this.localNameInput?.value ?? "";
    this.syncSuggestedLocalName();
    if (this.localNameInput?.value !== previousName) {
      this.scheduleDestination();
    }
    if (this.descriptionEl !== null) {
      this.descriptionEl.hidden = false;
      const description = payload.description?.trim();
      if (payload.state === "notFound") {
        this.descriptionEl.textContent = "Repository not found. Check owner/repository.";
      } else if (payload.state === "error") {
        this.descriptionEl.textContent =
          "Couldn’t load the repository description. Check your connection and try again.";
      } else {
        const visibility = payload.state === "private" ? "Private repository · " : "";
        this.descriptionEl.textContent = description
          ? `${visibility}Description: ${description}`
          : `${visibility}No description provided.`;
      }
    }
    this.updateCloneAvailability();
  }

  private executeClone(url: string, action: (url: string) => void): void {
    if (this.input === null) {
      return;
    }
    if (url === "" || this.cloneActionPending) {
      return;
    }
    // Keep the exact entry until the author changes it. Native clone can fail or be cancelled without a
    // correlated success frame; clearing here used to destroy the only retryable copy of owner/name.
    this.closeSuggestions();
    this.closeCloneMenu();
    this.cloneActionPending = true;
    try {
      action(url);
    } finally {
      // Sending IPC is synchronous. Refresh the destination because a native collision can make the path
      // stale before it rejects this request; the unchanged owner/name becomes retryable as soon as the
      // authoritative replacement arrives.
      this.cloneActionPending = false;
      this.scheduleDestination();
    }
  }

  private requestManagedClone(): void {
    const destination = this.managedDestination;
    const url = this.input?.value.trim() ?? "";
    const localName = this.localNameInput?.value.trim() ?? "";
    if (destination === null || url === "" || !isValidLocalName(localName)) {
      return;
    }
    this.requestCloneConfirmation({
      url,
      summary: `Create local copy “${localName}” at ${destination}?`,
      run: () => {
        this.pendingManagedClone = { url, localName, destination };
        this.executeClone(url, (value) =>
          this.callbacks.onCloneManaged(value, localName, destination),
        );
      },
    });
  }

  private clearManagedCloneInputAfterSuccess(): void {
    const pending = this.pendingManagedClone;
    if (pending === null || this.input?.value.trim() !== pending.url) {
      return;
    }
    const pendingRepo = normalizeGitHubRepository(pending.url);
    const completed =
      pendingRepo !== null &&
      this.repos.some(
        (repo) =>
          repo.id.toLocaleLowerCase() === pendingRepo &&
          repo.clones.some((clone) => sameLocalPath(clone.path, pending.destination)),
      );
    if (!completed) {
      return;
    }
    this.pendingManagedClone = null;
    this.input.value = "";
    if (this.localNameInput !== null) {
      this.localNameInput.value = "";
    }
    this.localNameCustomized = false;
    this.updateSuggestions();
    this.scheduleDestination();
    this.scheduleDescription();
  }

  private requestFolderClone(): void {
    const url = this.input?.value.trim() ?? "";
    const localName = this.localNameInput?.value.trim() ?? "";
    if (url === "" || !isValidLocalName(localName)) {
      return;
    }
    this.requestCloneConfirmation({
      url,
      summary: `Choose where to create local copy “${localName}”?`,
      run: () => {
        // Keep the entry visible: the native folder picker may be cancelled and intentionally emits no
        // success frame. The modal picker prevents a second click while it is open, so no pending flag is
        // needed here; retaining the text makes cancel-and-retry immediate and lossless.
        this.closeSuggestions();
        this.closeCloneMenu();
        this.callbacks.onCloneToFolder(url, localName);
      },
    });
  }

  private requestCloneConfirmation(pending: PendingCloneConfirmation): void {
    if (this.cloneActionPending || this.pendingConfirmation !== null) {
      return;
    }
    this.closeCloneMenu();
    if (this.skipCloneConfirmation) {
      pending.run();
      return;
    }
    this.pendingConfirmation = pending;
    this.confirmationReturnFocus = this.clonePrimaryEl;
    if (this.confirmationSummaryEl !== null) {
      this.confirmationSummaryEl.textContent = pending.summary;
    }
    if (this.confirmationSkipEl !== null) {
      this.confirmationSkipEl.checked = false;
    }
    if (this.confirmationEl !== null) {
      this.confirmationEl.hidden = false;
    }
    this.setCloneContentInert(true);
    this.confirmationYesEl?.focus();
  }

  private acceptCloneConfirmation(): void {
    const pending = this.pendingConfirmation;
    if (pending === null) {
      return;
    }
    this.pendingConfirmation = null;
    if (this.confirmationSkipEl?.checked === true) {
      this.skipCloneConfirmation = true;
      writeSkipCloneConfirmation();
    }
    if (this.confirmationEl !== null) {
      this.confirmationEl.hidden = true;
    }
    this.setCloneContentInert(false);
    this.confirmationReturnFocus = null;
    pending.run();
    this.input?.focus();
  }

  private cancelCloneConfirmation(): void {
    if (this.pendingConfirmation === null) {
      return;
    }
    this.pendingConfirmation = null;
    if (this.confirmationEl !== null) {
      this.confirmationEl.hidden = true;
    }
    this.setCloneContentInert(false);
    this.confirmationReturnFocus?.focus();
    this.confirmationReturnFocus = null;
  }

  private setCloneContentInert(inert: boolean): void {
    if (this.cloneFormEl !== null) {
      this.cloneFormEl.inert = inert;
    }
    if (this.listEl !== null) {
      this.listEl.inert = inert;
    }
  }

  private scheduleDestination(): void {
    if (this.destinationTimer !== null) {
      window.clearTimeout(this.destinationTimer);
      this.destinationTimer = null;
    }
    this.destinationRequestId++;
    this.managedDestination = null;
    this.managedDestinationOccupied = false;
    this.updateCloneAvailability();
    const url = this.input?.value.trim() ?? "";
    const localName = this.localNameInput?.value.trim() ?? "";
    const validName = isValidLocalName(localName);
    this.localNameInput?.setAttribute("aria-invalid", String(localName !== "" && !validName));
    if (url === "" || !validName) {
      if (this.destinationEl !== null) {
        this.destinationEl.hidden = url === "" && localName === "";
        this.destinationEl.textContent =
          url === "" && localName === ""
            ? ""
            : "Choose a local name without Windows path characters.";
      }
      return;
    }
    if (this.destinationEl !== null) {
      this.destinationEl.hidden = false;
      this.destinationEl.textContent = "Managed destination: checking…";
      this.destinationEl.title = "";
    }
    const requestId = this.destinationRequestId;
    this.destinationTimer = window.setTimeout(() => {
      this.destinationTimer = null;
      this.callbacks.onDestinationRequest(url, localName, requestId);
    }, 120);
  }

  private scheduleDescription(): void {
    if (this.descriptionTimer !== null) {
      window.clearTimeout(this.descriptionTimer);
      this.descriptionTimer = null;
    }
    this.descriptionRequestId++;
    this.descriptionReady = false;
    this.closeCloneMenu();
    this.updateCloneAvailability();
    const url = this.input?.value.trim() ?? "";
    if (url === "") {
      if (this.descriptionEl !== null) {
        this.descriptionEl.hidden = true;
      }
      return;
    }
    if (this.descriptionEl !== null) {
      this.descriptionEl.hidden = false;
      this.descriptionEl.textContent = "Repository description: loading…";
    }
    const requestId = this.descriptionRequestId;
    this.descriptionTimer = window.setTimeout(() => {
      this.descriptionTimer = null;
      this.callbacks.onDescriptionRequest(url, requestId);
    }, 220);
  }

  private updateCloneAvailability(): void {
    const validName = isValidLocalName(this.localNameInput?.value.trim() ?? "");
    const managedAvailable =
      this.descriptionReady &&
      validName &&
      this.managedDestination !== null &&
      !this.managedDestinationOccupied;
    if (this.clonePrimaryEl !== null) {
      this.clonePrimaryEl.disabled = !managedAvailable;
    }
    if (this.cloneToggleEl !== null) {
      this.cloneToggleEl.disabled = !this.descriptionReady || !validName;
    }
    if (this.managedActionEl !== null) {
      this.managedActionEl.disabled = !managedAvailable;
    }
    if (this.folderActionEl !== null) {
      this.folderActionEl.disabled = !this.descriptionReady || !validName;
    }
  }

  private toggleCloneMenu(): void {
    if (this.cloneMenuEl?.hidden === false) {
      this.closeCloneMenu();
    } else {
      this.openCloneMenu();
    }
  }

  private openCloneMenu(): void {
    if (this.input?.value.trim() === "" || this.cloneMenuEl === null) {
      return;
    }
    this.cloneMenuEl.hidden = false;
    this.cloneToggleEl?.setAttribute("aria-expanded", "true");
  }

  private closeCloneMenu(): void {
    if (this.cloneMenuEl !== null) {
      this.cloneMenuEl.hidden = true;
    }
    this.cloneToggleEl?.setAttribute("aria-expanded", "false");
  }

  private updateSuggestions(): void {
    if (this.input === null || this.suggestionsEl === null) {
      return;
    }
    const query = this.input.value.trim().toLocaleLowerCase();
    if (query === "") {
      this.closeSuggestions();
      return;
    }
    this.filteredSuggestions = this.suggestions
      .filter((suggestion) => {
        const fullName = suggestion.fullName.toLocaleLowerCase();
        const repoName = fullName.slice(fullName.lastIndexOf("/") + 1);
        return fullName.includes(query) || repoName.includes(query);
      })
      .slice(0, 8);
    this.publicHintEl?.toggleAttribute(
      "hidden",
      this.filteredSuggestions.length > 0 || !isOwnerRepository(query),
    );
    this.activeSuggestion = this.filteredSuggestions.length > 0 ? 0 : -1;
    this.renderSuggestions();
  }

  private renderSuggestions(): void {
    if (this.input === null || this.suggestionsEl === null) {
      return;
    }
    this.suggestionsEl.replaceChildren();
    this.suggestionsEl.hidden = this.filteredSuggestions.length === 0;
    this.input.setAttribute("aria-expanded", String(this.filteredSuggestions.length > 0));
    this.input.removeAttribute("aria-activedescendant");
    for (const [index, suggestion] of this.filteredSuggestions.entries()) {
      const option = document.createElement("li");
      option.id = `${this.suggestionsEl.id}-option-${index}`;
      option.className = "repo-suggestion";
      option.setAttribute("role", "option");
      option.setAttribute("aria-selected", String(index === this.activeSuggestion));
      option.textContent = suggestion.fullName;
      option.addEventListener("mousedown", (event) => {
        event.preventDefault();
        this.chooseSuggestion(index);
      });
      this.suggestionsEl.append(option);
      if (index === this.activeSuggestion) {
        this.input.setAttribute("aria-activedescendant", option.id);
      }
    }
  }

  private onSuggestionKeydown(event: KeyboardEvent): void {
    if (event.key === "Escape") {
      this.closeSuggestions();
      return;
    }
    if (this.filteredSuggestions.length === 0) {
      return;
    }
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      const direction = event.key === "ArrowDown" ? 1 : -1;
      this.activeSuggestion =
        (this.activeSuggestion + direction + this.filteredSuggestions.length) %
        this.filteredSuggestions.length;
      this.renderSuggestions();
      return;
    }
    if (event.key === "Enter" && this.activeSuggestion >= 0) {
      event.preventDefault();
      this.chooseSuggestion(this.activeSuggestion);
    }
  }

  private chooseSuggestion(index: number): void {
    const suggestion = this.filteredSuggestions[index];
    if (suggestion === undefined || this.input === null) {
      return;
    }
    this.input.value = suggestion.fullName;
    this.localNameCustomized = false;
    this.syncSuggestedLocalName();
    this.closeSuggestions();
    this.scheduleDestination();
    this.scheduleDescription();
  }

  private syncSuggestedLocalName(): void {
    if (this.input === null || this.localNameInput === null) {
      return;
    }
    if (this.input.value.trim() === "") {
      this.localNameCustomized = false;
      this.localNameInput.value = "";
      return;
    }
    if (this.localNameCustomized) {
      return;
    }
    const normalized = normalizeGitHubRepository(this.input.value);
    this.localNameInput.value = normalized?.split("/")[1] ?? "";
  }

  private openExistingClone(path: string): void {
    const url = this.input?.value.trim();
    if (url) {
      this.callbacks.onOpenExistingClone(url, path);
    }
  }

  private closeSuggestions(): void {
    this.filteredSuggestions = [];
    this.activeSuggestion = -1;
    if (this.suggestionsEl !== null) {
      this.suggestionsEl.hidden = true;
      this.suggestionsEl.replaceChildren();
    }
    if (this.publicHintEl !== null) {
      this.publicHintEl.hidden = true;
    }
    this.input?.setAttribute("aria-expanded", "false");
    this.input?.removeAttribute("aria-activedescendant");
  }

  private render(): void {
    if (this.listEl === null || this.emptyEl === null) {
      return;
    }
    // Preserve focus on a remove button across the rebuild the host's re-emitted state triggers.
    const focusedId = this.focusedRemoveId();
    const hasRepos = this.repos.length > 0;
    this.emptyEl.hidden = hasRepos;
    this.listEl.hidden = !hasRepos;
    this.listEl.replaceChildren();
    if (!hasRepos) {
      return;
    }
    for (const repo of this.repos) {
      this.listEl.appendChild(this.buildRow(repo));
    }
    this.restoreFocus(focusedId);
  }

  private buildRow(repo: RegisteredRepo): HTMLLIElement {
    const li = document.createElement("li");
    li.className = "repo-row";
    li.classList.toggle(
      "is-highlighted",
      this.highlightedRepoId?.toLowerCase() === repo.id.toLowerCase(),
    );

    // Clicking a repo browses its remote tree. Copying it locally remains an explicit adjacent action.
    const open = document.createElement("button");
    open.type = "button";
    open.className = "repo-open";
    open.dataset.id = repo.id;
    if (this.highlightedRepoId?.toLowerCase() === repo.id.toLowerCase()) {
      open.setAttribute("aria-current", "true");
    }
    open.title = repo.url;
    open.setAttribute("aria-label", `View files in GitHub repository ${repo.name}`);
    const repoName = document.createElement("span");
    repoName.className = "repo-name";
    repoName.textContent = repo.name;
    const repoContext = document.createElement("span");
    repoContext.className = "repo-kind";
    repoContext.textContent = `GitHub · ${repo.clones.length} local ${repo.clones.length === 1 ? "copy" : "copies"}`;
    open.append(repoName, repoContext);
    open.addEventListener("click", () => this.callbacks.onBrowseRepo(repo));

    const remove = this.iconAction(
      "delete",
      `Remove repository ${repo.name} from SpecDesk`,
      () =>
        this.openDestructiveConfirmation(
          remove,
          `Remove ${repo.name} from SpecDesk?`,
          "This removes only its registration and favorites from SpecDesk. The GitHub repository and local copies stay untouched.",
          () => this.callbacks.onUnregister(repo.id),
        ),
      "repo-remove",
    );
    remove.dataset.id = repo.id;

    const copy = this.iconAction(
      "createCopy",
      `Create a new local copy of ${repo.name}`,
      () => this.prepareClone(repo),
      "repo-create-copy",
    );

    const favored = this.favorites.some(
      (item) =>
        item.kind === "repository" && item.repositoryId?.toLowerCase() === repo.id.toLowerCase(),
    );
    const star = document.createElement("button");
    star.type = "button";
    star.className = "workspace-star repo-star";
    star.dataset.id = `favorite:${repo.id}`;
    star.classList.toggle("is-favorite", favored);
    star.setAttribute("aria-label", `Favorite repository ${repo.name}`);
    star.setAttribute("aria-pressed", String(favored));
    star.title = favored ? "Remove from favorites" : "Add to favorites";
    star.innerHTML = icon("favorites");
    star.addEventListener("click", () => this.callbacks.onToggleFavorite?.(repo, !favored));

    const more = this.iconAction(
      "more",
      `More actions for repository ${repo.name}`,
      () => {
        this.openContextMenu(more, this.repositoryMenuItems(repo, favored));
      },
      "repo-more",
    );
    more.setAttribute("aria-haspopup", "menu");

    const header = document.createElement("div");
    header.className = "repo-row-header";
    header.append(open, copy, star, remove, more);
    this.bindContextMenu(header, open, () => this.repositoryMenuItems(repo, favored));
    li.append(header);

    if (repo.clones.length > 0) {
      const clones = document.createElement("ul");
      clones.className = "repo-clones";
      for (const clone of repo.clones) {
        const cloneRow = document.createElement("li");
        cloneRow.className = "repo-clone";
        const cloneButton = document.createElement("button");
        cloneButton.type = "button";
        cloneButton.className = "repo-clone-open";
        const cloneName = document.createElement("span");
        cloneName.className = "repo-clone-name";
        cloneName.textContent = clone.id;
        cloneButton.append(cloneName);
        if (clone.currentBranch !== null) {
          const cloneCurrent = document.createElement("span");
          cloneCurrent.className = "repo-clone-current";
          cloneCurrent.textContent = clone.currentBranch;
          cloneButton.append(cloneCurrent);
        }
        cloneButton.title = clone.path;
        cloneButton.setAttribute("aria-label", `Open local copy ${clone.id}`);
        cloneButton.addEventListener("click", () => this.callbacks.onOpenClone(repo, clone.path));
        const cloneFavored = this.favorites.some(
          (item) =>
            item.kind === "clone" &&
            item.repositoryId?.toLowerCase() === repo.id.toLowerCase() &&
            sameLocalPath(item.path, clone.path),
        );
        const cloneStar = this.favoriteButton(
          `local copy ${clone.id}`,
          `clone:${clone.path}`,
          cloneFavored,
          () => this.callbacks.onToggleCloneFavorite?.(repo, clone.path, !cloneFavored),
        );
        const cloneDelete = this.deleteButton(
          `local copy ${clone.id}`,
          this.operationKey("deleteClone", repo.id, clone.path, null),
          () => this.callbacks.onDeleteClone(repo, clone.path),
        );
        const createBranch = this.iconAction(
          "createBranch",
          `Create a new working line in ${clone.id}`,
          () =>
            this.promptForName(
              "New working line",
              "branch",
              "",
              (branch) => this.callbacks.onCreateBranch(repo, clone.path, branch),
              createBranch,
            ),
          "repo-create-branch",
        );
        const cloneMore = this.iconAction(
          "more",
          `More actions for local copy ${clone.id}`,
          () => {
            this.openContextMenu(cloneMore, this.cloneMenuItems(repo, clone, cloneFavored));
          },
          "repo-more",
        );
        cloneMore.setAttribute("aria-haspopup", "menu");
        const cloneHeader = document.createElement("div");
        cloneHeader.className = "repo-clone-header";
        cloneHeader.append(cloneButton, createBranch, cloneStar, cloneDelete, cloneMore);
        this.bindContextMenu(cloneHeader, cloneButton, () =>
          this.cloneMenuItems(repo, clone, cloneFavored),
        );
        cloneRow.append(cloneHeader, this.statusSummary(clone.status, `Local copy ${clone.id}`));

        if (clone.branches.length > 0) {
          const branches = document.createElement("ul");
          branches.className = "repo-branches";
          const sortedBranches = [...clone.branches].sort((left, right) => {
            const leftDefault = left.name.toLowerCase() === repo.defaultBranch.toLowerCase();
            const rightDefault = right.name.toLowerCase() === repo.defaultBranch.toLowerCase();
            if (leftDefault !== rightDefault) return leftDefault ? -1 : 1;
            return left.name.localeCompare(right.name, undefined, { sensitivity: "base" });
          });
          for (const branch of sortedBranches) {
            const branchRow = document.createElement("li");
            branchRow.className = "repo-branch";
            branchRow.classList.toggle("is-current", clone.currentBranch === branch.name);
            const branchButton = document.createElement("button");
            branchButton.type = "button";
            branchButton.className = "repo-branch-open";
            branchButton.textContent = branch.name;
            if (clone.currentBranch === branch.name) {
              branchButton.setAttribute("aria-current", "true");
            }
            branchButton.setAttribute(
              "aria-label",
              `Switch ${clone.id} to ${branch.name} and open its files`,
            );
            branchButton.addEventListener("click", () =>
              this.callbacks.onSwitchBranch(repo, clone.path, branch.name),
            );
            const branchFavored = this.favorites.some(
              (item) =>
                item.kind === "branch" &&
                item.repositoryId?.toLowerCase() === repo.id.toLowerCase() &&
                item.branch === branch.name &&
                sameLocalPath(item.path, clone.path),
            );
            const branchStar = this.favoriteButton(
              `branch ${branch.name} in ${clone.id}`,
              `branch:${clone.path}:${branch.name}`,
              branchFavored,
              () =>
                this.callbacks.onToggleBranchFavorite?.(
                  repo,
                  clone.path,
                  branch.name,
                  !branchFavored,
                ),
            );
            const branchHeader = document.createElement("div");
            branchHeader.className = "repo-branch-header";
            branchHeader.append(branchButton, branchStar);
            if (branch.canDelete) {
              branchHeader.append(
                this.deleteButton(
                  `branch ${branch.name} in ${clone.id}`,
                  this.operationKey("deleteBranch", repo.id, clone.path, branch.name),
                  () => this.callbacks.onDeleteBranch(repo, clone.path, branch.name),
                ),
              );
            }
            const branchMore = this.iconAction(
              "more",
              `More actions for working line ${branch.name}`,
              () =>
                this.openContextMenu(
                  branchMore,
                  this.branchMenuItems(repo, clone, branch, branchFavored),
                ),
              "repo-more",
            );
            branchMore.setAttribute("aria-haspopup", "menu");
            branchHeader.append(branchMore);
            this.bindContextMenu(branchHeader, branchButton, () =>
              this.branchMenuItems(repo, clone, branch, branchFavored),
            );
            branchRow.append(
              branchHeader,
              this.statusSummary(branch.status, `Working line ${branch.name}`),
            );
            branches.append(branchRow);
          }
          cloneRow.append(branches);
        }
        clones.append(cloneRow);
      }
      li.append(clones);
    }
    return li;
  }

  private prepareClone(repo: RegisteredRepo): void {
    if (this.input === null) {
      return;
    }
    this.input.value = repo.name;
    this.localNameCustomized = false;
    this.syncSuggestedLocalName();
    this.updateSuggestions();
    this.scheduleDestination();
    this.scheduleDescription();
    this.input.focus();
    this.input.select();
  }

  private iconAction(
    iconName: string,
    label: string,
    run: () => void,
    className: string,
  ): HTMLButtonElement {
    const button = document.createElement("button");
    button.type = "button";
    button.className = `repo-inline-action ${className}`;
    button.setAttribute("aria-label", label);
    button.title = label;
    button.innerHTML = icon(iconName);
    button.addEventListener("click", (event) => {
      event.stopPropagation();
      run();
    });
    return button;
  }

  private repositoryMenuItems(repo: RegisteredRepo, favored: boolean): RepositoryMenuItem[] {
    return [
      { label: "View repository files", run: () => this.callbacks.onBrowseRepo(repo) },
      { label: "Create local copy…", run: () => this.prepareClone(repo) },
      {
        label: favored ? "Remove from favorites" : "Add to favorites",
        run: () => this.callbacks.onToggleFavorite?.(repo, !favored),
      },
      {
        label: "Remove from SpecDesk",
        danger: true,
        destructiveDescription:
          "This removes only its registration and favorites from SpecDesk. The GitHub repository and local copies stay untouched.",
        run: () => this.callbacks.onUnregister(repo.id),
      },
    ];
  }

  private cloneMenuItems(
    repo: RegisteredRepo,
    clone: RegisteredClone,
    favored: boolean,
  ): RepositoryMenuItem[] {
    return [
      { label: "Open local copy", run: () => this.callbacks.onOpenClone(repo, clone.path) },
      {
        label: "Create working line…",
        run: () =>
          this.promptForName(
            "New working line",
            "branch",
            "",
            (name) => this.callbacks.onCreateBranch(repo, clone.path, name),
            this.contextMenuReturnFocus,
          ),
      },
      {
        label: "Rename local copy…",
        run: () =>
          this.promptForName(
            "Rename local copy",
            "clone",
            clone.id,
            (name) => this.callbacks.onRenameClone(repo, clone.path, name),
            this.contextMenuReturnFocus,
          ),
      },
      {
        label: favored ? "Remove from favorites" : "Add to favorites",
        run: () => this.callbacks.onToggleCloneFavorite?.(repo, clone.path, !favored),
      },
      {
        label: "Delete local copy…",
        danger: true,
        destructiveDescription:
          "The local folder will be deleted from this computer. SpecDesk will inspect unfinished edits, unshared versions, and protected snapshots before proceeding.",
        run: () => this.callbacks.onDeleteClone(repo, clone.path),
      },
    ];
  }

  private branchMenuItems(
    repo: RegisteredRepo,
    clone: RegisteredClone,
    branch: RegisteredBranch,
    favored: boolean,
  ): RepositoryMenuItem[] {
    const items: RepositoryMenuItem[] = [
      {
        label: clone.currentBranch === branch.name ? "Open working line" : "Switch and open",
        run: () => this.callbacks.onSwitchBranch(repo, clone.path, branch.name),
      },
    ];
    if (branch.canRename) {
      items.push({
        label: "Rename working line…",
        run: () =>
          this.promptForName(
            "Rename working line",
            "branch",
            branch.name,
            (name) => this.callbacks.onRenameBranch(repo, clone.path, branch.name, name),
            this.contextMenuReturnFocus,
          ),
      });
    }
    items.push({
      label: favored ? "Remove from favorites" : "Add to favorites",
      run: () => this.callbacks.onToggleBranchFavorite?.(repo, clone.path, branch.name, !favored),
    });
    if (branch.canDelete) {
      items.push({
        label: "Delete local working line…",
        danger: true,
        destructiveDescription:
          "This working line will be deleted locally. SpecDesk will inspect unfinished edits, unshared versions, and protected snapshots before proceeding.",
        run: () => this.callbacks.onDeleteBranch(repo, clone.path, branch.name),
      });
    }
    return items;
  }

  private bindContextMenu(
    row: HTMLElement,
    focusTarget: HTMLElement,
    items: () => RepositoryMenuItem[],
  ): void {
    row.addEventListener("contextmenu", (event) => {
      event.preventDefault();
      this.openContextMenu(focusTarget, items(), event.clientX, event.clientY);
    });
    focusTarget.addEventListener("keydown", (event) => {
      if (event.key === "ContextMenu" || (event.shiftKey && event.key === "F10")) {
        event.preventDefault();
        this.openContextMenu(focusTarget, items());
      }
    });
  }

  private openContextMenu(
    returnFocus: HTMLElement,
    items: readonly RepositoryMenuItem[],
    clientX?: number,
    clientY?: number,
  ): void {
    const menu = this.contextMenuEl;
    if (menu === null || items.length === 0) return;
    this.destructiveConfirmation.close(false);
    this.contextMenuReturnFocus = returnFocus;
    menu.replaceChildren(
      ...items.map((item) => {
        const button = document.createElement("button");
        button.type = "button";
        button.setAttribute("role", "menuitem");
        button.textContent = item.label;
        button.classList.toggle("is-danger", item.danger === true);
        button.setAttribute("aria-expanded", "false");
        button.addEventListener("click", (event) => {
          event.stopPropagation();
          if (item.danger) {
            this.destructiveConfirmation.open({
              trigger: button,
              anchor: button,
              title: item.label.replace(/…$/, "?"),
              description:
                item.destructiveDescription ?? "This action permanently deletes local data.",
              focusAfterConfirm: () => this.input,
              onConfirm: () => {
                this.closeContextMenu(false);
                item.run();
              },
            });
            return;
          }
          this.closeContextMenu(false);
          item.run();
        });
        return button;
      }),
    );
    menu.hidden = false;
    const anchor = returnFocus.getBoundingClientRect();
    menu.style.left = `${clientX ?? anchor.left}px`;
    menu.style.top = `${clientY ?? anchor.bottom}px`;
    const bounds = menu.getBoundingClientRect();
    menu.style.left = `${Math.max(8, Math.min(Number.parseFloat(menu.style.left), innerWidth - bounds.width - 8))}px`;
    menu.style.top = `${Math.max(8, Math.min(Number.parseFloat(menu.style.top), innerHeight - bounds.height - 8))}px`;
    menu.querySelector<HTMLButtonElement>('[role="menuitem"]')?.focus();
    menu.onkeydown = (event) => {
      const buttons = [...menu.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')];
      const index = buttons.indexOf(document.activeElement as HTMLButtonElement);
      if (event.key === "Escape") {
        event.preventDefault();
        this.closeContextMenu();
      } else if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        event.preventDefault();
        const direction = event.key === "ArrowDown" ? 1 : -1;
        buttons[(index + direction + buttons.length) % buttons.length]?.focus();
      } else if (event.key === "Home") {
        event.preventDefault();
        buttons[0]?.focus();
      } else if (event.key === "End") {
        event.preventDefault();
        buttons.at(-1)?.focus();
      }
    };
  }

  private closeContextMenu(restoreFocus = true): void {
    this.destructiveConfirmation.close(false);
    if (this.contextMenuEl !== null) this.contextMenuEl.hidden = true;
    if (restoreFocus) this.contextMenuReturnFocus?.focus();
  }

  private promptForName(
    title: string,
    kind: "clone" | "branch",
    initialValue: string,
    run: (value: string) => void,
    returnFocus: HTMLElement | null,
  ): void {
    if (this.nameDialogEl === null || this.nameDialogInputEl === null) return;
    this.closeContextMenu(false);
    this.contextMenuReturnFocus = returnFocus;
    this.pendingNameAction = run;
    this.nameDialogEl.dataset.kind = kind;
    const heading = this.nameDialogEl.querySelector<HTMLElement>(".repo-name-dialog-title");
    if (heading !== null) heading.textContent = title;
    this.nameDialogInputEl.value = initialValue;
    if (this.nameDialogErrorEl !== null) this.nameDialogErrorEl.hidden = true;
    if (typeof this.nameDialogEl.showModal === "function") {
      this.nameDialogEl.showModal();
    } else {
      this.nameDialogEl.setAttribute("open", "");
    }
    this.nameDialogInputEl.focus();
    this.nameDialogInputEl.select();
  }

  private closeNameDialog(): void {
    if (this.nameDialogEl === null) return;
    if (typeof this.nameDialogEl.close === "function") {
      this.nameDialogEl.close();
    } else {
      this.nameDialogEl.removeAttribute("open");
      this.nameDialogEl.dispatchEvent(new Event("close"));
    }
  }

  private statusSummary(status: RepositoryStatusPayload, ownerLabel: string): HTMLElement {
    const summary = document.createElement("div");
    summary.className = "repo-health";
    summary.setAttribute("aria-label", `${ownerLabel} status`);
    if (status.ahead > 0) {
      summary.append(this.statusBadge(`${status.ahead} not shared`, "unshared"));
    }
    if (status.behind > 0) {
      summary.append(
        this.statusBadge(
          `${status.behind} ${status.behind === 1 ? "update" : "updates"} available`,
          "incoming",
        ),
      );
    }
    if (status.hasUncommitted) {
      summary.append(this.statusBadge("Unsaved changes", "unsaved"));
    }
    if (status.stashCount > 0) {
      summary.append(
        this.statusBadge(
          `${status.stashCount} ${status.stashCount === 1 ? "held change" : "held changes"}`,
          "held",
        ),
      );
    }
    if (status.hasConflicts) {
      summary.append(this.statusBadge("Conflict needs attention", "conflict"));
    }
    if (summary.childElementCount === 0) {
      summary.hidden = true;
    }
    return summary;
  }

  private statusBadge(
    label: string,
    kind: "unshared" | "incoming" | "unsaved" | "held" | "conflict",
  ): HTMLElement {
    const badge = document.createElement("span");
    badge.className = `repo-health-badge is-${kind}`;
    badge.textContent = label;
    return badge;
  }

  private favoriteButton(
    label: string,
    id: string,
    favored: boolean,
    onClick: () => void,
  ): HTMLButtonElement {
    const star = document.createElement("button");
    star.type = "button";
    star.className = "workspace-star repo-star";
    star.dataset.id = `favorite:${id}`;
    star.classList.toggle("is-favorite", favored);
    star.setAttribute("aria-label", `Favorite ${label}`);
    star.setAttribute("aria-pressed", String(favored));
    star.title = favored ? "Remove from favorites" : "Add to favorites";
    star.innerHTML = icon("favorites");
    star.addEventListener("click", onClick);
    return star;
  }

  private deleteButton(
    label: string,
    operationKey: string,
    onClick: () => void,
  ): HTMLButtonElement {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "repo-item-delete";
    button.textContent = "×";
    button.setAttribute("aria-label", `Delete ${label} locally`);
    button.title = "Delete locally";
    button.setAttribute("aria-expanded", "false");
    button.addEventListener("click", () => {
      this.openDestructiveConfirmation(
        button,
        `Delete ${label}?`,
        "SpecDesk will inspect unfinished edits, unshared versions, and protected snapshots before deleting local data.",
        () => {
          this.operationFocusTargets.set(operationKey, button);
          onClick();
        },
      );
    });
    return button;
  }

  private openDestructiveConfirmation(
    trigger: HTMLButtonElement,
    title: string,
    description: string,
    onConfirm: () => void,
  ): void {
    const anchor = trigger.parentElement ?? trigger;
    this.destructiveConfirmation.open({
      trigger,
      anchor,
      title,
      description,
      onConfirm,
      focusAfterConfirm: () => this.input,
    });
  }

  private operationKey(
    operation: RepoConfirmationPayload["operation"],
    id: string,
    clonePath: string,
    branch: string | null,
  ): string {
    return [
      operation,
      id.toLowerCase(),
      clonePath
        .replaceAll("/", "\\")
        .replace(/[\\]+$/, "")
        .toLowerCase(),
      branch ?? "",
    ].join(":");
  }

  private closeOperationConfirmation(restoreFocus = true): void {
    const operation = this.pendingOperation;
    if (operation !== null) {
      this.operationFocusTargets.delete(
        this.operationKey(operation.operation, operation.id, operation.clonePath, operation.branch),
      );
    }
    this.pendingOperation = null;
    if (this.operationConfirmationEl !== null) {
      this.operationConfirmationEl.hidden = true;
    }
    this.cloneFormEl?.toggleAttribute("inert", false);
    this.listEl?.toggleAttribute("inert", false);
    if (restoreFocus) {
      this.operationReturnFocus?.focus();
    }
    this.operationReturnFocus = null;
  }

  private confirmOperation(): void {
    const operation = this.pendingOperation;
    if (operation === null) {
      return;
    }
    const repo = this.repos.find(
      (candidate) => candidate.id.toLowerCase() === operation.id.toLowerCase(),
    );
    const fallback = this.listEl?.querySelector<HTMLButtonElement>(".repo-open");
    this.closeOperationConfirmation(false);
    fallback?.focus();
    if (repo === undefined) {
      return;
    }
    if (operation.operation === "deleteBranch" && operation.branch) {
      this.callbacks.onDeleteBranch(
        repo,
        operation.clonePath,
        operation.branch,
        operation.confirmationToken,
      );
    } else {
      this.callbacks.onDeleteClone(repo, operation.clonePath, operation.confirmationToken);
    }
  }

  /** The id of the focused remove button, or null if focus is elsewhere — captured before a rebuild. */
  private focusedRemoveId(): string | null {
    const active = document.activeElement;
    if (
      active instanceof HTMLElement &&
      this.listEl?.contains(active) &&
      active.dataset.id !== undefined
    ) {
      return active.dataset.id;
    }
    return null;
  }

  /** Re-focus the same repo's remove button after a rebuild; if that repo is gone (it was the one removed)
   *  but rows remain, fall back to the first row's remove so focus doesn't drop to <body>. No-op if focus
   *  wasn't in the list, or when the list emptied (render() returns before this — nothing left to focus). */
  private restoreFocus(id: string | null): void {
    if (id === null || this.listEl === null) {
      return;
    }
    const buttons = this.listEl.querySelectorAll<HTMLElement>("button[data-id]");
    for (const el of buttons) {
      if (el.dataset.id === id) {
        el.focus();
        return;
      }
    }
    buttons[0]?.focus();
  }
}

function isOwnerRepository(value: string): boolean {
  return /^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\/[a-z0-9._-]+$/i.test(value);
}

function isValidLocalName(value: string): boolean {
  if (value === "" || value.length > 80 || /[<>:"/\\|?*]/.test(value) || /[ .]$/.test(value)) {
    return false;
  }
  return !/^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\..*)?$/i.test(value);
}

function isValidBranchName(value: string): boolean {
  const forbidden = " ~^:?*[\\";
  return (
    value !== "" &&
    value.length <= 240 &&
    !(
      value.startsWith("-") ||
      value.startsWith(".") ||
      value.endsWith(".") ||
      value.endsWith("/") ||
      value.includes("..") ||
      value.includes("@{") ||
      [...value].some((character) => {
        const code = character.charCodeAt(0);
        return code < 32 || code === 127 || forbidden.includes(character);
      })
    )
  );
}

function sameLocalPath(left: string, right: string): boolean {
  const normalize = (path: string): string => path.replace(/[\\/]+$/, "").replaceAll("/", "\\");
  return normalize(left).toLocaleLowerCase() === normalize(right).toLocaleLowerCase();
}

function normalizeGitHubRepository(value: string): string | null {
  let spec = value.trim();
  const scpPrefix = "git@github.com:";
  if (spec.toLocaleLowerCase().startsWith(scpPrefix)) {
    spec = spec.slice(scpPrefix.length);
  } else {
    const url = /^(?:https?:\/\/)?(?:www\.)?github\.com\/(.+)$/i.exec(spec);
    if (url?.[1] !== undefined) {
      spec = url[1];
    }
  }
  spec = spec.replace(/^\/+|\/+$/g, "").replace(/\.git$/i, "");
  return isOwnerRepository(spec) ? spec.toLocaleLowerCase() : null;
}

function readSkipCloneConfirmation(): boolean {
  try {
    return window.localStorage.getItem(SKIP_CLONE_CONFIRMATION_KEY) === "true";
  } catch {
    return false;
  }
}

function writeSkipCloneConfirmation(): void {
  try {
    window.localStorage.setItem(SKIP_CLONE_CONFIRMATION_KEY, "true");
  } catch {
    // Storage can be disabled by policy; confirmation remains skipped only for this app session.
  }
}
