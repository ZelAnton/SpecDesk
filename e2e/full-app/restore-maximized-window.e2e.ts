import { expect, test } from "@playwright/test";
import { writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { type FullApp, launchAndAttach, stopAndDump } from "../lib/full-app";

// Layer 2: the REAL SpecDesk.Host.exe, launched against a data root PRE-SEEDED with a `preferences.json`
// whose saved window geometry has `maximized: true` — the "start already maximized" path R-01 found
// unreachable by any test in the original T-077 diff (both the interactive toggle in startup.e2e.ts and
// the mock-host window-controls.e2e.ts only ever drive maximize AFTER boot, via a user gesture or a
// synthetic `window.state` event). This proves the real host replays the true initial state to the
// webview's custom titlebar control on its own, without any interaction.
test.describe.configure({ mode: "serial" });

let ctx: FullApp;

test.beforeAll(async () => {
  ctx = await launchAndAttach({
    seedDataRoot: (dataRoot) => {
      // Mirrors PreferencesStore's on-disk shape (camelCase PersistedState/WindowGeometry) — a safely
      // on-screen rectangle so WindowGeometryValidator.IsValidForCurrentMonitors accepts it on the CI
      // machine's virtual screen (see PreferencesStoreTests for the same geometry used host-side).
      writeFileSync(
        resolve(dataRoot, "preferences.json"),
        JSON.stringify({
          theme: null,
          wrap: true,
          viewMode: "split",
          window: { x: 100, y: 100, width: 1024, height: 768, maximized: true },
        }),
      );
    },
  });
});

test.afterAll(async ({}, testInfo) => {
  await stopAndDump(ctx, testInfo);
});

test("a window restored already maximized reflects that state on the custom titlebar control without any interaction", async ({}, testInfo) => {
  const { page } = ctx;
  await expect(page).toHaveTitle("SpecDesk");

  const restore = page.getByRole("button", { name: "Restore" });
  await expect(restore).toHaveAttribute("aria-pressed", "true");
  await expect(restore).toHaveAttribute("data-window-state", "maximized");
  await expect(restore.locator(".window-control-glyph--restore")).toBeVisible();
  await expect(restore.locator(".window-control-glyph--maximize")).toBeHidden();

  await page.screenshot({
    path: testInfo.outputPath("restored-maximized-titlebar.png"),
    fullPage: true,
  });
});
