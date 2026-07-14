import { expect, test } from "@playwright/test";
import { emit, installMockHost, loadDoc, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

test("the complete formatting toolbar stays discoverable and edits source Markdown", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await loadDoc(page, { path: "C:\\work\\spec.md", text: "Title" });

  const toolbar = page.locator("#format-bar");
  await expect(toolbar).toBeVisible();
  await expect(toolbar).toBeDisabled();

  await emit(page, { kind: "status", payload: { state: "draft", label: "Saved" } });
  await expect(toolbar).toBeEnabled();
  for (const name of [
    "Bold (Ctrl+B)",
    "Inline code (Ctrl+`)",
    "Heading 3 (Ctrl+Alt+3)",
    "Bullet list (Ctrl+Shift+8)",
    "Insert link (Ctrl+K)",
    "Insert table (Ctrl+Alt+T)",
    "Insert image reference (Ctrl+Shift+I)",
    "Insert divider (Ctrl+Shift+R)",
  ]) {
    await expect(page.getByRole("button", { name })).toBeVisible();
  }

  await page.locator("#mode-code").click();
  const source = page.locator("#editor .cm-content");
  await source.click();
  await page.keyboard.press("Control+A");
  await page.getByRole("button", { name: "Insert link (Ctrl+K)" }).click();
  await expect(source).toContainText("[Title](https://)");

  await page.screenshot({ path: testInfo.outputPath("complete-formatting-toolbar.png"), fullPage: true });
});
