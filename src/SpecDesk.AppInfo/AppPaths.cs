namespace SpecDesk.AppInfo;

/// <summary>
/// The app's local, unpackaged data root and the fixed subdirectories/file-name prefix under it. Before
/// this type existed, <c>%LOCALAPPDATA%\SpecDesk</c> was assembled independently at three call sites
/// (the sample repo, the GitHub auth/token directory, and the log directory) — each one, by construction,
/// byte-identical, but only by convention, not by a shared source. <see cref="Auth"/> in particular backs
/// the DPAPI-encrypted token store: any future rename/centralization must keep producing the exact same
/// path, or an existing installation's signed-in session is silently orphaned (see
/// AppPathsTests.Auth_IsByteIdenticalToTheOriginalHandRolledPath in SpecDesk.AppInfo.Tests, which pins the
/// literal string).
///
/// The root is overridable with <c>SPECDESK_DATA_ROOT</c> (a dev run or the full-app E2E points it at a
/// disposable directory so the sample repo, auth, and logs all move together). Unset, the default is
/// byte-for-byte the historical path.
/// </summary>
public static class AppPaths
{
	/// <summary><c>SPECDESK_DATA_ROOT</c> if set, else <c>%LOCALAPPDATA%\SpecDesk</c>.</summary>
	public static string Root { get; } = ResolveRoot(Environment.GetEnvironmentVariable);

	/// <summary>
	/// Resolve the data root from the environment: <c>SPECDESK_DATA_ROOT</c> (normalised to an absolute
	/// path) overrides the default <c>%LOCALAPPDATA%\SpecDesk</c>, moving the sample repo, auth, and logs
	/// together — overriding <c>LOCALAPPDATA</c> itself does not work, since .NET resolves the known
	/// folder through the OS rather than the env var. Pure over the env accessor so it is unit-testable
	/// without touching process env; a malformed override falls back to the default rather than crashing
	/// startup (this runs in the static initializer), and when unset the result is byte-identical to the
	/// historical hand-rolled path (pinned by AppPathsTests).
	/// </summary>
	internal static string ResolveRoot(Func<string, string?> getEnv)
	{
		string? overridden = getEnv("SPECDESK_DATA_ROOT");
		if (!string.IsNullOrWhiteSpace(overridden))
		{
			try
			{
				return Path.GetFullPath(overridden);
			}
			catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
			{
				// A malformed SPECDESK_DATA_ROOT (whitespace is handled above; a null character or an
				// invalid path lands here) must not crash startup — fall back to the default root below.
			}
		}

		return Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductInfo.Name);
	}

	/// <summary>The writable, git-versioned sample repo seeded on first run.</summary>
	public static string SampleRepo { get; } = Path.Combine(Root, "sample-repo");

	/// <summary>The GitHub device-flow auth directory: the DPAPI-encrypted token file lives here.</summary>
	public static string Auth { get; } = Path.Combine(Root, "auth");

	/// <summary>The rolling log file directory.</summary>
	public static string Logs { get; } = Path.Combine(Root, "logs");

	/// <summary>The rolling log files' shared name prefix (e.g. <c>specdesk-20260101.log</c>), derived
	/// from <see cref="ProductInfo.Name"/> rather than a second hard-coded lowercase literal.</summary>
	public static string LogFilePrefix { get; } = ProductInfo.Name.ToLowerInvariant() + "-";
}
