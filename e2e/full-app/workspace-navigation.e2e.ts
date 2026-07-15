import { expect, test } from "@playwright/test";
import { type FullApp, launchAndAttach, stopAndDump } from "../lib/full-app";

// Layer 2: exercise the workspace navigation against the real Host and WebView2 in one disposable app.
// This covers the panel and central-view interactions that the mock-host specs cannot prove are present
// in the shipped native bundle.
test.describe.configure({ mode: "serial" });

let ctx: FullApp;

test.beforeAll(async () => {
  ctx = await launchAndAttach();
});

test.afterAll(async ({}, testInfo) => {
  await stopAndDump(ctx, testInfo);
});

test("the real app exposes the workspace panel and navigation surfaces", async ({}, testInfo) => {
  const { page } = ctx;
  await expect(page).toHaveTitle("SpecDesk");

  // Task 10: panel controls live only on their mode rails. The active icon is the collapse/expand
  // affordance, and the collapsed bottom rail becomes a horizontal toolbar at the window edge.
  for (const id of ["toggle-left-dock", "toggle-bottom-dock", "toggle-right-dock"]) {
    await expect(page.locator(`#toolbar #${id}`)).toHaveCount(0);
  }

  for (const edge of ["left", "right"] as const) {
    const active = page.locator(`#${edge}-dock .dock-rail-btn[aria-checked="true"]`);
    await expect(active).toBeVisible();
    await expect(active).toHaveAttribute("aria-expanded", "false");
    await active.click();
    await expect(active).toHaveAttribute("aria-expanded", "true");
    await active.click();
    await expect(active).toHaveAttribute("aria-expanded", "false");
    await active.click();
    await expect(active).toHaveAttribute("aria-expanded", "true");
  }

  const bottomActive = page.locator('#bottom-dock .dock-rail-btn[aria-checked="true"]');
  const bottomRail = page.locator("#bottom-dock .dock-rail");
  await expect(bottomActive).toBeVisible();
  await expect(bottomActive).toHaveAttribute("aria-expanded", "false");
  await expect(bottomRail).toHaveAttribute("aria-orientation", "horizontal");
  await expect(bottomRail).toHaveCSS("flex-direction", "row");
  await bottomActive.click();
  await expect(bottomActive).toHaveAttribute("aria-expanded", "true");
  await expect(bottomRail).toHaveAttribute("aria-orientation", "vertical");
  await expect(bottomRail).toHaveCSS("flex-direction", "column");
  await page.screenshot({ path: testInfo.outputPath("workspace-panels.png"), fullPage: true });
  await bottomActive.click();
  await expect(bottomActive).toHaveAttribute("aria-expanded", "false");
  await expect(bottomRail).toHaveCSS("flex-direction", "row");
  const collapsedPixels = await page.screenshot({
    path: testInfo.outputPath("bottom-collapsed.png"),
    fullPage: true,
  });
  // Artifact sanity only: the completion review must still open and inspect this rendered evidence.
  expect(collapsedPixels.subarray(1, 4).toString("ascii")).toBe("PNG");
  expect(collapsedPixels.byteLength).toBeGreaterThan(1_000);

  // Task 11: Notifications substitutes the central view and exposes the deliberate list stub.
  await page.locator("#github-btn").click();
  await page.locator("#account-notifications").click();
  await expect(page.locator("#central-frame")).toHaveAttribute("data-view", "notifications");
  await expect(page.getByRole("heading", { name: "Notifications" })).toBeVisible();
  await expect(page.getByRole("list", { name: "Notifications" })).toContainText(
    "Review requests and mentions will appear here.",
  );
  await page.screenshot({ path: testInfo.outputPath("notifications.png"), fullPage: true });

  // Task 12: Start has repository navigation rather than an inline repository input/Open form.
  const navigator = page.locator('#left-dock .dock-rail-btn[aria-label="Navigator"]');
  await expect(navigator).toHaveAttribute("aria-expanded", "true");
  await page.locator('#left-dock .nav-item[data-view="home"]').click();
  await expect(page.locator("#central-frame")).toHaveAttribute("data-view", "home");
  await expect(page.locator("#home-view input")).toHaveCount(0);
  await expect(page.locator("#home-view").getByRole("button", { name: "Open", exact: true })).toHaveCount(
    0,
  );
  await page.screenshot({ path: testInfo.outputPath("start.png"), fullPage: true });
  await page.getByRole("button", { name: "Open Repository", exact: true }).click();
  const repositories = page.locator('#left-dock .dock-rail-btn[aria-label="Repositories"]');
  await expect(repositories).toHaveAttribute("aria-expanded", "true");
  await expect(page.locator('#left-dock .dock-tool[data-tool="repositories"]')).toBeVisible();
  const emptyClone = page.locator(".repo-clone-primary");
  await expect(page.locator(".repo-register-input")).toHaveValue("");
  await expect(emptyClone).toBeDisabled();
  await expect(page.locator(".repo-clone-toggle")).toBeDisabled();
  await expect(emptyClone).toHaveCSS("cursor", "not-allowed");
  await expect(emptyClone).toHaveCSS("background-color", "rgb(236, 238, 241)");
  await page.screenshot({ path: testInfo.outputPath("start-repositories.png"), fullPage: true });

  // Tasks 13-14: both GitHub work modes exist in the real shell and explain their signed-out state.
  await page.locator('#left-dock .dock-rail-btn[aria-label="PRs"]').click();
  const review = page.locator('#left-dock [data-tool="reviews"]');
  await expect(review).toBeVisible();
  await expect(review.locator('.remote-review-list[data-state="auth"] .remote-review-status')).toHaveText(
    "Connect a GitHub account to see review requests.",
  );
  await page.screenshot({ path: testInfo.outputPath("review-signed-out.png"), fullPage: true });

  const pullRequests = page.locator('#left-dock [data-tool="pullRequests"]');
  await expect(pullRequests).toBeVisible();
  await expect(
    pullRequests.locator('.remote-review-list[data-state="auth"] .remote-review-status'),
  ).toHaveText(
    "Connect a GitHub account to see pull requests.",
  );
  await page.screenshot({ path: testInfo.outputPath("final.png"), fullPage: true });
});
