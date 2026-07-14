import { expect, test } from "@playwright/test";
import { emit, installMockHost, loadDoc, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

const TREE = {
  kind: "tree",
  payload: {
    root: "C:\\specs\\repo",
    requestId: 0,
    nodes: [
      {
        name: "guides",
        path: "C:\\specs\\repo\\guides",
        isDirectory: true,
        children: [],
        hasChildren: true,
      },
      { name: "README.md", path: "C:\\specs\\repo\\README.md", isDirectory: false, children: [], hasChildren: false },
    ],
  },
};

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

async function openFilesTool(page: import("@playwright/test").Page): Promise<void> {
  await page.locator('#left-dock .dock-rail-btn[aria-label="Folders"]').click();
}

test("the file navigator renders identity, filter, and the host root level", async ({ page }, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await loadDoc(page, { path: "C:\\specs\\repo\\README.md", text: "# Doc" });

  // Loading a document asks the host for the tree of its folder.
  expect((await sentFrames(page)).some((f) => f.kind === "tree.request")).toBe(true);

  await emit(page, TREE);
  await emit(page, {
    kind: "workspace.context",
    payload: {
      repository: "octo/specs",
      repositoryRoot: "C:\\specs\\repo",
      branch: "spec/refunds",
      branchState: "named",
      defaultBranch: "main",
      path: "README.md",
    },
  });
  await openFilesTool(page);

  await expect(page.locator("#left-dock .file-tree-root")).toHaveText("repo");
  await expect(page.locator("#left-dock .file-tree-branch-name")).toHaveText("spec/refunds");
  await expect(page.locator("#left-dock .file-tree-folder")).toHaveText("guides");
  await expect(page.locator("#left-dock .file-tree-file")).toHaveText(["README.md"]);
  await expect(page.locator("#left-dock .file-tree-folder")).toHaveAttribute("aria-expanded", "false");

  const filter = page.locator("#left-dock .file-tree-filter");
  await filter.fill("read");
  await expect(page.locator("#left-dock .file-tree-folder")).toHaveCount(0);
  await expect(page.locator("#left-dock .file-tree-file")).toHaveText("README.md");
  await filter.fill("");
  await page.screenshot({ path: testInfo.outputPath("folder-panel.png") });

  await page.locator("#left-dock .file-tree-file", { hasText: "README.md" }).click();
  const opened = (await sentFrames(page)).find((f) => f.kind === "doc.open");
  expect(opened?.payload).toMatchObject({ path: "C:\\specs\\repo\\README.md" });
});

test("a folder row requests and expands one correlated level", async ({ page }) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await loadDoc(page, { path: "doc.md", text: "# Doc" });
  await emit(page, TREE);
  await openFilesTool(page);

  const folder = page.locator("#left-dock .file-tree-folder");
  await expect(folder).toHaveAttribute("aria-expanded", "false");
  await folder.click();
  const request = (await sentFrames(page)).findLast((frame) => frame.kind === "tree.request");
  expect(request?.payload).toMatchObject({ path: "C:\\specs\\repo\\guides" });
  const requestId = Number((request?.payload as { requestId?: number } | undefined)?.requestId);
  expect(requestId).toBeGreaterThan(0);
  await emit(page, {
    kind: "tree",
    payload: {
      root: "C:\\specs\\repo\\guides",
      requestId,
      nodes: [{ name: "intro.md", path: "C:\\specs\\repo\\guides\\intro.md", isDirectory: false, children: [], hasChildren: false }],
    },
  });
  await expect(page.locator("#left-dock .file-tree-folder")).toHaveAttribute("aria-expanded", "true");
  await expect(page.locator("#left-dock .file-tree-file", { hasText: "intro.md" })).toBeVisible();
});

test("the Start screen opens a file or a folder via the host pickers", async ({ page }) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await loadDoc(page, { path: "doc.md", text: "# Doc" });

  // Go to the Start screen via the left-rail navigator.
  await page.locator('#left-dock .dock-rail-btn[aria-label="Navigator"]').click();
  await page.locator('#left-dock .nav-item[data-view="home"]').click();

  await page.locator("#home-view .home-open", { hasText: "Open a file" }).click();
  await page.locator("#home-view .home-open", { hasText: "Open a folder" }).click();

  const sent = await sentFrames(page);
  expect(sent.some((f) => f.kind === "folder.open")).toBe(true);
  expect(sent.some((f) => f.kind === "doc.open")).toBe(true);
});
