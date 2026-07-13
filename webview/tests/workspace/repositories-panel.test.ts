// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { RegisteredRepo, WorkspaceStatePayload } from "../../src/wire/protocol.js";
import { RepositoriesPanel } from "../../src/workspace/tools/repositories-panel.js";

const REPO: RegisteredRepo = {
  id: "acme/specs",
  name: "acme/specs",
  url: "https://github.com/acme/specs",
  defaultBranch: "main",
  clones: [{ id: "acme-specs", path: "C:\\repos\\acme-specs", branches: ["draft"] }],
};
const STATE: WorkspaceStatePayload = { recent: [], favorites: [], repositories: [REPO] };

beforeEach(() => {
  window.localStorage.clear();
});

function ready() {
  const onCloneManaged = vi.fn<(url: string, destinationPath: string) => void>();
  const onCloneToFolder = vi.fn<(url: string) => void>();
  const onDestinationRequest = vi.fn<(url: string, requestId: number) => void>();
  const onDescriptionRequest = vi.fn<(url: string, requestId: number) => void>();
  const onUnregister = vi.fn<(id: string) => void>();
  const onBrowseRepo = vi.fn<(repo: RegisteredRepo) => void>();
  const onOpenClone = vi.fn<(repo: RegisteredRepo, path: string) => void>();
  const onClone = vi.fn<(repo: RegisteredRepo) => void>();
  const onToggleFavorite = vi.fn<(repo: RegisteredRepo, favorite: boolean) => void>();
  const panel = new RepositoriesPanel({
    onCloneManaged,
    onCloneToFolder,
    onDestinationRequest,
    onDescriptionRequest,
    onUnregister,
    onBrowseRepo,
    onOpenClone,
    onClone,
    onToggleFavorite,
  });
  const body = document.createElement("div");
  document.body.appendChild(body);
  panel.mount(body);
  const input = body.querySelector<HTMLInputElement>(".repo-register-input");
  const add = body.querySelector<HTMLButtonElement>(".repo-register-add");
  if (!input || !add) {
    throw new Error("repositories panel did not mount its register form");
  }
  return {
    panel,
    body,
    input,
    add,
    onCloneManaged,
    onCloneToFolder,
    onDestinationRequest,
    onDescriptionRequest,
    onUnregister,
    onBrowseRepo,
    onOpenClone,
    onClone,
    onToggleFavorite,
  };
}

function resolveDescription(
  panel: RepositoriesPanel,
  url: string,
  requestId = 0,
  state: "found" | "private" = "found",
): void {
  panel.setDescription({ url, requestId, state, description: "Repository description" });
}

describe("RepositoriesPanel", () => {
  it("shows the empty hint until repositories are set", () => {
    const { body } = ready();
    expect(body.querySelector<HTMLElement>(".repo-empty")?.hidden).toBe(false);
    expect(body.querySelector<HTMLElement>(".repo-empty")?.textContent).toBe(
      "Register a repository to keep it handy.",
    );
    expect(body.querySelector<HTMLElement>(".repo-list")?.hidden).toBe(true);
  });

  it("clones the typed repo to managed storage and clears the field", () => {
    const { panel, body, input, add, onCloneManaged } = ready();
    input.value = "  owner/name  ";
    panel.setManagedDestination({
      url: "owner/name",
      requestId: 0,
      path: "C:\\managed\\owner_name",
    });
    resolveDescription(panel, "owner/name");
    add.click();
    body.querySelector<HTMLButtonElement>('[role="menuitem"]')?.click();
    body.querySelector<HTMLButtonElement>(".repo-clone-confirm-yes")?.click();
    expect(onCloneManaged).toHaveBeenCalledWith("owner/name", "C:\\managed\\owner_name");
    expect(input.value).toBe(""); // cleared for the next entry
  });

  it("ignores a blank register submit", () => {
    const { input, add, onCloneManaged, onCloneToFolder } = ready();
    input.value = "   ";
    add.click();
    expect(onCloneManaged).not.toHaveBeenCalled();
    expect(onCloneToFolder).not.toHaveBeenCalled();
  });

  it("suggests full owner/name values while matching the repository segment", () => {
    const { panel, body, input, onCloneManaged, onCloneToFolder } = ready();
    panel.setSuggestions([
      { fullName: "acme/specifications" },
      { fullName: "octocat/notes" },
      { fullName: "ACME/SPECIFICATIONS" },
    ]);
    input.value = "spec";
    input.dispatchEvent(new Event("input"));

    const options = body.querySelectorAll<HTMLElement>('[role="option"]');
    expect(options).toHaveLength(1);
    expect(options[0]?.textContent).toBe("acme/specifications");
    expect(input.getAttribute("aria-expanded")).toBe("true");
    expect(input.getAttribute("aria-activedescendant")).toBe(options[0]?.id);

    input.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
    expect(input.value).toBe("acme/specifications");
    expect(onCloneManaged).not.toHaveBeenCalled();
    expect(onCloneToFolder).not.toHaveBeenCalled();
    expect(input.getAttribute("aria-expanded")).toBe("false");
  });

  it("supports arrow navigation and mouse selection without blocking arbitrary input", () => {
    const { panel, body, input, onCloneManaged, onCloneToFolder } = ready();
    panel.setSuggestions([{ fullName: "acme/specs" }, { fullName: "octo/specs" }]);
    input.value = "specs";
    input.dispatchEvent(new Event("input"));
    input.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true }));
    expect(body.querySelector('[aria-selected="true"]')?.textContent).toBe("octo/specs");

    const first = body.querySelector<HTMLElement>('[role="option"]');
    first?.dispatchEvent(new MouseEvent("mousedown", { bubbles: true }));
    expect(input.value).toBe("acme/specs");
    expect(onCloneManaged).not.toHaveBeenCalled();
    expect(onCloneToFolder).not.toHaveBeenCalled();

    input.value = "outside/public";
    input.dispatchEvent(new Event("input"));
    panel.setManagedDestination({
      url: "outside/public",
      requestId: 3,
      path: "C:\\managed\\outside_public",
    });
    resolveDescription(panel, "outside/public", 3);
    body.querySelector<HTMLButtonElement>(".repo-register-add")?.click();
    body.querySelector<HTMLButtonElement>('[role="menuitem"]')?.click();
    body.querySelector<HTMLButtonElement>(".repo-clone-confirm-yes")?.click();
    expect(onCloneManaged).toHaveBeenCalledWith("outside/public", "C:\\managed\\outside_public");
  });

  it("identifies a valid public owner/repository outside the connected account list", () => {
    const { panel, body, input, add, onCloneManaged } = ready();
    panel.setSuggestions([{ fullName: "acme/specs" }]);
    input.value = "outside/public-specs";
    input.dispatchEvent(new Event("input"));

    expect(body.querySelector<HTMLElement>(".repo-public-hint")?.hidden).toBe(false);
    expect(body.querySelector<HTMLElement>(".repo-public-hint")?.textContent).toContain(
      "you can still use a public",
    );
    panel.setManagedDestination({
      url: "outside/public-specs",
      requestId: 1,
      path: "C:\\managed\\outside_public-specs",
    });
    resolveDescription(panel, "outside/public-specs", 1);
    add.click();
    body.querySelector<HTMLButtonElement>('[role="menuitem"]')?.click();
    body.querySelector<HTMLButtonElement>(".repo-clone-confirm-yes")?.click();
    expect(onCloneManaged).toHaveBeenCalledWith(
      "outside/public-specs",
      "C:\\managed\\outside_public-specs",
    );
  });

  it("offers both clone destinations and suppresses double submission", () => {
    const { panel, body, input, add, onCloneManaged, onCloneToFolder } = ready();
    input.value = "owner/managed";
    panel.setManagedDestination({
      url: "owner/managed",
      requestId: 0,
      path: "C:\\managed\\owner_managed",
    });
    resolveDescription(panel, "owner/managed");
    add.click();
    const actions = body.querySelectorAll<HTMLButtonElement>('[role="menuitem"]');
    expect([...actions].map((action) => action.textContent)).toEqual([
      "Clone…",
      "Clone to folder…",
    ]);
    actions[0]?.click();
    actions[0]?.click();
    const yes = body.querySelector<HTMLButtonElement>(".repo-clone-confirm-yes");
    yes?.click();
    yes?.click();
    expect(onCloneManaged).toHaveBeenCalledTimes(1);

    input.value = "owner/folder";
    input.dispatchEvent(new Event("input"));
    resolveDescription(panel, "owner/folder", 2);
    add.click();
    actions[1]?.click();
    actions[1]?.click();
    yes?.click();
    yes?.click();
    expect(onCloneToFolder).toHaveBeenCalledTimes(1);
    expect(onCloneToFolder).toHaveBeenCalledWith("owner/folder");
  });

  it("requires Yes and keeps the input unchanged when No is chosen", () => {
    const { panel, body, input, add, onCloneManaged } = ready();
    input.value = "owner/cancelled";
    panel.setManagedDestination({
      url: "owner/cancelled",
      requestId: 0,
      path: "C:\\managed\\owner_cancelled",
    });
    resolveDescription(panel, "owner/cancelled");
    add.click();
    body.querySelector<HTMLButtonElement>('[role="menuitem"]')?.click();
    const confirmation = body.querySelector<HTMLElement>(".repo-clone-confirmation");
    const form = body.querySelector<HTMLFormElement>(".repo-register");
    const list = body.querySelector<HTMLElement>(".repo-list");
    const yes = body.querySelector<HTMLButtonElement>(".repo-clone-confirm-yes");
    expect(confirmation?.hidden).toBe(false);
    expect(confirmation?.textContent).toContain("C:\\managed\\owner_cancelled");
    expect(form?.inert).toBe(true);
    expect(list?.inert).toBe(true);
    expect(document.activeElement).toBe(yes);
    body.querySelector<HTMLButtonElement>(".repo-clone-confirm-actions button")?.click();

    expect(onCloneManaged).not.toHaveBeenCalled();
    expect(input.value).toBe("owner/cancelled");
    expect(confirmation?.hidden).toBe(true);
    expect(form?.inert).toBe(false);
    expect(list?.inert).toBe(false);
    expect(document.activeElement).toBe(add);
  });

  it("persists Do not show again and skips later confirmations", () => {
    const first = ready();
    first.input.value = "owner/first";
    first.panel.setManagedDestination({
      url: "owner/first",
      requestId: 0,
      path: "C:\\managed\\owner_first",
    });
    resolveDescription(first.panel, "owner/first");
    first.add.click();
    first.body.querySelector<HTMLButtonElement>('[role="menuitem"]')?.click();
    const skip = first.body.querySelector<HTMLInputElement>(".repo-clone-confirm-skip input");
    if (skip) {
      skip.checked = true;
    }
    first.body.querySelector<HTMLButtonElement>(".repo-clone-confirm-yes")?.click();
    expect(first.onCloneManaged).toHaveBeenCalledTimes(1);

    const second = ready();
    second.input.value = "owner/second";
    resolveDescription(second.panel, "owner/second");
    second.add.click();
    second.body.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')[1]?.click();
    expect(second.onCloneToFolder).toHaveBeenCalledWith("owner/second");
    expect(second.body.querySelector<HTMLElement>(".repo-clone-confirmation")?.hidden).toBe(true);
  });

  it("shows the exact managed destination and ignores stale destination responses", () => {
    const { panel, body, input } = ready();
    input.value = "owner/first";
    input.dispatchEvent(new Event("input"));
    input.value = "owner/current";
    input.dispatchEvent(new Event("input"));

    panel.setManagedDestination({
      url: "owner/first",
      requestId: 1,
      path: "C:\\managed\\owner_first",
    });
    expect(body.querySelector(".repo-managed-destination")?.textContent).toContain("checking");

    panel.setManagedDestination({
      url: "owner/current",
      requestId: 2,
      path: "C:\\managed\\owner_current",
    });
    resolveDescription(panel, "owner/current", 2);
    const destination = body.querySelector<HTMLElement>(".repo-managed-destination");
    expect(destination?.textContent).toBe("Managed destination: C:\\managed\\owner_current");
    expect(destination?.title).toBe("C:\\managed\\owner_current");
    expect(body.querySelector<HTMLButtonElement>('[role="menuitem"]')?.disabled).toBe(false);
  });

  it("debounces description requests and ignores stale responses", () => {
    vi.useFakeTimers();
    try {
      const { panel, body, input, add, onDescriptionRequest } = ready();
      input.value = "owner/old";
      input.dispatchEvent(new Event("input"));
      input.value = "owner/current";
      input.dispatchEvent(new Event("input"));

      expect(body.querySelector(".repo-description")?.textContent).toContain("loading");
      expect(add.disabled).toBe(true);
      vi.advanceTimersByTime(219);
      expect(onDescriptionRequest).not.toHaveBeenCalled();
      vi.advanceTimersByTime(1);
      expect(onDescriptionRequest).toHaveBeenCalledOnce();
      expect(onDescriptionRequest).toHaveBeenCalledWith("owner/current", 2);

      panel.setDescription({
        url: "owner/old",
        requestId: 1,
        state: "found",
        description: "Stale description",
      });
      expect(body.querySelector(".repo-description")?.textContent).toContain("loading");
      panel.setDescription({
        url: "owner/current",
        requestId: 2,
        state: "found",
        description: "Current description",
      });
      expect(body.querySelector(".repo-description")?.textContent).toBe(
        "Description: Current description",
      );
      expect(add.disabled).toBe(false);
    } finally {
      vi.useRealTimers();
    }
  });

  it("shows private, not-found, and error description states explicitly", () => {
    const { panel, body, input, add } = ready();
    input.value = "owner/repository";

    panel.setDescription({
      url: "owner/repository",
      requestId: 0,
      state: "private",
      description: "Internal specifications",
    });
    expect(body.querySelector(".repo-description")?.textContent).toBe(
      "Private repository · Description: Internal specifications",
    );
    expect(add.disabled).toBe(false);

    panel.setDescription({ url: "owner/repository", requestId: 0, state: "notFound" });
    expect(body.querySelector(".repo-description")?.textContent).toContain("not found");
    expect(add.disabled).toBe(true);

    panel.setDescription({ url: "owner/repository", requestId: 0, state: "error" });
    expect(body.querySelector(".repo-description")?.textContent).toContain("Couldn’t load");
    expect(add.disabled).toBe(true);
  });

  it("does not claim that an unverified owner/repository is public", () => {
    const { panel, body, input } = ready();
    panel.setSuggestions([]);
    input.value = "unknown/maybe-private";
    input.dispatchEvent(new Event("input"));

    const hint = body.querySelector<HTMLElement>(".repo-public-hint");
    expect(hint?.hidden).toBe(false);
    expect(hint?.textContent).toContain("Not in your suggestions");
    expect(hint?.textContent?.startsWith("Public repository")).toBe(false);
  });

  it("renders repo rows: clicking opens the repo, the trailing × removes it", () => {
    const { panel, body, onUnregister, onBrowseRepo } = ready();
    panel.setState(STATE);

    expect(body.querySelector<HTMLElement>(".repo-empty")?.hidden).toBe(true);
    const open = body.querySelector<HTMLButtonElement>(".repo-open");
    expect(open?.textContent).toBe("acme/specs");
    expect(open?.title).toBe(REPO.url);
    open?.click();
    expect(onBrowseRepo).toHaveBeenCalledWith(REPO);

    const remove = body.querySelector<HTMLButtonElement>(".repo-remove");
    expect(remove?.getAttribute("aria-label")).toBe("Remove repository acme/specs");
    remove?.click();
    expect(onUnregister).toHaveBeenCalledWith("acme/specs");
  });

  it("renders local copies and non-default branches as a nested tree", () => {
    const { panel, body, onOpenClone, onClone } = ready();
    panel.setState(STATE);

    expect(body.querySelector(".repo-clone-open")?.textContent).toBe("acme-specs");
    expect(body.querySelector(".repo-branches li")?.textContent).toBe("draft");
    body.querySelector<HTMLButtonElement>(".repo-clone-open")?.click();
    expect(onOpenClone).toHaveBeenCalledWith(REPO, "C:\\repos\\acme-specs");
    body.querySelector<HTMLButtonElement>(".repo-clone-action")?.click();
    expect(onClone).toHaveBeenCalledWith(REPO);
    expect(body.querySelector(".repo-clone-action")?.getAttribute("aria-label")).toContain(
      "acme/specs",
    );
  });

  it("toggles the repository star and preserves its keyboard focus after state refresh", () => {
    const { panel, body, onToggleFavorite } = ready();
    panel.setState(STATE);
    const star = body.querySelector<HTMLButtonElement>(".repo-star");
    expect(star?.getAttribute("aria-pressed")).toBe("false");
    star?.click();
    expect(onToggleFavorite).toHaveBeenCalledWith(REPO, true);

    star?.focus();
    panel.setState({
      ...STATE,
      favorites: [
        {
          path: REPO.id,
          label: REPO.name,
          isFolder: true,
          kind: "repository",
          repositoryId: REPO.id,
        },
      ],
    });
    const refreshed = body.querySelector<HTMLButtonElement>(".repo-star");
    expect(refreshed?.getAttribute("aria-pressed")).toBe("true");
    expect(document.activeElement).toBe(refreshed);
  });
});
