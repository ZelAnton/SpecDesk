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
	[TestCase("mailto:")]
	[TestCase("mailto:?subject=x")]
	[TestCase("")]
	[TestCase("   ")]
	[TestCase(null)]
	public void TryGetSafeExternalUrl_RejectsEverythingElse(string? raw)
	{
		bool ok = ExternalLink.TryGetSafeExternalUrl(raw, out string url);

		Assert.That(ok, Is.False);
		Assert.That(url, Is.Empty);
	}

	// The canonical form must never begin with '-', or it could be mistaken for a flag when passed as
	// the argument to `open` / `xdg-open` on macOS / Linux. A scheme always precedes the rest, so this
	// holds — pin it so a future loosening of the validator can't silently regress the guarantee.
	[TestCase("http://-example.com")]
	[TestCase("https://-x.com/-y")]
	[TestCase("mailto:-flag@x.com")]
	public void TryGetSafeExternalUrl_ResultNeverStartsWithDash(string raw)
	{
		Assert.That(ExternalLink.TryGetSafeExternalUrl(raw, out string url), Is.True);
		Assert.That(url, Does.Not.StartWith("-"));
	}

	[TestCase("mailto:a@b.com", "mailto:a@b.com")]
	[TestCase("mailto:a@b.com?subject=Hi", "mailto:a@b.com")]
	[TestCase("mailto:a@b.com?subject=Hi%0D%0ABcc:victim@x.com", "mailto:a@b.com")]
	public void TryGetSafeExternalUrl_StripsMailtoQuery(string raw, string expected)
	{
		Assert.That(ExternalLink.TryGetSafeExternalUrl(raw, out string url), Is.True);
		Assert.That(url, Is.EqualTo(expected));
	}
}
