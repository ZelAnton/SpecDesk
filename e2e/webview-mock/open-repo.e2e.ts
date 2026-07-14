import { expect, test } from "@playwright/test";
import { emit, installMockHost, loadDoc, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

// The tree the host would send back after cloning/opening a workspace.
const TREE = {
  kind: "tree",
  payload: {
    root: "C:\\specs\\repo",
    nodes: [
      { name: "README.md", path: "C:\\specs\\repo\\README.md", isDirectory: false, children: [] },
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
  await expect(page.locator("#repo-input")).toBeFocused();
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

  // Load a document (which makes the app request its folder's tree) — with the left dock collapsed and no
  // folder/repo open initiated, the arriving tree must NOT force the Files panel open.
  await loadDoc(page, { path: "C:\\specs\\repo\\doc.md", text: "# Doc" });
  await expect(page.locator("#left-dock")).toHaveClass(/dock--collapsed/);
  await emit(page, TREE);
  await expect(page.locator("#left-dock")).toHaveClass(/dock--collapsed/);
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
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await emit(page, {
    kind: "workspace.state",
    payload: {
      recent: [],
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
          clones: [
            {
              id: "quarterly-specs",
              path: "C:\\SpecDesk\\repos\\quarterly-specs",
              currentBranch: "draft",
              branches: [
                {
                  name: "main",
                  status: {
                    ahead: 0,
                    behind: 0,
                    hasUncommitted: false,
                    stashCount: 0,
                    hasConflicts: false,
                  },
                },
                {
                  name: "draft",
                  status: {
                    ahead: 2,
                    behind: 1,
                    hasUncommitted: true,
                    stashCount: 1,
                    hasConflicts: true,
                  },
                },
              ],
              status: {
                ahead: 2,
                behind: 1,
                hasUncommitted: true,
                stashCount: 1,
                hasConflicts: true,
              },
            },
          ],
        },
      ],
    },
  });
  await page.locator('#left-dock .dock-rail-btn[aria-label="Navigator"]').click();
  const browseCount = (await sentFrames(page)).filter((frame) => frame.kind === "repo.browse").length;
  await page.getByRole("button", { name: "Repository acme/specs" }).click();
  await expect(page.locator('#left-dock .dock-tool[data-tool="repositories"]')).toBeVisible();
  await expect(page.locator(".repo-row.is-highlighted")).toContainText("acme/specs");
  expect((await sentFrames(page)).filter((frame) => frame.kind === "repo.browse")).toHaveLength(
    browseCount,
  );
  await page.locator('#left-dock .dock-rail-btn[aria-label="Repositories"]').click();
  await page.getByRole("button", { name: "Refresh" }).click();
  expect((await sentFrames(page)).some((frame) => frame.kind === "repo.refreshAll")).toBe(true);
  await expect(page.locator(".repo-branch-open")).toHaveText(["main", "draft"]);
  await expect(page.getByText("2 not shared")).toHaveCount(2);
  await expect(page.getByText("1 update available")).toHaveCount(2);
  await expect(page.getByText("Unsaved changes")).toHaveCount(2);
  await expect(page.getByText("1 held change")).toHaveCount(2);
  await expect(page.getByText("Conflict needs attention")).toHaveCount(2);
  await page.getByRole("button", { name: "Get updates" }).click();
  await page.getByRole("button", { name: "Share changes" }).click();
  const syncFrames = (await sentFrames(page)).filter(
    (frame) => frame.kind === "repo.pull" || frame.kind === "repo.push",
  );
  expect(syncFrames.slice(-2).map((frame) => ({ kind: frame.kind, payload: frame.payload }))).toEqual([
    {
      kind: "repo.pull",
      payload: {
        id: "acme/specs",
        clonePath: "C:\\SpecDesk\\repos\\quarterly-specs",
        branch: "draft",
      },
    },
    {
      kind: "repo.push",
      payload: {
        id: "acme/specs",
        clonePath: "C:\\SpecDesk\\repos\\quarterly-specs",
        branch: "draft",
      },
    },
  ]);
  await expect(page.getByRole("button", { name: /Switch quarterly-specs to draft/ })).toHaveAttribute(
    "aria-current",
    "true",
  );
  await expect(page.locator(".repo-add")).not.toHaveAttribute("open");
  await page.screenshot({ path: testInfo.outputPath("repository-copy-branches.png"), fullPage: true });
  await page.getByRole("button", { name: "Create a new local copy of acme/specs" }).click();
  await expect(page.locator(".repo-add")).toHaveAttribute("open");
  await expect(page.locator(".repo-register-input")).toBeFocused();
  await expect(page.locator(".repo-local-name-input")).toHaveValue("specs");
  await page.getByRole("button", { name: "Favorite local copy quarterly-specs" }).click();
  await page.getByRole("button", { name: "Favorite branch draft in quarterly-specs" }).click();
  const favoriteFrames = (await sentFrames(page)).filter(
    (frame) => frame.kind === "workspace.favorite",
  );
  expect(favoriteFrames.slice(-2).map((frame) => frame.payload)).toEqual([
    {
      path: "C:\\SpecDesk\\repos\\quarterly-specs",
      repositoryId: "acme/specs",
      kind: "clone",
      isFolder: true,
      favorite: true,
    },
    {
      path: "C:\\SpecDesk\\repos\\quarterly-specs",
      repositoryId: "acme/specs",
      branch: "draft",
      kind: "branch",
      isFolder: true,
      favorite: true,
    },
  ]);
  await page.getByRole("button", { name: "Delete branch draft in quarterly-specs locally" }).click();
  expect(
    (await sentFrames(page)).filter((frame) => frame.kind === "repo.deleteBranch").at(-1)?.payload,
  ).toEqual({
    id: "acme/specs",
    clonePath: "C:\\SpecDesk\\repos\\quarterly-specs",
    branch: "draft",
  });
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
  await expect(page.locator(".repo-operation-confirmation")).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("delete-branch-warning.png"), fullPage: true });
  await page.getByRole("button", { name: "Delete local branch" }).click();
  expect(
    (await sentFrames(page)).filter((frame) => frame.kind === "repo.deleteBranch").at(-1)?.payload,
  ).toMatchObject({ branch: "draft", confirmationToken: "branch-risk-v1" });

  await page.locator(".repo-clone-open").click();
  expect((await sentFrames(page)).filter((frame) => frame.kind === "repo.open").at(-1)?.payload).toEqual({
    url: "https://github.com/acme/specs",
    clonePath: "C:\\SpecDesk\\repos\\quarterly-specs",
  });
  await expect(page.locator('#left-dock .dock-tool[data-tool="files"]')).toBeVisible();

  await page.locator('#left-dock .dock-rail-btn[aria-label="Repositories"]').click();
  await page.getByRole("button", { name: "Switch quarterly-specs to draft and open its files" }).click();
  expect(
    (await sentFrames(page)).filter((frame) => frame.kind === "repo.switchBranch").at(-1)?.payload,
  ).toEqual({
    id: "acme/specs",
    clonePath: "C:\\SpecDesk\\repos\\quarterly-specs",
    branch: "draft",
  });
  await expect(page.locator('#left-dock .dock-tool[data-tool="files"]')).toBeVisible();
});
