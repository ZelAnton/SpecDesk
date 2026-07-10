using System.Security.Cryptography;
using System.Text;

namespace SpecDesk.Host;

/// <summary>
/// The content-fingerprint algorithm for the webview bundle, mirrored verbatim from
/// <c>webview/scripts/webview-manifest.mjs</c>. The two implementations MUST agree bit for bit; a
/// cross-language parity test (WebviewFingerprintParityTests) pins them against the real webview tree
/// and its freshly built manifest, so a change to one that silently diverges from the other fails the
/// build.
/// <para>
/// Every path this class touches is a POSIX logical path relative to the webview/ input root (e.g.
/// <c>src/index.ts</c>, <c>package.json</c>) — never an absolute local path — so nothing derived from
/// it can leak the machine's directory layout into a manifest or a diagnostic line.
/// </para>
/// </summary>
internal static class WebviewFingerprint
{
    /// <summary>The runtime files the host serves; the bundle's outputs, in manifest order.</summary>
    public static readonly IReadOnlyList<string> OutputFiles = ["webview.js", "index.html", "styles.css"];

    // The top-level webview inputs (besides src/**) that feed the bundle: configs and the lock file.
    // An input that does not exist simply contributes no entry (matching the Node side's ENOENT skip).
    private static readonly string[] TopLevelInputs =
    [
        "index.html",
        "styles.css",
        "package.json",
        "package-lock.json",
        "tsconfig.json",
        "tsconfig.build.json",
    ];

    /// <summary>SHA-256 of a byte span as lowercase hex — the same encoding as Node's
    /// <c>createHash("sha256").digest("hex")</c>.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>SHA-256 (lowercase hex) of a file's raw bytes.</summary>
    public static string Sha256HexOfFile(string path) => Sha256Hex(File.ReadAllBytes(path));

    /// <summary>
    /// Fold a set of <c>(path, sha256)</c> entries into one fingerprint: sort by logical path in
    /// ordinal (byte) order, serialize each as <c>path\tsha256\n</c>, then SHA-256 the UTF-8 whole.
    /// The <c>sha256:</c> prefix labels the algorithm the way the manifest and diagnostics show it.
    /// Ordinal order matches the Node side's UTF-8 byte comparison because every logical path here is
    /// ASCII.
    /// </summary>
    public static string FingerprintOf(IEnumerable<(string Path, string Sha256)> entries)
    {
        StringBuilder builder = new();
        foreach ((string Path, string Sha256) entry in entries.OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            builder.Append(entry.Path).Append('\t').Append(entry.Sha256).Append('\n');
        }

        return "sha256:" + Sha256Hex(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    /// <summary>
    /// Recompute the input fingerprint over the current webview inputs under
    /// <paramref name="webviewDir"/>. The bundle-parameters entry is taken from
    /// <paramref name="bundleParams"/> — the canonical string the manifest recorded at build time —
    /// rather than re-derived here, because esbuild's options live only on the build side. That makes
    /// the host's freshness check sensitive to any source/config/lock content change (its purpose)
    /// while leaving bundle-parameter changes to the build-time verifier, which owns those options.
    /// </summary>
    public static string ComputeInputFingerprint(string webviewDir, string bundleParams) =>
        FingerprintOf(ComputeInputEntries(webviewDir, bundleParams));

    /// <summary>The <c>(path, sha256)</c> entries that feed the input fingerprint: every file under
    /// <c>src/**</c>, each top-level input that exists, and the synthetic <c>#bundle-params</c> entry.</summary>
    public static IReadOnlyList<(string Path, string Sha256)> ComputeInputEntries(
        string webviewDir, string bundleParams)
    {
        List<(string Path, string Sha256)> entries = [];

        string srcDir = Path.Combine(webviewDir, "src");
        if (Directory.Exists(srcDir))
        {
            foreach (string file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                string logical = "src/" + Path.GetRelativePath(srcDir, file).Replace('\\', '/');
                entries.Add((logical, Sha256HexOfFile(file)));
            }
        }

        foreach (string name in TopLevelInputs)
        {
            string path = Path.Combine(webviewDir, name);
            if (File.Exists(path))
            {
                entries.Add((name, Sha256HexOfFile(path)));
            }
        }

        entries.Add(("#bundle-params", Sha256Hex(Encoding.UTF8.GetBytes(bundleParams))));
        return entries;
    }
}
