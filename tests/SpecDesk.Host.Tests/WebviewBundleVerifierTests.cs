namespace SpecDesk.Host.Tests;

// The heart of the "prove the served UI by content, not timestamp" feature. Each test builds a
// genuinely fresh input tree + bundle + manifest, then perturbs exactly one thing and asserts the
// verifier reaches the right verdict — crucially, that its verdict never depends on file modification
// times. `webviewSourceDir` non-null models a dev run (source tree beside the build); null models a
// published app that ships the manifest but no sources.
[TestFixture]
public sealed class WebviewBundleVerifierTests
{
    private string _webviewDir = string.Empty;
    private string _wwwrootDir = string.Empty;

    [SetUp]
    public void SetUp()
    {
        string root = Path.Combine(Path.GetTempPath(), "specdesk-webview-verify-" + Guid.NewGuid().ToString("N"));
        _webviewDir = Path.Combine(root, "webview");
        _wwwrootDir = Path.Combine(root, "wwwroot");
        WebviewTestBundle.CreateSource(_webviewDir);
        WebviewTestBundle.CreateOutputs(_wwwrootDir);
        WebviewTestBundle.WriteManifest(_wwwrootDir, _webviewDir);
    }

    [TearDown]
    public void TearDown()
    {
        string root = Directory.GetParent(_webviewDir)!.FullName;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Verify_GenuinelyFreshBundle_IsUpToDate()
    {
        WebviewBundleVerification result = WebviewBundleVerifier.Verify(_wwwrootDir, _webviewDir);

        Assert.That(result.Status, Is.EqualTo(WebviewBundleStatus.UpToDate));
    }

    [Test]
    public void Verify_FreshBundle_WithOutputTimestampsInTheFuture_IsStillUpToDate()
    {
        // The old timestamp scheme would treat a bundle whose outputs are newer than every input as up
        // to date. Content verification must reach the same (correct) verdict here — a newer mtime with
        // unchanged content is a no-op, not a spurious rebuild.
        foreach (string name in WebviewFingerprint.OutputFiles)
        {
            File.SetLastWriteTimeUtc(Path.Combine(_wwwrootDir, name), DateTime.UtcNow.AddDays(1));
        }

        WebviewBundleVerification result = WebviewBundleVerifier.Verify(_wwwrootDir, _webviewDir);

        Assert.That(result.Status, Is.EqualTo(WebviewBundleStatus.UpToDate));
    }

    [Test]
    public void Verify_ChangedSourceWithAnOlderTimestamp_IsInputMismatch()
    {
        // A source edited but stamped *older* than the bundle is exactly what a working-copy switch or
        // a timestamp-preserving restore produces; the timestamp scheme would miss it. Content wins.
        string edited = Path.Combine(_webviewDir, "src", "index.ts");
        File.WriteAllText(edited, "export const version = 2;\n");
        File.SetLastWriteTimeUtc(edited, DateTime.UtcNow.AddDays(-30));

        WebviewBundleVerification result = WebviewBundleVerifier.Verify(_wwwrootDir, _webviewDir);

        Assert.That(result.Status, Is.EqualTo(WebviewBundleStatus.InputMismatch));
    }

    [Test]
    public void Verify_StaleBundleWithANewerOutputTimestamp_IsStillInputMismatch()
    {
        // The "old bundle, newer mtime" trap: a source changed, yet the output file is stamped newer
        // than it. Timestamp logic would call this fresh; content logic must call it stale.
        File.WriteAllText(Path.Combine(_webviewDir, "src", "util", "log.ts"), "export const log = (x) => x;\n");
        foreach (string name in WebviewFingerprint.OutputFiles)
        {
            File.SetLastWriteTimeUtc(Path.Combine(_wwwrootDir, name), DateTime.UtcNow.AddDays(1));
        }

        WebviewBundleVerification result = WebviewBundleVerifier.Verify(_wwwrootDir, _webviewDir);

        Assert.That(result.Status, Is.EqualTo(WebviewBundleStatus.InputMismatch));
    }

    [Test]
    public void Verify_ChangedLockFile_IsInputMismatch()
    {
        File.WriteAllText(Path.Combine(_webviewDir, "package-lock.json"), "{ \"lockfileVersion\": 4 }\n");

        WebviewBundleVerification result = WebviewBundleVerifier.Verify(_wwwrootDir, _webviewDir);

        Assert.That(result.Status, Is.EqualTo(WebviewBundleStatus.InputMismatch));
    }

    [Test]
    public void Verify_ChangedTsconfig_IsInputMismatch()
    {
        File.WriteAllText(Path.Combine(_webviewDir, "tsconfig.json"), "{ \"compilerOptions\": { \"strict\": true } }\n");

        WebviewBundleVerification result = WebviewBundleVerifier.Verify(_wwwrootDir, _webviewDir);

        Assert.That(result.Status, Is.EqualTo(WebviewBundleStatus.InputMismatch));
    }

    [Test]
    public void Verify_MissingOutput_IsOutputMissing()
    {
        File.Delete(Path.Combine(_wwwrootDir, "styles.css"));

        WebviewBundleVerification result = WebviewBundleVerifier.Verify(_wwwrootDir, _webviewDir);

        Assert.That(result.Status, Is.EqualTo(WebviewBundleStatus.OutputMissing));
    }

    [Test]
    public void Verify_CorruptedOutput_IsOutputCorrupt()
    {
        File.WriteAllText(Path.Combine(_wwwrootDir, "webview.js"), "console.log('tampered');\n");

        WebviewBundleVerification result = WebviewBundleVerifier.Verify(_wwwrootDir, _webviewDir);

        Assert.That(result.Status, Is.EqualTo(WebviewBundleStatus.OutputCorrupt));
    }

    [Test]
    public void Verify_MissingManifest_IsManifestMissing()
    {
        File.Delete(Path.Combine(_wwwrootDir, WebviewBundleManifest.FileName));

        WebviewBundleVerification result = WebviewBundleVerifier.Verify(_wwwrootDir, _webviewDir);

        Assert.That(result.Status, Is.EqualTo(WebviewBundleStatus.ManifestMissing));
    }

    [Test]
    public void Verify_MalformedManifest_IsManifestUnreadable()
    {
        File.WriteAllText(Path.Combine(_wwwrootDir, WebviewBundleManifest.FileName), "{ not json");

        WebviewBundleVerification result = WebviewBundleVerifier.Verify(_wwwrootDir, _webviewDir);

        Assert.That(result.Status, Is.EqualTo(WebviewBundleStatus.ManifestUnreadable));
    }

    [Test]
    public void Verify_OlderSchemaManifest_IsSchemaMismatch()
    {
        WebviewTestBundle.WriteManifest(_wwwrootDir, _webviewDir, schema: WebviewBundleManifest.SupportedSchema - 1);

        WebviewBundleVerification result = WebviewBundleVerifier.Verify(_wwwrootDir, _webviewDir);

        Assert.That(result.Status, Is.EqualTo(WebviewBundleStatus.SchemaMismatch));
    }

    [Test]
    public void Verify_ForeignKindManifest_IsSchemaMismatch()
    {
        WebviewTestBundle.WriteManifest(_wwwrootDir, _webviewDir, kind: "something-else");

        WebviewBundleVerification result = WebviewBundleVerifier.Verify(_wwwrootDir, _webviewDir);

        Assert.That(result.Status, Is.EqualTo(WebviewBundleStatus.SchemaMismatch));
    }

    [Test]
    public void Verify_PublishedApp_ValidatesIntegrityWithoutASourceTree()
    {
        // Published mode passes null for the source dir: even a source change is invisible (there is no
        // source), so an intact bundle is up to date...
        File.WriteAllText(Path.Combine(_webviewDir, "src", "index.ts"), "export const version = 99;\n");

        WebviewBundleVerification intact = WebviewBundleVerifier.Verify(_wwwrootDir, webviewSourceDir: null);
        Assert.That(intact.Status, Is.EqualTo(WebviewBundleStatus.UpToDate));

        // ...but a corrupt shipped file is still caught with no sources present.
        File.WriteAllText(Path.Combine(_wwwrootDir, "index.html"), "<!doctype html><title>tampered</title>\n");
        WebviewBundleVerification corrupt = WebviewBundleVerifier.Verify(_wwwrootDir, webviewSourceDir: null);
        Assert.That(corrupt.Status, Is.EqualTo(WebviewBundleStatus.OutputCorrupt));
    }

    [Test]
    public void Verify_ExposesFingerprintsForDiagnostics_WithoutAbsolutePaths()
    {
        WebviewBundleVerification result = WebviewBundleVerifier.Verify(_wwwrootDir, _webviewDir);

        Assert.Multiple(() =>
        {
            Assert.That(result.InputFingerprint, Does.StartWith("sha256:"));
            Assert.That(result.OutputFingerprint, Does.StartWith("sha256:"));
            // A path-free reason: no drive letter / temp path leaks into diagnostics.
            Assert.That(result.Reason, Does.Not.Contain(Path.GetTempPath()));
            Assert.That(result.Reason, Does.Not.Contain(":\\"));
        });
    }
}
