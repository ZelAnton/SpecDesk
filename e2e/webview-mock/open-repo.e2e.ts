import { expect, test } from "@playwright/test";
import { emit, installMockHost, loadDoc, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

// The tree the host would send back after cloning/opening a workspace.
const TREE = {
  kind: "tree",
  payload: {
    root: "C:\\specs\\repo",
    requestId: 0,
    nodes: [
      { name: "README.md", path: "C:\\specs\\repo\\README.md", isDirectory: false, children: [], hasChildren: false },
    ],
  },
};

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

/** Open the left dock and switch the central frame to the Start screen via the navigator. */
async function goToStart(page: import("@playwright/test").Page): Promise<void> {
  await page.locator('#left-dock .dock-rail-btn[aria-label="Navigator"]').click();
  await page.locator('#left-dock .nav-item[data-view="home"]').click();
  await expect(page.locator("#home-view")).toBeVisible();
}

test("the Start screen opens the Repositories panel without its own repo input", async ({ page }, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await emit(page, {
    kind: "workspace.state",
    payload: {
      recent: [
        {
          path: "C:\\specs\\recent.md",
          label: "recent.md",
          isFolder: false,
          kind: "local",
        },
      ],
      favorites: [
        {
          path: "acme/specs",
          label: "acme/specs",
          isFolder: true,
          kind: "repository",
          repositoryId: "acme/specs",
        },
      ],
      repositories: [
        {
          id: "acme/specs",
          name: "acme/specs",
          url: "https://github.com/acme/specs",
          defaultBranch: "main",
          clones: [],
        },
      ],
    },
  });
  await goToStart(page);

  await expect(page.locator("#home-view input")).toHaveCount(0);
  await expect(page.locator("#home-view form")).toHaveCount(0);
  await expect(page.locator("#home-view .home-favorites-label")).toHaveText("Favorites");
  await expect(page.locator("#home-view .home-favorites-list")).toContainText("acme/specs");
  await expect(page.locator("#home-view .home-recents-list")).toContainText("recent.md");
  await page.locator("#home-view .home-open", { hasText: "Open Repository" }).click();

  await expect(page.locator('#left-dock .dock-tool[data-tool="repositories"]')).toBeVisible();
  await expect(
    page.locator('#left-dock .dock-rail-btn[aria-label="Repositories"]'),
  ).toHaveAttribute("aria-expanded", "true");
  await expect(
    page.getByRole("combobox", { name: "Repository owner/name or GitHub URL" }),
  ).toBeFocused();
  expect((await sentFrames(page)).some((frame) => frame.kind === "repo.open")).toBe(false);
  await page.screenshot({ path: testInfo.outputPath("start-repositories.png"), fullPage: true });
});

test("opening a folder reveals the Files panel even when the left dock was collapsed", async ({
  page,
}) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await goToStart(page);

  // Collapse the left dock: opening a folder must re-open it and show the tree (the known-gap fix, where
  // opening a folder gave no visible feedback while the dock was collapsed).
  await page.locator('#left-dock .dock-rail-btn[aria-checked="true"]').click();
  await expect(page.locator("#left-dock")).toHaveClass(/dock--collapsed/);

  await page.locator("#home-view .home-open", { hasText: "Open a folder" }).click();
  expect((await sentFrames(page)).some((f) => f.kind === "folder.open")).toBe(true);

  await emit(page, TREE);
  await expect(page.locator("#left-dock")).toBeVisible();
  await expect(page.locator('#left-dock .dock-tool[data-tool="files"]')).toBeVisible();
});

test("a tree from a plain document load does NOT reveal the Files panel", async ({ page }) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");

  // Loading reveals the contextual Editor mode. A later tree must not replace it with Folders.
  await loadDoc(page, { path: "C:\\specs\\repo\\doc.md", text: "# Doc" });
  await expect(page.locator('#left-dock .dock-rail-btn[aria-label="Editor"]')).toHaveAttribute(
    "aria-expanded",
    "true",
  );
  await emit(page, TREE);
  await expect(page.locator('#left-dock .dock-rail-btn[aria-label="Folders"]')).toHaveAttribute(
    "aria-expanded",
    "false",
  );
});

test("clicking a repository in the Repositories panel browses it", async ({ page }) => {
  const STATE = {
    kind: "workspace.state",
    payload: {
      recent: [],
      favorites: [],
      repositories: [
        {
          id: "acme/specs",
          name: "acme/specs",
          url: "https://github.com/acme/specs",
          defaultBranch: "main",
          clones: [],
        },
      ],
    },
  };

  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await emit(page, STATE);

  await page.locator('#left-dock .dock-rail-btn[aria-label="Repositories"]').click();
  await page.locator('#left-dock .dock-tool[data-tool="repositories"] .repo-open').click();

  expect((await sentFrames(page)).find((f) => f.kind === "repo.browse")?.payload).toMatchObject({
    id: "acme/specs",
  });

  // The returned remote tree surfaces the Files panel.
  await emit(page, TREE);
  await expect(page.locator('#left-dock .dock-tool[data-tool="files"]')).toBeVisible();
});

test("local copies and branches open their files directly", async ({ page }, testInfo) => {
  test.setTimeout(60_000);
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await emit(page, {
    kind: "github.account",
    payload: {
      available: true,
      signedIn: true,
      login: "octocat",
      organizations: ["acme", "octo-labs"],
    },
  });
  await expect(page.locator("#toolbar #github-auth-btn")).toBeHidden();
  await expect(page.locator("#github-btn")).toHaveAttribute("aria-label", /@octocat/);
  await expect(page.locator("#status-bar #github-account-status")).toHaveText(
    "GitHub: @octocat · Organizations: acme, octo-labs",
  );
  const cleanStatus = {
    ahead: 0,
    behind: 0,
    hasUncommitted: false,
    stashCount: 0,
    hasConflicts: false,
  };
  const repositoryFavorite = {
    path: "acme/specs",
    label: "acme/specs",
    isFolder: true,
    kind: "repository",
    repositoryId: "acme/specs",
  } as const;
  const cloneFavorite = {
    path: "C:\\SpecDesk\\repos\\quarterly-specs",
    label: "quarterly-specs",
    isFolder: true,
    kind: "clone",
    repositoryId: "acme/specs",
  } as const;
  const branchFavorite = {
    path: "C:\\SpecDesk\\repos\\quarterly-specs",
    label: "quarterly-specs · draft",
    isFolder: true,
    kind: "branch",
    repositoryId: "acme/specs",
    branch: "draft",
  } as const;
  type FavoriteItem =
    | typeof repositoryFavorite
    | typeof cloneFavorite
    | typeof branchFavorite;
  const repositoryFavorites: readonly FavoriteItem[] = [repositoryFavorite];
  const cloneFavorites: readonly FavoriteItem[] = [repositoryFavorite, cloneFavorite];
  const allFavorites: readonly FavoriteItem[] = [
    repositoryFavorite,
    cloneFavorite,
    branchFavorite,
  ];
  const localStateFor = (
    draftStatus: typeof cleanStatus,
    favorites: readonly FavoriteItem[] = repositoryFavorites,
  ) => ({
    kind: "workspace.state",
    payload: {
      recent: [],
      favorites,
      repositories: [
        {
          id: "acme/specs",
          name: "acme/specs",
          url: "https://github.com/acme/specs",
          defaultBranch: "main",
          clones: [
            {
              id: "quarterly-specs",
              path: "C:\\SpecDesk\\repos\\quarterly-specs",
              currentBranch: "draft",
              branches: [
                {
                  name: "draft",
                  canDelete: true,
                  status: draftStatus,
                },
                {
                  name: "main",
                  canDelete: false,
                  status: cleanStatus,
                },
              ],
              status: draftStatus,
            },
          ],
        },
      ],
    },
  });
  const divergentState = localStateFor({
    ahead: 2,
    behind: 1,
    hasUncommitted: true,
    stashCount: 1,
    hasConflicts: true,
  });
  const pullReadyState = localStateFor({ ...cleanStatus, behind: 1 });
  const cleanState = localStateFor(cleanStatus);
  const cloneFavoriteState = localStateFor(cleanStatus, cloneFavorites);
  const branchFavoriteState = localStateFor(cleanStatus, allFavorites);
  const deleteRiskState = localStateFor(
    {
      ...cleanStatus,
      ahead: 1,
      hasUncommitted: true,
      stashCount: 1,
    },
    allFavorites,
  );
  await emit(page, divergentState);
  await page.locator('#left-dock .dock-rail-btn[aria-label="Navigator"]').click();
  const browseCount = (await sentFrames(page)).filter((frame) => frame.kind === "repo.browse").length;
  await page.getByRole("button", { name: "Repository acme/specs", exact: true }).click();
  await expect(page.locator('#left-dock .dock-tool[data-tool="repositories"]')).toBeVisible();
  await expect(page.locator(".repo-row.is-highlighted")).toContainText("acme/specs");
  expect((await sentFrames(page)).filter((frame) => frame.kind === "repo.browse")).toHaveLength(
    browseCount,
  );
  // Selecting a repository favorite already reveals and focuses the Repositories panel.
  const refreshFrameCount = (await sentFrames(page)).filter(
    (frame) => frame.kind === "repo.refreshAll",
  ).length;
  await page.getByRole("button", { name: "Refresh" }).click();
  await expect
    .poll(
      async () =>
        (await sentFrames(page)).filter((frame) => frame.kind === "repo.refreshAll").length,
    )
    .toBe(refreshFrameCount + 1);
  const refreshPayload = (await sentFrames(page))
    .filter((frame) => frame.kind === "repo.refreshAll")
    .at(refreshFrameCount)?.payload as { requestId?: unknown } | undefined;
  const refreshRequestId = refreshPayload?.requestId;
  expect(refreshRequestId).toEqual(expect.any(Number));
  expect(refreshRequestId).toBeGreaterThan(0);
  await emit(page, divergentState);
  await emit(page, {
    kind: "repo.operationCompleted",
    payload: { requestId: refreshRequestId },
  });
  await expect(page.getByRole("button", { name: "Refresh" })).toBeEnabled();
  await expect(page.locator(".repo-branch-open")).toHaveText(["main", "draft"]);

  const createBranchCount = (await sentFrames(page)).filter(
    (frame) => frame.kind === "repo.createBranch",
  ).length;
  await page.locator(".repo-clone-header").hover();
  const createBranch = page.getByRole("button", {
    name: "Create a new working line in quarterly-specs",
  });
  await expect(createBranch).toBeVisible();
  await createBranch.click();
  await expect(page.getByRole("dialog")).toBeVisible();
  await page.locator(".repo-name-dialog-input").fill("q3-review");
  await page.getByRole("button", { name: "Continue" }).click();
  await expect.poll(async () => (await sentFrames(page)).filter(
    (frame) => frame.kind === "repo.createBranch",
  ).length).toBe(createBranchCount + 1);
  const createBranchPayload = (await sentFrames(page)).filter(
    (frame) => frame.kind === "repo.createBranch",
  ).at(-1)?.payload as { requestId?: number } | undefined;
  expect(createBranchPayload).toMatchObject({
    id: "acme/specs",
    clonePath: "C:\\SpecDesk\\repos\\quarterly-specs",
    branch: "q3-review",
  });
  await emit(page, { kind: "repo.operationCompleted", payload: {
    requestId: createBranchPayload?.requestId,
  } });
  await page.locator('#left-dock .dock-rail-btn[aria-label="Repositories"]').click();

  await page.locator(".repo-clone-open").click({ button: "right" });
  const entityMenu = page.locator(".repo-context-menu");
  await expect(entityMenu).toBeVisible();
  await expect(entityMenu.getByRole("menuitem")).toHaveText([
    "Open local copy",
    "Create working line…",
    "Rename local copy…",
    "Add to favorites",
    "Delete local copy…",
  ]);
  await entityMenu.getByRole("menuitem", { name: "Rename local copy…" }).click();
  await page.locator(".repo-name-dialog-input").fill("q3-specs");
  await page.getByRole("button", { name: "Continue" }).click();
  await expect.poll(async () => (await sentFrames(page)).filter(
    (frame) => frame.kind === "repo.renameClone",
  ).length).toBe(1);
  const renameClonePayload = (await sentFrames(page)).find(
    (frame) => frame.kind === "repo.renameClone",
  )?.payload as { requestId?: number } | undefined;
  expect(renameClonePayload).toMatchObject({
    id: "acme/specs",
    clonePath: "C:\\SpecDesk\\repos\\quarterly-specs",
    localName: "q3-specs",
  });
  await emit(page, { kind: "repo.operationCompleted", payload: {
    requestId: renameClonePayload?.requestId,
  } });

  await page.getByRole("button", {
    name: "Switch quarterly-specs to draft and open its files",
  }).click({ button: "right" });
  await entityMenu.getByRole("menuitem", { name: "Rename working line…" }).click();
  await page.locator(".repo-name-dialog-input").fill("approved-draft");
  await page.getByRole("button", { name: "Continue" }).click();
  await expect.poll(async () => (await sentFrames(page)).filter(
    (frame) => frame.kind === "repo.renameBranch",
  ).length).toBe(1);
  const renameBranchPayload = (await sentFrames(page)).find(
    (frame) => frame.kind === "repo.renameBranch",
  )?.payload as { requestId?: number } | undefined;
  expect(renameBranchPayload).toMatchObject({
    id: "acme/specs",
    clonePath: "C:\\SpecDesk\\repos\\quarterly-specs",
    branch: "draft",
    newBranch: "approved-draft",
  });
  await emit(page, { kind: "repo.operationCompleted", payload: {
    requestId: renameBranchPayload?.requestId,
  } });

  await page.locator(".repo-open").click({ button: "right" });
  await expect(entityMenu.getByRole("menuitem", { name: "Remove from SpecDesk" })).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("repository-context-menu.png"), fullPage: true });
  await page.keyboard.press("Escape");
  await expect(page.getByText("2 not shared")).toHaveCount(2);
  await expect(page.getByText("1 update available")).toHaveCount(2);
  await expect(page.getByText("Unsaved changes")).toHaveCount(2);
  await expect(page.getByText("1 held change")).toHaveCount(2);
  await expect(page.getByText("Conflict needs attention")).toHaveCount(2);
  await emit(page, pullReadyState);
  await expect(page.getByText("1 update available")).toHaveCount(2);
  await expect(page.getByText("2 not shared")).toHaveCount(0);
  await expect(page.getByText("Unsaved changes")).toHaveCount(0);
  await expect(page.getByText("1 held change")).toHaveCount(0);
  await expect(page.getByText("Conflict needs attention")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Get updates" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Share changes" })).toHaveCount(0);
  await emit(page, cleanState);
  await expect(page.getByText("1 update available")).toHaveCount(0);
  await expect(page.getByText("2 not shared")).toHaveCount(0);
  await expect(page.getByRole("button", { name: /Switch quarterly-specs to draft/ })).toHaveAttribute(
    "aria-current",
    "true",
  );
  const favoriteFrameCount = (await sentFrames(page)).filter(
    (frame) => frame.kind === "workspace.favorite",
  ).length;
  await page.locator(".repo-clone-header").hover();
  await page
    .getByRole("button", { name: "Favorite local copy quarterly-specs", exact: true })
    .click();
  await expect
    .poll(
      async () =>
        (await sentFrames(page)).filter((frame) => frame.kind === "workspace.favorite").length,
    )
    .toBe(favoriteFrameCount + 1);
  let favoriteFrames = (await sentFrames(page)).filter(
    (frame) => frame.kind === "workspace.favorite",
  );
  expect(favoriteFrames.at(favoriteFrameCount)?.payload).toEqual({
    path: "C:\\SpecDesk\\repos\\quarterly-specs",
    repositoryId: "acme/specs",
    kind: "clone",
    isFolder: true,
    favorite: true,
  });
  await emit(page, cloneFavoriteState);
  await expect(
    page.locator('#left-dock [data-tool="favorites"] .workspace-item-label'),
  ).toHaveText(["acme/specs", "quarterly-specs"]);
  await expect(
    page.getByRole("button", { name: "Favorite local copy quarterly-specs", exact: true }),
  ).toHaveAttribute("aria-pressed", "true");
  await page.locator(".repo-branch-header").filter({ hasText: "draft" }).hover();
  await page
    .getByRole("button", { name: "Favorite branch draft in quarterly-specs", exact: true })
    .click();
  await expect
    .poll(
      async () =>
        (await sentFrames(page)).filter((frame) => frame.kind === "workspace.favorite").length,
    )
    .toBe(favoriteFrameCount + 2);
  favoriteFrames = (await sentFrames(page)).filter(
    (frame) => frame.kind === "workspace.favorite",
  );
  expect(favoriteFrames.at(favoriteFrameCount + 1)?.payload).toEqual({
    path: "C:\\SpecDesk\\repos\\quarterly-specs",
    repositoryId: "acme/specs",
    branch: "draft",
    kind: "branch",
    isFolder: true,
    favorite: true,
  });
  await emit(page, branchFavoriteState);
  await expect(
    page.locator('#left-dock [data-tool="favorites"] .workspace-item-label'),
  ).toHaveText(["acme/specs", "quarterly-specs", "quarterly-specs · draft"]);
  await expect(
    page.getByRole("button", { name: "Favorite branch draft in quarterly-specs", exact: true }),
  ).toHaveAttribute("aria-pressed", "true");
  await expect(page.locator(".repo-add")).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("repository-copy-branches.png"), fullPage: true });

  await page.locator(".repo-clone-open").click();
  expect((await sentFrames(page)).filter((frame) => frame.kind === "repo.open").at(-1)?.payload).toEqual({
    url: "https://github.com/acme/specs",
    clonePath: "C:\\SpecDesk\\repos\\quarterly-specs",
  });
  await expect(page.locator('#left-dock .dock-tool[data-tool="files"]')).toBeVisible();

  await page.locator('#left-dock .dock-rail-btn[aria-label="Repositories"]').click();
  await page.getByRole("button", { name: "Switch quarterly-specs to draft and open its files" }).click();
  const switchPayload = (await sentFrames(page))
    .filter((frame) => frame.kind === "repo.switchBranch")
    .at(-1)?.payload as { requestId?: unknown } | undefined;
  expect(switchPayload).toMatchObject({
    id: "acme/specs",
    clonePath: "C:\\SpecDesk\\repos\\quarterly-specs",
    branch: "draft",
  });
  const switchRequestId = switchPayload?.requestId;
  expect(switchRequestId).toEqual(expect.any(Number));
  expect(switchRequestId).toBeGreaterThan(0);
  await emit(page, branchFavoriteState);
  await emit(page, {
    kind: "repo.operationCompleted",
    payload: { requestId: switchRequestId },
  });
  await expect(page.locator('#left-dock .dock-tool[data-tool="files"]')).toBeVisible();

  await emit(page, deleteRiskState);
  await page.locator('#left-dock .dock-rail-btn[aria-label="Repositories"]').click();
  await expect(page.getByText("1 not shared")).toHaveCount(2);
  await expect(page.getByText("Unsaved changes")).toHaveCount(2);
  await expect(page.getByText("1 held change")).toHaveCount(2);
  await expect(
    page.locator('#left-dock [data-tool="favorites"] .workspace-item-label'),
  ).toHaveText(["acme/specs", "quarterly-specs", "quarterly-specs · draft"]);
  await page.locator(".repo-branch-header").filter({ hasText: "draft" }).hover();
  await page.getByRole("button", { name: "Delete branch draft in quarterly-specs locally" }).click();
  const initialDeletePayload = (await sentFrames(page))
    .filter((frame) => frame.kind === "repo.deleteBranch")
    .at(-1)?.payload as { requestId?: unknown } | undefined;
  expect(initialDeletePayload).toMatchObject({
    id: "acme/specs",
    clonePath: "C:\\SpecDesk\\repos\\quarterly-specs",
    branch: "draft",
  });
  const initialDeleteRequestId = initialDeletePayload?.requestId;
  expect(initialDeleteRequestId).toEqual(expect.any(Number));
  expect(initialDeleteRequestId).toBeGreaterThan(0);
  await emit(page, {
    kind: "repo.confirmation",
    payload: {
      operation: "deleteBranch",
      id: "acme/specs",
      clonePath: "C:\\SpecDesk\\repos\\quarterly-specs",
      branch: "draft",
      message: "This working line still has local work.",
      warnings: [
        "2 changes have not been saved as a version.",
        "1 saved version has not been shared.",
        "SpecDesk is holding work for this version.",
      ],
      confirmationToken: "branch-risk-v1",
    },
  });
  await emit(page, {
    kind: "repo.operationCompleted",
    payload: { requestId: initialDeleteRequestId },
  });
  const operationConfirmation = page.locator(".repo-operation-confirmation");
  await expect(operationConfirmation).toBeVisible();
  const keepAction = operationConfirmation.getByRole("button", { name: "Keep it", exact: true });
  const deleteAction = operationConfirmation.getByRole("button", {
    name: "Delete local branch",
    exact: true,
  });
  const confirmationBox = await operationConfirmation.boundingBox();
  const keepBox = await keepAction.boundingBox();
  const deleteBox = await deleteAction.boundingBox();
  if (confirmationBox === null || keepBox === null || deleteBox === null) {
    throw new Error("The local-branch warning and both of its actions must participate in layout");
  }
  const viewport = page.viewportSize();
  if (viewport === null) {
    throw new Error("The local-branch warning requires a viewport for its containment check");
  }
  for (const actionBox of [keepBox, deleteBox]) {
    expect(actionBox.x).toBeGreaterThanOrEqual(confirmationBox.x);
    expect(actionBox.x + actionBox.width).toBeLessThanOrEqual(
      confirmationBox.x + confirmationBox.width,
    );
    expect(actionBox.x).toBeGreaterThanOrEqual(0);
    expect(actionBox.x + actionBox.width).toBeLessThanOrEqual(viewport.width);
  }
  const confirmationWidths = await operationConfirmation.evaluate((element) => ({
    client: element.clientWidth,
    scroll: element.scrollWidth,
  }));
  expect(confirmationWidths.scroll).toBeLessThanOrEqual(confirmationWidths.client);
  await page.screenshot({ path: testInfo.outputPath("delete-branch-warning.png"), fullPage: true });
  await deleteAction.click();
  const confirmedDeletePayload = (await sentFrames(page))
    .filter((frame) => frame.kind === "repo.deleteBranch")
    .at(-1)?.payload as { requestId?: unknown } | undefined;
  expect(confirmedDeletePayload).toMatchObject({
    id: "acme/specs",
    clonePath: "C:\\SpecDesk\\repos\\quarterly-specs",
    branch: "draft",
    confirmationToken: "branch-risk-v1",
  });
  const confirmedDeleteRequestId = confirmedDeletePayload?.requestId;
  expect(confirmedDeleteRequestId).toEqual(expect.any(Number));
  expect(confirmedDeleteRequestId).toBeGreaterThan(0);
  expect(confirmedDeleteRequestId).not.toBe(initialDeleteRequestId);
  await emit(page, {
    kind: "workspace.state",
    payload: {
      recent: [],
      favorites: cloneFavorites,
      repositories: [
        {
          id: "acme/specs",
          name: "acme/specs",
          url: "https://github.com/acme/specs",
          defaultBranch: "main",
          clones: [
            {
              id: "quarterly-specs",
              path: "C:\\SpecDesk\\repos\\quarterly-specs",
              currentBranch: "main",
              branches: [
                {
                  name: "main",
                  canDelete: false,
                  status: {
                    ahead: 0,
                    behind: 0,
                    hasUncommitted: false,
                    stashCount: 0,
                    hasConflicts: false,
                  },
                },
              ],
              status: {
                ahead: 0,
                behind: 0,
                hasUncommitted: false,
                stashCount: 0,
                hasConflicts: false,
              },
            },
          ],
        },
      ],
    },
  });
  await emit(page, {
    kind: "repo.operationCompleted",
    payload: { requestId: confirmedDeleteRequestId },
  });
  await expect(page.locator(".repo-branch-open")).toHaveText(["main"]);
  await expect(
    page.getByRole("button", {
      name: "Switch quarterly-specs to draft and open its files",
      exact: true,
    }),
  ).toHaveCount(0);
  await expect(
    page.getByRole("button", {
      name: "Delete branch draft in quarterly-specs locally",
      exact: true,
    }),
  ).toHaveCount(0);
  await expect(page.locator(".repo-clone-current")).toHaveText("main");
  await expect(
    page.getByRole("button", {
      name: "Switch quarterly-specs to main and open its files",
      exact: true,
    }),
  ).toHaveAttribute("aria-current", "true");
  await expect(
    page.locator('#left-dock [data-tool="favorites"] .workspace-open[aria-label^="Branch draft "]'),
  ).toHaveCount(0);
  await expect(
    page.locator('#left-dock [data-tool="favorites"] .workspace-item-label'),
  ).toHaveText(["acme/specs", "quarterly-specs"]);

  // The copy form stays visible; the compact row action only fills and focuses it.
  await page.getByRole("button", { name: "Create a new local copy of acme/specs" }).click();
  await expect(page.locator(".repo-add")).toBeVisible();
  await expect(page.locator(".repo-register-input")).toBeFocused();
  await expect(page.locator(".repo-local-name-input")).toHaveValue("specs");
});
