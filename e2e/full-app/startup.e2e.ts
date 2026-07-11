import { expect, test } from "@playwright/test";
import { type FullApp, launchAndAttach, stopAndDump } from "../lib/full-app";
import { collectGeometry, waitForGeometrySettle } from "../lib/geometry";

// Layer 2: the REAL SpecDesk.Host.exe (Photino + WebView2), driven over CDP against a disposable git
// fixture repo. This proves the whole native startup path Layer 1's mock host can't: ready → auto-load
// of the fixture's welcome.md → lifecycle resolution from git → doc.loaded → real render. One app for
// the file (serial), built fresh (unless E2E_SKIP_BUILD=1) so a stale host can't pass.
test.describe.configure({ mode: "serial" });

let ctx: FullApp;

test.beforeAll(async () => {
  ctx = await launchAndAttach();
});

test.afterAll(async ({}, testInfo) => {
  await stopAndDump(ctx, testInfo);
});

test("the real host boots, auto-loads welcome.md from the fixture repo, and renders both panes", async ({}, testInfo) => {
  const { page } = ctx;
  // The real Photino shell loaded.
  await expect(page).toHaveTitle("SpecDesk");

  // ready → the host auto-loaded the fixture's welcome.md → both real editors mounted from doc.loaded.
  await expect(page.locator("#editor .cm-editor")).toHaveCount(1);
  await expect(page.locator("#formatted .ProseMirror")).toHaveCount(1);
  // The formatted pane rendered the FIXTURE document, not the byte-identical-h1 bundled sample: assert
  // on text UNIQUE to the fixture's welcome.md, so a broken SPECDESK_DATA_ROOT redirect or a failed seed
  // short-circuit (either would load the bundled sample) fails here instead of passing green.
  await expect(page.locator("#formatted .ProseMirror h1")).toHaveText("Welcome to SpecDesk");
  await expect(page.locator("#formatted .ProseMirror")).toContainText("disposable fixture spec");

  // The lifecycle status surfaced a plain (non-empty, non-git) word — the git→lifecycle resolution ran.
  // Auto-retried so a status word that lands a beat after the render can't flake it.
  await expect(page.locator("#status")).not.toBeEmpty();

  // The full render + height-sync pipeline ran in the real WebView2: settle, then real spacers exist.
  await waitForGeometrySettle(page);
  const geometry = await collectGeometry(page);
  expect(geometry.spacers.length).toBeGreaterThan(0);

  // Evidence the agent reads — the real app's pixels.
  await page.screenshot({ path: testInfo.outputPath("final.png"), fullPage: true });
});
