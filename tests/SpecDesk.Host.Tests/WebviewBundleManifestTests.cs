namespace SpecDesk.Host.Tests;

// The manifest parser is the trust boundary: anything it accepts is treated as a real origin record,
// so it must reject malformed or structurally incomplete JSON exactly as if the manifest were absent.
[TestFixture]
public sealed class WebviewBundleManifestTests
{
    private const string Valid = """
        {
          "schema": 1,
          "kind": "specdesk-webview-bundle",
          "inputFingerprint": "sha256:aaa",
          "outputFingerprint": "sha256:bbb",
          "bundleParams": "{\"bundle\":true}",
          "outputs": [
            { "path": "webview.js", "sha256": "111" },
            { "path": "index.html", "sha256": "222" },
            { "path": "styles.css", "sha256": "333" }
          ]
        }
        """;

    [Test]
    public void TryParse_ValidManifest_ReadsEveryField()
    {
        bool ok = WebviewBundleManifest.TryParse(Valid, out WebviewBundleManifest? manifest);

        Assert.That(ok, Is.True);
        Assert.That(manifest, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(manifest!.Schema, Is.EqualTo(1));
            Assert.That(manifest.Kind, Is.EqualTo("specdesk-webview-bundle"));
            Assert.That(manifest.InputFingerprint, Is.EqualTo("sha256:aaa"));
            Assert.That(manifest.OutputFingerprint, Is.EqualTo("sha256:bbb"));
            Assert.That(manifest.BundleParams, Is.EqualTo("{\"bundle\":true}"));
            Assert.That(manifest.Outputs, Has.Count.EqualTo(3));
            Assert.That(manifest.Outputs[0].Path, Is.EqualTo("webview.js"));
            Assert.That(manifest.Outputs[0].Sha256, Is.EqualTo("111"));
        });
    }

    [TestCase("{ not valid json")]
    [TestCase("[]")]
    [TestCase("\"a string\"")]
    [TestCase("42")]
    public void TryParse_NonObjectOrBrokenJson_Fails(string json)
    {
        bool ok = WebviewBundleManifest.TryParse(json, out WebviewBundleManifest? manifest);

        Assert.That(ok, Is.False);
        Assert.That(manifest, Is.Null);
    }

    [TestCase("schema")]
    [TestCase("kind")]
    [TestCase("inputFingerprint")]
    [TestCase("outputFingerprint")]
    [TestCase("bundleParams")]
    [TestCase("outputs")]
    public void TryParse_MissingRequiredField_Fails(string fieldToDrop)
    {
        string json = Valid.Replace($"\"{fieldToDrop}\"", "\"_removed_\"");

        bool ok = WebviewBundleManifest.TryParse(json, out WebviewBundleManifest? manifest);

        Assert.That(ok, Is.False);
        Assert.That(manifest, Is.Null);
    }

    [Test]
    public void TryParse_OutputEntryMissingHash_Fails()
    {
        const string json = """
            {
              "schema": 1,
              "kind": "specdesk-webview-bundle",
              "inputFingerprint": "sha256:aaa",
              "outputFingerprint": "sha256:bbb",
              "bundleParams": "{}",
              "outputs": [ { "path": "webview.js" } ]
            }
            """;

        bool ok = WebviewBundleManifest.TryParse(json, out WebviewBundleManifest? manifest);

        Assert.That(ok, Is.False);
        Assert.That(manifest, Is.Null);
    }

    [Test]
    public void TryParse_WrongFieldType_Fails()
    {
        // schema as a string, not a number.
        string json = Valid.Replace("\"schema\": 1", "\"schema\": \"1\"");

        bool ok = WebviewBundleManifest.TryParse(json, out _);

        Assert.That(ok, Is.False);
    }
}
