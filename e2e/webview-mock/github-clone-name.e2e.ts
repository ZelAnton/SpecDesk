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
  await expect
    .poll(async () =>
      (await sentFrames(page)).some((frame) => {
        const payload = frame.payload as { url?: unknown; localName?: unknown } | undefined;
        return (
          frame.kind === "repo.cloneDestination.request" &&
          payload?.url === "acme/specs" &&
          payload.localName === "quarterly-specs"
        );
      }),
    )
    .toBe(true);
  const occupiedRequest = (await sentFrames(page)).findLast((frame) => {
    const payload = frame.payload as { url?: unknown; localName?: unknown } | undefined;
    return (
      frame.kind === "repo.cloneDestination.request" &&
      payload?.url === "acme/specs" &&
      payload.localName === "quarterly-specs"
    );
  });
  const occupiedRequestId = (occupiedRequest?.payload as { requestId?: unknown } | undefined)
    ?.requestId;
  expect(occupiedRequestId).toEqual(expect.any(Number));
  expect(occupiedRequestId).toBeGreaterThan(0);
  await emit(page, {
    kind: "repo.cloneDestination",
    payload: {
      url: "acme/specs",
      localName: "quarterly-specs",
      requestId: occupiedRequestId,
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
  await expect
    .poll(async () =>
      (await sentFrames(page)).some((frame) => {
        const payload = frame.payload as { url?: unknown; localName?: unknown } | undefined;
        return (
          frame.kind === "repo.cloneDestination.request" &&
          payload?.url === "acme/specs" &&
          payload.localName === "quarterly-specs-2"
        );
      }),
    )
    .toBe(true);
  const availableRequest = (await sentFrames(page)).findLast((frame) => {
    const payload = frame.payload as { url?: unknown; localName?: unknown } | undefined;
    return (
      frame.kind === "repo.cloneDestination.request" &&
      payload?.url === "acme/specs" &&
      payload.localName === "quarterly-specs-2"
    );
  });
  const availableRequestId = (availableRequest?.payload as { requestId?: unknown } | undefined)
    ?.requestId;
  expect(availableRequestId).toEqual(expect.any(Number));
  expect(availableRequestId).toBeGreaterThan(0);
  await emit(page, {
    kind: "repo.cloneDestination",
    payload: {
      url: "acme/specs",
      localName: "quarterly-specs-2",
      requestId: availableRequestId,
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
