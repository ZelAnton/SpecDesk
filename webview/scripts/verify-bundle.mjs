/**
 * The cheap, content-based up-to-date check the MSBuild `BundleWebview` target runs on every build to
 * decide whether esbuild must run again. It never spawns esbuild and imports only Node built-ins, so
 * it works before node_modules is installed and costs only a few file hashes.
 *
 * Exit code 0  → the bundle is present, intact, and matches the current webview inputs (skip rebuild).
 * Exit code 1  → the bundle is missing/partial/corrupt/stale or built by an older schema (rebuild).
 *
 * The reason is printed so a build log records WHY a rebuild happened (or did not).
 */

import {
  defaultWebviewDir,
  defaultWwwrootDir,
  VerifyStatus,
  verifyBundle,
} from "./webview-manifest.mjs";

const webviewDir = defaultWebviewDir(import.meta.url);
const wwwrootDir = defaultWwwrootDir(import.meta.url);

const result = verifyBundle(webviewDir, wwwrootDir);
if (result.status === VerifyStatus.UpToDate) {
  process.stdout.write(`webview bundle up to date (${result.reason})\n`);
  process.exit(0);
}

process.stdout.write(`webview bundle needs rebuild: ${result.status} — ${result.reason}\n`);
process.exit(1);
