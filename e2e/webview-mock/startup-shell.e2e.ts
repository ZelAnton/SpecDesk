import { expect, test } from "@playwright/test";
import { installMockHost, loadDoc, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
  await context.addInitScript(() => {
    localStorage.setItem(
      "specdesk.docks.v1",
      JSON.stringify({
        left: { open: true, size: 311, mode: "repositories" },
        right: { open: true, size: 422, mode: "assistant" },
        bottom: { open: true, size: 233, mode: "comment" },
      }),
    );
  });
});

test("startup stays on Start with every optional panel collapsed", async ({ page }, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await loadDoc(page, {
    path: "C:\\work\\specs\\welcome.md",
    text: "# Welcome\n\nChoose a specification to continue.\n",
    reveal: false,
  });

  await expect(page.locator("#central-frame")).toHaveAttribute("data-view", "home");
  await expect(page.locator("body")).toHaveAttribute("data-central-view", "home");
  await expect(page.locator("#app-title")).toBeVisible();
  await expect(page.locator("#context-panels")).toBeHidden();
  await expect(page.locator("#toolbar-search")).toBeHidden();
  await expect(page.getByRole("heading", { name: "SpecDesk" })).toBeVisible();
  for (const edge of ["left", "right"] as const) {
    const dock = page.locator("#" + edge + "-dock");
    await expect(dock).toHaveClass(/dock--collapsed/);
    await expect(dock.locator('.dock-rail-btn[aria-checked="true"]')).toHaveAttribute(
      "aria-expanded",
      "false",
    );
  }
  await expect(page.locator("#bottom-dock")).toBeHidden();
  await expect(page.locator("#bottom-dock .dock-rail")).toHaveCount(0);
  await expect(page.locator('#right-dock [data-action="bottom-panel"]')).toHaveAttribute(
    "aria-pressed",
    "false",
  );

  await page.screenshot({ path: testInfo.outputPath("startup-start-collapsed.png"), fullPage: true });
});
