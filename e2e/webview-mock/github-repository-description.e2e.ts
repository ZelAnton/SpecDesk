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
  const clone = page.locator(".repo-clone-primary");
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
  const layout = await page.locator(".repo-register").evaluate((form) => {
    const input = form.querySelector<HTMLElement>(".repo-register-input")?.getBoundingClientRect();
    const description = form.querySelector<HTMLElement>(".repo-description")?.getBoundingClientRect();
    return {
      inputWidth: input?.width ?? 0,
      inputBottom: input?.bottom ?? 0,
      descriptionWidth: description?.width ?? 0,
      descriptionTop: description?.top ?? 0,
    };
  });
  expect(layout.inputWidth).toBeGreaterThan(80);
  expect(layout.descriptionWidth).toBeGreaterThan(150);
  expect(layout.descriptionTop).toBeGreaterThanOrEqual(layout.inputBottom);
  await page.screenshot({ path: testInfo.outputPath("repository-description.png"), fullPage: true });
});
