using System.Text.Json;

namespace SpecDesk.Host.Tests;

/// <summary>
/// Builds synthetic webview input trees, output bundles, and manifests in temp directories for the
/// bundle-verification tests. The manifest is written with the SAME fingerprint algorithm the host
/// verifier reads (<see cref="WebviewFingerprint"/>); a separate cross-language parity test
/// (WebviewFingerprintParityTests) proves that algorithm agrees with the Node build side, so using it
/// here to synthesize a "genuinely fresh" manifest is legitimate rather than circular.
/// </summary>
internal static class WebviewTestBundle
{
    /// <summary>The canonical bundle-parameters string the real build records (sorted keys). Its exact
    /// value does not matter to these tests — the manifest and the verifier both use whatever string
    /// is recorded — but using the real one keeps the fixtures realistic.</summary>
    public const string BundleParams =
        "{\"bundle\":true,\"entry\":\"src/index.ts\",\"format\":\"esm\",\"outfile\":\"webview.js\"}";

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    /// <summary>Create a small but representative webview input tree: nested src/**, the two copied
    /// runtime files, and every config/lock input the fingerprint covers.</summary>
    public static void CreateSource(string webviewDir)
    {
        Directory.CreateDirectory(Path.Combine(webviewDir, "src", "util"));
        File.WriteAllText(Path.Combine(webviewDir, "src", "index.ts"), "export const version = 1;\n");
        File.WriteAllText(Path.Combine(webviewDir, "src", "util", "log.ts"), "export const log = () => {};\n");
        File.WriteAllText(Path.Combine(webviewDir, "index.html"), "<!doctype html><title>spec</title>\n");
        File.WriteAllText(Path.Combine(webviewDir, "styles.css"), "body { color: #123 }\n");
        File.WriteAllText(Path.Combine(webviewDir, "package.json"), "{ \"name\": \"specdesk-webview\" }\n");
        File.WriteAllText(Path.Combine(webviewDir, "package-lock.json"), "{ \"lockfileVersion\": 3 }\n");
        File.WriteAllText(Path.Combine(webviewDir, "tsconfig.json"), "{ \"compilerOptions\": {} }\n");
        File.WriteAllText(Path.Combine(webviewDir, "tsconfig.build.json"), "{ \"extends\": \"./tsconfig.json\" }\n");
    }

    /// <summary>Write the three served runtime files into a wwwroot directory.</summary>
    public static void CreateOutputs(string wwwrootDir)
    {
        Directory.CreateDirectory(wwwrootDir);
        File.WriteAllText(Path.Combine(wwwrootDir, "webview.js"), "console.log('specdesk');\n");
        File.WriteAllText(Path.Combine(wwwrootDir, "index.html"), "<!doctype html><title>spec</title>\n");
        File.WriteAllText(Path.Combine(wwwrootDir, "styles.css"), "body { color: #123 }\n");
    }

    /// <summary>Write a manifest describing the current outputs in <paramref name="wwwrootDir"/> and
    /// the current inputs in <paramref name="webviewDir"/>. The optional overrides let negative tests
    /// forge an older schema or a foreign kind without hand-writing the whole JSON.</summary>
    public static void WriteManifest(
        string wwwrootDir,
        string webviewDir,
        int schema = WebviewBundleManifest.SupportedSchema,
        string kind = WebviewBundleManifest.ExpectedKind,
        string bundleParams = BundleParams)
    {
        string inputFingerprint = WebviewFingerprint.ComputeInputFingerprint(webviewDir, bundleParams);
        var outputs = WebviewFingerprint.OutputFiles
            .Select(name => new
            {
                path = name,
                sha256 = WebviewFingerprint.Sha256HexOfFile(Path.Combine(wwwrootDir, name)),
            })
            .ToList();
        string outputFingerprint = WebviewFingerprint.FingerprintOf(
            outputs.Select(o => (o.path, o.sha256)));

        string json = JsonSerializer.Serialize(
            new
            {
                schema,
                kind,
                inputFingerprint,
                outputFingerprint,
                bundleParams,
                outputs,
            },
            ManifestJson);
        File.WriteAllText(Path.Combine(wwwrootDir, WebviewBundleManifest.FileName), json);
    }
}
