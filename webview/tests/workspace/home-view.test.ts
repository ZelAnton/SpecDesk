// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import type { WorkspaceItem } from "../../src/wire/protocol.js";
import { buildHomeView } from "../../src/workspace/tools/home-view.js";

function ready() {
  const host = document.createElement("div");
  document.body.appendChild(host);
  const onOpenFile = vi.fn();
  const onOpenFolder = vi.fn();
  const onOpenItem = vi.fn<(item: WorkspaceItem) => void>();
  const onOpenRepositories = vi.fn();
  const home = buildHomeView(host, {
    onOpenFile,
    onOpenFolder,
    onOpenItem,
    onOpenRepositories,
  });
  return { host, home, onOpenFile, onOpenFolder, onOpenItem, onOpenRepositories };
}

describe("buildHomeView", () => {
  it("builds the Start screen with a title, prompt, recents, and open-file / open-folder actions", () => {
    const { host, onOpenFile, onOpenFolder } = ready();

    expect(host.querySelector(".home-title")?.textContent).toBe("SpecDesk");
    expect(host.querySelector(".home-prompt")).not.toBeNull();
    expect(host.querySelector(".home-recents-label")?.textContent).toBe("Recent");
    // No recents fed yet: the hint shows and the list is hidden.
    expect(host.querySelector<HTMLElement>(".home-recents-empty")?.hidden).toBe(false);
    expect(host.querySelector<HTMLElement>(".home-recents-list")?.hidden).toBe(true);

    const buttons = host.querySelectorAll<HTMLButtonElement>(".home-open");
    expect(Array.from(buttons).map((b) => b.textContent)).toEqual([
      "Open a file",
      "Open a folder",
      "Open Repository",
    ]);

    buttons[0]?.click();
    expect(onOpenFile).toHaveBeenCalledTimes(1);
    buttons[1]?.click();
    expect(onOpenFolder).toHaveBeenCalledTimes(1);
  });

  it("opens the Repositories panel without rendering a repository input on Start", () => {
    const { host, onOpenRepositories } = ready();
    expect(host.querySelector("input")).toBeNull();
    expect(host.querySelector("form")).toBeNull();

    const button = Array.from(host.querySelectorAll<HTMLButtonElement>(".home-open")).find(
      (candidate) => candidate.textContent === "Open Repository",
    );
    button?.click();
    expect(onOpenRepositories).toHaveBeenCalledTimes(1);
  });

  it("lists the recent items and opens the clicked one", () => {
    const { host, home, onOpenItem } = ready();
    const folder: WorkspaceItem = { path: "C:\\a", label: "a", isFolder: true };
    const file: WorkspaceItem = { path: "C:\\a\\b.md", label: "b.md", isFolder: false };
    home.setRecents([folder, file]);

    expect(host.querySelector<HTMLElement>(".home-recents-empty")?.hidden).toBe(true);
    const rows = host.querySelectorAll<HTMLButtonElement>(".home-recent");
    expect(Array.from(rows).map((r) => r.querySelector(".home-recent-label")?.textContent)).toEqual(
      ["a", "b.md"],
    );

    rows[1]?.click();
    expect(onOpenItem).toHaveBeenCalledWith(file);
  });

  it("caps the list to a few items and restores the hint when empty", () => {
    const { host, home } = ready();
    const many: WorkspaceItem[] = Array.from({ length: 9 }, (_, i) => ({
      path: `C:\\f${i}.md`,
      label: `f${i}.md`,
      isFolder: false,
    }));
    home.setRecents(many);
    expect(host.querySelectorAll(".home-recent")).toHaveLength(6);

    home.setRecents([]);
    expect(host.querySelectorAll(".home-recent")).toHaveLength(0);
    expect(host.querySelector<HTMLElement>(".home-recents-empty")?.hidden).toBe(false);
  });
});
