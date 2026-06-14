namespace SpecDesk.Host;

/// <summary>A file resolved from an <c>app://</c> request: an absolute path proven to be inside
/// the asset root, plus its MIME content type.</summary>
public sealed record ResolvedAsset(string FilePath, string ContentType);

/// <summary>
/// Resolves <c>app://</c> request URLs to safe absolute file paths under a root directory. Pure
/// and unit-testable — no Photino and no I/O beyond path canonicalization. The containment check
/// in <see cref="ResolveRelative"/> is the security boundary: a request can never escape the root
/// via <c>..</c>, backslashes, or an absolute path, regardless of how the URL was encoded.
/// <para>
/// Limitation: containment is textual. <see cref="Path.GetFullPath(string)"/> does not resolve
/// symlinks/junctions, so a reparse point <em>inside</em> the root that targets a location outside
/// it would still be served. This is safe while the root is content we author (<c>samples/</c>),
/// but must be revisited when the root becomes an untrusted opened repo (PoC-3/4) — e.g. by
/// resolving the final real path and re-checking containment.
/// </para>
/// </summary>
public static class AppAssetResolver
{
	/// <summary>
	/// Resolve a full <c>app://</c> URL against <paramref name="root"/>. Returns <c>null</c> for a
	/// non-absolute URL or a path that escapes the root. Existence is NOT checked here (the caller
	/// does), so the content type is available even for a missing file.
	/// </summary>
	public static ResolvedAsset? Resolve(string root, string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
		{
			return null;
		}

		return ResolveRelative(root, Uri.UnescapeDataString(uri.AbsolutePath));
	}

	/// <summary>
	/// Resolve a relative path against <paramref name="root"/>, rejecting (returning <c>null</c>)
	/// anything that resolves outside the root. This is the security core, tested directly with
	/// adversarial inputs so the defense does not depend on <see cref="Uri"/> normalization.
	/// </summary>
	public static ResolvedAsset? ResolveRelative(string root, string relativePath)
	{
		string relative = relativePath.Replace('\\', '/').TrimStart('/');
		if (relative.Length == 0)
		{
			return null;
		}

		string rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
		string candidate = Path.GetFullPath(Path.Combine(rootFull, relative));
		string prefix = rootFull + Path.DirectorySeparatorChar;
		if (!candidate.StartsWith(prefix, PathComparison))
		{
			return null;
		}

		return new ResolvedAsset(candidate, ContentTypeFor(candidate));
	}

	/// <summary>Map a file extension to a MIME content type, defaulting to a binary stream.</summary>
	public static string ContentTypeFor(string path) =>
		Path.GetExtension(path).ToLowerInvariant() switch
		{
			".png" => "image/png",
			".jpg" or ".jpeg" => "image/jpeg",
			".gif" => "image/gif",
			".webp" => "image/webp",
			".svg" => "image/svg+xml",
			".css" => "text/css",
			".js" => "text/javascript",
			".html" or ".htm" => "text/html",
			".json" => "application/json",
			_ => "application/octet-stream",
		};

	// The working tree is case-insensitive on Windows (the v1 target) and case-sensitive elsewhere.
	private static StringComparison PathComparison =>
		OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
