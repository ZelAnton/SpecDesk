import { expect, test } from "@playwright/test";
import { emit, installMockHost, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

test("Pull Requests mode lists authored and involved open work", async ({ page }, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: true, login: "alice" },
  });
  await page.locator('#left-dock .dock-rail-btn[aria-label="PRs"]').click();
  await waitForSent(page, "pr.list.request");
  const request = (await sentFrames(page)).find(
    (frame) =>
      frame.kind === "pr.list.request" &&
      (frame.payload as { scope?: string } | undefined)?.scope === "pullRequests",
  );
  const requestId = request?.id;
  if (requestId === undefined) {
    throw new Error("pull-request list was not correlated");
  }
  await emit(page, {
    kind: "pr.list",
    id: requestId,
    payload: {
      items: [
        {
          number: 1,
          title: "Mine",
          url: "https://github.com/o/r/pull/1",
          repo: "o/r",
          role: "author",
          status: "inReview",
          label: "In review",
        },
        {
          number: 2,
          title: "Joined",
          url: "https://github.com/o/x/pull/2",
          repo: "o/x",
          role: "reviewer",
          status: "inReview",
          label: "In review",
        },
      ],
      error: null,
    },
  });

  const pullRequests = page.locator('#left-dock [data-tool="pullRequests"]');
  await expect(pullRequests).toBeVisible();
  await expect(pullRequests.locator(".remote-review-title")).toHaveText(["Mine", "Joined"]);
  await page.screenshot({ path: testInfo.outputPath("pull-requests.png"), fullPage: true });
});
