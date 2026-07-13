import { expect, test } from "@playwright/test";
import { openDockTool } from "../lib/dock";
import { emit, installMockHost, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

test("Clone menu offers managed and chosen-folder destinations", async ({ page }, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await openDockTool(page, "left", "Repositories");

  await page.locator(".repo-register-input").fill("outside/public-specs");
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
  await expect(page.locator(".repo-managed-destination")).toHaveText(
    "Managed destination: C:\\SpecDesk\\repos\\outside_public-specs",
  );
  const toggle = page.locator(".repo-register-add");
  await expect(toggle).toHaveText("Clone…");
  await toggle.click();
  const actions = page.locator('.repo-clone-menu [role="menuitem"]');
  await expect(actions).toHaveText(["Clone…", "Clone to folder…"]);
  await page.screenshot({ path: testInfo.outputPath("clone-menu.png"), fullPage: true });

  await actions.filter({ hasText: /^Clone to folder…$/ }).click();
  await page.locator(".repo-clone-confirm-yes").click();
  expect((await sentFrames(page)).find((frame) => frame.kind === "repo.cloneToFolder")?.payload).toEqual({
    url: "outside/public-specs",
  });
});
