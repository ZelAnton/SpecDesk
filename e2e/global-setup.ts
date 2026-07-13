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
 * Once per run: wipe stale artifacts, then normally build the webview bundle the SAME way
 * `npm run bundle` does and assert it is up to date. A spawn-restricted sandbox may use the same explicit
 * `SPECDESK_DELIVERY_PREBUILT=1` escape hatch as the delivery gate after a native-executable build; manifest
 * verification remains mandatory. Layer 1 always serves this exact `wwwroot/webview.js`.
 */
export default async function globalSetup(): Promise<void> {
  rmSync(artifactsDir, { recursive: true, force: true });

  if (process.env.SPECDESK_DELIVERY_PREBUILT !== "1") {
    execFileSync(process.execPath, [resolve(webviewDir, "scripts", "bundle.mjs")], {
      cwd: webviewDir,
      stdio: "inherit",
    });
  }

  const verification = verifyBundle(webviewDir, wwwrootDir);
  if (verification.status !== VerifyStatus.UpToDate) {
    throw new Error(
      `webview bundle is not up to date after building (${verification.status}: ${verification.reason})`,
    );
  }
}
