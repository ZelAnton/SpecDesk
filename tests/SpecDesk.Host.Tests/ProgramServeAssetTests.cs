namespace SpecDesk.Host.Tests;

// ServeAsset is the WebView2 custom-scheme callback behind app://; per its own doc comment it must
// never let an exception escape into the message pump (that would crash the whole process). These
// tests drive it with the same adversarial inputs AppAssetResolverTests.cs exercises at the resolver
// level, to pin the end-to-end invariant at the boundary that actually matters to the running app.
[TestFixture]
public sealed class ProgramServeAssetTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "specdesk-serveasset-" + Guid.NewGuid().ToString("N"));
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
    public void ServeAsset_PercentEncodedNul_ReturnsTheBrokenResourceResponseInsteadOfCrashing()
    {
        // The exact DoS trigger from a `![x](a%00.png)` image link: Uri.UnescapeDataString decodes
        // %00 to an embedded NUL before it ever reaches AppAssetResolver.
        Stream stream = Program.ServeAsset(_root, "app://repo/a%00.png", out string contentType);

        Assert.Multiple(() =>
        {
            Assert.That(stream, Is.SameAs(Stream.Null));
            Assert.That(contentType, Is.EqualTo("text/plain"));
        });
    }

    [TestCase("app://repo/a%00.png")]
    [TestCase("app://repo/../../outside.txt")]
    [TestCase("app://repo/%2e%2e%2foutside.txt")]
    [TestCase("not-a-url")]
    [TestCase("")]
    [TestCase("app://repo/missing-file.png")]
    public void ServeAsset_NeverThrowsForAdversarialOrMissingInput(string url)
    {
        Assert.That(() => Program.ServeAsset(_root, url, out _), Throws.Nothing);
    }

    [Test]
    public void ServeAsset_NoRepoRootYet_ReturnsTheBrokenResourceResponse()
    {
        Stream stream = Program.ServeAsset(null, "app://repo/a.png", out string contentType);

        Assert.Multiple(() =>
        {
            Assert.That(stream, Is.SameAs(Stream.Null));
            Assert.That(contentType, Is.EqualTo("text/plain"));
        });
    }

    [Test]
    public void ServeAsset_ExistingFile_ReturnsItsContentAndContentType()
    {
        File.WriteAllText(Path.Combine(_root, "sample.svg"), "<svg/>");

        Stream stream = Program.ServeAsset(_root, "app://repo/sample.svg", out string contentType);
        using StreamReader reader = new(stream);

        Assert.Multiple(() =>
        {
            Assert.That(contentType, Is.EqualTo("image/svg+xml"));
            Assert.That(reader.ReadToEnd(), Is.EqualTo("<svg/>"));
        });
    }
}
