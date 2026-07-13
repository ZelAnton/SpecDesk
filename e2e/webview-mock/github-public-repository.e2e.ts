import { expect, test } from "@playwright/test";
import { emit, installMockHost, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

test("a public owner/repository outside suggestions remains available", async ({ page }, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await emit(page, {
    kind: "github.repositories",
    payload: { repositories: [{ fullName: "acme/specs" }] },
  });
  await page.locator("#toggle-left-dock").click();
  await page.locator('#left-dock .dock-rail-btn[aria-label="Repositories"]').click();

  const input = page.locator(".repo-register-input");
  await input.fill("outside/public-specs");
  await waitForSent(page, "repo.cloneDestination.request");
  const destinationRequest = (await sentFrames(page)).find(
    (frame) => frame.kind === "repo.cloneDestination.request",
  );
  await emit(page, {
    kind: "repo.cloneDestination",
    payload: {
      url: "outside/public-specs",
      requestId: (destinationRequest?.payload as { requestId: number }).requestId,
      path: "C:\\SpecDesk\\repos\\outside_public-specs",
    },
  });
  await expect(page.locator(".repo-public-hint")).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath("public-repository.png"), fullPage: true });
  await page.locator(".repo-register-add").click();
  await page.locator('[role="menuitem"]').filter({ hasText: /^Clone…$/ }).click();
  expect((await sentFrames(page)).find((frame) => frame.kind === "repo.cloneManaged")?.payload).toEqual({
    url: "outside/public-specs",
    destinationPath: "C:\\SpecDesk\\repos\\outside_public-specs",
  });
});
