namespace SpecDesk.Host;

/// <summary>
/// Validates links the webview asks the host to open externally. The webview is untrusted, so this is
/// the security boundary: only an absolute <c>http</c>/<c>https</c> URL is honoured, and a
/// <c>javascript:</c>, <c>file:</c>, <c>data:</c> or otherwise crafted scheme is rejected before it
/// can reach the OS shell. Pure and unit-testable — no I/O and no process launch.
/// </summary>
public static class ExternalLink
{
	/// <summary>
	/// True when <paramref name="raw"/> is an absolute http or https URL; <paramref name="url"/> then
	/// holds its canonical form. Otherwise false and <paramref name="url"/> is empty.
	/// </summary>
	public static bool TryGetSafeHttpUrl(string? raw, out string url)
	{
		url = string.Empty;
		if (string.IsNullOrWhiteSpace(raw))
		{
			return false;
		}

		if (!Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri))
		{
			return false;
		}

		if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
		{
			return false;
		}

		url = uri.AbsoluteUri;
		return true;
	}
}
