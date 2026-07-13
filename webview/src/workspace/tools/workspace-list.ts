/**
 * A left-rail list of workspace items (design concept §9) — the shared engine behind the Recent and
 * Favorites panels, which render identically (a list of the author's files/folders) and differ only in
 * WHICH list they show and how the trailing star reflects/toggles the favorite. Each row opens its item
 * (a folder → `folder.open`, a file → `doc.open`, resolved by the owner via {@link
 * WorkspaceListCallbacks.onOpen}) and carries a trailing star that adds/removes the favorite ({@link
 * WorkspaceListCallbacks.onToggleFavorite}).
 *
 * Like FileTree, this keeps NO IPC/Kinds knowledge: the integrator (index.ts) passes plain callbacks, so
 * the panel is unit-testable without a host bridge. The rows are a real `<ul>`/`<li>` with real `<button>`s
 * (discernible text + an aria-labelled, aria-pressed star), and keyboard focus is preserved across the
 * re-render a favorite toggle triggers (the host re-emits `workspace.state`, rebuilding the list).
 */

import type { WorkspaceItem, WorkspaceStatePayload } from "../../wire/protocol.js";
import { icon } from "../icons.js";
import type { PanelTool } from "../panel-tool.js";

export interface WorkspaceListCallbacks {
  /** Open the item (the owner maps a folder to `folder.open` and a file to `doc.open`). */
  onOpen(item: WorkspaceItem): void;
  /** Set whether the item at `item.path` is a favorite (`favorite` true adds it, false removes it). */
  onToggleFavorite(item: WorkspaceItem, favorite: boolean): void;
}

/** Per-panel configuration — the only thing that differs between the Recent and Favorites panels. */
export interface WorkspaceListConfig {
  readonly id: string;
  readonly label: string;
  readonly icon: string;
  /** The muted hint shown when the list is empty. */
  readonly emptyHint: string;
  /** This panel's items, picked out of the workspace state. */
  items(state: WorkspaceStatePayload): readonly WorkspaceItem[];
  /** Whether the trailing star shows as "on" (filled + aria-pressed) for `item` in this state. */
  isFavorite(item: WorkspaceItem, state: WorkspaceStatePayload): boolean;
}

/** The starting state before the host's first `workspace.state` arrives (renders the empty hint). */
const EMPTY_STATE: WorkspaceStatePayload = { recent: [], favorites: [], repositories: [] };

/** The focused row control captured before a rebuild: typed stable identity plus button (open / star). */
interface FocusRef {
  key: string;
  control: string;
}

export class WorkspaceListPanel implements PanelTool {
  readonly id: string;
  readonly label: string;
  readonly icon: string;

  private listEl: HTMLElement | null = null;
  private emptyEl: HTMLElement | null = null;
  private state: WorkspaceStatePayload = EMPTY_STATE;

  constructor(
    private readonly config: WorkspaceListConfig,
    private readonly callbacks: WorkspaceListCallbacks,
  ) {
    this.id = config.id;
    this.label = config.label;
    this.icon = config.icon;
  }

  mount(body: HTMLElement): void {
    const root = document.createElement("div");
    root.className = "workspace-list";

    const empty = document.createElement("p");
    empty.className = "workspace-list-empty";
    empty.textContent = this.config.emptyHint;

    const list = document.createElement("ul");
    list.className = "workspace-list-items";
    list.setAttribute("aria-label", this.config.label);

    root.append(empty, list);
    body.appendChild(root);
    this.emptyEl = empty;
    this.listEl = list;
    this.render();
  }

  /** Replace the list with the host's latest workspace state. */
  setState(state: WorkspaceStatePayload): void {
    this.state = state;
    this.render();
  }

  private render(): void {
    if (this.listEl === null || this.emptyEl === null) {
      return;
    }
    // Rebuilding drops whatever button had focus (to <body>); remember it so a keyboard user who just
    // toggled a star keeps their place after the host re-emits state and the list rebuilds.
    const focused = this.focusedControl();
    const items = this.config.items(this.state);
    const hasItems = items.length > 0;
    this.emptyEl.hidden = hasItems;
    this.listEl.hidden = !hasItems;
    this.listEl.replaceChildren();
    if (!hasItems) {
      return;
    }
    for (const item of items) {
      this.listEl.appendChild(this.buildRow(item));
    }
    this.restoreFocus(focused);
  }

  private buildRow(item: WorkspaceItem): HTMLLIElement {
    const li = document.createElement("li");
    li.className = "workspace-list-row";
    const itemKey = workspaceItemKey(item);
    const context = workspaceItemContext(item);

    // The open button: a leading folder/file affordance (decorative) beside the label; the full path is
    // the tooltip and the label is the accessible name. A folder opens the workspace, a file opens the doc.
    const open = document.createElement("button");
    open.type = "button";
    open.className = "workspace-open";
    open.dataset.path = item.path;
    open.dataset.itemKey = itemKey;
    open.dataset.control = "open";
    open.title = context.tooltip;
    open.setAttribute("aria-label", context.accessibleName);

    const affordance = document.createElement("span");
    affordance.className = "workspace-item-icon";
    affordance.dataset.kind = item.kind ?? "local";
    affordance.setAttribute("aria-hidden", "true");
    // Trusted in-repo markup (workspace/icons.ts) — never interpolated with host input.
    affordance.innerHTML = icon(
      item.kind === "repository" ? "repositories" : item.isFolder ? "files" : "file",
    );

    const text = document.createElement("span");
    text.className = "workspace-item-text";
    const label = document.createElement("span");
    label.className = "workspace-item-label";
    label.textContent = item.label;

    text.append(label);
    if (context.secondary !== null) {
      const secondary = document.createElement("span");
      secondary.className = "workspace-item-context";
      secondary.textContent = context.secondary;
      text.append(secondary);
    }

    open.append(affordance, text);
    open.addEventListener("click", () => this.callbacks.onOpen(item));

    // The trailing star: pressed (filled) when the item is a favorite, and its click flips that — for the
    // Recent panel this adds/removes; for Favorites (every row a favorite) it always removes.
    const favored = this.config.isFavorite(item, this.state);
    const star = document.createElement("button");
    star.type = "button";
    star.className = "workspace-star";
    star.classList.toggle("is-favorite", favored);
    star.dataset.path = item.path;
    star.dataset.itemKey = itemKey;
    star.dataset.control = "star";
    star.setAttribute("aria-pressed", String(favored));
    // A toggle button keeps a STABLE accessible name and lets aria-pressed carry the on/off state (a name
    // that flips to "Remove from favorites" while also pressed reads contradictorily to a screen reader);
    // the mouse tooltip still shows the concrete action.
    star.setAttribute("aria-label", `Favorite ${context.accessibleName}`);
    star.title = favored ? "Remove from favorites" : "Add to favorites";
    star.innerHTML = icon("favorites");
    star.addEventListener("click", () => this.callbacks.onToggleFavorite(item, !favored));

    li.append(open, star);
    return li;
  }

  /** The path + control of the focused row button, or null if focus is elsewhere — captured pre-rebuild. */
  private focusedControl(): FocusRef | null {
    const active = document.activeElement;
    if (
      active instanceof HTMLElement &&
      this.listEl?.contains(active) &&
      active.dataset.itemKey !== undefined &&
      active.dataset.control !== undefined
    ) {
      return { key: active.dataset.itemKey, control: active.dataset.control };
    }
    return null;
  }

  /** Re-focus the same row's same control after a rebuild; if that control is gone (its row was removed,
   *  e.g. un-favoriting from the Favorites panel) but rows remain, fall back to the first row so focus
   *  doesn't drop to <body>. No-op when focus wasn't in the list to begin with, or when the list emptied
   *  (render() returns before this — there is nothing left to focus). */
  private restoreFocus(ref: FocusRef | null): void {
    if (ref === null || this.listEl === null) {
      return;
    }
    const buttons = this.listEl.querySelectorAll<HTMLElement>("button[data-item-key]");
    for (const el of buttons) {
      if (el.dataset.itemKey === ref.key && el.dataset.control === ref.control) {
        el.focus();
        return;
      }
    }
    buttons[0]?.focus();
  }
}

function workspaceItemKey(item: WorkspaceItem): string {
  const kind = item.kind ?? "local";
  if (kind === "remote") {
    return `remote:${item.repositoryId?.toLowerCase() ?? ""}:${item.branch ?? ""}:${item.path}`;
  }
  if (kind === "repository") {
    return `repository:${(item.repositoryId ?? item.path).toLowerCase()}`;
  }
  return `local:${item.path.toLowerCase()}`;
}

function workspaceItemContext(item: WorkspaceItem): {
  tooltip: string;
  accessibleName: string;
  secondary: string | null;
} {
  if (item.kind === "remote") {
    const repository = item.repositoryId ?? "Unknown repository";
    const branch = item.branch ?? "Unknown version";
    return {
      tooltip: `${repository} · ${branch} · ${item.path}`,
      accessibleName: `${item.label}, ${item.isFolder ? "folder" : "file"} in ${repository}, version ${branch}, path ${item.path}`,
      secondary: `${repository} · ${branch}`,
    };
  }
  if (item.kind === "repository") {
    const repository = item.repositoryId ?? item.path;
    return {
      tooltip: `Repository ${repository}`,
      accessibleName: `Repository ${repository}`,
      secondary: "Repository",
    };
  }
  return {
    tooltip: item.path,
    accessibleName: `${item.label}, ${item.isFolder ? "folder" : "file"}, ${item.path}`,
    secondary: null,
  };
}

/** Case-insensitive path identity — the same file/folder can reach the store under different casing on
 *  Windows, and the host de-duplicates that way, so the favorite membership check must too. */
function samePath(a: string, b: string): boolean {
  return a.toLowerCase() === b.toLowerCase();
}

/** The left-rail Recent panel: the author's recently-opened files/folders, each star reflecting whether
 *  the item is also a favorite (so a starred recent shows filled). */
export function recentPanel(callbacks: WorkspaceListCallbacks): WorkspaceListPanel {
  return new WorkspaceListPanel(
    {
      id: "recent",
      label: "Recent",
      icon: icon("recent"),
      emptyHint: "Files and folders you open will appear here.",
      items: (state) => state.recent,
      isFavorite: (item, state) => state.favorites.some((fav) => samePath(fav.path, item.path)),
    },
    callbacks,
  );
}

/** The left-rail Favorites panel: the items the author starred. Every row is a favorite, so the star is
 *  always filled and its click removes it. */
export function favoritesPanel(callbacks: WorkspaceListCallbacks): WorkspaceListPanel {
  return new WorkspaceListPanel(
    {
      id: "favorites",
      label: "Favorites",
      icon: icon("favorites"),
      emptyHint: "Star a repository, file, or folder to keep it here.",
      items: (state) => state.favorites,
      isFavorite: () => true,
    },
    callbacks,
  );
}
