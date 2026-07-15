import { expect, test } from "@playwright/test";
import { emit, installMockHost, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

test("Review mode loads assigned work and opens it in the pull-request view", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: true, login: "alice" },
  });

  await page.locator('#left-dock .dock-rail-btn[aria-label="Change requests"]').click();
  const reviewPanel = page.locator('#left-dock [data-tool="reviews"]');
  await expect(reviewPanel).toBeVisible();
  await expect(reviewPanel.locator(".remote-review-list")).toHaveAttribute("data-state", "loading");
  await waitForSent(page, "pr.list.request");
  const request = (await sentFrames(page)).find(
    (frame) =>
      frame.kind === "pr.list.request" &&
      (frame.payload as { scope?: string } | undefined)?.scope === "reviewRequests",
  );
  const requestId = request?.id;
  expect(requestId).toBeTruthy();
  if (requestId === undefined) {
    throw new Error("review request was not correlated");
  }
  await emit(page, {
    kind: "pr.list",
    id: requestId,
    payload: {
      items: [
        {
          number: 7,
          title: "Payment terms",
          url: "https://github.com/octo/spec/pull/7",
          repo: "octo/spec",
          role: "reviewer",
          status: "inReview",
          label: "In review",
        },
      ],
      error: null,
    },
  });

  await expect(reviewPanel.locator(".remote-review-title")).toHaveText("Payment terms");
  await reviewPanel.locator(".remote-review-open").click();
  await waitForSent(page, "pr.details.request");
  const detailsRequest = (await sentFrames(page)).find(
    (frame) => frame.kind === "pr.details.request",
  );
  expect(detailsRequest?.payload).toEqual({ repo: "octo/spec", number: 7 });
  await page.screenshot({ path: testInfo.outputPath("review-requests.png"), fullPage: true });
});
