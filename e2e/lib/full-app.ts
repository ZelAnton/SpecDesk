import { existsSync, readdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import type { Browser, Page, TestInfo } from "@playwright/test";
import { buildHost, launchApp, type RunningApp, stopApp } from "./app-process";
import { dumpArtifacts } from "./artifacts";
import { attachToApp } from "./cdp";

/** A launched-and-attached real app plus the captured page console — the handle every full-app spec drives. */
export interface FullApp {
  app: RunningApp;
  browser: Browser;
  page: Page;
  consoleLog: string[];
}

/**
 * Build (unless E2E_SKIP_BUILD=1), launch the real SpecDesk.Host.exe against a fresh disposable fixture
 * repo, attach Playwright over CDP, and start capturing the page's console + uncaught errors — the shared
 * `beforeAll` for the full-app (Layer 2) specs.
 */
export async function launchAndAttach(): Promise<FullApp> {
  buildHost();
  const app = launchApp();
  try {
    const { browser, page } = await attachToApp(app.port);
    const consoleLog: string[] = [];
    page.on("console", (message) => consoleLog.push(`[${message.type()}] ${message.text()}`));
    page.on("pageerror", (error) => consoleLog.push(`[pageerror] ${error.message}\n${error.stack ?? ""}`));
    return { app, browser, page, consoleLog };
  } catch (error) {
    // Attach failed (the host never exposed CDP, or the page never navigated — the most common real
    // Layer-2 failure). Kill the spawned host + WebView2 tree before rethrowing, or it leaks (holding
    // its UDF + port) with no handle for afterAll to recover.
    await stopApp(app);
    throw error;
  }
}

/**
 * The shared `afterAll`: on a failing test, dump the evidence bundle (screenshot / geometry / trace-ring /
 * console via {@link dumpArtifacts}) PLUS the isolated native app-log tail — captured BEFORE {@link stopApp}
 * wipes the run dir — then disconnect and kill the host. Never throws.
 */
export async function stopAndDump(ctx: FullApp | undefined, testInfo: TestInfo): Promise<void> {
  if (ctx === undefined) {
    // launchAndAttach threw in beforeAll — it self-cleaned the spawned host, so there is nothing to
    // dump or kill here; return rather than dereferencing an undefined handle (which would bury the
    // real beforeAll cause under a secondary TypeError).
    return;
  }
  if (testInfo.status !== testInfo.expectedStatus) {
    await dumpArtifacts(ctx.page, testInfo, ctx.consoleLog).catch(() => {
      // Evidence-dumping must never itself fail teardown.
    });
    dumpAppLog(ctx.app, testInfo);
  }
  await ctx.browser.close().catch(() => {
    // Already disconnected (the app was killed) — nothing to close.
  });
  await stopApp(ctx.app);
}

/** Copy the tail of the run's isolated Serilog file into the per-test dir, so a full-app failure is
 *  diagnosable from the NATIVE side too (the webview trace-ring covers the UI side). Best-effort. */
function dumpAppLog(app: RunningApp, testInfo: TestInfo): void {
  try {
    const logsDir = resolve(app.dataRoot, "logs");
    if (!existsSync(logsDir)) {
      return;
    }
    const logs = readdirSync(logsDir)
      .filter((name) => name.startsWith("specdesk-") && name.endsWith(".log"))
      .map((name) => resolve(logsDir, name));
    const newest = logs.sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs)[0];
    if (newest === undefined) {
      return;
    }
    const lines = readFileSync(newest, "utf8").split("\n");
    writeFileSync(testInfo.outputPath("app-log.txt"), lines.slice(-200).join("\n"));
  } catch {
    // The log may be locked or absent; the other artifacts still capture the failure.
  }
}
