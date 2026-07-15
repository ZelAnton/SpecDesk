// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import type { TreePayload, WorkspaceContextPayload } from "../../src/wire/protocol.js";
import { remoteWirePath } from "../../src/workspace/remote-path.js";
import { FileTree } from "../../src/workspace/tools/file-tree.js";

function ready() {
  const onOpenFile = vi.fn<(path: string) => void>();
  const onOpenFolder = vi.fn<() => void>();
  const onRequestLevel = vi.fn<(path: string | undefined, requestId: number) => void>();
  const onToggleFavorite = vi.fn();
  const onShowEditor = vi.fn();
  const tree = new FileTree({
    onOpenFile,
    onOpenFolder,
    onRequestLevel,
    onToggleFavorite,
    onShowEditor,
  });
  const body = document.createElement("div");
  tree.mount(body);
  return {
    tree,
    onOpenFile,
    onOpenFolder,
    onRequestLevel,
    onToggleFavorite,
    onShowEditor,
    body,
  };
}

const ROOT: TreePayload = {
  root: "C:\\specs\\repo",
  requestId: 0,
  nodes: [
    {
      name: "guides",
      path: "C:\\specs\\repo\\guides",
      isDirectory: true,
      children: [],
      hasChildren: true,
    },
    {
      name: "README.md",
      path: "C:\\specs\\repo\\README.md",
      isDirectory: false,
      children: [],
      hasChildren: false,
    },
  ],
};

const CONTEXT: WorkspaceContextPayload = {
  repository: "octo/specs",
  repositoryRoot: "C:\\specs\\repo",
  branch: "spec/refunds",
  branchState: "named",
  defaultBranch: "main",
  path: "README.md",
};

describe("FileTree", () => {
  it("shows the folder picker until a root arrives", () => {
    const { body, onOpenFolder } = ready();
    expect(body.querySelector<HTMLElement>(".file-tree-empty")?.hidden).toBe(false);
    body.querySelector<HTMLButtonElement>(".file-tree-open-folder")?.click();
    expect(onOpenFolder).toHaveBeenCalledOnce();
  });

  it("renders only the root level and requests a collapsed directory on first expansion", () => {
    const { tree, body, onRequestLevel } = ready();
    tree.setTree(ROOT);

    const folder = body.querySelector<HTMLButtonElement>(".file-tree-folder");
    expect(folder?.getAttribute("aria-expanded")).toBe("false");
    expect(body.querySelectorAll(".file-tree-file")).toHaveLength(1);
    folder?.click();
    expect(body.querySelector(".file-tree-folder")?.getAttribute("aria-busy")).toBe("true");
    expect(onRequestLevel).toHaveBeenCalledWith("C:\\specs\\repo\\guides", 1);

    tree.setTree({
      root: "C:\\specs\\repo\\guides",
      requestId: 1,
      nodes: [
        {
          name: "intro.md",
          path: "C:\\specs\\repo\\guides\\intro.md",
          isDirectory: false,
          children: [],
          hasChildren: false,
        },
      ],
    });
    expect(body.querySelectorAll(".file-tree-file")).toHaveLength(2);
    expect(
      body.querySelector<HTMLButtonElement>(".file-tree-folder")?.getAttribute("aria-expanded"),
    ).toBe("true");

    body.querySelector<HTMLButtonElement>(".file-tree-folder")?.click();
    body.querySelector<HTMLButtonElement>(".file-tree-folder")?.click();
    expect(onRequestLevel).toHaveBeenCalledTimes(1);
  });

  it("ignores a stale correlated level after an unsolicited root replacement", () => {
    const { tree, body, onRequestLevel } = ready();
    tree.setTree(ROOT);
    body.querySelector<HTMLButtonElement>(".file-tree-folder")?.click();
    expect(onRequestLevel).toHaveBeenCalledOnce();

    tree.setTree({ root: "C:\\other", requestId: 0, nodes: [] });
    tree.setTree({
      root: "C:\\specs\\repo\\guides",
      requestId: 1,
      nodes: [
        {
          name: "stale.md",
          path: "C:\\specs\\repo\\guides\\stale.md",
          isDirectory: false,
          children: [],
          hasChildren: false,
        },
      ],
    });
    expect(body.textContent).not.toContain("stale.md");
    expect(body.querySelector(".file-tree-root")?.textContent).toBe("other");
  });

  it("correlates an explicit root refresh and rejects an unknown response", () => {
    const { tree, body, onRequestLevel } = ready();
    tree.setTree(ROOT);
    tree.requestRoot();
    expect(onRequestLevel).toHaveBeenCalledWith(undefined, 1);
    tree.setTree({ root: "C:\\wrong", requestId: 99, nodes: [] });
    expect(body.querySelector(".file-tree-root")?.textContent).toBe("repo");
    tree.setTree({ root: "C:\\fresh", requestId: 1, nodes: [] });
    expect(body.querySelector(".file-tree-root")?.textContent).toBe("fresh");
  });

  it("keeps a failed lazy folder retryable instead of presenting it as empty", () => {
    const { tree, body, onRequestLevel } = ready();
    tree.setTree(ROOT);
    body.querySelector<HTMLButtonElement>(".file-tree-folder")?.click();
    tree.setTree({
      root: "C:\\specs\\repo\\guides",
      requestId: 1,
      nodes: [],
      error: "Could not read that folder. Try again.",
    });

    expect(
      body.querySelector<HTMLButtonElement>(".file-tree-folder")?.getAttribute("aria-busy"),
    ).toBe("false");
    expect(
      body.querySelector<HTMLButtonElement>(".file-tree-folder")?.getAttribute("aria-expanded"),
    ).toBe("false");
    body.querySelector<HTMLButtonElement>(".file-tree-folder")?.click();
    expect(onRequestLevel).toHaveBeenLastCalledWith("C:\\specs\\repo\\guides", 2);
  });

  it("renders an authoritative root failure instead of calling the repository empty", () => {
    const { tree, body } = ready();
    tree.setTree({
      root: "octo/specs",
      requestId: 0,
      nodes: [],
      error: "This repository is too large for a complete preview.",
      remote: true,
    });

    expect(body.querySelector(".file-tree-error")?.textContent).toContain("too large");
    expect(body.querySelector(".file-tree-error")?.getAttribute("role")).toBe("alert");
    expect(body.textContent).not.toContain("This folder is empty");

    tree.clearAccountState();
    expect(body.textContent).not.toContain("too large");
    expect(body.querySelector<HTMLElement>(".file-tree-empty")?.hidden).toBe(false);
  });

  it("opens files and preserves keyboard focus across an authoritative render", () => {
    const { tree, body, onOpenFile } = ready();
    document.body.append(body);
    tree.setTree(ROOT);
    const readme = body.querySelector<HTMLButtonElement>(".file-tree-file");
    readme?.focus();
    tree.setFavorites([]);
    const refreshed = body.querySelector<HTMLButtonElement>(".file-tree-file");
    expect(document.activeElement).toBe(refreshed);
    refreshed?.click();
    expect(onOpenFile).toHaveBeenCalledWith("C:\\specs\\repo\\README.md");
    body.remove();
  });

  it("shows local-copy and branch identity from authoritative workspace context", () => {
    const { tree, body } = ready();
    tree.setTree(ROOT);
    tree.setContext(CONTEXT);
    expect(body.querySelector(".file-tree-root")?.textContent).toBe("repo");
    expect(body.querySelector(".file-tree-branch-name")?.textContent).toBe("spec/refunds");
    tree.setContext({
      ...CONTEXT,
      repository: null,
      repositoryRoot: null,
      branch: null,
      branchState: "unavailable",
    });
    expect(body.querySelector(".file-tree-root")?.textContent).toBe("repo");
    expect(body.querySelector<HTMLElement>(".file-tree-branch-name")?.hidden).toBe(true);
  });

  it("drops a previous repository identity when an unrelated root replaces the tree", () => {
    const { tree, body } = ready();
    tree.setTree(ROOT);
    tree.setContext(CONTEXT);
    tree.setTree({ root: "C:\\other", requestId: 0, nodes: [] });
    expect(body.querySelector(".file-tree-root")?.textContent).toBe("other");
    expect(body.querySelector<HTMLElement>(".file-tree-branch-name")?.hidden).toBe(true);
  });

  it("rejects a late repository identity that does not own the visible tree", () => {
    const { tree, body } = ready();
    tree.setTree({ root: "C:\\other", requestId: 0, nodes: [] });
    tree.setContext(CONTEXT);
    expect(body.querySelector(".file-tree-root")?.textContent).toBe("other");
    expect(body.querySelector<HTMLElement>(".file-tree-branch-name")?.hidden).toBe(true);
  });

  it("applies repository identity when its matching tree arrives after the context", () => {
    const { tree, body } = ready();
    tree.setTree({ root: "C:\\other", requestId: 0, nodes: [] });
    tree.setContext(CONTEXT);
    tree.setTree(ROOT);
    expect(body.querySelector(".file-tree-root")?.textContent).toBe("repo");
    expect(body.querySelector(".file-tree-branch-name")?.textContent).toBe("spec/refunds");
  });

  it("filters only loaded nodes, keeps ancestors, clears on Escape, and sends no request", () => {
    const { tree, body, onRequestLevel } = ready();
    tree.setTree(ROOT);
    const filter = body.querySelector<HTMLInputElement>(".file-tree-filter");
    if (filter === null) throw new Error("missing filter");
    filter.value = "read";
    filter.dispatchEvent(new Event("input"));
    expect(body.querySelectorAll(".file-tree-file")).toHaveLength(1);
    expect(body.querySelectorAll(".file-tree-folder")).toHaveLength(0);
    filter.value = "guid";
    filter.dispatchEvent(new Event("input"));
    expect(body.querySelector(".file-tree-folder")?.textContent).toBe("guides");
    expect(onRequestLevel).not.toHaveBeenCalled();
    filter.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    expect(filter.value).toBe("");
    expect(body.querySelectorAll(".file-tree-file")).toHaveLength(1);
  });

  it("reveals a loaded descendant that matches the filter even after its folder was collapsed", () => {
    const { tree, body } = ready();
    tree.setTree(ROOT);
    body.querySelector<HTMLButtonElement>(".file-tree-folder")?.click();
    tree.setTree({
      root: "C:\\specs\\repo\\guides",
      requestId: 1,
      nodes: [
        {
          name: "intro.md",
          path: "C:\\specs\\repo\\guides\\intro.md",
          isDirectory: false,
          children: [],
          hasChildren: false,
        },
      ],
    });
    body.querySelector<HTMLButtonElement>(".file-tree-folder")?.click();

    const filter = body.querySelector<HTMLInputElement>(".file-tree-filter");
    if (filter === null) throw new Error("missing filter");
    filter.value = "intro";
    filter.dispatchEvent(new Event("input"));

    expect(
      body.querySelector<HTMLButtonElement>(".file-tree-folder")?.getAttribute("aria-expanded"),
    ).toBe("true");
    expect(body.querySelector<HTMLButtonElement>(".file-tree-file")?.textContent).toBe("intro.md");
  });

  it("loads the active file's ancestor one level at a time", () => {
    const { tree, onRequestLevel } = ready();
    tree.setActiveFile("C:\\specs\\repo\\guides\\intro.md");
    tree.setTree(ROOT);
    expect(onRequestLevel).toHaveBeenCalledWith("C:\\specs\\repo\\guides", 1);
  });

  it("favorites remote files with stable repository coordinates", () => {
    const { tree, body, onToggleFavorite } = ready();
    const path = remoteWirePath("Octo/Specs", "feature/Docs", "Docs/Guide.md");
    tree.setTree({
      root: "Octo/Specs",
      requestId: 0,
      nodes: [{ name: "Guide.md", path, isDirectory: false, children: [], hasChildren: false }],
    });
    body.querySelector<HTMLButtonElement>(".file-tree-star")?.click();
    expect(onToggleFavorite).toHaveBeenCalledWith(
      {
        path: "Docs/Guide.md",
        label: "Guide.md",
        isFolder: false,
        kind: "remote",
        repositoryId: "Octo/Specs",
        branch: "feature/Docs",
      },
      true,
    );
  });

  it("marks only existing Disk favorites as selected and keeps both toggles keyboard-addressable", () => {
    const { tree, body } = ready();
    tree.setTree(ROOT);
    tree.setFavorites([
      {
        path: "C:\\specs\\repo\\README.md",
        label: "README.md",
        isFolder: false,
      },
    ]);

    const [folderStar, fileStar] = body.querySelectorAll<HTMLButtonElement>(".file-tree-star");
    expect(folderStar?.classList.contains("is-favorite")).toBe(false);
    expect(folderStar?.getAttribute("aria-pressed")).toBe("false");
    expect(folderStar?.tabIndex).toBe(0);
    expect(fileStar?.classList.contains("is-favorite")).toBe(true);
    expect(fileStar?.getAttribute("aria-pressed")).toBe("true");
    expect(fileStar?.tabIndex).toBe(0);
  });

  it("keeps case-distinct GitHub folders independent", () => {
    const { tree, body, onRequestLevel } = ready();
    const upper = remoteWirePath("octo/specs", "main", "Docs");
    const lower = remoteWirePath("octo/specs", "main", "docs");
    tree.setTree({
      root: "octo/specs",
      requestId: 0,
      nodes: [
        { name: "Docs", path: upper, isDirectory: true, children: [], hasChildren: true },
        { name: "docs", path: lower, isDirectory: true, children: [], hasChildren: true },
      ],
    });

    const folders = body.querySelectorAll<HTMLButtonElement>(".file-tree-folder");
    folders[1]?.click();

    expect(folders[0]?.getAttribute("aria-expanded")).toBe("false");
    expect(
      body
        .querySelectorAll<HTMLButtonElement>(".file-tree-folder")[1]
        ?.getAttribute("aria-expanded"),
    ).toBe("true");
    expect(onRequestLevel).toHaveBeenCalledWith(lower, 1);
  });

  it("clears published GitHub Folder data at an account boundary but preserves local folders", () => {
    const remote = ready();
    const privateFolder = remoteWirePath("octo/specs", "main", "private");
    remote.tree.setContext({
      repository: "octo/specs",
      repositoryRoot: null,
      branch: "main",
      branchState: "named",
      defaultBranch: "main",
      path: "",
    });
    remote.tree.setTree({
      root: "octo/specs",
      requestId: 0,
      nodes: [
        {
          name: "private",
          path: privateFolder,
          isDirectory: true,
          children: [],
          hasChildren: true,
        },
      ],
    });
    remote.body.querySelector<HTMLButtonElement>(".file-tree-folder")?.click();
    remote.tree.clearAccountState();
    expect(remote.body.textContent).not.toContain("private");
    expect(remote.body.querySelector<HTMLElement>(".file-tree-empty")?.hidden).toBe(false);

    remote.tree.setTree({ root: privateFolder, requestId: 1, nodes: [] });
    expect(remote.body.textContent).not.toContain("private");

    const local = ready();
    local.tree.setTree(ROOT);
    local.tree.setContext(CONTEXT);
    local.tree.clearAccountState();
    expect(local.body.querySelector(".file-tree-root")?.textContent).toBe("repo");
    expect(local.body.textContent).toContain("README.md");
  });

  it("opens the editor whenever the Disk mode is shown", () => {
    const { tree, onShowEditor } = ready();
    tree.onShow();
    expect(onShowEditor).toHaveBeenCalledOnce();
  });
});
