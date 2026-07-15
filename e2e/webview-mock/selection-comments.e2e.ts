import { expect, test } from "../lib/fixtures";
import { emit, loadDoc, sentFrames } from "../lib/mock-host";
import { BASE_URL } from "../lib/serve-bundle";

const TABLE = "| Area | Owner |\n| --- | --- |\n| Checkout | Product |\n";

test("Code and WYSIWYG share a stationary local-comment toolbar without changing Markdown", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await loadDoc(page, { path: "docs/ownership.md", text: TABLE });
  await emit(page, { kind: "status", payload: { state: "draft", label: "Saved" } });

  await page.locator("#mode-code").click();
  const source = page.locator("#editor .cm-content");
  await source.click();
  await page.keyboard.press("Control+A");
  await page.locator("#editor .cm-selectionBackground").first().hover();
  const toolbar = page.getByRole("toolbar", {
    name: "Format selected text or add a comment",
  });
  await expect(toolbar).toBeVisible();
  await expect(toolbar.getByRole("button", { name: "Bold", exact: true })).toBeVisible();
  await expect(toolbar.getByRole("button", { name: "Code", exact: true })).toBeVisible();
  const stationary = await toolbar.boundingBox();
  await page.mouse.move((stationary?.x ?? 0) + 4, (stationary?.y ?? 0) + 4);
  expect(await toolbar.boundingBox()).toEqual(stationary);

  await toolbar.getByRole("button", { name: "Add comment to selection" }).click();
  await expect(toolbar).toContainText("not posted to GitHub");
  await toolbar.getByRole("textbox", { name: "Comment text" }).fill("Confirm the table owner.");
  await toolbar.getByRole("button", { name: "Add comment", exact: true }).click();
  await expect(page.locator("#editor .selection-comment-block--code")).toContainText(
    "Confirm the table owner.",
  );
  expect((await sentFrames(page)).filter((frame) => frame.kind === "editor.changed")).toHaveLength(0);

  await page.locator("#mode-formatted").click();
  const formatted = page.locator("#formatted .ProseMirror");
  const table = formatted.locator("table");
  const firstComment = formatted.locator(".selection-comment-block--formatted").first();
  await expect(firstComment).toContainText("Confirm the table owner.");
  expect(
    await firstComment.locator("strong").evaluate((label) => getComputedStyle(label, "::after").content),
  ).toContain("local, not on GitHub");
  expect(
    await formatted.evaluate((root) => {
      const tableNode = root.querySelector("table");
      const commentNode = root.querySelector(".selection-comment-block--formatted");
      return (
        tableNode !== null &&
        commentNode !== null &&
        Boolean(tableNode.compareDocumentPosition(commentNode) & Node.DOCUMENT_POSITION_FOLLOWING)
      );
    }),
  ).toBe(true);

  await formatted.click();
  await page.keyboard.press("Control+A");
  await table.locator("td").first().dispatchEvent("mousemove", { clientX: 320, clientY: 180 });
  await expect(toolbar).toBeVisible();
  await toolbar.getByRole("button", { name: "Add comment to selection" }).click();
  await toolbar.getByRole("textbox", { name: "Comment text" }).fill("WYSIWYG note.");
  await toolbar.getByRole("button", { name: "Add comment", exact: true }).click();
  await expect(formatted.locator(".selection-comment-block--formatted")).toHaveCount(2);
  expect((await sentFrames(page)).filter((frame) => frame.kind === "editor.changed")).toHaveLength(0);

  await page.screenshot({ path: testInfo.outputPath("selection-comments.png"), fullPage: true });
});
