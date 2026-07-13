/**
 * The left-rail file navigator (design concept §9): the open workspace folder's Markdown tree. Folders
 * expand/collapse; clicking a file opens it (via the owner's onOpenFile → `doc.open {path}`). The tree data
 * comes from the host as a `tree` event ({@link FileTree.setTree}); an empty state offers to open a folder.
 *
 * The tree is rendered as nested `<ul>`/`<li>` with folders as `aria-expanded` toggle buttons and files as
 * plain buttons, so the hierarchy and expand state are programmatic (a screen reader announces both).
 */

import type { TreeNode, TreePayload, WorkspaceItem } from "../../wire/protocol.js";
import { icon } from "../icons.js";
import type { PanelTool } from "../panel-tool.js";

export interface FileTreeCallbacks {
  /** Open the file at `path` (the owner maps this to `doc.open {path}`). */
  onOpenFile(path: string): void;
  /** Open a folder as the workspace (the empty-state action; maps to `folder.open`). */
  onOpenFolder(): void;
  onToggleFavorite?(item: WorkspaceItem, favorite: boolean): void;
}

export class FileTree implements PanelTool {
  readonly id = "files";
  readonly label = "Files";
  readonly icon = icon("files");

  private headerEl: HTMLElement | null = null;
  private listEl: HTMLElement | null = null;
  private emptyEl: HTMLElement | null = null;
  private tree: TreePayload | null = null;
  // Folder paths the author collapsed — folders default to expanded, so only the exceptions are tracked
  // (and they survive a re-render from a fresh `tree` event, since the paths are stable). Pruned to the
  // current tree on each setTree so it can't accumulate paths from folders/workspaces no longer shown.
  private readonly collapsed = new Set<string>();
  // The open document's path, highlighted in the tree so the author keeps their place (like the navigator
  // highlighting the active view). null when nothing is open or the open file isn't in this tree.
  private activeFile: string | null = null;
  private favorites: readonly WorkspaceItem[] = [];
  // Per-render counter for the folder→child-list `aria-controls` ids (reset at the top of each render).
  private branchSeq = 0;

  constructor(private readonly callbacks: FileTreeCallbacks) {}

  mount(body: HTMLElement): void {
    const root = document.createElement("div");
    root.className = "file-tree";

    const header = document.createElement("p");
    header.className = "file-tree-root";

    const empty = document.createElement("div");
    empty.className = "file-tree-empty";
    const hint = document.createElement("p");
    hint.className = "file-tree-empty-hint";
    hint.textContent = "Open a folder to browse its specs.";
    const openFolder = document.createElement("button");
    openFolder.type = "button";
    openFolder.className = "file-tree-open-folder";
    openFolder.textContent = "Open a folder";
    openFolder.addEventListener("click", () => this.callbacks.onOpenFolder());
    empty.append(hint, openFolder);

    const list = document.createElement("nav");
    list.className = "file-tree-list";
    list.setAttribute("aria-label", "Workspace files");

    root.append(header, empty, list);
    body.appendChild(root);
    this.headerEl = header;
    this.emptyEl = empty;
    this.listEl = list;
    this.render();
  }

  /** Replace the tree with the host's latest `tree` payload. */
  setTree(tree: TreePayload): void {
    this.tree = tree;
    // Drop collapse state for folders no longer in the tree, so the set stays bounded to what's shown.
    const present = new Set<string>();
    collectFolderPaths(tree.nodes, present);
    for (const path of [...this.collapsed]) {
      if (!present.has(path)) {
        this.collapsed.delete(path);
      }
    }
    this.render();
  }

  /** Mark `path` as the currently-open document (highlighted in the tree); null clears the highlight. */
  setActiveFile(path: string | null): void {
    if (path === this.activeFile) {
      return;
    }
    this.activeFile = path;
    // Reveal the active file: expand any collapsed ANCESTOR folder, so opening a file inside a folder the
    // author had collapsed doesn't hide the very highlight meant to keep their place.
    if (path !== null) {
      for (const folder of [...this.collapsed]) {
        if (path === folder || path.startsWith(`${folder}/`) || path.startsWith(`${folder}\\`)) {
          this.collapsed.delete(folder);
        }
      }
    }
    this.render();
  }

  setFavorites(favorites: readonly WorkspaceItem[]): void {
    this.favorites = favorites;
    this.render();
  }

  private render(): void {
    if (this.headerEl === null || this.listEl === null || this.emptyEl === null) {
      return;
    }
    // Rebuilding the tree removes whatever button had focus (dropping it to <body>); remember the focused
    // item so it can be restored after the rebuild — a keyboard user opening files keeps their place.
    const focusedPath = this.focusedItemPath();
    this.branchSeq = 0;
    // A tree with a non-empty root but zero nodes is a real (opened-but-Markdown-less) folder; a null tree
    // (nothing opened yet) is the true empty state that offers the open-folder action.
    const tree = this.tree;
    const hasFolder = tree !== null && tree.root.length > 0;
    this.emptyEl.hidden = hasFolder;
    this.headerEl.hidden = !hasFolder;
    this.listEl.hidden = !hasFolder;

    this.headerEl.textContent = tree !== null && hasFolder ? folderName(tree.root) : "";
    this.headerEl.title = tree !== null && hasFolder ? tree.root : "";

    this.listEl.replaceChildren();
    if (tree === null || tree.nodes.length === 0) {
      if (hasFolder) {
        // An opened folder that holds no Markdown — say so rather than showing a blank pane.
        const none = document.createElement("p");
        none.className = "file-tree-none";
        none.textContent = "No specs in this folder.";
        this.listEl.appendChild(none);
      }
      return;
    }
    this.listEl.appendChild(this.buildList(tree.nodes));
    this.restoreFocus(focusedPath);
  }

  /** The `path` of the focused tree item, or null if focus is elsewhere — captured before a rebuild. */
  private focusedItemPath(): string | null {
    const active = document.activeElement;
    if (
      active instanceof HTMLElement &&
      this.listEl?.contains(active) &&
      (active.classList.contains("file-tree-item") || active.classList.contains("file-tree-star"))
    ) {
      return active.dataset.path ?? null;
    }
    return null;
  }

  /** Re-focus the item at `path` after a rebuild (no-op if focus wasn't in the tree or the item is gone). */
  private restoreFocus(path: string | null): void {
    if (path === null || this.listEl === null) {
      return;
    }
    for (const el of this.listEl.querySelectorAll<HTMLElement>(
      ".file-tree-item, .file-tree-star",
    )) {
      if (el.dataset.path === path) {
        el.focus();
        return;
      }
    }
  }

  private buildList(nodes: readonly TreeNode[]): HTMLUListElement {
    const ul = document.createElement("ul");
    ul.className = "file-tree-branch";
    for (const node of nodes) {
      ul.appendChild(node.isDirectory ? this.buildFolder(node) : this.buildFile(node));
    }
    return ul;
  }

  private buildFolder(node: TreeNode): HTMLLIElement {
    const li = document.createElement("li");
    const expanded = !this.collapsed.has(node.path);
    const row = document.createElement("div");
    row.className = "file-tree-row";

    const childList = this.buildList(node.children);
    const listId = `file-tree-branch-${this.branchSeq++}`;
    childList.id = listId;
    childList.hidden = !expanded;

    const toggle = document.createElement("button");
    toggle.type = "button";
    toggle.className = "file-tree-item file-tree-folder";
    toggle.dataset.path = node.path;
    toggle.setAttribute("aria-expanded", String(expanded));
    toggle.setAttribute("aria-controls", listId);
    toggle.textContent = node.name;
    toggle.title = node.name;
    if (node.path === this.activeFile) {
      toggle.classList.add("is-current");
      toggle.setAttribute("aria-current", "true");
    }

    toggle.addEventListener("click", () => {
      // Collapsed ≡ present in the set; toggle membership and derive the new expanded state from it.
      const willExpand = this.collapsed.has(node.path);
      if (willExpand) {
        this.collapsed.delete(node.path);
      } else {
        this.collapsed.add(node.path);
      }
      toggle.setAttribute("aria-expanded", String(willExpand));
      childList.hidden = !willExpand;
    });

    row.append(toggle, this.favoriteButton(node));
    li.append(row, childList);
    return li;
  }

  private buildFile(node: TreeNode): HTMLLIElement {
    const li = document.createElement("li");
    const row = document.createElement("div");
    row.className = "file-tree-row";
    const button = document.createElement("button");
    button.type = "button";
    button.className = "file-tree-item file-tree-file";
    button.dataset.path = node.path;
    button.textContent = node.name;
    button.title = node.name;
    if (node.path === this.activeFile) {
      button.classList.add("is-current");
      button.setAttribute("aria-current", "true");
    }
    button.addEventListener("click", () => this.callbacks.onOpenFile(node.path));
    row.append(button, this.favoriteButton(node));
    li.append(row);
    return li;
  }

  private favoriteButton(node: TreeNode): HTMLButtonElement {
    const item = workspaceItemForNode(node);
    const favored = this.favorites.some((favorite) => sameWorkspaceItem(favorite, item));
    const star = document.createElement("button");
    star.type = "button";
    star.className = "workspace-star file-tree-star";
    star.dataset.path = `favorite:${node.path}`;
    star.classList.toggle("is-favorite", favored);
    star.setAttribute(
      "aria-label",
      `Favorite ${node.isDirectory ? "folder" : "file"} ${node.name}`,
    );
    star.setAttribute("aria-pressed", String(favored));
    star.title = favored ? "Remove from favorites" : "Add to favorites";
    star.innerHTML = icon("favorites");
    star.addEventListener("click", () => this.callbacks.onToggleFavorite?.(item, !favored));
    return star;
  }
}

function workspaceItemForNode(node: TreeNode): WorkspaceItem {
  const remote = parseRemotePath(node.path);
  return remote
    ? {
        path: remote.path,
        label: node.name,
        isFolder: node.isDirectory,
        kind: "remote",
        repositoryId: remote.repositoryId,
        branch: remote.branch,
      }
    : { path: node.path, label: node.name, isFolder: node.isDirectory, kind: "local" };
}

function parseRemotePath(
  path: string,
): { repositoryId: string; branch: string; path: string } | null {
  if (!path.startsWith("github://")) return null;
  const parts = path.slice("github://".length).split("/", 4);
  if (parts.length !== 4) return null;
  try {
    return {
      repositoryId: `${parts[0]}/${parts[1]}`,
      branch: decodeURIComponent(parts[2] ?? ""),
      path: decodeURIComponent(parts[3] ?? ""),
    };
  } catch {
    return null;
  }
}

function sameWorkspaceItem(left: WorkspaceItem, right: WorkspaceItem): boolean {
  if (
    (left.kind ?? "local") !== (right.kind ?? "local") ||
    left.repositoryId?.toLowerCase() !== right.repositoryId?.toLowerCase() ||
    left.branch !== right.branch
  )
    return false;
  return left.kind === "remote"
    ? left.path === right.path
    : left.path.toLowerCase() === right.path.toLowerCase();
}

/** Collect every directory node's path (recursively) into `into` — for pruning stale collapse state. */
function collectFolderPaths(nodes: readonly TreeNode[], into: Set<string>): void {
  for (const node of nodes) {
    if (node.isDirectory) {
      into.add(node.path);
      collectFolderPaths(node.children, into);
    }
  }
}

/** The last path segment (the folder's display name), tolerant of a trailing separator and either slash. */
function folderName(path: string): string {
  const trimmed = path.replace(/[/\\]+$/, "");
  const segments = trimmed.split(/[/\\]/);
  return segments[segments.length - 1] || trimmed;
}
