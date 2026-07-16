import { expect, test } from "@playwright/test";
import { emit, installMockHost, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

test("GitHub sign-in, avatar identity, and account status use their dedicated chrome", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");

  await emit(page, {
    kind: "github.account",
    payload: {
      available: true,
      signedIn: true,
      login: "octocat",
      organizations: ["acme", "octo-labs"],
      avatarUrl: "https://avatars.githubusercontent.com/u/583231?v=4",
      publicationId: "account-publication-1",
    },
  });
  await waitForSent(page, "github.accountApplied");

  const signIn = page.locator("#toolbar #github-auth-btn");
  await expect(signIn).toBeHidden();
  const avatar = page.locator("#account-avatar");
  await expect(avatar).toBeVisible();
  await expect(avatar).toHaveAttribute("alt", "GitHub avatar for @octocat");
  await expect(page.locator("#account-notification-count")).toBeHidden();
  await page.locator("#github-btn").click();
  await expect(page.locator("#account-signout")).toBeVisible();
  await expect(page.locator("#account-notifications")).toBeVisible();
  const refresh = page.locator("#account-refresh");
  await expect(refresh).toBeVisible();
  const status = page.locator("#status-bar #github-account-status");
  await expect(status).toBeVisible();
  await expect(status).toHaveText("GitHub: @octocat · Organizations: acme, octo-labs");

  await refresh.click();
  expect((await sentFrames(page)).some((frame) => frame.kind === "github.account.refresh")).toBe(true);
  await expect(page.locator("#account-menu")).toBeHidden();
  await expect(refresh).toBeDisabled();
  await expect(refresh).toHaveText("Refreshing GitHub access…");
  await emit(page, {
    kind: "github.account",
    payload: {
      available: true,
      signedIn: true,
      login: "octocat",
      organizations: ["acme", "newly-approved"],
    },
  });
  await expect(status).toHaveText("GitHub: @octocat · Organizations: acme, newly-approved");
  await page.locator("#github-btn").click();
  await expect(refresh).toBeEnabled();
  await expect(refresh).toHaveText("Refresh GitHub access");
  await page.waitForTimeout(150);
  await page.screenshot({ path: testInfo.outputPath("account-access-refreshed.png"), fullPage: true });

  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: false },
  });
  await expect(signIn).toBeVisible();
  await expect(signIn).toHaveText("Sign in");
  await expect(page.locator("#account-avatar-fallback")).toBeVisible();

  await page.screenshot({ path: testInfo.outputPath("final.png"), fullPage: true });
});
