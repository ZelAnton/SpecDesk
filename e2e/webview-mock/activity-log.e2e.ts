import { expect, test } from "@playwright/test";
import { emit, installMockHost, loadDoc, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

test("renders GitHub, context, view, and user-operation activity in the bottom Log", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await loadDoc(page, {
    path: "C:\\SpecDesk\\quarterly-specs\\guides\\intro.md",
    text: "# Introduction\n",
  });
  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: true, login: "alice" },
  });
  await page.locator('#left-dock .dock-rail-btn[aria-label="PRs"]').click();
  await waitForSent(page, "pr.list.request");
  await page.locator("#open-btn").click();

  await page.locator("#github-btn").click();
  await page.locator("#account-notifications").click();
  await page.locator('#bottom-dock .dock-rail-btn[aria-label="Log"]').click();

  const log = page.getByRole("list", { name: "Application activity" });
  await expect(log).toBeVisible();
  await expect(log).toContainText("GitHub");
  await expect(log).toContainText("Requested pr.list.request");
  await expect(log).toContainText("Context");
  await expect(log).toContainText("Active context:");
  await expect(log).toContainText("View");
  await expect(log).toContainText("Central view: notifications");
  await expect(log).toContainText("Action");
  await expect(log).toContainText("Requested doc.open");

  await page.screenshot({ path: testInfo.outputPath("activity-log.png"), fullPage: true });
});
