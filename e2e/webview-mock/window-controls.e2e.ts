import { expect, test } from "@playwright/test";
import { emit, installMockHost, loadDoc, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

test("in-content window controls route commands and reflect native maximize state", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");

  await loadDoc(page, {
    path: "C:\\work\\specs\\window.md",
    text: "# Window chrome\n",
  });
  const search = page.locator("#toolbar-search");
  const controls = page.locator("#window-controls");
  const close = page.getByRole("button", { name: "Close" });
  await expect(search).toBeVisible();
  const restoredControls = await controls.boundingBox();
  const restoredSearch = await search.boundingBox();
  if (restoredControls === null || restoredSearch === null) {
    throw new Error("titlebar controls must have rendered geometry");
  }
  expect(restoredControls.x + restoredControls.width).toBeGreaterThanOrEqual(1279);
  expect(restoredSearch.x + restoredSearch.width).toBeLessThanOrEqual(restoredControls.x);
  await page.screenshot({ path: testInfo.outputPath("titlebar-restored.png"), fullPage: true });

  await page.setViewportSize({ width: 420, height: 720 });
  await expect(search).toBeVisible();
  await expect(close).toBeVisible();
  const narrowControls = await controls.boundingBox();
  const narrowSearch = await search.boundingBox();
  if (narrowControls === null || narrowSearch === null) {
    throw new Error("narrow titlebar controls must retain rendered geometry");
  }
  expect(narrowControls.x + narrowControls.width).toBeGreaterThanOrEqual(419);
  expect(narrowSearch.x + narrowSearch.width).toBeLessThanOrEqual(narrowControls.x);
  await page.screenshot({ path: testInfo.outputPath("titlebar-narrow.png"), fullPage: true });
  await page.setViewportSize({ width: 1280, height: 800 });

  await expect(page.getByRole("group", { name: "Window controls" })).toBeVisible();
  await page.locator("#toolbar").click({ position: { x: 8, y: 20 } });
  let kinds = (await sentFrames(page)).map((frame) => frame.kind);
  expect(kinds.filter((kind) => kind === "window.drag")).toHaveLength(1);
  expect(kinds.filter((kind) => kind === "window.toggleMaximize")).toHaveLength(0);

  // Reload for an independent real double-click sequence: Playwright supplies the browser's trusted
  // mousedown click counts, while the mock host bridge gets a fresh outbound-frame list.
  await page.reload();
  await waitForSent(page, "ready");
  await page.getByRole("button", { name: "Minimize" }).click();
  await page.getByRole("button", { name: "Maximize" }).click();
  await page.locator("#app-title").dblclick();

  kinds = (await sentFrames(page)).map((frame) => frame.kind);
  expect(kinds).toContain("window.minimize");
  expect(kinds.filter((kind) => kind === "window.toggleMaximize")).toHaveLength(2);
  expect(kinds.filter((kind) => kind === "window.drag")).toHaveLength(1);

  await emit(page, { kind: "window.state", payload: { maximized: true } });
  const restore = page.getByRole("button", { name: "Restore" });
  await expect(restore).toHaveAttribute("aria-pressed", "true");
  await expect(restore).toHaveAttribute("data-window-state", "maximized");
  await expect(restore.locator(".window-control-glyph--restore")).toBeVisible();
  await expect(restore.locator(".window-control-glyph--maximize")).toBeHidden();
  await page.screenshot({ path: testInfo.outputPath("titlebar-maximized-state.png"), fullPage: true });
  await emit(page, { kind: "window.state", payload: { maximized: false } });
  await expect(page.getByRole("button", { name: "Maximize" })).toHaveAttribute(
    "aria-pressed",
    "false",
  );

  await close.click();
  expect((await sentFrames(page)).some((frame) => frame.kind === "window.close")).toBe(true);
  await page.screenshot({ path: testInfo.outputPath("custom-window-controls.png"), fullPage: true });
});
