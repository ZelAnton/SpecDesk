import { expect, test } from "@playwright/test";
import { emit, installMockHost, loadDoc, sentFrames, waitForSent } from "../lib/mock-host";
import { BASE_URL, serveBundle } from "../lib/serve-bundle";

test.beforeEach(async ({ context }) => {
  await serveBundle(context);
  await installMockHost(context);
});

test("global context and Markdown controls live in the correct toolbars and remain operable", async ({
  page,
}, testInfo) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await loadDoc(page, {
    path: "C:\\work\\specs\\guides\\intro.md",
    docDir: "guides",
    text: "# Intro\n\nFind this target in the document.\n",
  });
  await emit(page, {
    kind: "workspace.context",
    payload: {
      repository: "acme/specs",
      repositoryRoot: "C:\\work\\specs",
      branch: "spec/navigation",
      branchState: "named",
      defaultBranch: "master",
      path: "guides/intro.md",
    },
  });
  await emit(page, {
    kind: "status",
    payload: { state: "draft", label: "Saved", branch: "spec/navigation" },
  });

  await expect(page.locator("#current-repository")).toHaveText("acme/specs");
  await expect(page.locator("#current-branch")).toHaveText("spec/navigation");
  await expect(page.locator("#current-path")).toHaveText("guides/intro.md");

  await emit(page, {
    kind: "workspace.context",
    payload: {
      repository: "acme/specs",
      repositoryRoot: "C:\\work\\specs",
      branch: null,
      branchState: "detached",
      defaultBranch: "master",
      path: "guides/intro.md",
    },
  });
  await expect(page.locator("#current-branch")).toHaveText("Unnamed version");
  await emit(page, {
    kind: "workspace.context",
    payload: {
      repository: "acme/specs",
      repositoryRoot: "C:\\work\\specs",
      branch: "spec/navigation",
      branchState: "named",
      defaultBranch: "master",
      path: "guides/intro.md",
    },
  });

  for (const id of [
    "open-btn",
    "edit-btn",
    "save-version-btn",
    "send-for-review-btn",
    "discard-btn",
    "save-btn",
    "compare-btn",
    "wrap-btn",
    "view-modes",
  ]) {
    await expect(page.locator(`#editor-view #${id}`)).toHaveCount(1);
    await expect(page.locator(`#toolbar #${id}`)).toHaveCount(0);
  }

  await page.locator("#mode-formatted").click();
  await expect(page.locator('#panes[data-mode="formatted"]')).toHaveCount(1);
  await page.locator("#toolbar-search").fill("target");
  await page.locator("#toolbar-search").press("Enter");
  await expect(page.locator("#toolbar-announcer")).toHaveText("Found target.");
  await expect(page.locator('#panes[data-mode="formatted"]')).toHaveCount(1);

  await page.locator("#notifications-btn").click();
  await expect(page.locator("#toolbar-announcer")).toHaveText("You have no new notifications.");

  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: true, login: "octocat" },
  });
  const account = page.locator("#github-btn");
  await expect(account).toHaveAttribute("aria-label", "Account, signed in as @octocat");
  await account.click();
  await expect(page.locator("#account-menu")).toBeVisible();
  await expect(page.locator("#account-settings")).toHaveText("Settings");
  await expect(page.locator("#account-help")).toHaveText("Help");
  const theme = page.getByRole("menuitemcheckbox", { name: "Dark theme" });
  await expect(theme).toHaveAttribute("aria-checked", "false");
  await theme.click();
  await account.click();
  await expect(page.getByRole("menuitemcheckbox", { name: "Dark theme" })).toHaveAttribute(
    "aria-checked",
    "true",
  );
  await page.locator("#account-signout").click();
  expect((await sentFrames(page)).some((frame) => frame.kind === "github.signOut")).toBe(true);

  await page.screenshot({ path: testInfo.outputPath("final.png"), fullPage: true });
});
