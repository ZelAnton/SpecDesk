import { resolve } from "node:path";
import { defineConfig } from "@playwright/test";

// Repo-root-relative artifact directory. Per-test folders (screenshot / trace / geometry dump) land
// under here; global-setup wipes it each run so stale evidence can't be misread.
const e2eDir = import.meta.dirname;
const artifactsDir = resolve(e2eDir, "..", "artifacts", "e2e");

export default defineConfig({
  testDir: e2eDir,
  // The `.e2e.ts` suffix keeps these specs out of Vitest's default include (webview/) entirely.
  testMatch: "**/*.e2e.ts",
  outputDir: artifactsDir,
  globalSetup: "./global-setup.ts",
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: 0,
  reporter: [["list"]],
  use: {
    headless: true,
    // The agent's evidence: a screenshot on failure (each scenario also writes one on success),
    // plus a Playwright trace for a human to open with `npx playwright show-trace`.
    screenshot: "only-on-failure",
    trace: "retain-on-failure",
    viewport: { width: 1280, height: 800 },
  },
  projects: [
    // Layer 1: the real built bundle in Playwright's own Chromium against a mock host.
    {
      name: "webview-mock",
      testMatch: "webview-mock/**/*.e2e.ts",
      use: { browserName: "chromium" },
    },
    // Layer 2: the REAL SpecDesk.Host.exe over CDP against a disposable fixture repo — Windows-only
    // (Photino + WebView2), and run only via `npm run e2e:app` (never the default `npm run e2e`). It
    // does not use a Playwright-launched browser; app-process/cdp own the process + CDP attach.
    ...(process.platform === "win32"
      ? [{ name: "full-app", testMatch: "full-app/**/*.e2e.ts" }]
      : []),
  ],
});
