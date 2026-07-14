import { expect, test } from "@playwright/test";
import { installMockHost, loadDoc, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

test("Split mirrors a distinct sand hover wash in both directions", async ({ page }, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await loadDoc(page, {
    path: "C:\\work\\specs\\hover.md",
    text: "# Hover\n\nFirst paragraph.\n\nSecond paragraph.\n",
  });
  await expect(page.locator('#panes[data-mode="split"]')).toBeVisible();

  const firstSource = page.locator("#editor .cm-line", { hasText: "First paragraph." });
  const firstFormatted = page.locator("#formatted .ProseMirror p", {
    hasText: "First paragraph.",
  });
  await firstSource.hover();
  await expect(firstSource).toHaveClass(/cm-hover-line/);
  await expect(firstFormatted).toHaveClass(/sd-hover-block/);

  const colors = await page.evaluate(() => ({
    hover: getComputedStyle(document.documentElement).getPropertyValue("--ed-hover").trim(),
    active: getComputedStyle(document.documentElement).getPropertyValue("--ed-active").trim(),
  }));
  expect(colors.hover).not.toBe(colors.active);
  await expect(firstSource).toHaveCSS("background-color", "rgb(251, 240, 205)");
  await expect(firstFormatted).toHaveCSS("background-color", "rgb(251, 240, 205)");

  const secondSource = page.locator("#editor .cm-line", { hasText: "Second paragraph." });
  const secondFormatted = page.locator("#formatted .ProseMirror p", {
    hasText: "Second paragraph.",
  });
  await secondFormatted.hover();
  await expect(secondFormatted).toHaveClass(/sd-hover-block/);
  await expect(secondSource).toHaveClass(/cm-hover-line/);
  await expect(firstSource).not.toHaveClass(/cm-hover-line/);
  await expect(firstFormatted).not.toHaveClass(/sd-hover-block/);

  await page.screenshot({ path: testInfo.outputPath("split-hover-sand.png"), fullPage: true });
});
