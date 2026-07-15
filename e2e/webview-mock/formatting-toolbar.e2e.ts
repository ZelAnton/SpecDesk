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
  await expect(toolbar).toHaveAttribute("disabled", "");
  await expect(toolbar.getByRole("button").first()).toBeDisabled();

  await emit(page, { kind: "status", payload: { state: "draft", label: "Saved" } });
  await expect(toolbar).not.toHaveAttribute("disabled", "");
  await expect(toolbar.getByRole("button").first()).toBeEnabled();
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
  await page.locator('#left-dock .dock-rail-btn[aria-label="Navigator"]').click();
  const source = page.locator("#editor .cm-content");
  await source.click();
  await page.keyboard.press("Control+A");
  const selectionToolbar = page.getByRole("toolbar", {
    name: "Format selected text or add a comment",
  });
  await expect(selectionToolbar).toBeVisible();
  await page.keyboard.press("Tab");
  await expect(selectionToolbar.getByRole("button", { name: "Bold", exact: true })).toBeFocused();
  await page.keyboard.press("Escape");
  await expect(selectionToolbar).toBeHidden();
  await expect(source).toBeFocused();
  await page.locator("#editor .cm-selectionBackground").first().hover();
  await expect(selectionToolbar).toBeVisible();
  const editorBox = await page.locator("#editor").boundingBox();
  const selectionBox = await selectionToolbar.boundingBox();
  if (editorBox === null || selectionBox === null) throw new Error("missing editor geometry");
  expect(selectionBox.x).toBeGreaterThanOrEqual(editorBox.x);
  expect(selectionBox.y).toBeGreaterThanOrEqual(editorBox.y);
  expect(selectionBox.x + selectionBox.width).toBeLessThanOrEqual(editorBox.x + editorBox.width);
  expect(selectionBox.y + selectionBox.height).toBeLessThanOrEqual(editorBox.y + editorBox.height);
  await toolbar.getByRole("button", { name: "Insert link (Ctrl+K)" }).click();
  await expect(source).toContainText("[Title](https://)");

  await page.setViewportSize({ width: 720, height: 700 });
  await page.getByRole("button", { name: "Wrap: on" }).click();
  await source.click();
  await page.keyboard.press("Control+A");
  await page.keyboard.type(`${"long source text ".repeat(12)}target`);
  await page.keyboard.press("Control+End");
  await page.keyboard.press("Control+Shift+ArrowLeft");
  await expect(selectionToolbar).toBeVisible();
  const narrowEditorBox = await page.locator("#editor").boundingBox();
  const narrowSelectionBox = await selectionToolbar.boundingBox();
  if (narrowEditorBox === null || narrowSelectionBox === null) {
    throw new Error("missing narrow editor geometry");
  }
  expect(narrowSelectionBox.x).toBeGreaterThanOrEqual(narrowEditorBox.x);
  expect(narrowSelectionBox.x + narrowSelectionBox.width).toBeLessThanOrEqual(
    narrowEditorBox.x + narrowEditorBox.width,
  );
  expect(narrowSelectionBox.y + narrowSelectionBox.height).toBeLessThanOrEqual(
    narrowEditorBox.y + narrowEditorBox.height,
  );

  await source.click();
  await page.keyboard.press("Control+A");
  await page.keyboard.type(Array.from({ length: 80 }, (_, index) => `line ${index}`).join("\n"));
  await page.keyboard.press("Control+A");
  await page.locator("#editor .cm-scroller").evaluate((scroller) => {
    scroller.scrollTop = scroller.scrollHeight;
  });
  await expect(selectionToolbar).toBeHidden();
  await page.locator("#editor .cm-selectionBackground").last().hover();
  await expect(selectionToolbar).toBeVisible();
  const scrolledSelectionBox = await selectionToolbar.boundingBox();
  if (scrolledSelectionBox === null) throw new Error("missing scrolled selection geometry");
  expect(scrolledSelectionBox.y).toBeGreaterThanOrEqual(narrowEditorBox.y);
  expect(scrolledSelectionBox.y + scrolledSelectionBox.height).toBeLessThanOrEqual(
    narrowEditorBox.y + narrowEditorBox.height,
  );

  await page.screenshot({
    path: testInfo.outputPath("complete-formatting-toolbar.png"),
    fullPage: true,
  });
});
