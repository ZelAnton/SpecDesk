// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import type { WorkspaceItem, WorkspaceStatePayload } from "../../src/wire/protocol.js";
import {
  favoritesPanel,
  recentPanel,
  type WorkspaceListCallbacks,
} from "../../src/workspace/tools/workspace-list.js";

const FOLDER: WorkspaceItem = { path: "C:\\specs\\repo", label: "repo", isFolder: true };
const FILE: WorkspaceItem = {
  path: "C:\\specs\\repo\\intro.md",
  label: "intro.md",
  isFolder: false,
};
// `intro.md` is both recent AND a favorite; `repo` is recent but not a favorite.
const STATE: WorkspaceStatePayload = {
  recent: [FOLDER, FILE],
  favorites: [FILE],
  repositories: [],
};

function ready(kind: "recent" | "favorites") {
  const onOpen = vi.fn<(item: WorkspaceItem) => void>();
  const onToggleFavorite = vi.fn<(item: WorkspaceItem, favorite: boolean) => void>();
  const callbacks: WorkspaceListCallbacks = { onOpen, onToggleFavorite };
  const panel = kind === "recent" ? recentPanel(callbacks) : favoritesPanel(callbacks);
  const body = document.createElement("div");
  document.body.appendChild(body);
  panel.mount(body);
  return { panel, body, onOpen, onToggleFavorite };
}

describe("WorkspaceListPanel — Recent", () => {
  it("shows the empty hint until a state is set", () => {
    const { body } = ready("recent");
    expect(body.querySelector<HTMLElement>(".workspace-list-empty")?.hidden).toBe(false);
    expect(body.querySelector<HTMLElement>(".workspace-list-empty")?.textContent).toBe(
      "Files and folders you open will appear here.",
    );
    expect(body.querySelector<HTMLElement>(".workspace-list-items")?.hidden).toBe(true);
  });

  it("renders the recent rows and opens a folder vs a file with the right item", () => {
    const { panel, body, onOpen } = ready("recent");
    panel.setState(STATE);

    expect(body.querySelector<HTMLElement>(".workspace-list-empty")?.hidden).toBe(true);
    const rows = body.querySelectorAll<HTMLElement>(".workspace-list-row");
    expect(rows).toHaveLength(2);

    const opens = body.querySelectorAll<HTMLButtonElement>(".workspace-open");
    expect(
      Array.from(opens).map((o) => o.querySelector(".workspace-item-label")?.textContent),
    ).toEqual(["repo", "intro.md"]);
    // The full path is the tooltip (the label truncates in the narrow rail).
    expect(opens[0]?.title).toBe(FOLDER.path);

    opens[0]?.click();
    expect(onOpen).toHaveBeenCalledWith(FOLDER);
    opens[1]?.click();
    expect(onOpen).toHaveBeenCalledWith(FILE);
  });

  it("reflects each item's favorite state on the star and toggles it", () => {
    const { panel, body, onToggleFavorite } = ready("recent");
    panel.setState(STATE);
    const stars = body.querySelectorAll<HTMLButtonElement>(".workspace-star");

    // `repo` is not a favorite → the star is off (a stable "Favorite" name + aria-pressed=false; the
    // concrete action is on the tooltip), and clicking it ADDS the favorite.
    expect(stars[0]?.getAttribute("aria-pressed")).toBe("false");
    expect(stars[0]?.classList.contains("is-favorite")).toBe(false);
    expect(stars[0]?.getAttribute("aria-label")).toContain(`Favorite ${FOLDER.label}`);
    expect(stars[0]?.title).toBe("Add to favorites");
    stars[0]?.click();
    expect(onToggleFavorite).toHaveBeenCalledWith(FOLDER, true);

    // `intro.md` is a favorite → the star is filled (aria-pressed=true), and clicking it REMOVES it.
    expect(stars[1]?.getAttribute("aria-pressed")).toBe("true");
    expect(stars[1]?.classList.contains("is-favorite")).toBe(true);
    expect(stars[1]?.getAttribute("aria-label")).toContain(`Favorite ${FILE.label}`);
    expect(stars[1]?.title).toBe("Remove from favorites");
    stars[1]?.click();
    expect(onToggleFavorite).toHaveBeenCalledWith(FILE, false);
  });

  it("reflects favorite state case-insensitively (matching the host's Windows path identity)", () => {
    const { panel, body } = ready("recent");
    // The recent entry and the favorite name the same file with different casing (dialog vs tree-click).
    panel.setState({
      recent: [{ path: "C:\\Docs\\Spec.md", label: "Spec.md", isFolder: false }],
      favorites: [{ path: "c:\\docs\\spec.md", label: "spec.md", isFolder: false }],
      repositories: [],
    });
    const star = body.querySelector<HTMLButtonElement>(".workspace-star");
    expect(star?.getAttribute("aria-pressed")).toBe("true"); // still recognized as favorited
    expect(star?.classList.contains("is-favorite")).toBe(true);
  });

  it("keeps keyboard focus on the same star across a re-render", () => {
    const { panel, body } = ready("recent");
    panel.setState(STATE);
    const star = body.querySelectorAll<HTMLButtonElement>(".workspace-star")[0];
    star?.focus();
    expect(document.activeElement).toBe(star);

    panel.setState(STATE); // the host re-emits state (e.g. after a favorite toggle) → rebuild
    const active = document.activeElement as HTMLElement | null;
    expect(active).not.toBe(star); // rebuilt
    expect(active?.dataset.path).toBe(FOLDER.path);
    expect(active?.dataset.control).toBe("star");
  });
});

describe("WorkspaceListPanel — Favorites", () => {
  it("shows the empty hint until a state is set", () => {
    const { body } = ready("favorites");
    expect(body.querySelector<HTMLElement>(".workspace-list-empty")?.textContent).toBe(
      "Star a repository, file, or folder to keep it here.",
    );
  });

  it("renders the favorites and always removes on the trailing star", () => {
    const { panel, body, onOpen, onToggleFavorite } = ready("favorites");
    panel.setState(STATE);

    // Only the favorites list is shown (not the recent items).
    const opens = body.querySelectorAll<HTMLButtonElement>(".workspace-open");
    expect(
      Array.from(opens).map((o) => o.querySelector(".workspace-item-label")?.textContent),
    ).toEqual(["intro.md"]);
    opens[0]?.click();
    expect(onOpen).toHaveBeenCalledWith(FILE);

    const star = body.querySelector<HTMLButtonElement>(".workspace-star");
    expect(star?.getAttribute("aria-pressed")).toBe("true");
    expect(star?.getAttribute("aria-label")).toContain(`Favorite ${FILE.label}`);
    expect(star?.title).toBe("Remove from favorites");
    star?.click();
    expect(onToggleFavorite).toHaveBeenCalledWith(FILE, false);
  });

  it("removes a remote favorite with its complete stable identity", () => {
    const { panel, body, onToggleFavorite } = ready("favorites");
    const remote: WorkspaceItem = {
      path: "Docs/Guide.md",
      label: "Guide.md",
      isFolder: false,
      kind: "remote",
      repositoryId: "octo/spec",
      branch: "feature/Docs",
    };
    panel.setState({ recent: [], favorites: [remote], repositories: [] });

    body.querySelector<HTMLButtonElement>(".workspace-star")?.click();
    expect(onToggleFavorite).toHaveBeenCalledWith(remote, false);
  });

  it("distinguishes identical remote leaf paths and restores focus by typed identity", () => {
    const { panel, body } = ready("favorites");
    const first: WorkspaceItem = {
      path: "docs/README.md",
      label: "README.md",
      isFolder: false,
      kind: "remote",
      repositoryId: "octo/one",
      branch: "main",
    };
    const second: WorkspaceItem = { ...first, repositoryId: "octo/two", branch: "release" };
    const state = { recent: [], favorites: [first, second], repositories: [] };
    panel.setState(state);

    const opens = body.querySelectorAll<HTMLButtonElement>(".workspace-open");
    expect(opens[0]?.title).toContain("octo/one · main");
    expect(opens[1]?.title).toContain("octo/two · release");
    expect(opens[1]?.getAttribute("aria-label")).toContain("octo/two");
    const stars = body.querySelectorAll<HTMLButtonElement>(".workspace-star");
    expect(stars[0]?.getAttribute("aria-label")).toContain("octo/one");
    expect(stars[1]?.getAttribute("aria-label")).toContain("octo/two");
    stars[1]?.focus();
    panel.setState(state);

    expect((document.activeElement as HTMLElement | null)?.dataset.itemKey).toContain("octo/two");
  });

  it("renders repository favorites with repository semantics", () => {
    const { panel, body } = ready("favorites");
    panel.setState({
      recent: [],
      favorites: [
        {
          path: "octo/spec",
          label: "octo/spec",
          isFolder: true,
          kind: "repository",
          repositoryId: "octo/spec",
        },
      ],
      repositories: [],
    });

    expect(body.querySelector('.workspace-item-icon[data-kind="repository"]')).not.toBeNull();
    expect(body.querySelector(".workspace-item-context")?.textContent).toBe("Repository");
    expect(body.querySelector(".workspace-open")?.getAttribute("aria-label")).toBe(
      "Repository octo/spec",
    );
  });
});
