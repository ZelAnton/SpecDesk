/**
 * Deterministic content manifest for the webview bundle — the single source of truth that ties the
 * served `webview.js` / `index.html` / `styles.css` back to the exact webview inputs they were built
 * from, by cryptographic hash rather than file timestamps.
 *
 * This module is intentionally dependency-free (only Node built-ins) so it can run during a `dotnet
 * build` that has not installed node_modules yet, and so the fingerprint algorithm can be mirrored
 * verbatim by the C# host verifier (SpecDesk.Host `WebviewFingerprint` / `WebviewBundleVerifier`). A
 * cross-language parity test pins the two implementations to agree on the real tree, so a change to
 * one that silently diverges from the other is caught.
 *
 * Design notes:
 * - No timestamps and no absolute paths are stored: the manifest is reproducible and leaks no local
 *   layout. Every path recorded is a POSIX logical path relative to the webview/ or wwwroot/ root.
 * - Files are hashed as raw bytes (no newline normalization), which makes the C# mirror trivial: both
 *   sides just SHA-256 the exact bytes on disk.
 * - The manifest is written only after esbuild AND the html/css copy have all succeeded (see
 *   bundle.mjs), so its mere presence means "a build fully completed"; a crashed/partial build leaves
 *   no manifest and is therefore treated as stale.
 */

import { createHash } from "node:crypto";
import { readdirSync, readFileSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

/** Bump when the manifest shape or the fingerprint algorithm changes; older manifests are rejected. */
export const SCHEMA_VERSION = 1;

/** A discriminator so an unrelated JSON file that happens to sit in wwwroot is never mistaken for us. */
export const MANIFEST_KIND = "specdesk-webview-bundle";

/** The manifest lives beside the bundle it describes, inside wwwroot/. */
export const MANIFEST_FILENAME = "webview.manifest.json";

/** The three runtime files the host serves — hashed as the bundle's outputs. */
export const OUTPUT_FILES = ["webview.js", "index.html", "styles.css"];

/**
 * The esbuild invocation parameters, folded into the input fingerprint so that changing HOW the
 * bundle is produced (entry, format, output name, the bundle flag) forces a rebuild even when no
 * source file changed. bundle.mjs drives the real esbuild call from exactly this object, and the
 * canonical string form is stored in the manifest so the host can reproduce the fingerprint without
 * re-deriving these options itself.
 */
export const BUNDLE_PARAMS = Object.freeze({
  entry: "src/index.ts",
  outfile: "webview.js",
  format: "esm",
  bundle: true,
});

/** The top-level webview files (besides src/**) that feed the bundle: configs and the lock file. */
const TOP_LEVEL_INPUTS = [
  "index.html",
  "styles.css",
  "package.json",
  "package-lock.json",
  "tsconfig.json",
  "tsconfig.build.json",
];

/** SHA-256 of a byte buffer, lowercase hex. */
function sha256Hex(buffer) {
  return createHash("sha256").update(buffer).digest("hex");
}

/** Byte-wise (UTF-8) comparison so the sort order matches C#'s ordinal sort on ASCII logical paths. */
function compareBytes(a, b) {
  return Buffer.compare(Buffer.from(a, "utf8"), Buffer.from(b, "utf8"));
}

/**
 * Fold a list of `{ path, sha256 }` entries into one fingerprint. Entries are sorted by logical path
 * (byte order) and serialized as `path\tsha256\n` lines before a final SHA-256 — deterministic and
 * independent of enumeration order. The `sha256:` prefix labels the algorithm in diagnostics.
 */
export function fingerprintOf(entries) {
  const lines = entries
    .slice()
    .sort((a, b) => compareBytes(a.path, b.path))
    .map((entry) => `${entry.path}\t${entry.sha256}\n`)
    .join("");
  return `sha256:${sha256Hex(Buffer.from(lines, "utf8"))}`;
}

/** The canonical, stable string form of the bundle parameters (sorted keys), stored in the manifest. */
export function bundleParamsString() {
  const sorted = {};
  for (const key of Object.keys(BUNDLE_PARAMS).sort()) {
    sorted[key] = BUNDLE_PARAMS[key];
  }
  return JSON.stringify(sorted);
}

/** Recursively list files under `dir`, returning POSIX logical paths relative to `dir`, sorted. */
function listFilesPosix(dir, prefix) {
  const out = [];
  for (const dirent of readdirSync(dir, { withFileTypes: true })) {
    const abs = join(dir, dirent.name);
    const logical = prefix ? `${prefix}/${dirent.name}` : dirent.name;
    if (dirent.isDirectory()) {
      out.push(...listFilesPosix(abs, logical));
    } else if (dirent.isFile()) {
      out.push({ abs, logical });
    }
  }
  return out;
}

/**
 * The list of `{ path, sha256 }` entries that feed the input fingerprint: every file under src/**,
 * each top-level config/lock input that exists, and a synthetic `#bundle-params` entry. `webviewDir`
 * is the absolute webview/ directory.
 */
export function computeInputEntries(webviewDir) {
  const entries = [];

  const srcDir = join(webviewDir, "src");
  for (const file of listFilesPosix(srcDir, "src")) {
    entries.push({ path: file.logical, sha256: sha256Hex(readFileSync(file.abs)) });
  }

  for (const name of TOP_LEVEL_INPUTS) {
    const abs = join(webviewDir, name);
    let bytes;
    try {
      bytes = readFileSync(abs);
    } catch (error) {
      // A genuinely optional input (e.g. a repo without tsconfig.build.json) simply does not
      // contribute an entry; anything other than "not found" is a real error worth surfacing.
      if (error && error.code === "ENOENT") {
        continue;
      }
      throw error;
    }
    entries.push({ path: name, sha256: sha256Hex(bytes) });
  }

  entries.push({
    path: "#bundle-params",
    sha256: sha256Hex(Buffer.from(bundleParamsString(), "utf8")),
  });
  return entries;
}

/** The input fingerprint over the current webview inputs (see {@link computeInputEntries}). */
export function computeInputFingerprint(webviewDir) {
  return fingerprintOf(computeInputEntries(webviewDir));
}

/**
 * The list of `{ path, sha256 }` entries for the bundle's outputs. Throws if any output is missing —
 * a manifest can only describe a complete bundle. `wwwrootDir` is the absolute wwwroot/ directory.
 */
export function computeOutputEntries(wwwrootDir) {
  return OUTPUT_FILES.map((name) => ({
    path: name,
    sha256: sha256Hex(readFileSync(join(wwwrootDir, name))),
  }));
}

/** Build the full manifest object for a freshly produced bundle. */
export function buildManifest(webviewDir, wwwrootDir) {
  const outputs = computeOutputEntries(wwwrootDir);
  return {
    schema: SCHEMA_VERSION,
    kind: MANIFEST_KIND,
    inputFingerprint: computeInputFingerprint(webviewDir),
    outputFingerprint: fingerprintOf(outputs),
    bundleParams: bundleParamsString(),
    outputs,
  };
}

/** Serialize the manifest deterministically: fixed key order, 2-space indent, trailing newline. */
export function serializeManifest(manifest) {
  const ordered = {
    schema: manifest.schema,
    kind: manifest.kind,
    inputFingerprint: manifest.inputFingerprint,
    outputFingerprint: manifest.outputFingerprint,
    bundleParams: manifest.bundleParams,
    outputs: manifest.outputs.map((o) => ({ path: o.path, sha256: o.sha256 })),
  };
  return `${JSON.stringify(ordered, null, 2)}\n`;
}

/**
 * Write the manifest atomically: to a temp file then rename over the final name, so a reader never
 * observes a half-written manifest.
 */
export function writeManifestAtomic(wwwrootDir, manifest) {
  const finalPath = join(wwwrootDir, MANIFEST_FILENAME);
  const tempPath = `${finalPath}.tmp`;
  writeFileSync(tempPath, serializeManifest(manifest));
  renameSync(tempPath, finalPath);
}

/** Remove any existing manifest (best-effort). Called at the start of a build so a crashed rebuild
 * leaves no manifest — the bundle is then unambiguously "incomplete" rather than stale-but-valid. */
export function removeManifest(wwwrootDir) {
  rmSync(join(wwwrootDir, MANIFEST_FILENAME), { force: true });
}

/** Read and JSON-parse the manifest; returns null if it is absent or unparseable. */
export function readManifest(wwwrootDir) {
  let text;
  try {
    text = readFileSync(join(wwwrootDir, MANIFEST_FILENAME), "utf8");
  } catch (error) {
    if (error && error.code === "ENOENT") {
      return null;
    }
    throw error;
  }
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

/** Verification outcomes, mirrored by the C# host verifier's status enum. */
export const VerifyStatus = Object.freeze({
  UpToDate: "up-to-date",
  ManifestMissing: "manifest-missing",
  SchemaMismatch: "schema-mismatch",
  OutputMissing: "output-missing",
  OutputCorrupt: "output-corrupt",
  InputMismatch: "input-mismatch",
});

/**
 * Verify a built bundle in `wwwrootDir` against the webview inputs in `webviewDir` by content. This
 * is the cheap check the MSBuild target and the CI/local build all share: it recomputes the input
 * fingerprint and re-hashes the outputs, and never trusts a timestamp. Returns `{ status, reason }`.
 */
export function verifyBundle(webviewDir, wwwrootDir) {
  const manifest = readManifest(wwwrootDir);
  if (manifest === null) {
    return { status: VerifyStatus.ManifestMissing, reason: "manifest is absent or unparseable" };
  }
  if (manifest.schema !== SCHEMA_VERSION || manifest.kind !== MANIFEST_KIND) {
    return {
      status: VerifyStatus.SchemaMismatch,
      reason: `manifest schema/kind is ${manifest.schema}/${manifest.kind}, expected ${SCHEMA_VERSION}/${MANIFEST_KIND}`,
    };
  }

  for (const expected of manifest.outputs ?? []) {
    let bytes;
    try {
      bytes = readFileSync(join(wwwrootDir, expected.path));
    } catch {
      return { status: VerifyStatus.OutputMissing, reason: `output ${expected.path} is missing` };
    }
    if (sha256Hex(bytes) !== expected.sha256) {
      return {
        status: VerifyStatus.OutputCorrupt,
        reason: `output ${expected.path} does not match its recorded hash`,
      };
    }
  }

  const currentInput = computeInputFingerprint(webviewDir);
  if (currentInput !== manifest.inputFingerprint) {
    return {
      status: VerifyStatus.InputMismatch,
      reason: `webview inputs changed: current ${currentInput} != manifest ${manifest.inputFingerprint}`,
    };
  }

  return { status: VerifyStatus.UpToDate, reason: "bundle matches current inputs" };
}

/** The scripts/ directory that holds this module, as an absolute native path. */
function scriptsDir(scriptUrl) {
  return dirname(fileURLToPath(scriptUrl));
}

/** Resolve the webview/ input directory (the parent of this scripts/ folder). */
export function defaultWebviewDir(scriptUrl) {
  return join(scriptsDir(scriptUrl), "..");
}

/** Resolve the wwwroot/ output directory the bundle is written to, as an absolute native path. */
export function defaultWwwrootDir(scriptUrl) {
  return join(scriptsDir(scriptUrl), "..", "..", "src", "SpecDesk.Host", "wwwroot");
}
