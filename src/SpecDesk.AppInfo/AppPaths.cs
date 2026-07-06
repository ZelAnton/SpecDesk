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
/// </summary>
public static class AppPaths
{
    /// <summary><c>%LOCALAPPDATA%\SpecDesk</c>.</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductInfo.Name);

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
