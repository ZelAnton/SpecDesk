/**
 * The left-rail Repositories panel (design concept §9): the GitHub repositories the author registered, so
 * they're at hand. A small form at the top registers a new one from an `owner/name` or a GitHub URL — the
 * host validates and stores it, or emits an `error` the app already surfaces; each listed repo can be
 * opened or removed.
 *
 * A6: clicking a repo opens it as the workspace — the host clones it into a managed folder (if not already
 * local) and opens that folder (via {@link RepositoriesCallbacks.onOpenRepo}). Like FileTree, this keeps NO
 * IPC/Kinds knowledge — the integrator (index.ts) passes plain callbacks, so the panel is unit-testable
 * without a host bridge. The author never sees git vocabulary: it's "Repositories", "Add", "Open", and
 * "Remove", not clone/branch/remote.
 */

import type {
  GitHubRepositoryOptionPayload,
  RegisteredRepo,
  WorkspaceItem,
  WorkspaceStatePayload,
} from "../../wire/protocol.js";
import { icon } from "../icons.js";
import type { PanelTool } from "../panel-tool.js";

export interface RepositoriesCallbacks {
  /** Register the repository named by `url` (an `owner/name` or a GitHub URL); the host validates it. */
  onRegister(url: string): void;
  /** Remove the registered repository whose id is `id`. */
  onUnregister(id: string): void;
  /** Open the repository as the workspace — the host clones it into a managed folder (if needed) and opens it. */
  onBrowseRepo(repo: RegisteredRepo): void;
  onOpenClone(repo: RegisteredRepo, clonePath: string): void;
  onClone(repo: RegisteredRepo): void;
  onToggleFavorite?(repo: RegisteredRepo, favorite: boolean): void;
}

let suggestionListSequence = 0;

export class RepositoriesPanel implements PanelTool {
  readonly id = "repositories";
  readonly label = "Repositories";
  readonly icon = icon("repositories");

  private input: HTMLInputElement | null = null;
  private suggestionsEl: HTMLUListElement | null = null;
  private listEl: HTMLElement | null = null;
  private emptyEl: HTMLElement | null = null;
  private repos: readonly RegisteredRepo[] = [];
  private favorites: readonly WorkspaceItem[] = [];
  private suggestions: readonly GitHubRepositoryOptionPayload[] = [];
  private filteredSuggestions: readonly GitHubRepositoryOptionPayload[] = [];
  private activeSuggestion = -1;

  constructor(private readonly callbacks: RepositoriesCallbacks) {}

  mount(body: HTMLElement): void {
    const root = document.createElement("div");
    root.className = "repositories";

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
    input.addEventListener("input", () => this.updateSuggestions());
    input.addEventListener("keydown", (event) => this.onSuggestionKeydown(event));
    input.addEventListener("blur", () => this.closeSuggestions());

    const add = document.createElement("button");
    add.type = "submit";
    add.className = "repo-register-add";
    add.textContent = "Add";

    form.append(input, add, suggestions);
    form.addEventListener("submit", (event) => {
      event.preventDefault();
      this.submit();
    });

    const empty = document.createElement("p");
    empty.className = "repo-empty";
    empty.textContent = "Register a repository to keep it handy.";

    const list = document.createElement("ul");
    list.className = "repo-list";
    list.setAttribute("aria-label", "Registered repositories");

    root.append(form, empty, list);
    body.appendChild(root);
    this.input = input;
    this.suggestionsEl = suggestions;
    this.emptyEl = empty;
    this.listEl = list;
    this.render();
  }

  /** Replace the repository list with the host's latest workspace state. */
  setState(state: WorkspaceStatePayload): void {
    this.repos = state.repositories;
    this.favorites = state.favorites;
    this.render();
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

  private submit(): void {
    if (this.input === null) {
      return;
    }
    const url = this.input.value.trim();
    if (url === "") {
      return;
    }
    // Clear immediately: the host validates and either adds it (a `workspace.state` follows and rebuilds
    // the list) or emits an `error` the app surfaces — either way the field is ready for the next entry.
    this.input.value = "";
    this.closeSuggestions();
    this.callbacks.onRegister(url);
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
    this.closeSuggestions();
  }

  private closeSuggestions(): void {
    this.filteredSuggestions = [];
    this.activeSuggestion = -1;
    if (this.suggestionsEl !== null) {
      this.suggestionsEl.hidden = true;
      this.suggestionsEl.replaceChildren();
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

    // Clicking a repo opens it as the workspace — the host clones it into a managed folder (if it isn't
    // already local) and opens that folder.
    const open = document.createElement("button");
    open.type = "button";
    open.className = "repo-open";
    open.title = repo.url;
    open.textContent = repo.name;
    open.addEventListener("click", () => this.callbacks.onBrowseRepo(repo));

    // The trailing remove control (an ×, like the dock's collapse button); the aria-label carries the
    // accessible name so a screen reader announces which repository it removes.
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "repo-remove";
    remove.dataset.id = repo.id;
    remove.textContent = "×";
    remove.setAttribute("aria-label", `Remove repository ${repo.name}`);
    remove.title = "Remove repository";
    remove.addEventListener("click", () => this.callbacks.onUnregister(repo.id));

    const copy = document.createElement("button");
    copy.type = "button";
    copy.className = "repo-clone-action";
    copy.textContent = "Copy locally";
    copy.setAttribute("aria-label", `Copy repository ${repo.name} locally`);
    copy.addEventListener("click", () => this.callbacks.onClone(repo));

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

    const header = document.createElement("div");
    header.className = "repo-row-header";
    header.append(open, copy, star, remove);
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
        cloneButton.textContent = clone.id;
        cloneButton.title = clone.path;
        cloneButton.addEventListener("click", () => this.callbacks.onOpenClone(repo, clone.path));
        cloneRow.append(cloneButton);

        if (clone.branches.length > 0) {
          const branches = document.createElement("ul");
          branches.className = "repo-branches";
          for (const branch of clone.branches) {
            const branchRow = document.createElement("li");
            branchRow.textContent = branch;
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
