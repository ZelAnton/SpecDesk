import { expect, test } from "@playwright/test";
import { emit, installMockHost, loadDoc, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

const TREE = {
  kind: "tree",
  payload: {
    root: "C:\\specs\\repo",
    nodes: [
      {
        name: "guides",
        path: "C:\\specs\\repo\\guides",
        isDirectory: true,
        children: [
          {
            name: "intro.md",
            path: "C:\\specs\\repo\\guides\\intro.md",
            isDirectory: false,
            children: [],
          },
        ],
      },
      { name: "README.md", path: "C:\\specs\\repo\\README.md", isDirectory: false, children: [] },
    ],
  },
};

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

async function openFilesTool(page: import("@playwright/test").Page): Promise<void> {
  await page.locator('#left-dock .dock-rail-btn[aria-label="Files"]').click();
}

test("the file navigator renders the host's tree and opens a clicked file", async ({ page }) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await loadDoc(page, { path: "doc.md", text: "# Doc" });

  // Loading a document asks the host for the tree of its folder.
  expect((await sentFrames(page)).some((f) => f.kind === "tree.request")).toBe(true);

  await emit(page, TREE);
  await openFilesTool(page);

  await expect(page.locator("#left-dock .file-tree-root")).toHaveText("repo");
  await expect(page.locator("#left-dock .file-tree-folder")).toHaveText("guides");
  await expect(page.locator("#left-dock .file-tree-file")).toHaveText(["intro.md", "README.md"]);

  await page.locator("#left-dock .file-tree-file", { hasText: "README.md" }).click();
  const opened = (await sentFrames(page)).find((f) => f.kind === "doc.open");
  expect(opened?.payload).toMatchObject({ path: "C:\\specs\\repo\\README.md" });
});

test("a folder row collapses and expands its children", async ({ page }) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await loadDoc(page, { path: "doc.md", text: "# Doc" });
  await emit(page, TREE);
  await openFilesTool(page);

  const folder = page.locator("#left-dock .file-tree-folder");
  await expect(folder).toHaveAttribute("aria-expanded", "true");
  await expect(page.locator("#left-dock .file-tree-file", { hasText: "intro.md" })).toBeVisible();

  await folder.click();
  await expect(folder).toHaveAttribute("aria-expanded", "false");
  await expect(page.locator("#left-dock .file-tree-file", { hasText: "intro.md" })).toBeHidden();
});

test("the Start screen opens a file or a folder via the host pickers", async ({ page }) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await loadDoc(page, { path: "doc.md", text: "# Doc" });

  // Go to the Start screen via the left-rail navigator.
  await page.locator('#left-dock .dock-rail-btn[aria-checked="true"]').click();
  await page.locator('#left-dock .nav-item[data-view="home"]').click();

  await page.locator("#home-view .home-open", { hasText: "Open a folder" }).click();
  await page.locator("#home-view .home-open", { hasText: "Open a file" }).click();

  const sent = await sentFrames(page);
  expect(sent.some((f) => f.kind === "folder.open")).toBe(true);
  expect(sent.some((f) => f.kind === "doc.open")).toBe(true);
});
