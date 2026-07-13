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
    expect(labels).toEqual(["Assistant", "Outline", "Versions", "Comments", "Change history"]);
  });
});
