/**
 * Minimal ambient declarations for the Node built-ins and the T-107 manifest module that the Split
 * delivery-smoke harness needs. The webview's tsconfig keeps `types: []` on purpose (this is browser
 * code), so there is no `@types/node`; this delivery gate is the one test that must shell out to run the
 * real bundle and read the built artifact off disk, so it declares exactly the surface it uses — nothing
 * more — rather than pulling the whole Node type surface into the browser-code typecheck.
 */

declare module "node:child_process" {
  export function execFileSync(
    file: string,
    args: readonly string[],
    options?: { cwd?: string; stdio?: "inherit" | "pipe" | "ignore" },
  ): void;
}

declare module "node:fs" {
  export function readFileSync(path: string, encoding: "utf8"): string;
  export function readFileSync(path: string): Uint8Array;
}

declare module "node:path" {
  export function join(...parts: string[]): string;
  export function dirname(path: string): string;
}

declare module "node:url" {
  export function fileURLToPath(url: string): string;
  export function pathToFileURL(path: string): { readonly href: string };
}

declare module "node:crypto" {
  export function createHash(algorithm: string): {
    update(data: Uint8Array | string): { digest(encoding: "hex"): string };
  };
}

declare const process: { readonly execPath: string };

/**
 * The T-107 content-manifest module (scripts/webview-manifest.mjs) — the single source of truth the
 * host verifier mirrors. Only the surface this gate calls is declared; the `*` matches the relative
 * specifier the harness imports it by.
 */
declare module "*/webview-manifest.mjs" {
  export const VerifyStatus: {
    readonly UpToDate: string;
    readonly ManifestMissing: string;
    readonly SchemaMismatch: string;
    readonly OutputMissing: string;
    readonly OutputCorrupt: string;
    readonly InputMismatch: string;
  };
  export const MANIFEST_FILENAME: string;
  export const OUTPUT_FILES: readonly string[];
  export function verifyBundle(
    webviewDir: string,
    wwwrootDir: string,
  ): { status: string; reason: string };
  export function readManifest(wwwrootDir: string): {
    schema: number;
    kind: string;
    inputFingerprint: string;
    outputFingerprint: string;
    bundleParams: string;
    outputs: { path: string; sha256: string }[];
  } | null;
}
