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
  await page.locator('#left-dock .dock-rail-btn[aria-label="Navigation"]').click();
  await page.locator('#left-dock .nav-item[data-view="home"]').click();
  await expect(page.locator("#home-view")).toBeVisible();
}

test("the Start screen opens a GitHub repo and reveals Files when its tree arrives", async ({
  page,
}) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await goToStart(page);

  const note = page.locator("#home-view .home-open-repo-note");
  await page.locator("#home-view .home-open-repo-input").fill("acme/specs");
  await page.locator("#home-view .home-open-repo-btn").click();

  // Clone-and-open: sends `repo.open` with the typed value, and shows the immediate "Opening…" feedback.
  expect((await sentFrames(page)).find((f) => f.kind === "repo.open")?.payload).toMatchObject({
    url: "acme/specs",
  });
  await expect(note).toBeVisible();

  // The host answers with the cloned repo's tree — the Files panel is revealed and the busy note clears.
  await emit(page, TREE);
  await expect(page.locator('#left-dock [data-tool="files"]')).toBeVisible();
  await expect(page.locator("#left-dock .file-tree-file")).toHaveText(["README.md"]);
  await expect(note).toBeHidden();
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
  await expect(page.locator('#left-dock [data-tool="files"]')).toBeVisible();
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

test("clicking a repository in the Repositories panel clones-and-opens it", async ({ page }) => {
  const STATE = {
    kind: "workspace.state",
    payload: {
      recent: [],
      favorites: [],
      repositories: [{ id: "acme/specs", name: "acme/specs", url: "https://github.com/acme/specs" }],
    },
  };

  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await emit(page, STATE);

  await page.locator('#left-dock .dock-rail-btn[aria-label="Repositories"]').click();
  await page.locator('#left-dock [data-tool="repositories"] .repo-open').click();

  expect((await sentFrames(page)).find((f) => f.kind === "repo.open")?.payload).toMatchObject({
    url: "https://github.com/acme/specs",
  });

  // The reveal arms on repo.open too: the returned tree surfaces the Files panel.
  await emit(page, TREE);
  await expect(page.locator('#left-dock [data-tool="files"]')).toBeVisible();
});
