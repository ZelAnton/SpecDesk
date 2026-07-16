import { expect, test, type Locator, type Page } from "@playwright/test";
import { openDockTool } from "../lib/dock";
import { emit, installMockHost, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

const REPOSITORY_STATE = {
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

const DISK_TREE = {
  kind: "tree",
  payload: {
    root: "C:\\specs\\repo",
    requestId: 0,
    nodes: [
      {
        name: "README.md",
        path: "C:\\specs\\repo\\README.md",
        isDirectory: false,
        children: [],
        hasChildren: false,
      },
    ],
  },
};

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

async function assertInsideViewport(page: Page, confirmation: Locator): Promise<void> {
  const box = await confirmation.boundingBox();
  const viewport = page.viewportSize();
  if (box === null || viewport === null) {
    throw new Error("The destructive confirmation must participate in viewport layout");
  }
  expect(box.x).toBeGreaterThanOrEqual(0);
  expect(box.y).toBeGreaterThanOrEqual(0);
  expect(box.x + box.width).toBeLessThanOrEqual(viewport.width);
  expect(box.y + box.height).toBeLessThanOrEqual(viewport.height);
  const widths = await confirmation.evaluate((element) => ({
    client: element.clientWidth,
    scroll: element.scrollWidth,
  }));
  expect(widths.scroll).toBeLessThanOrEqual(widths.client);
}

for (const viewport of [
  { name: "normal", width: 1280, height: 800 },
  { name: "narrow", width: 620, height: 720 },
] as const) {
  test(`repository deletion is an inline two-step action at ${viewport.name} width`, async ({
    page,
  }, testInfo) => {
    await page.setViewportSize(viewport);
    await page.goto(BASE_URL);
    await waitForSent(page, "ready");
    await emit(page, REPOSITORY_STATE);
    await openDockTool(page, "left", "Repositories");

    const row = page.locator(".repo-row", { hasText: "acme/specs" });
    await row.locator(".repo-row-header").hover();
    await row.getByRole("button", { name: "Remove repository acme/specs from SpecDesk" }).click();

    const confirmation = row.locator(".destructive-confirmation");
    await expect(confirmation).toContainText("Nothing has been deleted.");
    expect((await sentFrames(page)).some((frame) => frame.kind === "repo.unregister")).toBe(false);
    await assertInsideViewport(page, confirmation);
    await page.screenshot({
      path: testInfo.outputPath(`repository-confirmation-${viewport.name}.png`),
      fullPage: true,
    });

    await confirmation.getByRole("button", { name: "Confirm deletion" }).click();
    await expect(page.locator(".repo-register-input")).toBeFocused();
    await expect
      .poll(async () => (await sentFrames(page)).filter((frame) => frame.kind === "repo.unregister"))
      .toHaveLength(1);
  });

  test(`Disk file deletion is an inline two-step action at ${viewport.name} width`, async ({
    page,
  }, testInfo) => {
    await page.setViewportSize(viewport);
    await page.goto(BASE_URL);
    await waitForSent(page, "ready");
    await emit(page, DISK_TREE);
    await openDockTool(page, "left", "Disk");

    const row = page.locator(".file-tree-row", { hasText: "README.md" });
    await row.hover();
    await row.getByRole("button", { name: "Delete file README.md" }).click();

    const confirmation = row.locator("xpath=..", { has: page.locator(".destructive-confirmation") })
      .locator(".destructive-confirmation");
    await expect(confirmation).toContainText("Folders are never deleted.");
    expect((await sentFrames(page)).some((frame) => frame.kind === "file.delete")).toBe(false);
    await assertInsideViewport(page, confirmation);
    await page.screenshot({
      path: testInfo.outputPath(`disk-confirmation-${viewport.name}.png`),
      fullPage: true,
    });

    await confirmation.getByRole("button", { name: "Confirm deletion" }).click();
    await expect(page.locator(".file-tree-filter")).toBeFocused();
    await expect
      .poll(async () => (await sentFrames(page)).filter((frame) => frame.kind === "file.delete"))
      .toHaveLength(1);
    expect((await sentFrames(page)).find((frame) => frame.kind === "file.delete")?.payload).toEqual({
      path: "C:\\specs\\repo\\README.md",
      root: "C:\\specs\\repo",
      requestId: 1,
    });
  });
}
