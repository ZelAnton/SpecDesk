/**
 * Build the webview bundle and write its content manifest.
 *
 * Order matters for correctness: the manifest is the proof that a build fully completed, so it is
 * removed up front and written only at the very end, after esbuild has produced webview.js AND the
 * html/css have been copied. If anything throws in between, no manifest is left behind and the next
 * verify (build-time or host-time) treats the bundle as incomplete rather than stale-but-valid.
 *
 * esbuild is the only external tool here and is already a devDependency — no new dependency is added.
 */

import { copyFileSync, mkdirSync, utimesSync } from "node:fs";
import { join } from "node:path";
import { build } from "esbuild";
import {
  BUNDLE_PARAMS,
  buildManifest,
  defaultWebviewDir,
  defaultWwwrootDir,
  removeManifest,
  writeManifestAtomic,
} from "./webview-manifest.mjs";

const webviewDir = defaultWebviewDir(import.meta.url);
const wwwrootDir = defaultWwwrootDir(import.meta.url);

mkdirSync(wwwrootDir, { recursive: true });

// A crashed/partial rebuild must not leave a manifest that still validates against the old outputs.
removeManifest(wwwrootDir);

await build({
  entryPoints: [join(webviewDir, BUNDLE_PARAMS.entry)],
  bundle: BUNDLE_PARAMS.bundle,
  format: BUNDLE_PARAMS.format,
  outfile: join(wwwrootDir, BUNDLE_PARAMS.outfile),
});

// index.html and styles.css are served verbatim alongside the bundle. copyFileSync preserves the
// SOURCE file's timestamp on Windows (CopyFileW semantics), so stamp the copies "now": up-to-dateness
// is decided by content (the manifest), but MSBuild still stages wwwroot/ into bin/ with PreserveNewest
// — and a rebuild triggered by a content change whose sources carry OLD timestamps (a working-copy
// switch, a timestamp-preserving restore) must still land in bin/, not be skipped as "not newer".
copyFileSync(join(webviewDir, "index.html"), join(wwwrootDir, "index.html"));
copyFileSync(join(webviewDir, "styles.css"), join(wwwrootDir, "styles.css"));
const now = new Date();
utimesSync(join(wwwrootDir, "index.html"), now, now);
utimesSync(join(wwwrootDir, "styles.css"), now, now);

writeManifestAtomic(wwwrootDir, buildManifest(webviewDir, wwwrootDir));
