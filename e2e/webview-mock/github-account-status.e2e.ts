import { expect, test } from "@playwright/test";
import { emit, installMockHost, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

test("GitHub authorization stays in the main toolbar and identity is visible in the status bar", async ({
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
    },
  });

  const account = page.locator("#toolbar #github-btn");
  await expect(account).toBeVisible();
  await expect(account).toHaveText("Sign out @octocat");
  const status = page.locator("#status-bar #github-account-status");
  await expect(status).toBeVisible();
  await expect(status).toHaveText("GitHub: @octocat · Organizations: acme, octo-labs");

  await page.screenshot({ path: testInfo.outputPath("final.png"), fullPage: true });
});
