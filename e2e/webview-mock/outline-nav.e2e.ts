import { expect, test } from "@playwright/test";
import { installMockHost, loadDoc, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

// A document tall enough that the target heading sits well below the fold, so a real jump produces a
// large, unambiguous scrollTop (a short doc barely scrolls and would make the assertion meaningless).
const DOC = [
  "# Title",
  "",
  ...Array.from({ length: 200 }, (_, i) => `filler line ${i}`),
  "## Deep Heading",
  "",
  "body",
].join("\n");

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

function editorScrollTop(page: import("@playwright/test").Page): Promise<number> {
  return page.locator("#editor .cm-scroller").evaluate((el) => (el as HTMLElement).scrollTop);
}

async function openOutline(page: import("@playwright/test").Page): Promise<void> {
  await page.locator('#right-dock .dock-rail-btn[aria-label="Outline"]').click();
}

test("the outline lists the document headings and jumps the editor to a clicked heading", async ({
  page,
}) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await loadDoc(page, { path: "doc.md", text: DOC });
  await openOutline(page);

  // The outline mirrors the document's ATX headings.
  await expect(page.locator("#right-dock .outline-item")).toHaveText(["Title", "Deep Heading"]);

  await page.locator("#right-dock .outline-item", { hasText: "Deep Heading" }).click();
  await expect.poll(() => editorScrollTop(page)).toBeGreaterThan(100);
});

test("clicking an outline heading from the Start screen returns to the editor and scrolls to it", async ({
  page,
}) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await loadDoc(page, { path: "doc.md", text: DOC });
  await openOutline(page);

  // Leave the editor for the Start (home) screen — the panes go display:none while the outline stays.
  await page.locator('#left-dock .dock-rail-btn[aria-label="Navigation"]').click();
  await page.locator('#left-dock .nav-item[data-view="home"]').click();
  await expect(page.locator("#central-frame")).toHaveAttribute("data-view", "home");

  // Clicking a heading must both bring the editor back AND land on the heading (not a dead-end).
  await page.locator("#right-dock .outline-item", { hasText: "Deep Heading" }).click();
  await expect(page.locator("#central-frame")).toHaveAttribute("data-view", "editor");
  await expect.poll(() => editorScrollTop(page)).toBeGreaterThan(100);
});
