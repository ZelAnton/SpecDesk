import { test, expect } from "../lib/fixtures";
import { openDockTool } from "../lib/dock";
import { emit, loadDoc, waitForSent } from "../lib/mock-host";
import { BASE_URL } from "../lib/serve-bundle";

const labels = (page: import("@playwright/test").Page): Promise<string[]> =>
  page
    .locator("#right-dock .dock-rail-btn:visible")
    .evaluateAll((buttons) => buttons.map((button) => button.getAttribute("aria-label") ?? ""));

test("right-panel modes follow named, detached, and review context", async ({ page }) => {
  await page.goto(BASE_URL);
  await waitForSent(page, "ready");
  await expect.poll(() => labels(page)).toEqual(["Assistant"]);

  await loadDoc(page, {
    path: "C:\\specs\\repo\\.spectool.toml",
    text: '[repo]\ndefault-base = "main"\n',
    docDir: "",
  });
  await emit(page, {
    kind: "workspace.context",
    payload: {
      repository: "acme/specs",
      repositoryRoot: "C:\\specs\\repo",
      branchState: "detached",
      defaultBranch: "main",
      path: ".spectool.toml",
    },
  });
  await expect.poll(() => labels(page)).toEqual(["Assistant", "Versions"]);

  await loadDoc(page, {
    path: "C:\\specs\\repo\\docs\\proposal.md",
    text: "# Repository proposal\n\nReady for review.\n",
    docDir: "docs",
  });

  await emit(page, {
    kind: "workspace.context",
    payload: {
      repository: "acme/specs",
      repositoryRoot: "C:\\specs\\repo",
      branch: "spec/proposal",
      branchState: "named",
      defaultBranch: "main",
      path: "docs/proposal.md",
    },
  });
  await emit(page, {
    kind: "status",
    payload: { state: "inReview", label: "In review", branch: "spec/proposal" },
  });
  await expect.poll(() => labels(page)).toEqual([
    "Assistant",
    "Versions",
    "Comments",
    "History",
  ]);
  await openDockTool(page, "right", "Comments");
  await expect(page.locator("#right-dock .dock-title")).toHaveText("Comments");
});
