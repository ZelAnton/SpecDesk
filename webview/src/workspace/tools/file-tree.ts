/** Lazy, one-level-at-a-time Folder navigator for the active workspace. */

import type {
  FileDeleteCompletedPayload,
  TreeNode,
  TreePayload,
  WorkspaceContextPayload,
  WorkspaceItem,
} from "../../wire/protocol.js";
import { DestructiveConfirmation } from "../destructive-confirmation.js";
import { icon } from "../icons.js";
import type { PanelTool } from "../panel-tool.js";

export interface FileTreeCallbacks {
  onOpenFile(path: string): void;
  onOpenFolder(): void;
  onRequestLevel(path: string | undefined, requestId: number): void;
  onDeleteFile(path: string, root: string, requestId: number): void;
  onToggleFavorite?(item: WorkspaceItem, favorite: boolean): void;
  onShowEditor?(): void;
}

interface PendingLevel {
  readonly generation: number;
  readonly path?: string;
}

interface PendingDeletion {
  readonly generation: number;
  readonly path: string;
  readonly root: string;
}

export class FileTree implements PanelTool {
  readonly id = "files";
  readonly label = "Disk";
  readonly icon = icon("files");

  private rootEl: HTMLElement | null = null;
  private branchEl: HTMLElement | null = null;
  private listEl: HTMLElement | null = null;
  private emptyEl: HTMLElement | null = null;
  private filterEl: HTMLInputElement | null = null;
  private tree: TreePayload | null = null;
  private activeFile: string | null = null;
  private favorites: readonly WorkspaceItem[] = [];
  private context: WorkspaceContextPayload | null = null;
  private readonly expanded = new Set<string>();
  private readonly loaded = new Set<string>();
  private readonly pending = new Map<number, PendingLevel>();
  private requestSequence = 0;
  private generation = 0;
  private branchSeq = 0;
  private filter = "";
  private deleteRequestSequence = 0;
  private readonly pendingDeletions = new Map<number, PendingDeletion>();
  private readonly destructiveConfirmation = new DestructiveConfirmation();

  constructor(private readonly callbacks: FileTreeCallbacks) {}

  mount(body: HTMLElement): void {
    const root = document.createElement("div");
    root.className = "file-tree";

    const identity = document.createElement("div");
    identity.className = "file-tree-identity";
    const heading = document.createElement("p");
    heading.className = "file-tree-root";
    const branch = document.createElement("p");
    branch.className = "file-tree-branch-name";
    identity.append(heading, branch);

    const filterRow = document.createElement("div");
    filterRow.className = "file-tree-filter-row";
    const filter = document.createElement("input");
    filter.type = "search";
    filter.className = "file-tree-filter";
    filter.placeholder = "Filter files and folders";
    filter.setAttribute("aria-label", "Filter files and folders");
    filter.autocomplete = "off";
    filter.addEventListener("input", () => {
      this.filter = filter.value.trim().toLocaleLowerCase();
      this.render();
    });
    filter.addEventListener("keydown", (event) => {
      if (event.key === "Escape" && filter.value.length > 0) {
        event.preventDefault();
        filter.value = "";
        this.filter = "";
        this.render();
      }
    });
    filterRow.append(filter);

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

    root.append(identity, filterRow, empty, list);
    body.appendChild(root);
    this.rootEl = heading;
    this.branchEl = branch;
    this.emptyEl = empty;
    this.listEl = list;
    this.filterEl = filter;
    this.render();
  }

  onShow(): void {
    this.callbacks.onShowEditor?.();
  }

  /** Ask for the active workspace root. Only the matching response may replace the current tree. */
  requestRoot(): void {
    const requestId = ++this.requestSequence;
    this.pending.set(requestId, { generation: this.generation });
    this.callbacks.onRequestLevel(undefined, requestId);
  }

  /** Apply an unsolicited root publication or a correlated root/directory response. */
  setTree(tree: TreePayload): void {
    this.destructiveConfirmation.close(false);
    if (tree.requestId === 0) {
      this.replaceRoot(tree);
      return;
    }
    const pending = this.pending.get(tree.requestId);
    if (pending === undefined) return;
    this.pending.delete(tree.requestId);
    if (pending.generation !== this.generation) return;
    if (tree.error !== undefined) {
      if (pending.path !== undefined) this.expanded.delete(normalizePath(pending.path));
      this.render();
      return;
    }
    if (pending.path === undefined) {
      this.replaceRoot(tree);
      return;
    }
    if (normalizePath(pending.path) !== normalizePath(tree.root) || this.tree === null) return;
    const replaced = replaceChildren(this.tree.nodes, pending.path, tree.nodes);
    if (!replaced) return;
    this.loaded.add(normalizePath(pending.path));
    this.expanded.add(normalizePath(pending.path));
    this.render();
    this.revealActiveBranch();
  }

  setContext(context: WorkspaceContextPayload | null): void {
    this.destructiveConfirmation.close(false);
    this.context = context;
    this.render();
  }

  setActiveFile(path: string | null): void {
    if (path === this.activeFile) return;
    this.destructiveConfirmation.close(false);
    this.activeFile = path;
    this.render();
    this.revealActiveBranch();
  }

  setFavorites(favorites: readonly WorkspaceItem[]): void {
    this.destructiveConfirmation.close(false);
    this.favorites = favorites;
    this.render();
  }

  /** Retire only GitHub-backed Folder data at an account boundary; local folders remain usable. */
  clearAccountState(): void {
    const remoteContext =
      this.context !== null &&
      this.context.repository !== null &&
      this.context.repositoryRoot === null;
    const remoteTree =
      this.tree?.remote === true || (this.tree?.nodes.some(nodeHasRemotePath) ?? false);
    const remoteFile = this.activeFile?.startsWith("github://") ?? false;
    if (!remoteContext && !remoteTree && !remoteFile) return;
    this.generation++;
    this.destructiveConfirmation.close(false);
    this.pendingDeletions.clear();
    this.pending.clear();
    this.expanded.clear();
    this.loaded.clear();
    this.tree = null;
    this.context = null;
    if (remoteFile) this.activeFile = null;
    this.render();
  }

  focusFilter(): void {
    this.filterEl?.focus();
  }

  private replaceRoot(tree: TreePayload): void {
    this.generation++;
    this.pendingDeletions.clear();
    this.pending.clear();
    this.expanded.clear();
    this.loaded.clear();
    this.tree = { ...tree, nodes: tree.nodes.map(cloneNode) };
    this.loaded.add(normalizePath(tree.root));
    this.render();
    this.revealActiveBranch();
  }

  private requestDirectory(path: string): void {
    const normalized = normalizePath(path);
    if (
      this.loaded.has(normalized) ||
      [...this.pending.values()].some((item) => item.path === path)
    ) {
      return;
    }
    const requestId = ++this.requestSequence;
    this.pending.set(requestId, { generation: this.generation, path });
    this.callbacks.onRequestLevel(path, requestId);
    this.render();
  }

  private revealActiveBranch(): void {
    if (this.tree === null || this.activeFile === null) return;
    const active = normalizePath(this.activeFile);
    let nodes = this.tree.nodes;
    while (true) {
      const folder = nodes.find(
        (node) => node.isDirectory && isDescendantOrSame(active, normalizePath(node.path)),
      );
      if (folder === undefined) return;
      const path = normalizePath(folder.path);
      this.expanded.add(path);
      if (!this.loaded.has(path)) {
        this.requestDirectory(folder.path);
        return;
      }
      nodes = folder.children;
    }
  }

  private render(): void {
    if (
      this.rootEl === null ||
      this.branchEl === null ||
      this.listEl === null ||
      this.emptyEl === null
    ) {
      return;
    }
    const focusedPath = this.focusedItemPath();
    this.branchSeq = 0;
    const tree = this.tree;
    const hasFolder = tree !== null && tree.root.length > 0;
    this.emptyEl.hidden = hasFolder;
    const identityEl = this.rootEl.parentElement;
    const filterRowEl = this.filterEl?.parentElement;
    if (identityEl !== null) identityEl.hidden = !hasFolder;
    if (filterRowEl !== null && filterRowEl !== undefined) filterRowEl.hidden = !hasFolder;
    this.listEl.hidden = !hasFolder;

    const identity = this.folderIdentity(tree);
    this.rootEl.textContent = identity.name;
    this.rootEl.title = identity.title;
    this.branchEl.textContent = identity.branch ?? "";
    this.branchEl.hidden = identity.branch === null;

    this.listEl.replaceChildren();
    if (tree === null) return;
    if (tree.error !== undefined) {
      const error = document.createElement("p");
      error.className = "file-tree-error";
      error.setAttribute("role", "alert");
      error.textContent = tree.error;
      this.listEl.appendChild(error);
      return;
    }
    const shown = this.filter.length === 0 ? tree.nodes : filterNodes(tree.nodes, this.filter);
    if (shown.length === 0) {
      const none = document.createElement("p");
      none.className = "file-tree-none";
      none.textContent =
        this.filter.length > 0 ? "No loaded items match this filter." : "This folder is empty.";
      this.listEl.appendChild(none);
      return;
    }
    this.listEl.appendChild(this.buildList(shown));
    this.restoreFocus(focusedPath);
  }

  private folderIdentity(tree: TreePayload | null): {
    name: string;
    title: string;
    branch: string | null;
  } {
    if (tree === null) return { name: "", title: "", branch: null };
    const context = contextMatchesTree(this.context, tree.root) ? this.context : null;
    if (context?.repository !== null && context?.repository !== undefined) {
      const name = context.repositoryRoot ? folderName(context.repositoryRoot) : context.repository;
      const branch = context.branchState === "named" ? context.branch : null;
      return { name, title: context.repositoryRoot ?? context.repository, branch };
    }
    return { name: folderName(tree.root), title: tree.root, branch: null };
  }

  private focusedItemPath(): string | null {
    const active = document.activeElement;
    return active instanceof HTMLElement && this.listEl?.contains(active)
      ? (active.dataset.path ?? null)
      : null;
  }

  private restoreFocus(path: string | null): void {
    if (path === null || this.listEl === null) return;
    for (const el of this.listEl.querySelectorAll<HTMLElement>("[data-path]")) {
      if (el.dataset.path === path) {
        el.focus();
        return;
      }
    }
  }

  private buildList(nodes: readonly TreeNode[]): HTMLUListElement {
    const ul = document.createElement("ul");
    ul.className = "file-tree-branch";
    for (const node of nodes)
      ul.appendChild(node.isDirectory ? this.buildFolder(node) : this.buildFile(node));
    return ul;
  }

  private buildFolder(node: TreeNode): HTMLLIElement {
    const li = document.createElement("li");
    const path = normalizePath(node.path);
    const expanded = this.expanded.has(path);
    const visiblyExpanded = expanded || (this.filter.length > 0 && node.children.length > 0);
    const loading = [...this.pending.values()].some((pending) => pending.path === node.path);
    const row = document.createElement("div");
    row.className = "file-tree-row";
    const childList = this.buildList(node.children);
    const listId = `file-tree-branch-${this.branchSeq++}`;
    childList.id = listId;
    childList.hidden = !visiblyExpanded;

    const toggle = document.createElement("button");
    toggle.type = "button";
    toggle.className = "file-tree-item file-tree-folder";
    toggle.dataset.path = node.path;
    toggle.setAttribute("aria-expanded", String(visiblyExpanded));
    toggle.setAttribute("aria-controls", listId);
    toggle.setAttribute("aria-busy", String(loading));
    toggle.textContent = node.name;
    toggle.title = loading ? `Loading ${node.name}` : node.name;
    this.markCurrent(toggle, node.path);
    toggle.addEventListener("click", () => {
      if (expanded) {
        this.expanded.delete(path);
        this.render();
      } else if (!this.loaded.has(path)) {
        this.expanded.add(path);
        this.requestDirectory(node.path);
      } else {
        this.expanded.add(path);
        this.render();
      }
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
    this.markCurrent(button, node.path);
    button.addEventListener("click", () => this.callbacks.onOpenFile(node.path));
    row.append(button, this.favoriteButton(node));
    if (this.tree?.remote !== true && !node.path.startsWith("github://")) {
      const deleteFile = document.createElement("button");
      deleteFile.type = "button";
      deleteFile.className = "file-tree-delete";
      deleteFile.dataset.path = `delete:${node.path}`;
      deleteFile.setAttribute("aria-label", `Delete file ${node.name}`);
      deleteFile.setAttribute("aria-expanded", "false");
      deleteFile.title = "Delete file";
      deleteFile.innerHTML = icon("delete");
      deleteFile.disabled = [...this.pendingDeletions.values()].some((pending) =>
        sameLocalEntryPath(pending.path, node.path),
      );
      deleteFile.addEventListener("click", (event) => {
        event.stopPropagation();
        const tree = this.tree;
        if (tree === null || tree.remote === true) return;
        this.destructiveConfirmation.open({
          trigger: deleteFile,
          anchor: row,
          title: `Delete ${node.name}?`,
          description:
            "This permanently deletes this file from the current Disk folder. Folders are never deleted.",
          focusAfterConfirm: () => this.filterEl,
          onConfirm: () => {
            const requestId = ++this.deleteRequestSequence;
            this.pendingDeletions.set(requestId, {
              generation: this.generation,
              path: node.path,
              root: tree.root,
            });
            this.callbacks.onDeleteFile(node.path, tree.root, requestId);
            this.render();
          },
        });
      });
      row.append(deleteFile);
    }
    li.append(row);
    return li;
  }

  fileDeleteCompleted(payload: FileDeleteCompletedPayload): void {
    const pending = this.pendingDeletions.get(payload.requestId);
    if (pending === undefined) return;
    this.pendingDeletions.delete(payload.requestId);
    if (
      pending.generation !== this.generation ||
      !sameLocalEntryPath(pending.path, payload.path) ||
      normalizePath(pending.root) !== normalizePath(payload.root)
    ) {
      return;
    }
    if (payload.succeeded && this.tree !== null) {
      this.tree = { ...this.tree, nodes: removeFileNode(this.tree.nodes, payload.path) };
      if (sameLocalEntryPath(this.activeFile ?? "", payload.path)) {
        this.activeFile = null;
      }
    }
    this.render();
  }

  private markCurrent(button: HTMLButtonElement, path: string): void {
    if (sameLocalEntryPath(path, this.activeFile ?? "")) {
      button.classList.add("is-current");
      button.setAttribute("aria-current", "true");
    }
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

function replaceChildren(nodes: TreeNode[], path: string, children: TreeNode[]): boolean {
  for (const node of nodes) {
    if (normalizePath(node.path) === normalizePath(path)) {
      node.children = children.map(cloneNode);
      node.hasChildren = children.length > 0;
      return true;
    }
    if (replaceChildren(node.children, path, children)) return true;
  }
  return false;
}

function cloneNode(node: TreeNode): TreeNode {
  return { ...node, children: node.children.map(cloneNode) };
}

function removeFileNode(nodes: readonly TreeNode[], path: string): TreeNode[] {
  return nodes
    .filter((node) => node.isDirectory || !sameLocalEntryPath(node.path, path))
    .map((node) =>
      node.isDirectory ? { ...node, children: removeFileNode(node.children, path) } : node,
    );
}

function nodeHasRemotePath(node: TreeNode): boolean {
  return node.path.startsWith("github://") || node.children.some(nodeHasRemotePath);
}

function filterNodes(nodes: readonly TreeNode[], query: string): TreeNode[] {
  const result: TreeNode[] = [];
  for (const node of nodes) {
    const selfMatches = node.name.toLocaleLowerCase().includes(query);
    const children = selfMatches ? node.children : filterNodes(node.children, query);
    if (selfMatches || children.length > 0) result.push({ ...node, children });
  }
  return result;
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
    : sameLocalEntryPath(left.path, right.path);
}

function isDescendantOrSame(candidate: string, parent: string): boolean {
  return candidate === parent || candidate.startsWith(`${parent}/`);
}

function normalizePath(path: string): string {
  const normalized = path.replaceAll("\\", "/").replace(/\/+$/, "");
  return normalized.startsWith("github://") ? normalized : normalized.toLocaleLowerCase();
}

function sameLocalEntryPath(left: string, right: string): boolean {
  return normalizeLocalEntryPath(left) === normalizeLocalEntryPath(right);
}

function normalizeLocalEntryPath(path: string): string {
  const normalized = path.replaceAll("\\", "/").replace(/\/+$/, "");
  if (/^[A-Za-z]:/.test(normalized)) {
    return `${normalized[0]?.toLocaleLowerCase()}${normalized.slice(1)}`;
  }
  const unc = /^\/\/([^/]+)\/([^/]+)(.*)$/.exec(normalized);
  return unc === null
    ? normalized
    : `//${unc[1]?.toLocaleLowerCase()}/${unc[2]?.toLocaleLowerCase()}${unc[3]}`;
}

function contextMatchesTree(context: WorkspaceContextPayload | null, root: string): boolean {
  if (context?.repository === null || context?.repository === undefined) return true;
  const normalizedRoot = normalizePath(root);
  if (context.repositoryRoot !== null) {
    return isDescendantOrSame(normalizedRoot, normalizePath(context.repositoryRoot));
  }
  return normalizedRoot === normalizePath(context.repository);
}

function folderName(path: string): string {
  const trimmed = path.replace(/[/\\]+$/, "");
  const segments = trimmed.split(/[/\\]/);
  return segments[segments.length - 1] || trimmed;
}
