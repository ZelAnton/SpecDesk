import { existsSync } from "node:fs";
import { resolve } from "node:path";
import { expect, test } from "@playwright/test";
import { type FullApp, launchAndAttach, stopAndDump } from "../lib/full-app";

test.describe.configure({ mode: "serial" });

let ctx: FullApp;

test.beforeAll(async () => {
  ctx = await launchAndAttach();
});

test.afterAll(async ({}, testInfo) => {
  await stopAndDump(ctx, testInfo);
});

test("Disk deletes one local file only after inline confirmation", async ({}, testInfo) => {
  const { app, page } = ctx;
  const target = resolve(app.repo, "spec-a.md");
  expect(existsSync(target)).toBe(true);

  const disk = page.locator('#left-dock .dock-rail-btn[aria-label="Disk"]');
  if ((await disk.getAttribute("aria-expanded")) !== "true") {
    await disk.click();
  }
  const row = page.locator("#left-dock .file-tree-row", { hasText: "spec-a.md" });
  await expect(row).toBeVisible();
  await row.hover();
  await row.getByRole("button", { name: "Delete file spec-a.md" }).click();

  const confirmation = row.locator("xpath=..").locator(".destructive-confirmation");
  await expect(confirmation).toContainText("Nothing has been deleted.");
  expect(existsSync(target)).toBe(true);
  await page.screenshot({
    path: testInfo.outputPath("disk-delete-confirmation.png"),
    fullPage: true,
  });

  await confirmation.getByRole("button", { name: "Confirm deletion" }).click();
  await expect(page.locator("#left-dock .file-tree-filter")).toBeFocused();
  await expect.poll(() => existsSync(target)).toBe(false);
  await expect(page.locator("#left-dock .file-tree-file", { hasText: "spec-a.md" })).toHaveCount(0);
  await expect(page.locator("#left-dock .file-tree-file", { hasText: "welcome.md" })).toBeVisible();
  await expect(page.locator("#left-dock .file-tree-filter")).toBeFocused();
  await page.screenshot({ path: testInfo.outputPath("disk-delete-completed.png"), fullPage: true });
});
