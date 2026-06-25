namespace SpecDesk.Host.Tests;

public sealed class ExternalLinkTests
{
	[TestCase("http://example.com")]
	[TestCase("https://example.com/path?q=1#frag")]
	[TestCase("HTTPS://Example.COM")]
	[TestCase("mailto:a@b.com")]
	[TestCase("mailto:a@b.com?subject=Hi")]
	public void TryGetSafeExternalUrl_AcceptsHttpHttpsAndMailto(string raw)
	{
		bool ok = ExternalLink.TryGetSafeExternalUrl(raw, out string url);

		Assert.That(ok, Is.True);
		Assert.That(url, Is.Not.Empty);
	}

	[TestCase("javascript:alert(1)")]
	[TestCase("file:///etc/passwd")]
	[TestCase("data:text/html,<script>x</script>")]
	[TestCase("ftp://example.com")]
	[TestCase("tel:+15551234")]
	[TestCase("/relative/path")]
	[TestCase("other-doc.md")]
	[TestCase("")]
	[TestCase("   ")]
	[TestCase(null)]
	public void TryGetSafeExternalUrl_RejectsEverythingElse(string? raw)
	{
		bool ok = ExternalLink.TryGetSafeExternalUrl(raw, out string url);

		Assert.That(ok, Is.False);
		Assert.That(url, Is.Empty);
	}
}
