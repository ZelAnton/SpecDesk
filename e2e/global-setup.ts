import { execFileSync } from "node:child_process";
import { rmSync } from "node:fs";
import { resolve } from "node:path";
// Plain-JS bundle helper, shared with the jsdom delivery gate — imported directly (no cross-package
// TS import) so Layer 1 verifies the same artifact freshness the delivery gate does.
import { VerifyStatus, verifyBundle } from "../webview/scripts/webview-manifest.mjs";

const e2eDir = import.meta.dirname;
const repoRoot = resolve(e2eDir, "..");
const webviewDir = resolve(repoRoot, "webview");
const wwwrootDir = resolve(repoRoot, "src", "SpecDesk.Host", "wwwroot");
const artifactsDir = resolve(repoRoot, "artifacts", "e2e");

/**
 * Once per run: wipe stale artifacts, then build the webview bundle the SAME way `npm run bundle`
 * does and assert it is up to date. Layer 1 then serves this exact `wwwroot/webview.js`, so it can
 * never validate a stale build — the failure mode this whole verification effort exists to kill.
 */
export default async function globalSetup(): Promise<void> {
  rmSync(artifactsDir, { recursive: true, force: true });

  execFileSync(process.execPath, [resolve(webviewDir, "scripts", "bundle.mjs")], {
    cwd: webviewDir,
    stdio: "inherit",
  });

  const verification = verifyBundle(webviewDir, wwwrootDir);
  if (verification.status !== VerifyStatus.UpToDate) {
    throw new Error(
      `webview bundle is not up to date after building (${verification.status}: ${verification.reason})`,
    );
  }
}
