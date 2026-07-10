using Microsoft.Extensions.Logging.Abstractions;

namespace SpecDesk.Host.Tests;

// The startup gate: given a base directory (where AppContext.BaseDirectory would point), it verifies
// the wwwroot bundle and either returns quietly or throws to stop the app loading an untrustworthy UI.
// A base under the system temp dir has no SpecDesk.slnx ancestor, so the guard treats it as a
// published app (integrity-only); the dev-mode tests plant a slnx + webview/ tree above the base so
// the guard discovers the sources and checks freshness too.
[TestFixture]
public sealed class WebviewBundleGuardTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "specdesk-webview-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public void EnsureServable_PublishedIntactBundle_ReturnsUpToDateWithoutThrowing()
    {
        string baseDir = Path.Combine(_root, "app");
        string wwwroot = Path.Combine(baseDir, "wwwroot");
        string webview = Path.Combine(_root, "authoring-only");
        WebviewTestBundle.CreateSource(webview);
        WebviewTestBundle.CreateOutputs(wwwroot);
        WebviewTestBundle.WriteManifest(wwwroot, webview);

        WebviewBundleVerification result =
            WebviewBundleGuard.EnsureServable(baseDir, NullLogger.Instance, allowStale: false);

        Assert.That(result.Status, Is.EqualTo(WebviewBundleStatus.UpToDate));
    }

    [Test]
    public void EnsureServable_PublishedCorruptBundle_Throws()
    {
        string baseDir = Path.Combine(_root, "app");
        string wwwroot = Path.Combine(baseDir, "wwwroot");
        string webview = Path.Combine(_root, "authoring-only");
        WebviewTestBundle.CreateSource(webview);
        WebviewTestBundle.CreateOutputs(wwwroot);
        WebviewTestBundle.WriteManifest(wwwroot, webview);
        File.WriteAllText(Path.Combine(wwwroot, "webview.js"), "console.log('tampered');\n");

        Assert.That(
            () => WebviewBundleGuard.EnsureServable(baseDir, NullLogger.Instance, allowStale: false),
            Throws.TypeOf<WebviewBundleException>());
    }

    [Test]
    public void EnsureServable_DevFreshBundle_ReturnsUpToDate()
    {
        (string baseDir, string webview, string wwwroot) = SetUpDevTree();
        WebviewTestBundle.WriteManifest(wwwroot, webview);

        WebviewBundleVerification result =
            WebviewBundleGuard.EnsureServable(baseDir, NullLogger.Instance, allowStale: false);

        Assert.That(result.Status, Is.EqualTo(WebviewBundleStatus.UpToDate));
    }

    [Test]
    public void EnsureServable_DevStaleBundle_ThrowsByDefault()
    {
        (string baseDir, string webview, string wwwroot) = SetUpDevTree();
        WebviewTestBundle.WriteManifest(wwwroot, webview);
        File.WriteAllText(Path.Combine(webview, "src", "index.ts"), "export const version = 7;\n");

        Assert.That(
            () => WebviewBundleGuard.EnsureServable(baseDir, NullLogger.Instance, allowStale: false),
            Throws.TypeOf<WebviewBundleException>());
    }

    [Test]
    public void EnsureServable_DevStaleBundle_WithExplicitOptIn_ReturnsInputMismatchWithoutThrowing()
    {
        (string baseDir, string webview, string wwwroot) = SetUpDevTree();
        WebviewTestBundle.WriteManifest(wwwroot, webview);
        File.WriteAllText(Path.Combine(webview, "src", "index.ts"), "export const version = 7;\n");

        WebviewBundleVerification result =
            WebviewBundleGuard.EnsureServable(baseDir, NullLogger.Instance, allowStale: true);

        Assert.That(result.Status, Is.EqualTo(WebviewBundleStatus.InputMismatch));
    }

    [Test]
    public void EnsureServable_DevCorruptBundle_ThrowsEvenWithOptIn()
    {
        // The stale opt-in must never relax an integrity failure — a corrupt bundle is unservable.
        (string baseDir, string webview, string wwwroot) = SetUpDevTree();
        WebviewTestBundle.WriteManifest(wwwroot, webview);
        File.WriteAllText(Path.Combine(wwwroot, "styles.css"), "body { color: tampered }\n");

        Assert.That(
            () => WebviewBundleGuard.EnsureServable(baseDir, NullLogger.Instance, allowStale: true),
            Throws.TypeOf<WebviewBundleException>());
    }

    // Lay out a dev-like tree: a repo root marked by SpecDesk.slnx, a webview/ source tree beside it,
    // and the app base directory (with its wwwroot) nested under the root — so the guard walks up from
    // the base, finds the slnx, and discovers the sources.
    private (string BaseDir, string Webview, string Wwwroot) SetUpDevTree()
    {
        File.WriteAllText(Path.Combine(_root, "SpecDesk.slnx"), "<Solution />\n");
        string webview = Path.Combine(_root, "webview");
        string baseDir = Path.Combine(_root, "src", "SpecDesk.Host", "bin", "Debug", "net10.0");
        string wwwroot = Path.Combine(baseDir, "wwwroot");
        WebviewTestBundle.CreateSource(webview);
        WebviewTestBundle.CreateOutputs(wwwroot);
        return (baseDir, webview, wwwroot);
    }
}
