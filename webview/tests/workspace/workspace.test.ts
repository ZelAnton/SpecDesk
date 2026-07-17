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
        onNewSpec: vi.fn(),
        onOpenFolder: vi.fn(),
        onOpenItem: vi.fn(),
        onOutlineNavigate: vi.fn(),
      },
    );

    const labels = Array.from(rightDock.querySelectorAll<HTMLElement>(".dock-rail-btn")).map(
      (button) => button.getAttribute("aria-label"),
    );
    expect(labels).toEqual(["Assistant", "Versions", "Comments", "History"]);
  });

  it("places one bottom-panel toggle at the foot of the right rail and has no bottom rail", () => {
    document.body.innerHTML = `
      <main id="central"><section id="editor"></section></main>
      <aside id="bottom"></aside><aside id="right"></aside>
    `;
    const centralFrame = document.querySelector<HTMLElement>("#central");
    const editorView = document.querySelector<HTMLElement>("#editor");
    const bottomDock = document.querySelector<HTMLElement>("#bottom");
    const rightDock = document.querySelector<HTMLElement>("#right");
    if (centralFrame === null || editorView === null || bottomDock === null || rightDock === null) {
      throw new Error("workspace fixture is incomplete");
    }
    setupWorkspace(
      {
        centralFrame,
        editorView,
        homeView: null,
        notificationsView: null,
        docks: { left: null, right: rightDock, bottom: bottomDock },
      },
      new DockStore(null),
      {
        onCentreResize: vi.fn(),
        onCentralViewChange: vi.fn(),
        onOpenFile: vi.fn(),
        onNewSpec: vi.fn(),
        onOpenFolder: vi.fn(),
        onOpenItem: vi.fn(),
        onOutlineNavigate: vi.fn(),
      },
    );

    const toggle = rightDock.querySelector<HTMLButtonElement>('[data-action="bottom-panel"]');
    expect(toggle?.getAttribute("aria-label")).toBe("Bottom panel");
    expect(toggle?.getAttribute("aria-pressed")).toBe("false");
    expect(rightDock.querySelector(".dock-rail")?.lastElementChild).toBe(toggle);
    expect(bottomDock.hidden).toBe(true);
    expect(bottomDock.querySelector(".dock-rail")).toBeNull();
    toggle?.click();
    expect(bottomDock.hidden).toBe(false);
    expect(toggle?.getAttribute("aria-pressed")).toBe("true");
    toggle?.click();
    expect(bottomDock.hidden).toBe(true);
    expect(toggle?.getAttribute("aria-pressed")).toBe("false");
  });
});

describe("workspace left toolbar", () => {
  it("starts collapsed with Navigator selected even when another mode was persisted", () => {
    document.body.innerHTML = `
      <main id="central" data-view="home"><section id="editor"></section><section id="home"></section></main>
      <aside id="left"></aside>
    `;
    const centralFrame = document.querySelector<HTMLElement>("#central");
    const editorView = document.querySelector<HTMLElement>("#editor");
    const homeView = document.querySelector<HTMLElement>("#home");
    const leftDock = document.querySelector<HTMLElement>("#left");
    if (centralFrame === null || editorView === null || homeView === null || leftDock === null) {
      throw new Error("workspace fixture is incomplete");
    }
    const storage = {
      getItem: vi.fn(() =>
        JSON.stringify({
          left: { open: true, size: 260, mode: "repositories" },
          right: { open: false, size: 320, mode: "assistant" },
          bottom: { open: false, size: 220, mode: "log" },
        }),
      ),
      setItem: vi.fn(),
      removeItem: vi.fn(),
      clear: vi.fn(),
      key: vi.fn(),
      length: 1,
    } satisfies Storage;

    setupWorkspace(
      {
        centralFrame,
        editorView,
        homeView,
        notificationsView: null,
        docks: { left: leftDock, right: null, bottom: null },
      },
      new DockStore(storage),
      {
        onCentreResize: vi.fn(),
        onCentralViewChange: vi.fn(),
        onOpenFile: vi.fn(),
        onNewSpec: vi.fn(),
        onOpenFolder: vi.fn(),
        onOpenItem: vi.fn(),
        onOutlineNavigate: vi.fn(),
      },
    );

    const navigator = leftDock.querySelector<HTMLButtonElement>(
      '.dock-rail-btn[aria-label="Navigator"]',
    );
    const repositories = leftDock.querySelector<HTMLButtonElement>(
      '.dock-rail-btn[aria-label="Repositories"]',
    );
    expect(leftDock.classList.contains("dock--collapsed")).toBe(true);
    expect(navigator?.getAttribute("aria-checked")).toBe("true");
    expect(navigator?.getAttribute("aria-expanded")).toBe("false");
    expect(repositories?.getAttribute("aria-checked")).toBe("false");
    navigator?.click();
    expect(leftDock.classList.contains("dock--collapsed")).toBe(false);
    expect(navigator?.getAttribute("aria-expanded")).toBe("true");
  });

  it("keeps global manager modes, nests history, and hides Outline until a document exists", () => {
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
        onNewSpec: vi.fn(),
        onOpenFolder: vi.fn(),
        onOpenItem: vi.fn(),
        onOutlineNavigate: vi.fn(),
      },
    );

    const labels = Array.from(
      leftDock.querySelectorAll<HTMLElement>(".dock-rail-btn:not([hidden])"),
    ).map((button) => button.getAttribute("aria-label"));
    expect(labels).toEqual(["Navigator", "Repositories", "Change requests", "Disk", "Search"]);
    const navigatorPanel = leftDock.querySelector('.dock-tool[data-tool="navigator"]');
    const prsPanel = leftDock.querySelector('.dock-tool[data-tool="prs"]');
    expect(navigatorPanel?.textContent).toContain("Favorites");
    expect(navigatorPanel?.textContent).toContain("History");
    expect(navigatorPanel?.textContent).not.toContain("GO TO");
    expect(navigatorPanel?.textContent).not.toContain("Document");
    expect(prsPanel?.textContent).toContain("Needs your review");
    expect(prsPanel?.textContent).toContain("Your change requests");
  });

  it("adds contextual Outline as the final mode for an active document", () => {
    document.body.innerHTML = `
      <main id="central"><section id="editor"></section><section id="home"></section></main>
      <aside id="left"></aside>
    `;
    const centralFrame = document.querySelector<HTMLElement>("#central");
    const editorView = document.querySelector<HTMLElement>("#editor");
    const homeView = document.querySelector<HTMLElement>("#home");
    const leftDock = document.querySelector<HTMLElement>("#left");
    if (centralFrame === null || editorView === null || homeView === null || leftDock === null) {
      throw new Error("workspace fixture is incomplete");
    }
    const handle = setupWorkspace(
      {
        centralFrame,
        editorView,
        homeView,
        notificationsView: null,
        docks: { left: leftDock, right: null, bottom: null },
      },
      new DockStore(null),
      {
        onCentreResize: vi.fn(),
        onCentralViewChange: vi.fn(),
        onOpenFile: vi.fn(),
        onNewSpec: vi.fn(),
        onOpenFolder: vi.fn(),
        onOpenItem: vi.fn(),
        onOutlineNavigate: vi.fn(),
      },
    );
    handle.setActiveContext({
      repository: null,
      branch: null,
      pullRequest: null,
      file: { kind: "file", path: "C:\\notes\\spec.md", type: "markdown", repository: null },
    });
    const labels = Array.from(
      leftDock.querySelectorAll<HTMLElement>(".dock-rail-btn:not([hidden])"),
    ).map((button) => button.getAttribute("aria-label"));
    expect(labels).toEqual([
      "Navigator",
      "Repositories",
      "Change requests",
      "Disk",
      "Search",
      "Outline",
    ]);
    leftDock.querySelector<HTMLButtonElement>('.dock-rail-btn[aria-label="Outline"]')?.click();
    expect(centralFrame.dataset.activeView).toBeUndefined();
    expect(leftDock.querySelector('.dock-tool[data-tool="editor"] .outline-empty')).not.toBeNull();
  });
});

describe("workspace context panels", () => {
  it("shows contextual layers above the editor and routes them to the matching left modes", () => {
    document.body.innerHTML = `
      <main id="central" data-view="editor">
        <nav id="contexts" hidden>
          <button class="context-panel" data-context="repository"><span id="current-repository"></span><span id="current-branch"></span></button>
          <button class="context-panel" data-context="local"><span id="current-local-path"></span></button>
          <div class="context-panel" data-context="file"><span id="current-path"></span></div>
          <button class="context-panel" data-context="pull-request"><span id="current-pull-request"></span></button>
        </nav>
        <section id="editor"></section><section id="home"></section>
      </main>
      <aside id="left"></aside>
    `;
    const centralFrame = document.querySelector<HTMLElement>("#central");
    const editorView = document.querySelector<HTMLElement>("#editor");
    const homeView = document.querySelector<HTMLElement>("#home");
    const contextPanels = document.querySelector<HTMLElement>("#contexts");
    const leftDock = document.querySelector<HTMLElement>("#left");
    if (
      centralFrame === null ||
      editorView === null ||
      homeView === null ||
      contextPanels === null ||
      leftDock === null
    ) {
      throw new Error("workspace fixture is incomplete");
    }
    const handle = setupWorkspace(
      {
        centralFrame,
        editorView,
        homeView,
        notificationsView: null,
        contextPanels,
        docks: { left: leftDock, right: null, bottom: null },
      },
      new DockStore(null),
      {
        onCentreResize: vi.fn(),
        onCentralViewChange: vi.fn(),
        onOpenFile: vi.fn(),
        onNewSpec: vi.fn(),
        onOpenFolder: vi.fn(),
        onOpenItem: vi.fn(),
        onOutlineNavigate: vi.fn(),
      },
    );
    handle.centralFrame.show("editor");
    handle.setActiveContext({
      repository: {
        kind: "repository",
        id: "acme/specs",
        root: "C:\\work\\specs",
        defaultBranch: "main",
      },
      branch: {
        kind: "branch",
        repository: {
          kind: "repository",
          id: "acme/specs",
          root: "C:\\work\\specs",
          defaultBranch: "main",
        },
        name: "review/navigation",
      },
      pullRequest: null,
      file: {
        kind: "file",
        path: "C:\\work\\specs\\guides\\intro.md",
        type: "markdown",
        repository: {
          kind: "repository",
          id: "acme/specs",
          root: "C:\\work\\specs",
          defaultBranch: "main",
        },
      },
    });

    expect(contextPanels.hidden).toBe(false);
    expect(contextPanels.querySelector("#current-repository")?.textContent).toBe("acme/specs");
    expect(contextPanels.querySelector("#current-branch")?.textContent).toBe("review/navigation");
    expect(contextPanels.querySelector("#current-local-path")?.textContent).toBe("C:\\work\\specs");
    expect(contextPanels.querySelector("#current-path")?.textContent).toContain("intro.md");
    expect(contextPanels.textContent).not.toContain("No document");
    contextPanels.querySelector<HTMLButtonElement>('[data-context="local"]')?.click();
    expect(
      leftDock.querySelector('.dock-tool[data-tool="files"]')?.getAttribute("hidden"),
    ).toBeNull();
    contextPanels.querySelector<HTMLButtonElement>('[data-context="repository"]')?.click();
    expect(
      leftDock.querySelector('.dock-tool[data-tool="repositories"]')?.getAttribute("hidden"),
    ).toBeNull();
    handle.setActiveContext({
      repository: {
        kind: "repository",
        id: "acme/specs",
        root: null,
        defaultBranch: "main",
      },
      branch: {
        kind: "branch",
        repository: {
          kind: "repository",
          id: "acme/specs",
          root: null,
          defaultBranch: "main",
        },
        name: "review/navigation",
      },
      pullRequest: {
        kind: "pullRequest",
        branch: {
          kind: "branch",
          repository: {
            kind: "repository",
            id: "acme/specs",
            root: null,
            defaultBranch: "main",
          },
          name: "review/navigation",
        },
      },
      file: null,
    });
    contextPanels.querySelector<HTMLButtonElement>('[data-context="pull-request"]')?.click();
    expect(
      leftDock.querySelector('.dock-tool[data-tool="prs"]')?.getAttribute("hidden"),
    ).toBeNull();
    expect(contextPanels.querySelector<HTMLElement>('[data-context="file"]')?.hidden).toBe(true);
    handle.centralFrame.show("home");
    expect(contextPanels.hidden).toBe(true);
  });
});
