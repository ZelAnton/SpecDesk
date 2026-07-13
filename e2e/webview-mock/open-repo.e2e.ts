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
  await goToStart(page);

  await expect(page.locator("#home-view input")).toHaveCount(0);
  await expect(page.locator("#home-view form")).toHaveCount(0);
  await page.locator("#home-view .home-open", { hasText: "Open Repository" }).click();

  await expect(page.locator('#left-dock .dock-tool[data-tool="repositories"]')).toBeVisible();
  await expect(
    page.locator('#left-dock .dock-rail-btn[aria-label="Repositories"]'),
  ).toHaveAttribute("aria-expanded", "true");
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
