namespace SpecDesk.Host.Tests;

public sealed class ExternalLinkTests
{
	[TestCase("http://example.com")]
	[TestCase("https://example.com/path?q=1#frag")]
	[TestCase("HTTPS://Example.COM")]
	public void TryGetSafeHttpUrl_AcceptsAbsoluteHttpAndHttps(string raw)
	{
		bool ok = ExternalLink.TryGetSafeHttpUrl(raw, out string url);

		Assert.That(ok, Is.True);
		Assert.That(url, Is.Not.Empty);
	}

	[TestCase("javascript:alert(1)")]
	[TestCase("file:///etc/passwd")]
	[TestCase("data:text/html,<script>x</script>")]
	[TestCase("ftp://example.com")]
	[TestCase("mailto:a@b.com")]
	[TestCase("/relative/path")]
	[TestCase("other-doc.md")]
	[TestCase("")]
	[TestCase("   ")]
	[TestCase(null)]
	public void TryGetSafeHttpUrl_RejectsEverythingElse(string? raw)
	{
		bool ok = ExternalLink.TryGetSafeHttpUrl(raw, out string url);

		Assert.That(ok, Is.False);
		Assert.That(url, Is.Empty);
	}
}
