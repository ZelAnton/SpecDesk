// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { DockStore } from "../../src/workspace/dock-store.js";
import { setupWorkspace } from "../../src/workspace/workspace.js";

describe("workspace right toolbar", () => {
  it("puts Assistant before every context tool", () => {
    document.body.innerHTML = `
      <main id="central"><section id="editor"></section></main>
      <aside id="right"></aside>
    `;
    const centralFrame = document.querySelector<HTMLElement>("#central");
    const editorView = document.querySelector<HTMLElement>("#editor");
    const rightDock = document.querySelector<HTMLElement>("#right");
    if (centralFrame === null || editorView === null || rightDock === null) {
      throw new Error("workspace fixture is incomplete");
    }

    setupWorkspace(
      {
        centralFrame,
        editorView,
        homeView: null,
        notificationsView: null,
        docks: { left: null, right: rightDock, bottom: null },
      },
      new DockStore(null),
      {
        onCentreResize: vi.fn(),
        onCentralViewChange: vi.fn(),
        onOpenFile: vi.fn(),
        onOpenFolder: vi.fn(),
        onOpenItem: vi.fn(),
        onOutlineNavigate: vi.fn(),
      },
    );

    const labels = Array.from(rightDock.querySelectorAll<HTMLElement>(".dock-rail-btn")).map(
      (button) => button.getAttribute("aria-label"),
    );
    expect(labels).toEqual(["Assistant", "Outline", "Versions", "Comments", "History"]);
  });
});

describe("workspace left toolbar", () => {
  it("keeps four manager-focused modes and nests favorites, history, and PR views", () => {
    document.body.innerHTML = `
      <main id="central"><section id="editor"></section></main>
      <aside id="left"></aside>
    `;
    const centralFrame = document.querySelector<HTMLElement>("#central");
    const editorView = document.querySelector<HTMLElement>("#editor");
    const leftDock = document.querySelector<HTMLElement>("#left");
    if (centralFrame === null || editorView === null || leftDock === null) {
      throw new Error("workspace fixture is incomplete");
    }

    setupWorkspace(
      {
        centralFrame,
        editorView,
        homeView: null,
        notificationsView: null,
        docks: { left: leftDock, right: null, bottom: null },
      },
      new DockStore(null),
      {
        onCentreResize: vi.fn(),
        onCentralViewChange: vi.fn(),
        onOpenFile: vi.fn(),
        onOpenFolder: vi.fn(),
        onOpenItem: vi.fn(),
        onOutlineNavigate: vi.fn(),
      },
    );

    const labels = Array.from(leftDock.querySelectorAll<HTMLElement>(".dock-rail-btn")).map(
      (button) => button.getAttribute("aria-label"),
    );
    expect(labels).toEqual(["Navigator", "Repositories", "Folders", "PRs"]);
    const navigatorPanel = leftDock.querySelector('.dock-tool[data-tool="navigator"]');
    const prsPanel = leftDock.querySelector('.dock-tool[data-tool="prs"]');
    expect(navigatorPanel?.textContent).toContain("Favorites");
    expect(navigatorPanel?.textContent).toContain("History");
    expect(prsPanel?.textContent).toContain("Needs your review");
    expect(prsPanel?.textContent).toContain("Your pull requests");
  });
});
