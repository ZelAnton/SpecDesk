import { expect, test } from "@playwright/test";
import { openDockTool } from "../lib/dock";
import { emit, installMockHost, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

test("a local copy can be named and an occupied name offers the existing copy", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await openDockTool(page, "left", "Repositories");

  await page.locator(".repo-register-input").fill("acme/specs");
  await expect(page.locator(".repo-local-name-input")).toHaveValue("specs");
  await page.locator(".repo-local-name-input").fill("quarterly-specs");
  await waitForSent(page, "repo.cloneDestination.request");
  const destinationRequests = (await sentFrames(page)).filter(
    (frame) => frame.kind === "repo.cloneDestination.request",
  );
  const occupiedRequest = destinationRequests.at(-1);
  expect(occupiedRequest?.payload).toMatchObject({
    url: "acme/specs",
    localName: "quarterly-specs",
  });
  await emit(page, {
    kind: "repo.cloneDestination",
    payload: {
      url: "acme/specs",
      localName: "quarterly-specs",
      requestId: (occupiedRequest?.payload as { requestId: number }).requestId,
      path: "C:\\SpecDesk\\repos\\quarterly-specs",
      exists: true,
      existingClonePath: "C:\\SpecDesk\\repos\\quarterly-specs",
    },
  });
  await waitForSent(page, "repo.description.request");
  const descriptionRequest = (await sentFrames(page))
    .filter((frame) => frame.kind === "repo.description.request")
    .at(-1);
  await emit(page, {
    kind: "repo.description",
    payload: {
      url: "acme/specs",
      requestId: (descriptionRequest?.payload as { requestId: number }).requestId,
      state: "found",
      description: "Product specifications",
    },
  });

  await expect(page.locator(".repo-destination-warning")).toContainText("already exists");
  await expect(page.locator(".repo-clone-primary")).toBeDisabled();
  await page.screenshot({ path: testInfo.outputPath("clone-name-conflict.png"), fullPage: true });
  await page.getByRole("button", { name: "Open existing copy" }).click();
  expect(
    (await sentFrames(page)).filter((frame) => frame.kind === "repo.open").at(-1)?.payload,
  ).toEqual({
    url: "acme/specs",
    clonePath: "C:\\SpecDesk\\repos\\quarterly-specs",
  });

  await page.locator(".repo-local-name-input").fill("quarterly-specs-2");
  await page.waitForTimeout(150);
  const availableRequest = (await sentFrames(page))
    .filter((frame) => frame.kind === "repo.cloneDestination.request")
    .at(-1);
  await emit(page, {
    kind: "repo.cloneDestination",
    payload: {
      url: "acme/specs",
      localName: "quarterly-specs-2",
      requestId: (availableRequest?.payload as { requestId: number }).requestId,
      path: "C:\\SpecDesk\\repos\\quarterly-specs-2",
      exists: false,
    },
  });
  await page.locator(".repo-clone-primary").click();
  await page.getByRole("button", { name: "Yes" }).click();
  expect(
    (await sentFrames(page)).filter((frame) => frame.kind === "repo.cloneManaged").at(-1)?.payload,
  ).toEqual({
    url: "acme/specs",
    localName: "quarterly-specs-2",
    destinationPath: "C:\\SpecDesk\\repos\\quarterly-specs-2",
  });
});
