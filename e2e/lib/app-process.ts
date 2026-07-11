import { type ChildProcess, execFileSync, spawn } from "node:child_process";
import { mkdirSync, mkdtempSync, readdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve } from "node:path";
import { createFixtureRepo } from "./fixture-repo";

const RUN_DIR_PREFIX = "specdesk-e2e-";

/** Reap temp dirs a prior run couldn't delete. WebView2 can hold a UDF handle for a beat past taskkill,
 *  so a teardown rmSync occasionally loses the race; by the next launch the handle is long released, so
 *  sweeping here bounds leakage to at most the current run's dir (workers:1, so none is ever in use). */
function sweepStaleRunDirs(): void {
  try {
    for (const name of readdirSync(tmpdir())) {
      if (name.startsWith(RUN_DIR_PREFIX)) {
        rmSync(resolve(tmpdir(), name), { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
      }
    }
  } catch {
    // Best-effort housekeeping; a temp dir we can't read/remove is harmless and the OS reaps it.
  }
}

const libDir = import.meta.dirname;
const repoRoot = resolve(libDir, "..", "..");
const hostProject = resolve(repoRoot, "src", "SpecDesk.Host", "SpecDesk.Host.csproj");
const hostExe = resolve(
  repoRoot,
  "src",
  "SpecDesk.Host",
  "bin",
  "Debug",
  "net10.0",
  "SpecDesk.Host.exe",
);

export interface RunningApp {
  process: ChildProcess;
  port: number;
  runDir: string;
  /** `<runDir>/data-root` — the redirected SPECDESK_DATA_ROOT; the isolated logs live at `<dataRoot>/logs`. */
  dataRoot: string;
  /** The seeded fixture repo (for asserting git effects in later scenarios — e.g. `git log`). */
  repo: string;
  /** Absolute path to the fixture's `welcome.md` (for asserting autosave-to-disk in later scenarios). */
  welcome: string;
}

/** Build the host (unless E2E_SKIP_BUILD=1) so the launch runs the CURRENT sources — the guards refuse a
 *  stale build at launch anyway, so this keeps the agent's inner loop honest. */
export function buildHost(): void {
  if (process.env.E2E_SKIP_BUILD === "1") {
    return;
  }
  execFileSync("dotnet", ["build", hostProject, "-c", "Debug"], { cwd: repoRoot, stdio: "inherit" });
}

/**
 * Launch the real SpecDesk.Host.exe against a fresh disposable fixture repo, with CDP on a per-run port
 * and an ISOLATED WebView2 user-data folder — without which a concurrent SpecDesk (or a leaked prior run)
 * owns the browser process and the `--remote-debugging-port` argument is silently ignored. The guards
 * (MainWorktreeGuard / WebviewBundleGuard) stay ARMED: a stale build must fail loudly, not be tested.
 */
export function launchApp(): RunningApp {
  sweepStaleRunDirs();
  const runDir = mkdtempSync(resolve(tmpdir(), RUN_DIR_PREFIX));
  const { dataRoot, repo, welcome } = createFixtureRepo(runDir);
  const udf = resolve(runDir, "wv2-udf");
  mkdirSync(udf, { recursive: true });
  const port = 9400 + (process.pid % 500);

  const child = spawn(hostExe, [], {
    env: {
      ...process.env,
      SPECDESK_DATA_ROOT: dataRoot,
      WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}`,
      WEBVIEW2_USER_DATA_FOLDER: udf,
    },
    stdio: "ignore",
  });

  return { process: child, port, runDir, dataRoot, repo, welcome };
}

/** Kill the host + its WebView2 child tree, then remove the run's temp dir. Best-effort, never throws.
 *  Async so it can let the killed WebView2 children release their UDF handles before deleting. */
export async function stopApp(app: RunningApp): Promise<void> {
  try {
    if (app.process.pid !== undefined) {
      // taskkill /T /F kills the whole tree (the msedgewebview2.exe children included) on Windows.
      execFileSync("taskkill", ["/PID", String(app.process.pid), "/T", "/F"], { stdio: "ignore" });
    }
  } catch {
    // The process may already have exited; nothing to kill.
  }
  // Give the killed WebView2 children a moment to release their UDF file handles before deleting, then
  // retry the Windows lock errors. A stale dir that still won't delete is reaped by the next run's sweep.
  await delay(500);
  try {
    rmSync(app.runDir, { recursive: true, force: true, maxRetries: 20, retryDelay: 200 });
  } catch {
    // Still locked after the retries (rare) — sweepStaleRunDirs() reaps it on the next launch.
  }
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
