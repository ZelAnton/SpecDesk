import { expect, test } from "@playwright/test";
import { openDockTool } from "../lib/dock";
import { emit, installMockHost, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

test("repository description is visible before Clone is enabled", async ({ page }, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await openDockTool(page, "left", "Repositories");

  await page.locator(".repo-register-input").fill("acme/specs");
  const clone = page.locator(".repo-register-add");
  await expect(page.locator(".repo-description")).toHaveText("Repository description: loading…");
  await expect(clone).toBeDisabled();

  await waitForSent(page, "repo.description.request");
  const request = (await sentFrames(page)).find(
    (frame) => frame.kind === "repo.description.request",
  );
  await emit(page, {
    kind: "repo.description",
    payload: {
      url: "acme/specs",
      requestId: (request?.payload as { requestId: number }).requestId,
      state: "found",
      description: "Product specifications for the Acme team",
    },
  });

  await expect(page.locator(".repo-description")).toHaveText(
    "Description: Product specifications for the Acme team",
  );
  await expect(clone).toBeEnabled();
  await page.screenshot({ path: testInfo.outputPath("repository-description.png"), fullPage: true });
});
