namespace SpecDesk.Host.Tests;

// Cross-language parity: the C# WebviewFingerprint algorithm and the Node webview-manifest.mjs it
// mirrors MUST produce identical fingerprints, or the host would reject a bundle the build just
// blessed (and vice versa). This test takes the REAL manifest the build's `npm run bundle` wrote and
// re-derives its fingerprints in C# from the REAL webview tree, asserting they match byte for byte —
// so any divergence between the two implementations fails the build.
//
// It runs only where a built bundle exists next to the source tree (a normal local `dotnet test`).
// The node-less CI .NET leg builds with -p:SkipWebview=true and has no manifest, so the test reports
// inconclusive there rather than failing; the webview is fingerprinted and bundled in the separate
// Node CI job.
[TestFixture]
public sealed class WebviewFingerprintParityTests
{
    private string _webviewDir = string.Empty;
    private string _wwwrootDir = string.Empty;
    private WebviewBundleManifest _manifest = null!;

    [SetUp]
    public void SetUp()
    {
        string? repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            Assert.Ignore("Repository root (SpecDesk.slnx) not found from the test assembly location.");
        }

        _webviewDir = Path.Combine(repoRoot!, "webview");
        _wwwrootDir = Path.Combine(repoRoot!, "src", "SpecDesk.Host", "wwwroot");
        string manifestPath = Path.Combine(_wwwrootDir, WebviewBundleManifest.FileName);

        if (!Directory.Exists(_webviewDir) || !File.Exists(manifestPath))
        {
            Assert.Ignore("No built webview bundle/manifest present (e.g. a SkipWebview build).");
        }

        bool parsed = WebviewBundleManifest.TryParse(File.ReadAllText(manifestPath), out WebviewBundleManifest? manifest);
        Assert.That(parsed, Is.True, "The build-produced manifest should parse.");
        _manifest = manifest!;
    }

    [Test]
    public void InputFingerprint_RecomputedInCSharp_MatchesTheNodeProducedManifest()
    {
        string recomputed = WebviewFingerprint.ComputeInputFingerprint(_webviewDir, _manifest.BundleParams);

        Assert.That(recomputed, Is.EqualTo(_manifest.InputFingerprint));
    }

    [Test]
    public void OutputHashes_RecomputedInCSharp_MatchTheNodeProducedManifest()
    {
        Assert.Multiple(() =>
        {
            foreach (WebviewOutputEntry output in _manifest.Outputs)
            {
                string actual = WebviewFingerprint.Sha256HexOfFile(Path.Combine(_wwwrootDir, output.Path));
                Assert.That(actual, Is.EqualTo(output.Sha256), $"output hash for {output.Path}");
            }
        });
    }

    [Test]
    public void OutputFingerprint_RecomputedInCSharp_MatchesTheNodeProducedManifest()
    {
        string recomputed = WebviewFingerprint.FingerprintOf(
            _manifest.Outputs.Select(o => (o.Path, o.Sha256)));

        Assert.That(recomputed, Is.EqualTo(_manifest.OutputFingerprint));
    }

    private static string? FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SpecDesk.slnx")))
            {
                return dir.FullName;
            }
        }

        return null;
    }
}
