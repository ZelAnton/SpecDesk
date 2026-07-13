import { expect, test } from "@playwright/test";
import { emit, installMockHost, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

test.beforeEach(async ({ context, page }) => {
  await serveBundle(context);
  await installMockHost(context);
  await page.addInitScript(() => localStorage.clear());
});

test("clone requires Yes and can persist Do not show again", async ({ page }, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await page.locator("#toggle-left-dock").click();
  await page.locator('#left-dock .dock-rail-btn[aria-label="Repositories"]').click();
  const input = page.locator(".repo-register-input");
  await input.fill("outside/public-specs");
  await waitForSent(page, "repo.cloneDestination.request");
  const request = (await sentFrames(page)).find(
    (frame) => frame.kind === "repo.cloneDestination.request",
  );
  await emit(page, {
    kind: "repo.cloneDestination",
    payload: {
      url: "outside/public-specs",
      requestId: (request?.payload as { requestId: number }).requestId,
      path: "C:\\SpecDesk\\repos\\outside_public-specs",
    },
  });
  await waitForSent(page, "repo.description.request");
  const descriptionRequest = (await sentFrames(page)).find(
    (frame) => frame.kind === "repo.description.request",
  );
  await emit(page, {
    kind: "repo.description",
    payload: {
      url: "outside/public-specs",
      requestId: (descriptionRequest?.payload as { requestId: number }).requestId,
      state: "found",
      description: "Public product specifications",
    },
  });
  await page.locator(".repo-register-add").click();
  await page.locator('[role="menuitem"]').filter({ hasText: /^Clone…$/ }).click();

  const confirmation = page.locator(".repo-clone-confirmation");
  await expect(confirmation).toContainText("C:\\SpecDesk\\repos\\outside_public-specs");
  await page.screenshot({ path: testInfo.outputPath("clone-confirmation.png"), fullPage: true });
  await confirmation.locator('input[type="checkbox"]').check();
  await confirmation.locator(".repo-clone-confirm-yes").click();
  expect((await sentFrames(page)).some((frame) => frame.kind === "repo.cloneManaged")).toBe(true);
});

