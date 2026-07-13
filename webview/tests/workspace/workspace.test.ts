// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { DockStore } from "../../src/workspace/dock-store.js";
import { setupWorkspace } from "../../src/workspace/workspace.js";

describe("workspace Start actions", () => {
  it("reveals the Repositories mode without leaving the Start central view", () => {
    document.body.innerHTML = `
      <main id="central-frame" data-view="editor">
        <div id="editor-view"></div>
        <div id="home-view"></div>
      </main>
      <div id="left-dock"></div>
      <div id="right-dock"></div>
      <div id="bottom-dock"></div>
    `;
    const byId = (id: string): HTMLElement => {
      const el = document.getElementById(id);
      if (el === null) {
        throw new Error(`missing #${id}`);
      }
      return el;
    };

    const workspace = setupWorkspace(
      {
        centralFrame: byId("central-frame"),
        editorView: byId("editor-view"),
        homeView: byId("home-view"),
        notificationsView: null,
        docks: {
          left: byId("left-dock"),
          right: byId("right-dock"),
          bottom: byId("bottom-dock"),
        },
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
    workspace.centralFrame.show("home");

    const open = Array.from(
      byId("home-view").querySelectorAll<HTMLButtonElement>(".home-open"),
    ).find((button) => button.textContent === "Open Repository");
    open?.click();

    expect(workspace.centralFrame.active()).toBe("home");
    expect(byId("left-dock").classList.contains("dock--collapsed")).toBe(false);
    const mode = byId("left-dock").querySelector<HTMLButtonElement>(
      '.dock-rail-btn[aria-label="Repositories"]',
    );
    expect(mode?.getAttribute("aria-checked")).toBe("true");
    expect(mode?.getAttribute("aria-expanded")).toBe("true");
    expect(byId("left-dock").querySelector<HTMLElement>('[data-tool="repositories"]')?.hidden).toBe(
      false,
    );
  });
});
