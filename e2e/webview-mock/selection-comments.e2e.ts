import type { Page } from "@playwright/test";
import { expect, test } from "../lib/fixtures";
import { emit, loadDoc, sentFrames } from "../lib/mock-host";
import { BASE_URL } from "../lib/serve-bundle";

const TABLE = "| Area | Owner |\n| --- | --- |\n| Checkout | Product |\n";

async function activateEditorMode(page: Page, id: string, name: string): Promise<void> {
  const original = page.locator(id);
  if (await original.isVisible()) {
    await original.click();
    return;
  }
  const toolbar = page.locator("#editor-toolbar");
  await toolbar.locator(".toolbar-overflow-trigger").click();
  await toolbar
    .locator(".toolbar-overflow-menu")
    .getByRole("menuitemradio", { name, exact: true })
    .click();
}

test("Code and WYSIWYG share durable anchored comment threads without changing Markdown", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: true, login: "alice" },
  });
  await loadDoc(page, { path: "docs/ownership.md", text: TABLE });
  await emit(page, { kind: "status", payload: { state: "draft", label: "Saved" } });

  await activateEditorMode(page, "#mode-code", "Code");
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
  await expect(toolbar).toBeHidden();
  const codeComposer = page.locator("#editor .selection-comment-block--code form");
  await expect(codeComposer).toBeVisible();
  const codeTextarea = codeComposer.getByRole("textbox", { name: "New comment" });
  await expect(codeTextarea).toBeFocused();
  await codeTextarea.fill("Confirm the table owner.\nInclude the backup owner too.");
  expect(
    await codeTextarea.evaluate(
      (node) => node instanceof HTMLTextAreaElement && node.scrollHeight <= node.offsetHeight + 1,
    ),
  ).toBe(true);
  await page.screenshot({ path: testInfo.outputPath("selection-comment-code-compose.png"), fullPage: true });
  await activateEditorMode(page, "#mode-formatted", "Formatted");
  const formatted = page.locator("#formatted .ProseMirror");
  const table = formatted.locator("table");
  const carriedComposer = formatted.locator(".selection-comment-block--formatted form");
  await expect(carriedComposer.getByRole("textbox", { name: "New comment" })).toHaveValue(
    "Confirm the table owner.\nInclude the backup owner too.",
  );
  expect(await table.locator("form").count()).toBe(0);
  await carriedComposer.getByRole("button", { name: "Add comment", exact: true }).click();
  const codeThread = page.locator("#editor .selection-comment-block--code");
  await expect(codeThread).toContainText(
    "Confirm the table owner.",
  );
  expect((await sentFrames(page)).filter((frame) => frame.kind === "editor.changed")).toHaveLength(0);

  const firstComment = formatted.locator(".selection-comment-block--formatted").first();
  await expect(firstComment).toContainText("Confirm the table owner.");
  await expect(firstComment).toContainText("alice");
  await expect(firstComment).toContainText("Local");
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

  await firstComment.getByRole("button", { name: "Reply", exact: true }).click();
  const replyComposer = firstComment.locator("form");
  await replyComposer.getByRole("textbox", { name: "Reply" }).fill("The backup owner is Support.");
  await replyComposer.getByRole("button", { name: "Reply", exact: true }).click();
  await expect(firstComment.locator(".selection-comment-reply")).toContainText("Support");

  await firstComment.locator(".selection-comment-message").getByRole("button", { name: "Edit" }).click();
  const editComposer = firstComment.locator("form");
  await editComposer.getByRole("textbox", { name: "Edit comment" }).fill("Confirm the primary owner.");
  await editComposer.getByRole("button", { name: "Save" }).click();
  await expect(firstComment.locator(".selection-comment-message")).toContainText("Confirm the primary owner.");
  await expect(firstComment.locator(".selection-comment-message")).toContainText("Edited");

  const reply = firstComment.locator(".selection-comment-reply");
  const deleteReply = reply.getByRole("button", { name: "Delete" });
  await deleteReply.click();
  await expect(reply).toContainText("The backup owner is Support.");
  const confirmReplyDeletion = reply.getByRole("button", { name: "Confirm deletion" });
  await expect(confirmReplyDeletion).toBeFocused();
  await page.screenshot({
    path: testInfo.outputPath("selection-comment-delete-confirm.png"),
    fullPage: true,
  });
  await page.keyboard.press("Escape");
  await expect(deleteReply).toBeFocused();
  await deleteReply.click();
  await reply.getByRole("button", { name: "Confirm deletion" }).click();
  await expect(firstComment.locator(".selection-comment-reply")).toHaveCount(0);
  await expect(firstComment.getByRole("button", { name: "Reply", exact: true })).toBeFocused();

  await table.locator("td").first().click();
  await page.keyboard.press("Control+A");
  await expect(toolbar).toBeVisible();
  await toolbar.getByRole("button", { name: "Add comment to selection" }).click();
  const formattedComposer = formatted.locator(".selection-comment-block--formatted form");
  await expect(formattedComposer).toBeVisible();
  expect(await table.locator("form").count()).toBe(0);
  await formattedComposer.getByRole("textbox", { name: "New comment" }).fill("WYSIWYG note.");
  await formattedComposer.getByRole("button", { name: "Add comment", exact: true }).click();
  await expect(formatted.locator(".selection-comment-block--formatted")).toHaveCount(2);
  expect((await sentFrames(page)).filter((frame) => frame.kind === "editor.changed")).toHaveLength(0);

  await page.setViewportSize({ width: 760, height: 720 });
  await expect(firstComment).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("selection-comments-formatted-narrow.png"), fullPage: true });

  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: true, login: "bob" },
  });
  await expect(formatted.locator(".selection-comment-block--formatted")).toHaveCount(0);
  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: true, login: "alice" },
  });
  await expect(formatted.locator(".selection-comment-block--formatted")).toHaveCount(2);

  await page.reload();
  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: true, login: "alice" },
  });
  await loadDoc(page, { path: "docs/ownership.md", text: TABLE });
  await activateEditorMode(page, "#mode-formatted", "Formatted");
  await expect(page.locator("#formatted .selection-comment-block--formatted")).toHaveCount(2);
  await expect(page.locator("#formatted .selection-comment-block--formatted").first()).toContainText(
    "Confirm the primary owner.",
  );
});

test("a comment storage failure stays visible and can be retried", async ({ page }, testInfo) => {
  await page.goto(BASE_URL);
  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: true, login: "alice" },
  });
  await loadDoc(page, { path: "docs/storage.md", text: "Selected text\n" });
  await page.locator("#mode-code").click();
  await page.evaluate(() => {
    const original = Storage.prototype.setItem;
    Object.defineProperty(window, "restoreCommentStorage", {
      configurable: true,
      value: () => {
        Storage.prototype.setItem = original;
      },
    });
    Storage.prototype.setItem = () => {
      throw new DOMException("Quota exceeded", "QuotaExceededError");
    };
  });
  const source = page.locator("#editor .cm-content");
  await source.click();
  await page.keyboard.press("Control+A");
  await page.locator("#editor .cm-selectionBackground").first().hover();
  const toolbar = page.getByRole("toolbar", {
    name: "Format selected text or add a comment",
  });
  await toolbar.getByRole("button", { name: "Add comment to selection" }).click();
  const composer = page.locator("#editor .selection-comment-block--code form");
  await composer.getByRole("textbox", { name: "New comment" }).fill("Keep this draft safe.");
  await composer.getByRole("button", { name: "Add comment" }).click();

  const error = page.locator("#editor .selection-comment-persistence-error");
  await expect(error).toContainText("couldn't be saved");
  await expect(toolbar).toBeHidden();
  await loadDoc(page, { path: "docs/other.md", text: "Other document\n" });
  await expect(error).toContainText("1 comment snapshot");
  await expect(page.locator("#editor .selection-comment-message")).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath("selection-comment-storage-error.png"), fullPage: true });
  await page.evaluate(() => {
    (window as unknown as Window & { restoreCommentStorage: () => void }).restoreCommentStorage();
  });
  await error.getByRole("button", { name: "Retry" }).click();
  await expect(error).toHaveCount(0);
  await loadDoc(page, { path: "docs/storage.md", text: "Selected text\n" });
  await expect(page.locator("#editor .selection-comment-block--code")).toContainText(
    "Keep this draft safe.",
  );
});

test("a failed comment load blocks mutations until Retry restores the saved thread", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: true, login: "alice" },
  });
  await loadDoc(page, { path: "docs/load-failure.md", text: "Selected text\n" });
  await page.locator("#mode-code").click();
  const source = page.locator("#editor .cm-content");
  await source.click();
  await page.keyboard.press("Control+A");
  await page.locator("#editor .cm-selectionBackground").first().hover();
  const toolbar = page.getByRole("toolbar", {
    name: "Format selected text or add a comment",
  });
  await toolbar.getByRole("button", { name: "Add comment to selection" }).click();
  const composer = page.locator("#editor .selection-comment-block--code form");
  await composer.getByRole("textbox", { name: "New comment" }).fill("Preserve stored thread.");
  await composer.getByRole("button", { name: "Add comment" }).click();
  await expect(page.locator("#editor .selection-comment-message")).toContainText(
    "Preserve stored thread.",
  );
  await page.waitForFunction(() =>
    Object.keys(localStorage).some((key) => key.startsWith("specdesk.selection-comments.")),
  );

  await page.evaluate(() => {
    const original = Storage.prototype.getItem;
    Object.defineProperty(window, "restoreCommentLoad", {
      configurable: true,
      value: () => {
        Storage.prototype.getItem = original;
      },
    });
    Storage.prototype.getItem = function (key: string): string | null {
      if (key.startsWith("specdesk.selection-comments.")) {
        throw new DOMException("Storage unavailable", "InvalidStateError");
      }
      return original.call(this, key);
    };
  });
  await loadDoc(page, { path: "docs/load-failure.md", text: "Selected text\n" });
  const error = page.locator("#editor .selection-comment-persistence-error");
  await expect(error).toContainText("unavailable until their saved snapshot loads");
  await source.click();
  await page.keyboard.press("Control+A");
  await page.locator("#editor .cm-selectionBackground").first().hover();
  const unavailable = toolbar.getByRole("button", {
    name: "Comments unavailable until saved comments are loaded",
  });
  await expect(unavailable).toBeDisabled();
  await expect(page.locator("#editor form.selection-comment-compose--inline")).toHaveCount(0);
  await page.screenshot({
    path: testInfo.outputPath("selection-comment-load-error.png"),
    fullPage: true,
  });

  await page.evaluate(() => {
    (window as unknown as Window & { restoreCommentLoad: () => void }).restoreCommentLoad();
  });
  await error.getByRole("button", { name: "Retry" }).click();
  await expect(error).toHaveCount(0);
  await expect(page.locator("#editor .selection-comment-message")).toContainText(
    "Preserve stored thread.",
  );
});

test("window close waits for comment durability and cancels the native handshake on failure", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: true, login: "alice" },
  });
  await loadDoc(page, { path: "docs/close-comments.md", text: "Selected text\n" });
  await page.locator("#mode-code").click();
  await page.evaluate(() => {
    const original = Storage.prototype.setItem;
    Object.defineProperty(window, "restoreCloseCommentStorage", {
      configurable: true,
      value: () => {
        Storage.prototype.setItem = original;
      },
    });
    Storage.prototype.setItem = () => {
      throw new DOMException("Quota exceeded", "QuotaExceededError");
    };
  });
  const source = page.locator("#editor .cm-content");
  await source.click();
  await page.keyboard.press("Control+A");
  await page.locator("#editor .cm-selectionBackground").first().hover();
  const toolbar = page.getByRole("toolbar", {
    name: "Format selected text or add a comment",
  });
  await toolbar.getByRole("button", { name: "Add comment to selection" }).click();
  const composer = page.locator("#editor .selection-comment-block--code form");
  await composer.getByRole("textbox", { name: "New comment" }).fill("Persist before closing.");
  await composer.getByRole("button", { name: "Add comment" }).click();

  await emit(page, { kind: "window.closeRequested", payload: { requestId: 91 } });
  await expect
    .poll(async () =>
      (await sentFrames(page)).some(
        (frame) =>
          frame.kind === "window.close" &&
          (frame.payload as { requestId?: number } | undefined)?.requestId === -91,
      ),
    )
    .toBe(true);
  const error = page.locator("#editor .selection-comment-persistence-error");
  await expect(error).toContainText("before closing");
  await page.screenshot({
    path: testInfo.outputPath("selection-comment-close-blocked.png"),
    fullPage: true,
  });
  expect(
    (await sentFrames(page)).some(
      (frame) =>
        frame.kind === "window.close" &&
        (frame.payload as { requestId?: number } | undefined)?.requestId === 91,
    ),
  ).toBe(false);

  await emit(page, {
    kind: "window.closeCompleted",
    payload: { requestId: 91, succeeded: false },
  });
  await page.evaluate(() => {
    (
      window as unknown as Window & { restoreCloseCommentStorage: () => void }
    ).restoreCloseCommentStorage();
  });
  await error.getByRole("button", { name: "Retry" }).click();
  await expect(error).toHaveCount(0);
  await emit(page, { kind: "window.closeRequested", payload: { requestId: 92 } });
  await expect
    .poll(async () =>
      (await sentFrames(page)).some(
        (frame) =>
          frame.kind === "window.close" &&
          (frame.payload as { requestId?: number } | undefined)?.requestId === 92,
      ),
    )
    .toBe(true);
});

test("an unresolved external edit shows a detached thread instead of guessing", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: true, login: "alice" },
  });
  await loadDoc(page, {
    path: "docs/detached.md",
    text: "Before\nselected text\nAfter\n",
  });
  await page.locator("#mode-code").click();
  const source = page.locator("#editor .cm-content");
  await source.click();
  await page.keyboard.press("Control+Home");
  await page.keyboard.press("ArrowDown");
  await page.keyboard.press("Home");
  await page.keyboard.down("Shift");
  await page.keyboard.press("End");
  await page.keyboard.up("Shift");
  await page.locator("#editor .cm-selectionBackground").first().hover();
  const toolbar = page.getByRole("toolbar", {
    name: "Format selected text or add a comment",
  });
  await toolbar.getByRole("button", { name: "Add comment to selection" }).click();
  const composer = page.locator("#editor .selection-comment-block--code form");
  await composer.getByRole("textbox", { name: "New comment" }).fill("Keep this review thread.");
  await composer.getByRole("button", { name: "Add comment" }).click();

  await loadDoc(page, {
    path: "docs/detached.md",
    text: "Inserted\nBefore\nAfter\n",
  });
  const detached = page.locator(
    '#editor .selection-comment-block--code[data-anchor-state="detached"]',
  );
  await expect(detached).toContainText("This thread is detached");
  await expect(detached).toContainText("Keep this review thread.");
  await page.screenshot({
    path: testInfo.outputPath("selection-comment-detached.png"),
    fullPage: true,
  });
});
