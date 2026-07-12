// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import type { TreePayload } from "../../src/wire/protocol.js";
import { FileTree } from "../../src/workspace/tools/file-tree.js";

function ready() {
  const onOpenFile = vi.fn<(path: string) => void>();
  const onOpenFolder = vi.fn<() => void>();
  const tree = new FileTree({ onOpenFile, onOpenFolder });
  const body = document.createElement("div");
  tree.mount(body);
  return { tree, onOpenFile, onOpenFolder, body };
}

const SAMPLE: TreePayload = {
  root: "C:\\specs\\repo",
  nodes: [
    {
      name: "guides",
      path: "C:\\specs\\repo\\guides",
      isDirectory: true,
      children: [
        {
          name: "intro.md",
          path: "C:\\specs\\repo\\guides\\intro.md",
          isDirectory: false,
          children: [],
        },
      ],
    },
    { name: "README.md", path: "C:\\specs\\repo\\README.md", isDirectory: false, children: [] },
  ],
};

describe("FileTree", () => {
  it("shows the empty state until a tree is set, and its button opens a folder", () => {
    const { body, onOpenFolder } = ready();
    const empty = body.querySelector<HTMLElement>(".file-tree-empty");
    expect(empty?.hidden).toBe(false);
    expect(body.querySelector<HTMLElement>(".file-tree-root")?.hidden).toBe(true);
    body.querySelector<HTMLButtonElement>(".file-tree-open-folder")?.click();
    expect(onOpenFolder).toHaveBeenCalledTimes(1);
  });

  it("renders the folder name and a nested folder/file tree, and opens a file on click", () => {
    const { tree, body, onOpenFile } = ready();
    tree.setTree(SAMPLE);

    expect(body.querySelector<HTMLElement>(".file-tree-empty")?.hidden).toBe(true);
    expect(body.querySelector(".file-tree-root")?.textContent).toBe("repo");

    const folders = body.querySelectorAll<HTMLButtonElement>(".file-tree-folder");
    const files = body.querySelectorAll<HTMLButtonElement>(".file-tree-file");
    expect(Array.from(folders).map((f) => f.textContent)).toEqual(["guides"]);
    expect(Array.from(files).map((f) => f.textContent)).toEqual(["intro.md", "README.md"]);
    // intro.md is nested inside the "guides" folder's <li>.
    expect(files[0]?.closest("li")?.parentElement?.closest("li")).toBe(folders[0]?.closest("li"));

    files[1]?.click();
    expect(onOpenFile).toHaveBeenCalledWith("C:\\specs\\repo\\README.md");
  });

  it("toggles a folder's children and keeps the collapse state across a re-render", () => {
    const { tree, body } = ready();
    tree.setTree(SAMPLE);
    const folder = body.querySelector<HTMLButtonElement>(".file-tree-folder");
    const childList = () => folder?.nextElementSibling as HTMLElement | null;

    expect(folder?.getAttribute("aria-expanded")).toBe("true");
    expect(childList()?.hidden).toBe(false);

    folder?.click(); // collapse
    expect(folder?.getAttribute("aria-expanded")).toBe("false");
    expect(childList()?.hidden).toBe(true);

    // A fresh tree event (same paths) must preserve the collapse.
    tree.setTree(SAMPLE);
    const folderAfter = body.querySelector<HTMLButtonElement>(".file-tree-folder");
    expect(folderAfter?.getAttribute("aria-expanded")).toBe("false");
    expect((folderAfter?.nextElementSibling as HTMLElement | null)?.hidden).toBe(true);
  });

  it("highlights the active file and moves the highlight when it changes", () => {
    const { tree, body } = ready();
    tree.setTree(SAMPLE);
    tree.setActiveFile("C:\\specs\\repo\\README.md");
    const current = body.querySelectorAll<HTMLButtonElement>(".file-tree-file.is-current");
    expect(current).toHaveLength(1);
    expect(current[0]?.textContent).toBe("README.md");
    expect(current[0]?.getAttribute("aria-current")).toBe("true");

    tree.setActiveFile("C:\\specs\\repo\\guides\\intro.md");
    const moved = body.querySelectorAll<HTMLButtonElement>(".file-tree-file.is-current");
    expect(moved).toHaveLength(1);
    expect(moved[0]?.textContent).toBe("intro.md");
  });

  it("expands a collapsed ancestor when a file inside it becomes active (so the highlight shows)", () => {
    const { tree, body } = ready();
    tree.setTree(SAMPLE);
    const folder = body.querySelector<HTMLButtonElement>(".file-tree-folder");
    folder?.click(); // collapse "guides"
    expect(folder?.getAttribute("aria-expanded")).toBe("false");

    tree.setActiveFile("C:\\specs\\repo\\guides\\intro.md");
    const folderAfter = body.querySelector<HTMLButtonElement>(".file-tree-folder");
    expect(folderAfter?.getAttribute("aria-expanded")).toBe("true");
    const current = body.querySelector<HTMLButtonElement>(".file-tree-file.is-current");
    expect(current?.textContent).toBe("intro.md");
    expect((folderAfter?.nextElementSibling as HTMLElement | null)?.hidden).toBe(false);
  });

  it("keeps keyboard focus on a tree item across a re-render", () => {
    const { tree, body } = ready();
    document.body.appendChild(body); // focus() needs the node in the document
    tree.setTree(SAMPLE);
    const readme = body.querySelector<HTMLButtonElement>('.file-tree-file[data-path$="README.md"]');
    readme?.focus();
    expect(document.activeElement).toBe(readme);

    tree.setActiveFile("C:\\specs\\repo\\README.md"); // triggers a re-render
    const readmeAfter = body.querySelector<HTMLButtonElement>(
      '.file-tree-file[data-path$="README.md"]',
    );
    expect(readmeAfter).not.toBe(readme); // it was rebuilt
    expect(document.activeElement).toBe(readmeAfter); // …but focus followed it
    body.remove();
  });

  it("points each folder toggle at the child list it controls (aria-controls)", () => {
    const { tree, body } = ready();
    tree.setTree(SAMPLE);
    const folder = body.querySelector<HTMLButtonElement>(".file-tree-folder");
    const controls = folder?.getAttribute("aria-controls");
    expect(controls).toBeTruthy();
    expect(body.querySelector(`#${controls}`)).toBe(folder?.nextElementSibling);
  });

  it("forgets collapse state for folders no longer in the tree", () => {
    const { tree, body } = ready();
    tree.setTree(SAMPLE);
    body.querySelector<HTMLButtonElement>(".file-tree-folder")?.click(); // collapse "guides"

    // A tree without "guides" prunes its collapse state; a later tree that has it again defaults to expanded.
    tree.setTree({
      root: "C:\\specs\\repo",
      nodes: [
        { name: "README.md", path: "C:\\specs\\repo\\README.md", isDirectory: false, children: [] },
      ],
    });
    tree.setTree(SAMPLE);
    expect(
      body.querySelector<HTMLButtonElement>(".file-tree-folder")?.getAttribute("aria-expanded"),
    ).toBe("true");
  });

  it("says so when an opened folder holds no specs", () => {
    const { tree, body } = ready();
    tree.setTree({ root: "C:\\empty", nodes: [] });
    expect(body.querySelector<HTMLElement>(".file-tree-empty")?.hidden).toBe(true);
    expect(body.querySelector(".file-tree-root")?.textContent).toBe("empty");
    expect(body.querySelector(".file-tree-none")?.textContent).toBe("No specs in this folder.");
  });
});
