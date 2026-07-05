namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class AppAssetResolverTests
{
	private static string Root =>
		Path.GetFullPath(Path.Combine(Path.GetTempPath(), "specdesk-assets"));

	[Test]
	public void ResolveRelative_HappyPath_ReturnsPathInsideRootWithContentType()
	{
		ResolvedAsset? asset = AppAssetResolver.ResolveRelative(Root, "sub/sample.png");

		Assert.That(asset, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(asset!.FilePath, Does.StartWith(Root));
			Assert.That(asset.FilePath, Does.EndWith($"sub{Path.DirectorySeparatorChar}sample.png"));
			Assert.That(asset.ContentType, Is.EqualTo("image/png"));
		});
	}

	// `..` / `..\` traversal escapes the root on every platform and must be rejected.
	[TestCase("../outside.txt")]
	[TestCase("a/../../x")]
	[TestCase("..\\outside")]
	[TestCase("")]
	public void ResolveRelative_EscapingOrEmptyPath_ReturnsNull(string relativePath)
	{
		Assert.That(AppAssetResolver.ResolveRelative(Root, relativePath), Is.Null);
	}

	[Test]
	public void ResolveRelative_WindowsDriveAbsolutePath_ReturnsNull()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Ignore("Drive-letter absolute paths only escape on Windows.");
		}

		Assert.That(AppAssetResolver.ResolveRelative(Root, "C:\\Windows\\win.ini"), Is.Null);
	}

	[Test]
	public void ResolveRelative_LeadingSlash_IsTreatedAsRootRelative()
	{
		// A leading slash (as in a URL path) is root-relative, not filesystem-absolute, so it
		// stays contained inside the asset root rather than escaping.
		ResolvedAsset? asset = AppAssetResolver.ResolveRelative(Root, "/nested/file.txt");

		Assert.That(asset, Is.Not.Null);
		Assert.That(asset!.FilePath, Does.StartWith(Root));
	}

	[Test]
	public void Resolve_AppUrl_ParsesPathAndContentType()
	{
		ResolvedAsset? asset = AppAssetResolver.Resolve(Root, "app://repo/sample.svg");

		Assert.That(asset, Is.Not.Null);
		Assert.That(asset!.ContentType, Is.EqualTo("image/svg+xml"));
	}

	// A percent-encoded `../` survives URL normalization and reaches the resolver, which must
	// decode it and then reject it via the containment check (the traversal probe relies on this).
	[TestCase("app://repo/..%2f..%2foutside.txt")]
	[TestCase("app://repo/%2e%2e%2foutside.txt")]
	public void Resolve_PercentEncodedTraversal_ReturnsNull(string url)
	{
		Assert.That(AppAssetResolver.Resolve(Root, url), Is.Null);
	}

	// A percent-encoded NUL (as in `![x](a%00.png)` rendered to `app://repo/a%00.png`) decodes to an
	// embedded '\0'. Path.GetFullPath throws ArgumentException on that rather than returning a path;
	// the resolver must reject it up front instead of letting that exception escape.
	[Test]
	public void Resolve_PercentEncodedNul_ReturnsNullInsteadOfThrowing()
	{
		Assert.That(() => AppAssetResolver.Resolve(Root, "app://repo/a%00.png"), Throws.Nothing);
		Assert.That(AppAssetResolver.Resolve(Root, "app://repo/a%00.png"), Is.Null);
	}

	[Test]
	public void ResolveRelative_EmbeddedNul_ReturnsNullInsteadOfThrowing()
	{
		Assert.That(() => AppAssetResolver.ResolveRelative(Root, "a\0.png"), Throws.Nothing);
		Assert.That(AppAssetResolver.ResolveRelative(Root, "a\0.png"), Is.Null);
	}

	[TestCase("not-a-url")]
	[TestCase("relative/only.png")]
	public void Resolve_NonAbsoluteOrGarbageUrl_ReturnsNull(string url)
	{
		Assert.That(AppAssetResolver.Resolve(Root, url), Is.Null);
	}

	[TestCase("a.png", ExpectedResult = "image/png")]
	[TestCase("a.svg", ExpectedResult = "image/svg+xml")]
	[TestCase("a.unknownext", ExpectedResult = "application/octet-stream")]
	public string ContentTypeFor_MapsExtension(string path) =>
		AppAssetResolver.ContentTypeFor(path);
}
