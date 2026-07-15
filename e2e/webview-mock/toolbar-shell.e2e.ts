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
      localCopy: "specs-manager",
    },
  });
  await expect(page.locator("body")).toHaveAttribute("data-central-view", "editor");
  await expect(page.locator("#app-title")).toBeHidden();
  await expect(page.locator("#context-panels")).toBeVisible();
  await expect(page.locator('[data-context="pull-request"]')).toBeHidden();
  await expect(page.locator("#toolbar-search")).toBeVisible();
  await emit(page, {
    kind: "status",
    payload: { state: "draft", label: "Saved", branch: "spec/navigation" },
  });

  for (const id of ["toggle-left-dock", "toggle-bottom-dock", "toggle-right-dock"]) {
    await expect(page.locator(`#toolbar #${id}`)).toHaveCount(0);
  }
  const leftActive = page.locator('#left-dock .dock-rail-btn[aria-checked="true"]');
  await expect(leftActive).toBeVisible();
  await expect(leftActive).toHaveAttribute("aria-expanded", "true");
  await leftActive.click();
  await expect(leftActive).toHaveAttribute("aria-expanded", "false");
  await leftActive.click();
  await expect(leftActive).toHaveAttribute("aria-expanded", "true");

  // An inactive mode selects and opens instead of collapsing; only a second click on that active icon
  // collapses the panel.
  const filesMode = page.locator('#left-dock .dock-rail-btn[aria-label="Disk"]');
  await filesMode.click();
  await expect(filesMode).toHaveAttribute("aria-expanded", "true");
  await filesMode.click();
  await expect(filesMode).toHaveAttribute("aria-expanded", "false");

  const bottomToggle = page.locator('#right-dock [data-action="bottom-panel"]');
  await expect(bottomToggle).toBeVisible();
  await expect(bottomToggle).toHaveAttribute("aria-pressed", "false");
  await expect(page.locator("#bottom-dock")).toBeHidden();
  await expect(page.locator("#bottom-dock .dock-rail")).toHaveCount(0);
  await bottomToggle.click();
  await expect(page.locator("#bottom-dock")).toBeVisible();
  await expect(bottomToggle).toHaveAttribute("aria-pressed", "true");
  await filesMode.click();
  await expect(filesMode).toHaveAttribute("aria-expanded", "true");
  await expect(page.locator("#editor-toolbar")).toHaveCSS(
    "background-color",
    await page
      .locator("#left-dock .dock-header")
      .evaluate((element) => getComputedStyle(element).backgroundColor),
  );
  const statusBar = page.locator("#status-bar");
  const leftRail = page.locator("#left-dock .dock-rail");
  expect(await statusBar.evaluate((element) => getComputedStyle(element).backgroundColor)).not.toBe(
    await leftRail.evaluate((element) => getComputedStyle(element).backgroundColor),
  );
  await expect(statusBar).toHaveCSS(
    "color",
    await filesMode.evaluate((element) => getComputedStyle(element).color),
  );

  const rightActive = page.locator('#right-dock .dock-rail-btn[aria-checked="true"]');
  if ((await rightActive.getAttribute("aria-expanded")) !== "true") {
    await rightActive.click();
  }
  await expect(rightActive).toHaveAttribute("aria-expanded", "true");

  // The bottom panel owns the full shell width. The right panel ends exactly where the bottom begins.
  const bottomBox = await page.locator("#bottom-dock").boundingBox();
  const rightBox = await page.locator("#right-dock").boundingBox();
  const rightSplitterBox = await page.locator("#workspace > .dock-splitter-right").boundingBox();
  if (bottomBox === null || rightBox === null || rightSplitterBox === null) {
    throw new Error("The bottom and right docks must both participate in layout");
  }
  expect(rightBox.y + rightBox.height).toBeLessThanOrEqual(bottomBox.y);
  expect(rightSplitterBox.y + rightSplitterBox.height).toBeLessThanOrEqual(bottomBox.y);
  expect(bottomBox.x).toBeLessThanOrEqual(rightBox.x);
  expect(bottomBox.x + bottomBox.width).toBeGreaterThanOrEqual(rightBox.x + rightBox.width);
  await page.screenshot({ path: testInfo.outputPath("panels-expanded.png"), fullPage: true });
  await bottomToggle.click();
  await expect(page.locator("#bottom-dock")).toBeHidden();
  await expect(bottomToggle).toHaveAttribute("aria-pressed", "false");

  await expect(page.locator("#current-repository")).toHaveText("acme/specs");
  await expect(page.locator("#current-branch")).toHaveText("spec/navigation");
  await expect(page.locator("#current-local-path")).toHaveText("C:\\work\\specs");
  await expect(page.locator("#current-path")).toHaveText(
    "C:\\work\\specs\\guides\\intro.md",
  );
  await expect(page.locator("#workspace-context-status")).toHaveText(
    /specs-manager\s*·\s*spec\/navigation\s*·\s*intro\.md/,
  );

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

  await page.locator("#github-btn").click();
  await page.locator("#account-notifications").click();
  await expect(page.locator("#app-title")).toBeVisible();
  await expect(page.locator("#context-panels")).toBeHidden();
  await expect(page.locator("#toolbar-search")).toBeHidden();
  await expect(page.locator("#central-frame")).toHaveAttribute("data-view", "notifications");
  await expect(page.locator("#notifications-view")).toBeVisible();
  await expect(page.locator("#notifications-view .notifications-list")).toContainText(
    "Review requests and mentions will appear here.",
  );

  await emit(page, {
    kind: "github.account",
    payload: { available: true, signedIn: true, login: "octocat" },
  });
  const account = page.locator("#github-btn");
  await expect(account).toHaveAttribute("aria-label", "Account, signed in as @octocat");
  await account.click();
  await expect(page.locator("#account-menu")).toBeVisible();
  await expect(page.locator("#account-settings")).toBeDisabled();
  await expect(page.locator("#account-settings")).toHaveText("Settings (coming soon)");
  await expect(page.locator("#account-updates")).toBeDisabled();
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
