// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { RegisteredRepo, WorkspaceStatePayload } from "../../src/wire/protocol.js";
import { RepositoriesPanel } from "../../src/workspace/tools/repositories-panel.js";

const CLEAN_STATUS = {
  ahead: 0,
  behind: 0,
  hasUncommitted: false,
  stashCount: 0,
  hasConflicts: false,
};
const REPO: RegisteredRepo = {
  id: "acme/specs",
  name: "acme/specs",
  url: "https://github.com/acme/specs",
  defaultBranch: "main",
  clones: [
    {
      id: "acme-specs",
      path: "C:\\repos\\acme-specs",
      currentBranch: "main",
      status: CLEAN_STATUS,
      branches: [
        { name: "main", status: CLEAN_STATUS, canDelete: false, canRename: false },
        { name: "draft", status: CLEAN_STATUS, canDelete: true, canRename: true },
      ],
    },
  ],
};
const STATE: WorkspaceStatePayload = { recent: [], favorites: [], repositories: [REPO] };

beforeEach(() => {
  window.localStorage.clear();
});

function ready() {
  const onCloneManaged = vi.fn<(url: string, localName: string, destinationPath: string) => void>();
  const onCloneToFolder = vi.fn<(url: string, localName: string) => void>();
  const onDestinationRequest = vi.fn<(url: string, localName: string, requestId: number) => void>();
  const onDescriptionRequest = vi.fn<(url: string, requestId: number) => void>();
  const onUnregister = vi.fn<(id: string) => void>();
  const onBrowseRepo = vi.fn<(repo: RegisteredRepo) => void>();
  const onOpenClone = vi.fn<(repo: RegisteredRepo, path: string) => void>();
  const onSwitchBranch = vi.fn<(repo: RegisteredRepo, path: string, branch: string) => void>();
  const onCreateBranch = vi.fn<(repo: RegisteredRepo, path: string, branch: string) => void>();
  const onRenameClone = vi.fn<(repo: RegisteredRepo, path: string, localName: string) => void>();
  const onRenameBranch =
    vi.fn<(repo: RegisteredRepo, path: string, branch: string, newBranch: string) => void>();
  const onOpenExistingClone = vi.fn<(url: string, path: string) => void>();
  const onToggleFavorite = vi.fn<(repo: RegisteredRepo, favorite: boolean) => void>();
  const onToggleCloneFavorite =
    vi.fn<(repo: RegisteredRepo, path: string, favorite: boolean) => void>();
  const onToggleBranchFavorite =
    vi.fn<(repo: RegisteredRepo, path: string, branch: string, favorite: boolean) => void>();
  const onDeleteClone =
    vi.fn<(repo: RegisteredRepo, path: string, confirmationToken?: string) => void>();
  const onDeleteBranch =
    vi.fn<
      (repo: RegisteredRepo, path: string, branch: string, confirmationToken?: string) => void
    >();
  const onRefresh = vi.fn<(requestId: number) => void>();
  const onPull = vi.fn<(repo: RegisteredRepo, path: string, branch: string) => void>();
  const onPush = vi.fn<(repo: RegisteredRepo, path: string, branch: string) => void>();
  const panel = new RepositoriesPanel({
    onCloneManaged,
    onCloneToFolder,
    onDestinationRequest,
    onDescriptionRequest,
    onUnregister,
    onBrowseRepo,
    onOpenClone,
    onSwitchBranch,
    onCreateBranch,
    onRenameClone,
    onRenameBranch,
    onOpenExistingClone,
    onToggleFavorite,
    onToggleCloneFavorite,
    onToggleBranchFavorite,
    onDeleteClone,
    onDeleteBranch,
    onRefresh,
    onPull,
    onPush,
  });
  const body = document.createElement("div");
  document.body.appendChild(body);
  panel.mount(body);
  const input = body.querySelector<HTMLInputElement>(".repo-register-input");
  const add = body.querySelector<HTMLButtonElement>(".repo-clone-primary");
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
    onSwitchBranch,
    onCreateBranch,
    onRenameClone,
    onRenameBranch,
    onOpenExistingClone,
    onToggleFavorite,
    onToggleCloneFavorite,
    onToggleBranchFavorite,
    onDeleteClone,
    onDeleteBranch,
    onRefresh,
    onPull,
    onPush,
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
      "Add a repository to keep it ready.",
    );
    expect(body.querySelector<HTMLElement>(".repo-list")?.hidden).toBe(true);
  });

  it("focuses repository entry when revealed from Start", () => {
    const { panel, body, input } = ready();
    panel.focusPrimary();
    expect(document.activeElement).toBe(input);
    panel.setState(STATE);
    expect(body.querySelector(".repo-add")?.tagName).toBe("SECTION");
  });

  it("refreshes all registered local copies from one action", () => {
    const { panel, body, onRefresh } = ready();
    const refresh = body.querySelector<HTMLButtonElement>(".repo-refresh");
    expect(refresh?.disabled).toBe(true);

    panel.setState(STATE);
    expect(refresh?.disabled).toBe(false);
    refresh?.click();
    expect(onRefresh).toHaveBeenCalledTimes(1);
    const requestId = onRefresh.mock.calls[0]?.[0];
    expect(requestId).toBeTypeOf("number");
    expect(requestId).toBeGreaterThan(0);
    expect(refresh?.disabled).toBe(true);
    expect(refresh?.textContent).toBe("Refreshing…");
    refresh?.click();
    expect(onRefresh).toHaveBeenCalledTimes(1);

    // An unrelated workspace update and another operation's completion must not release Refresh.
    panel.setState(STATE);
    panel.operationCompleted((requestId ?? 1) + 1);
    expect(refresh?.disabled).toBe(true);
    expect(refresh?.textContent).toBe("Refreshing…");

    panel.operationCompleted(requestId ?? 0);
    expect(refresh?.disabled).toBe(false);
    expect(refresh?.textContent).toBe("Refresh");

    panel.setState({ ...STATE, repositories: [{ ...REPO, clones: [] }] });
    expect(refresh?.disabled).toBe(true);
  });

  it("keeps synchronization status while removing manual Get and Share actions", () => {
    const { panel, body, onPull, onPush } = ready();
    panel.setState(STATE);

    expect(body.querySelector(".repo-pull")).toBeNull();
    expect(body.querySelector(".repo-push")).toBeNull();
    expect(onPull).not.toHaveBeenCalled();
    expect(onPush).not.toHaveBeenCalled();

    panel.setState({
      ...STATE,
      repositories: [
        {
          ...REPO,
          clones: REPO.clones.map((clone) => ({ ...clone, currentBranch: null })),
        },
      ],
    });
    expect(body.querySelector(".repo-pull")).toBeNull();
    expect(body.querySelector(".repo-push")).toBeNull();
  });

  it("retains and refreshes the managed clone entry until a matching success state arrives", () => {
    vi.useFakeTimers();
    const { panel, body, input, add, onCloneManaged, onDestinationRequest } = ready();
    input.value = "  owner/name  ";
    panel.setManagedDestination({
      url: "owner/name",
      requestId: 0,
      localName: "name",
      exists: false,
      path: "C:\\managed\\owner_name",
    });
    resolveDescription(panel, "owner/name");
    add.click();
    body.querySelector<HTMLButtonElement>(".repo-clone-confirm-yes")?.click();
    expect(onCloneManaged).toHaveBeenCalledWith("owner/name", "name", "C:\\managed\\owner_name");
    expect(input.value).toBe("  owner/name  "); // retained so a native failure can be retried
    expect(add.disabled).toBe(true);
    expect(body.querySelector<HTMLButtonElement>(".repo-clone-toggle")?.disabled).toBe(false);

    vi.advanceTimersByTime(120);
    expect(onDestinationRequest).toHaveBeenLastCalledWith("owner/name", "name", 1);
    panel.setManagedDestination({
      url: "owner/name",
      requestId: 1,
      localName: "name",
      exists: false,
      path: "C:\\managed\\owner_name-2",
    });

    // A native error/collision does not emit a success state. Any unrelated refresh must preserve the retry.
    panel.setState(STATE);
    expect(input.value).toBe("  owner/name  ");
    add.click();
    body.querySelector<HTMLButtonElement>(".repo-clone-confirm-yes")?.click();
    expect(onCloneManaged).toHaveBeenCalledTimes(2);
    expect(onCloneManaged).toHaveBeenLastCalledWith(
      "owner/name",
      "name",
      "C:\\managed\\owner_name-2",
    );

    // RegisterOpenedRepo emits workspace.state only after the clone completed at the requested path.
    panel.setState({
      recent: [],
      favorites: [],
      repositories: [
        {
          ...REPO,
          id: "owner/name",
          name: "owner/name",
          url: "https://github.com/owner/name",
          clones: [
            {
              id: "owner-name-2",
              path: "C:/managed/owner_name-2/",
              currentBranch: null,
              status: CLEAN_STATUS,
              branches: [],
            },
          ],
        },
      ],
    });
    expect(input.value).toBe("");
    expect(add.disabled).toBe(true);
    vi.useRealTimers();
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
      localName: "public",
      exists: false,
      path: "C:\\managed\\outside_public",
    });
    resolveDescription(panel, "outside/public", 3);
    body.querySelector<HTMLButtonElement>(".repo-clone-primary")?.click();
    body.querySelector<HTMLButtonElement>(".repo-clone-confirm-yes")?.click();
    expect(onCloneManaged).toHaveBeenCalledWith(
      "outside/public",
      "public",
      "C:\\managed\\outside_public",
    );
  });

  it("derives an editable local-copy name and sends it with managed and folder clones", () => {
    vi.useFakeTimers();
    try {
      const { panel, body, input, onDestinationRequest, onCloneManaged, onCloneToFolder } = ready();
      input.value = "acme/specs";
      input.dispatchEvent(new Event("input"));
      const localName = body.querySelector<HTMLInputElement>(".repo-local-name-input");
      expect(localName?.value).toBe("specs");
      if (!localName) {
        throw new Error("local copy name input was not mounted");
      }
      localName.value = "quarterly-specs";
      localName.dispatchEvent(new Event("input"));
      vi.advanceTimersByTime(120);
      expect(onDestinationRequest).toHaveBeenLastCalledWith("acme/specs", "quarterly-specs", 2);
      panel.setManagedDestination({
        url: "acme/specs",
        requestId: 2,
        localName: "quarterly-specs",
        path: "C:\\managed\\quarterly-specs",
        exists: false,
      });
      resolveDescription(panel, "acme/specs", 1);
      body.querySelector<HTMLButtonElement>(".repo-clone-primary")?.click();
      body.querySelector<HTMLButtonElement>(".repo-clone-confirm-yes")?.click();
      expect(onCloneManaged).toHaveBeenCalledWith(
        "acme/specs",
        "quarterly-specs",
        "C:\\managed\\quarterly-specs",
      );

      body.querySelector<HTMLButtonElement>(".repo-clone-toggle")?.click();
      body.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')[1]?.click();
      body.querySelector<HTMLButtonElement>(".repo-clone-confirm-yes")?.click();
      expect(onCloneToFolder).toHaveBeenCalledWith("acme/specs", "quarterly-specs");
    } finally {
      vi.useRealTimers();
    }
  });

  it("warns about an occupied name and offers to open the existing copy", () => {
    const { panel, body, input, add, onOpenExistingClone, onCloneManaged } = ready();
    input.value = "acme/specs";
    input.dispatchEvent(new Event("input"));
    resolveDescription(panel, "acme/specs", 1);
    panel.setManagedDestination({
      url: "acme/specs",
      requestId: 1,
      localName: "specs",
      path: "C:\\managed\\specs",
      exists: true,
      existingClonePath: "C:\\managed\\specs",
    });

    expect(add.disabled).toBe(true);
    expect(body.querySelector(".repo-destination-warning")?.textContent).toContain(
      "already exists",
    );
    body.querySelector<HTMLButtonElement>(".repo-open-existing")?.click();
    expect(onOpenExistingClone).toHaveBeenCalledWith("acme/specs", "C:\\managed\\specs");
    expect(onCloneManaged).not.toHaveBeenCalled();
  });

  it("blocks an occupied non-repository name without offering to open it", () => {
    const { panel, body, input, add, onOpenExistingClone, onCloneManaged } = ready();
    input.value = "acme/specs";
    input.dispatchEvent(new Event("input"));
    resolveDescription(panel, "acme/specs", 1);
    panel.setManagedDestination({
      url: "acme/specs",
      requestId: 1,
      localName: "specs",
      path: "C:\\managed\\specs",
      exists: true,
    });

    expect(add.disabled).toBe(true);
    expect(body.querySelector(".repo-destination-warning")?.textContent).toContain(
      "Choose another local copy name",
    );
    expect(body.querySelector(".repo-open-existing")).toBeNull();
    add.click();
    expect(onOpenExistingClone).not.toHaveBeenCalled();
    expect(onCloneManaged).not.toHaveBeenCalled();
  });

  it("surfaces a clone race as the same open-existing recovery", () => {
    const { panel, body, input, onOpenExistingClone } = ready();
    input.value = "acme/specs";
    input.dispatchEvent(new Event("input"));
    panel.setCloneConflict({
      url: "acme/specs",
      localName: "specs",
      existingClonePath: "C:\\managed\\specs",
      message: "That local copy was created by another action.",
    });
    expect(body.querySelector(".repo-destination-warning")?.textContent).toContain(
      "another action",
    );
    body.querySelector<HTMLButtonElement>(".repo-open-existing")?.click();
    expect(onOpenExistingClone).toHaveBeenCalledWith("acme/specs", "C:\\managed\\specs");
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
      localName: "public-specs",
      exists: false,
      path: "C:\\managed\\outside_public-specs",
    });
    resolveDescription(panel, "outside/public-specs", 1);
    add.click();
    body.querySelector<HTMLButtonElement>(".repo-clone-confirm-yes")?.click();
    expect(onCloneManaged).toHaveBeenCalledWith(
      "outside/public-specs",
      "public-specs",
      "C:\\managed\\outside_public-specs",
    );
  });

  it("offers both clone destinations and suppresses double submission", () => {
    const { panel, body, input, add, onCloneManaged, onCloneToFolder } = ready();
    input.value = "owner/managed";
    panel.setManagedDestination({
      url: "owner/managed",
      requestId: 0,
      localName: "managed",
      exists: false,
      path: "C:\\managed\\owner_managed",
    });
    resolveDescription(panel, "owner/managed");
    body.querySelector<HTMLButtonElement>(".repo-clone-toggle")?.click();
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
    resolveDescription(panel, "owner/folder", 1);
    add.click();
    actions[1]?.click();
    actions[1]?.click();
    yes?.click();
    yes?.click();
    expect(onCloneToFolder).toHaveBeenCalledTimes(1);
    expect(onCloneToFolder).toHaveBeenCalledWith("owner/folder", "folder");
    expect(input.value).toBe("owner/folder");
  });

  it("requires Yes and keeps the input unchanged when No is chosen", () => {
    const { panel, body, input, add, onCloneManaged } = ready();
    input.value = "owner/cancelled";
    panel.setManagedDestination({
      url: "owner/cancelled",
      requestId: 0,
      localName: "cancelled",
      exists: false,
      path: "C:\\managed\\owner_cancelled",
    });
    resolveDescription(panel, "owner/cancelled");
    add.click();
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
      localName: "first",
      exists: false,
      path: "C:\\managed\\owner_first",
    });
    resolveDescription(first.panel, "owner/first");
    first.add.click();
    const skip = first.body.querySelector<HTMLInputElement>(".repo-clone-confirm-skip input");
    if (skip) {
      skip.checked = true;
    }
    first.body.querySelector<HTMLButtonElement>(".repo-clone-confirm-yes")?.click();
    expect(first.onCloneManaged).toHaveBeenCalledTimes(1);

    const second = ready();
    second.input.value = "owner/second";
    resolveDescription(second.panel, "owner/second");
    second.body.querySelector<HTMLButtonElement>(".repo-clone-toggle")?.click();
    second.body.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')[1]?.click();
    expect(second.onCloneToFolder).toHaveBeenCalledWith("owner/second", "second");
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
      localName: "first",
      exists: false,
      path: "C:\\managed\\owner_first",
    });
    expect(body.querySelector(".repo-managed-destination")?.textContent).toContain("checking");

    panel.setManagedDestination({
      url: "owner/current",
      requestId: 2,
      localName: "current",
      exists: false,
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
      expect(add.disabled).toBe(true);
      expect(body.querySelector<HTMLButtonElement>(".repo-clone-toggle")?.disabled).toBe(false);
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
    expect(add.disabled).toBe(true);
    expect(body.querySelector<HTMLButtonElement>(".repo-clone-toggle")?.disabled).toBe(false);

    panel.setDescription({ url: "owner/repository", requestId: 0, state: "notFound" });
    expect(body.querySelector(".repo-description")?.textContent).toContain("not found");
    expect(add.disabled).toBe(true);

    panel.setDescription({ url: "owner/repository", requestId: 0, state: "error" });
    expect(body.querySelector(".repo-description")?.textContent).toContain("Couldn’t load");
    expect(add.disabled).toBe(true);
  });

  it("clears private lookup and pending clone state at an account boundary", () => {
    vi.useFakeTimers();
    try {
      const { panel, body, input, add, onCloneManaged } = ready();
      panel.setSuggestions([{ fullName: "account-a/private-specs" }]);
      input.value = "account-a/private-specs";
      input.dispatchEvent(new Event("input"));
      panel.setManagedDestination({
        url: "account-a/private-specs",
        requestId: 1,
        localName: "private-specs",
        exists: false,
        path: "C:\\managed\\account-a_private-specs",
      });
      panel.setDescription({
        url: "account-a/private-specs",
        requestId: 1,
        state: "private",
        description: "Account A confidential roadmap",
      });
      add.click();
      const oldYes = body.querySelector<HTMLButtonElement>(".repo-clone-confirm-yes");
      expect(body.textContent).toContain("Account A confidential roadmap");
      expect(body.querySelector<HTMLElement>(".repo-clone-confirmation")?.hidden).toBe(false);

      // github.account changed from A to signed-out (and later B): no private lookup or queued action survives.
      panel.clearAccountState();
      expect(input.value).toBe("");
      expect(body.textContent).not.toContain("Account A confidential roadmap");
      expect(body.querySelector<HTMLElement>(".repo-description")?.hidden).toBe(true);
      expect(body.querySelector<HTMLElement>(".repo-managed-destination")?.hidden).toBe(true);
      expect(body.querySelector<HTMLElement>(".repo-clone-confirmation")?.hidden).toBe(true);
      expect(add.disabled).toBe(true);
      oldYes?.click();
      expect(onCloneManaged).not.toHaveBeenCalled();

      // Account B can enter the same arbitrary public name, but an A-era response cannot authorize Clone.
      input.value = "account-a/private-specs";
      input.dispatchEvent(new Event("input"));
      panel.setDescription({
        url: "account-a/private-specs",
        requestId: 1,
        state: "private",
        description: "late Account A description",
      });
      panel.setManagedDestination({
        url: "account-a/private-specs",
        requestId: 1,
        localName: "private-specs",
        exists: false,
        path: "C:\\managed\\stale-account-a",
      });
      expect(body.textContent).not.toContain("late Account A description");
      expect(add.disabled).toBe(true);

      panel.setDescription({
        url: "account-a/private-specs",
        requestId: 3,
        state: "found",
        description: "Public description resolved for account B",
      });
      panel.setManagedDestination({
        url: "account-a/private-specs",
        requestId: 3,
        localName: "private-specs",
        exists: false,
        path: "C:\\managed\\account-b_public-specs",
      });
      expect(body.textContent).toContain("Public description resolved for account B");
      expect(add.disabled).toBe(false);
    } finally {
      vi.useRealTimers();
    }
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
    expect(open?.querySelector(".repo-name")?.textContent).toBe("acme/specs");
    expect(open?.querySelector(".repo-kind")?.textContent).toBe("GitHub · 1 local copy");
    expect(open?.title).toBe(REPO.url);
    expect(body.querySelector(".repo-panel-actions span")?.textContent).toBe(
      "1 repository · 1 local copy",
    );
    expect(body.querySelector(".repo-add")?.tagName).toBe("SECTION");
    open?.click();
    expect(onBrowseRepo).toHaveBeenCalledWith(REPO);

    const remove = body.querySelector<HTMLButtonElement>(".repo-remove");
    expect(remove?.getAttribute("aria-label")).toBe("Remove repository acme/specs from SpecDesk");
    remove?.click();
    expect(onUnregister).toHaveBeenCalledWith("acme/specs");
  });

  it("opens local copies and switches both default and non-default branches from the nested tree", () => {
    const { panel, body, input, onOpenClone, onSwitchBranch } = ready();
    panel.setState(STATE);

    expect(body.querySelector(".repo-clone-name")?.textContent).toBe("acme-specs");
    expect(
      [...body.querySelectorAll(".repo-branch-open")].map((branch) => branch.textContent),
    ).toEqual(["main", "draft"]);
    body.querySelector<HTMLButtonElement>(".repo-clone-open")?.click();
    expect(onOpenClone).toHaveBeenCalledWith(REPO, "C:\\repos\\acme-specs");
    body.querySelectorAll<HTMLButtonElement>(".repo-branch-open")[1]?.click();
    expect(onSwitchBranch).toHaveBeenCalledWith(REPO, "C:\\repos\\acme-specs", "draft");
    body.querySelector<HTMLButtonElement>(".repo-create-copy")?.click();
    expect(input.value).toBe("acme/specs");
    expect(body.querySelector<HTMLInputElement>(".repo-local-name-input")?.value).toBe("specs");
    expect(document.activeElement).toBe(input);
    expect(body.querySelector(".repo-create-copy")?.getAttribute("aria-label")).toContain(
      "acme/specs",
    );
  });

  it("sorts the actual default working line first and keeps the remainder deterministic", () => {
    const { panel, body } = ready();
    const firstClone = REPO.clones[0];
    if (firstClone === undefined) throw new Error("repository fixture requires a local copy");
    panel.setState({
      ...STATE,
      repositories: [
        {
          ...REPO,
          defaultBranch: "trunk",
          clones: [
            {
              ...firstClone,
              branches: [
                { name: "zebra", status: CLEAN_STATUS, canDelete: true, canRename: true },
                { name: "main", status: CLEAN_STATUS, canDelete: true, canRename: true },
                { name: "trunk", status: CLEAN_STATUS, canDelete: false, canRename: false },
                { name: "alpha", status: CLEAN_STATUS, canDelete: true, canRename: true },
              ],
            },
          ],
        },
      ],
    });
    expect(
      [...body.querySelectorAll(".repo-branch-open")].map((button) => button.textContent),
    ).toEqual(["trunk", "alpha", "main", "zebra"]);
  });

  it("creates a working line from the progressive local-copy icon", () => {
    const { panel, body, onCreateBranch } = ready();
    panel.setState(STATE);
    body.querySelector<HTMLButtonElement>(".repo-create-branch")?.click();
    const dialog = body.querySelector<HTMLDialogElement>(".repo-name-dialog");
    expect(dialog?.open).toBe(true);
    const input = body.querySelector<HTMLInputElement>(".repo-name-dialog-input");
    if (!input) throw new Error("name dialog missing");
    input.value = "q3-review";
    body
      .querySelector<HTMLFormElement>(".repo-name-dialog form")
      ?.dispatchEvent(new SubmitEvent("submit", { bubbles: true, cancelable: true }));
    expect(onCreateBranch).toHaveBeenCalledWith(REPO, "C:\\repos\\acme-specs", "q3-review");
  });

  it("offers entity-specific keyboard context menus and rename actions", () => {
    const { panel, body, onRenameClone, onRenameBranch, onUnregister } = ready();
    panel.setState(STATE);

    const clone = body.querySelector<HTMLButtonElement>(".repo-clone-open");
    clone?.dispatchEvent(
      new KeyboardEvent("keydown", {
        key: "F10",
        shiftKey: true,
        bubbles: true,
      }),
    );
    const menu = body.querySelector<HTMLElement>(".repo-context-menu");
    expect(menu?.hidden).toBe(false);
    expect(
      [...(menu?.querySelectorAll('[role="menuitem"]') ?? [])].map((item) => item.textContent),
    ).toEqual([
      "Open local copy",
      "Create working line…",
      "Rename local copy…",
      "Add to favorites",
      "Delete local copy…",
    ]);
    [...(menu?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]') ?? [])]
      .find((item) => item.textContent === "Rename local copy…")
      ?.click();
    const input = body.querySelector<HTMLInputElement>(".repo-name-dialog-input");
    if (!input) throw new Error("name dialog missing");
    input.value = "quarterly-specs";
    body
      .querySelector<HTMLFormElement>(".repo-name-dialog form")
      ?.dispatchEvent(new SubmitEvent("submit", { bubbles: true, cancelable: true }));
    expect(onRenameClone).toHaveBeenCalledWith(REPO, "C:\\repos\\acme-specs", "quarterly-specs");

    const draft = [...body.querySelectorAll<HTMLButtonElement>(".repo-branch-open")].find(
      (item) => item.textContent === "draft",
    );
    draft?.dispatchEvent(
      new KeyboardEvent("keydown", {
        key: "ContextMenu",
        bubbles: true,
      }),
    );
    [...(menu?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]') ?? [])]
      .find((item) => item.textContent === "Rename working line…")
      ?.click();
    input.value = "approved-draft";
    body
      .querySelector<HTMLFormElement>(".repo-name-dialog form")
      ?.dispatchEvent(new SubmitEvent("submit", { bubbles: true, cancelable: true }));
    expect(onRenameBranch).toHaveBeenCalledWith(
      REPO,
      "C:\\repos\\acme-specs",
      "draft",
      "approved-draft",
    );

    body.querySelector<HTMLButtonElement>(".repo-open")?.dispatchEvent(
      new MouseEvent("contextmenu", {
        bubbles: true,
        cancelable: true,
        clientX: 20,
        clientY: 20,
      }),
    );
    [...(menu?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]') ?? [])]
      .find((item) => item.textContent === "Remove from SpecDesk")
      ?.click();
    expect(onUnregister).toHaveBeenCalledWith(REPO.id);
  });

  it("does not offer rename for remote-only or protected working lines", () => {
    const { panel, body, onRenameBranch } = ready();
    const clone = REPO.clones[0];
    if (clone === undefined) throw new Error("clone fixture missing");
    panel.setState({
      ...STATE,
      repositories: [
        {
          ...REPO,
          clones: [
            {
              ...clone,
              branches: [
                ...clone.branches,
                {
                  name: "remote-only",
                  status: CLEAN_STATUS,
                  canDelete: false,
                  canRename: false,
                },
                {
                  name: "protected",
                  status: { ...CLEAN_STATUS, stashCount: 1 },
                  canDelete: true,
                  canRename: false,
                },
              ],
            },
          ],
        },
      ],
    });

    const remoteOnly = [...body.querySelectorAll<HTMLButtonElement>(".repo-branch-open")].find(
      (item) => item.textContent === "remote-only",
    );
    remoteOnly?.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true }));

    expect(
      [...body.querySelectorAll<HTMLElement>('.repo-context-menu [role="menuitem"]')].map(
        (item) => item.textContent,
      ),
    ).toEqual(["Switch and open", "Add to favorites"]);

    const protectedBranch = [...body.querySelectorAll<HTMLButtonElement>(".repo-branch-open")].find(
      (item) => item.textContent === "protected",
    );
    protectedBranch?.dispatchEvent(
      new MouseEvent("contextmenu", { bubbles: true, cancelable: true }),
    );
    expect(
      [...body.querySelectorAll<HTMLElement>('.repo-context-menu [role="menuitem"]')].map(
        (item) => item.textContent,
      ),
    ).toEqual(["Switch and open", "Add to favorites", "Delete local working line…"]);
    expect(onRenameBranch).not.toHaveBeenCalled();
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

  it("reveals and focuses the repository selected from Favorites", () => {
    const { panel, body, onBrowseRepo } = ready();
    panel.setState(STATE);
    panel.revealRepository("ACME/SPECS");

    expect(body.querySelector(".repo-row")?.classList.contains("is-highlighted")).toBe(true);
    const open = body.querySelector(".repo-open");
    expect(open?.getAttribute("aria-current")).toBe("true");
    expect(document.activeElement).toBe(open);
    expect(onBrowseRepo).not.toHaveBeenCalled();
  });

  it("favorites a local copy and an exact branch independently", () => {
    const { panel, body, onToggleCloneFavorite, onToggleBranchFavorite } = ready();
    panel.setState(STATE);
    const cloneStar = body.querySelector<HTMLButtonElement>(
      '[aria-label="Favorite local copy acme-specs"]',
    );
    const branchStar = body.querySelector<HTMLButtonElement>(
      '[aria-label="Favorite branch draft in acme-specs"]',
    );
    cloneStar?.click();
    branchStar?.click();
    expect(onToggleCloneFavorite).toHaveBeenCalledWith(REPO, "C:\\repos\\acme-specs", true);
    expect(onToggleBranchFavorite).toHaveBeenCalledWith(
      REPO,
      "C:\\repos\\acme-specs",
      "draft",
      true,
    );

    panel.setState({
      ...STATE,
      favorites: [
        {
          path: "C:\\repos\\acme-specs",
          label: "acme-specs",
          isFolder: true,
          kind: "clone",
          repositoryId: REPO.id,
        },
        {
          path: "C:\\repos\\acme-specs",
          label: "acme-specs",
          isFolder: true,
          kind: "branch",
          repositoryId: REPO.id,
          branch: "draft",
        },
      ],
    });
    expect(
      body
        .querySelector('[aria-label="Favorite local copy acme-specs"]')
        ?.getAttribute("aria-pressed"),
    ).toBe("true");
    expect(
      body
        .querySelector('[aria-label="Favorite branch draft in acme-specs"]')
        ?.getAttribute("aria-pressed"),
    ).toBe("true");
  });

  it("distinguishes unshared versions, unsaved edits, and held work", () => {
    const { panel, body } = ready();
    const status = {
      ...CLEAN_STATUS,
      ahead: 2,
      behind: 3,
      hasUncommitted: true,
      stashCount: 1,
      hasConflicts: true,
    };
    panel.setState({
      ...STATE,
      repositories: [
        {
          ...REPO,
          clones: REPO.clones.map((clone) => ({
            ...clone,
            status,
            branches: clone.branches.map((branch) =>
              branch.name === "main" ? { ...branch, status } : branch,
            ),
          })),
        },
      ],
    });

    const cloneStatus = body.querySelector('[aria-label="Local copy acme-specs status"]');
    expect(cloneStatus?.querySelector(".is-unshared")?.textContent).toBe("2 not shared");
    expect(cloneStatus?.querySelector(".is-incoming")?.textContent).toBe("3 updates available");
    expect(cloneStatus?.querySelector(".is-unsaved")?.textContent).toBe("Unsaved changes");
    expect(cloneStatus?.querySelector(".is-held")?.textContent).toBe("1 held change");
    expect(cloneStatus?.querySelector(".is-conflict")?.textContent).toBe(
      "Conflict needs attention",
    );
    const currentBranch = body.querySelector('[aria-current="true"]');
    expect(currentBranch?.textContent).toBe("main");
  });

  it("requests safe deletion and only confirms after the host reports warnings", () => {
    const { panel, body, onDeleteClone, onDeleteBranch } = ready();
    panel.setState(STATE);
    body
      .querySelector<HTMLButtonElement>('[aria-label="Delete local copy acme-specs locally"]')
      ?.click();
    expect(
      body.querySelector('button[aria-label="Delete branch main in acme-specs locally"]'),
    ).toBeNull();
    const branchDelete = body.querySelector<HTMLButtonElement>(
      '[aria-label="Delete branch draft in acme-specs locally"]',
    );
    branchDelete?.click();
    expect(onDeleteClone).toHaveBeenCalledWith(REPO, "C:\\repos\\acme-specs");
    expect(onDeleteBranch).toHaveBeenCalledWith(REPO, "C:\\repos\\acme-specs", "draft");

    panel.setOperationConfirmation({
      operation: "deleteBranch",
      id: REPO.id,
      clonePath: "C:\\repos\\acme-specs",
      branch: "draft",
      message: "This version still has local work.",
      warnings: [
        "2 changes have not been saved as a version.",
        "1 saved version has not been shared.",
        "SpecDesk is holding work for this version.",
      ],
      confirmationToken: "branch-risk-v1",
    });
    const confirmation = body.querySelector<HTMLElement>(".repo-operation-confirmation");
    expect(confirmation?.hidden).toBe(false);
    expect(confirmation?.textContent).toContain("not been saved");
    expect(confirmation?.textContent).toContain("not been shared");
    expect(confirmation?.textContent).toContain("holding work");
    expect(confirmation?.textContent).toContain("Delete local branch");
    confirmation?.querySelector<HTMLButtonElement>("button")?.click();
    expect(document.activeElement).toBe(branchDelete);
    expect(confirmation?.hidden).toBe(true);

    branchDelete?.click();
    panel.setOperationConfirmation({
      operation: "deleteBranch",
      id: REPO.id,
      clonePath: "C:\\repos\\acme-specs",
      branch: "draft",
      message: "This version still has local work.",
      warnings: ["2 changes have not been saved as a version."],
      confirmationToken: "branch-risk-v2",
    });
    body.querySelector<HTMLButtonElement>(".repo-operation-delete")?.click();
    expect(onDeleteBranch).toHaveBeenLastCalledWith(
      REPO,
      "C:\\repos\\acme-specs",
      "draft",
      "branch-risk-v2",
    );
    expect(confirmation?.hidden).toBe(true);
    expect(document.activeElement).toBe(body.querySelector(".repo-open"));
  });
});
