using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace SpecDesk.Host;

/// <summary>Thrown when the app is launched from a working copy that lags behind the local published
/// <c>main</c>, so startup stops instead of silently running an old UI built from stale sources.</summary>
internal sealed class MainWorktreeStaleException(string message) : Exception(message);

/// <summary>How the checked-out working copy relates to the local <c>main</c> — the outcome of
/// <see cref="MainWorktreeGuard.Inspect"/>.</summary>
internal enum MainWorktreeRelation
{
    /// <summary>A published/installed app, or no repository above the app — nothing to verify.</summary>
    NotApplicable,

    /// <summary>Running from an isolated task worktree (a jj workspace / git linked worktree). Exempt:
    /// a parallel publication advancing <c>main</c> must not make a valid in-progress tree look stale.</summary>
    IsolatedWorktree,

    /// <summary>No local <c>main</c> branch to compare against (e.g. a fresh CI checkout).</summary>
    NoTarget,

    /// <summary>HEAD equals <c>main</c>.</summary>
    Current,

    /// <summary><c>main</c> is an ancestor of HEAD — the sources already include it.</summary>
    Ahead,

    /// <summary>HEAD and <c>main</c> have diverged (a topic/PR checkout, not the main-copy drift).</summary>
    Diverged,

    /// <summary>HEAD is a strict ancestor of <c>main</c> — the working copy is genuinely behind the
    /// published main. This is the drift the guard exists to catch.</summary>
    Behind,
}

/// <summary>The verdict of inspecting the working copy the running app was built from.</summary>
internal sealed record MainWorktreeStatus(
    MainWorktreeRelation Relation, string Role, string? Head, string? Target, string Reason)
{
    /// <summary>Only a strictly-behind main working copy is a refusal; every other relation proceeds.</summary>
    public bool IsStale => Relation == MainWorktreeRelation.Behind;
}

/// <summary>
/// The startup gate that refuses to launch SpecDesk.Host from a repository main working copy that has
/// drifted behind the local published <c>main</c> — the counterpart, for the running app, of the build
/// target that blocks compiling stale sources. It mirrors <see cref="WebviewBundleGuard"/>: it turns an
/// <see cref="Inspect"/> verdict into an action — proceed, or throw to stop before the WebView loads an
/// old UI.
/// <para>
/// The role (main working copy vs isolated task worktree) is decided from explicit VCS structure — the
/// checkout's own repo root (the directory holding <c>SpecDesk.slnx</c>) versus git's tracked working
/// directory — never guessed from file content. A task worktree is a jj workspace nested under the
/// shared repo, so its root is not git's working directory; it is exempt. Only a genuine "behind"
/// verdict on the actual main working copy refuses, and only then unless the operator opts in with the
/// narrow <see cref="AllowStaleEnvironmentVariable"/> escape hatch.
/// </para>
/// </summary>
internal static class MainWorktreeGuard
{
    /// <summary>Set (to any non-empty value) to downgrade the stale-worktree refusal to a warning, for
    /// the deliberate "run the old build without re-syncing" case.</summary>
    public const string AllowStaleEnvironmentVariable = "SPECDESK_ALLOW_STALE_WORKTREE";

    /// <summary>
    /// Inspect the working copy under <paramref name="baseDirectory"/> and either log the verdict (when
    /// current/exempt/inapplicable) or throw <see cref="MainWorktreeStaleException"/> (when the main
    /// working copy is behind <c>main</c>). Returns the status so a caller/test can inspect it.
    /// <paramref name="allowStale"/> defaults to the environment opt-in but is a parameter so it is
    /// testable without touching process environment.
    /// </summary>
    public static MainWorktreeStatus EnsureCurrent(
        string baseDirectory, ILogger logger, bool? allowStale = null)
    {
        bool stalePermitted = allowStale
            ?? !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AllowStaleEnvironmentVariable));

        MainWorktreeStatus status = Inspect(baseDirectory);

        if (!status.IsStale)
        {
            logger.LogDebug(
                "Working copy currency ({Role}): {Relation} — {Reason}",
                status.Role, status.Relation, status.Reason);
            return status;
        }

        if (stalePermitted)
        {
            logger.LogWarning(
                "Working copy is behind 'main' but launched on request ({Variable} set): {Reason} "
                    + "head={Head} target={Target}",
                AllowStaleEnvironmentVariable, status.Reason, Short(status.Head), Short(status.Target));
            return status;
        }

        logger.LogCritical(
            "Refusing to launch from a stale working copy: {Reason} head={Head} target={Target}. "
                + "The app would otherwise run an old UI built from sources that predate the published "
                + "main. Re-sync with scripts/restore-main-worktree.ps1.",
            status.Reason, Short(status.Head), Short(status.Target));
        throw new MainWorktreeStaleException(
            $"Working copy is behind the local 'main' ({Short(status.Head)} < {Short(status.Target)}): "
                + "it would run an old UI. Re-sync with scripts/restore-main-worktree.ps1, or set "
                + $"{AllowStaleEnvironmentVariable} to run it anyway.");
    }

    /// <summary>
    /// Classify how the working copy the app was built from relates to the local <c>main</c>, purely by
    /// reading VCS state (never mutating anything). Returns <see cref="MainWorktreeRelation.NotApplicable"/>
    /// for a published app with no repository above it.
    /// </summary>
    internal static MainWorktreeStatus Inspect(string baseDirectory)
    {
        // A published/installed app ships without the source tree, so there is no SpecDesk.slnx above
        // the binaries — nothing to verify. (Same "dev vs published" signal WebviewBundleGuard uses.)
        string? devRoot = LocateDevRepoRoot(baseDirectory);
        if (devRoot is null)
        {
            return new MainWorktreeStatus(
                MainWorktreeRelation.NotApplicable, "published", null, null,
                "Published app (no source tree); working-copy currency is not applicable.");
        }

        string? gitDir = Repository.Discover(baseDirectory);
        if (gitDir is null)
        {
            return new MainWorktreeStatus(
                MainWorktreeRelation.NotApplicable, "no-repo", null, null,
                "No git repository discovered; working-copy currency is not applicable.");
        }

        using Repository repo = new(gitDir);
        string? workDir = repo.Info.WorkingDirectory;
        if (workDir is null)
        {
            return new MainWorktreeStatus(
                MainWorktreeRelation.NotApplicable, "bare", null, null,
                "Bare repository; nothing checked out.");
        }

        // Role by structure: if the checkout we run from is not git's own working directory, we are in a
        // tree nested under the shared repo — an isolated task worktree (jj workspace). Exempt it.
        if (!SamePath(devRoot, workDir))
        {
            return new MainWorktreeStatus(
                MainWorktreeRelation.IsolatedWorktree, "task-worktree", null, null,
                "Isolated task worktree; not required to include the latest published main.");
        }

        Branch? main = repo.Branches["main"];
        Commit? head = repo.Head.Tip;
        if (main?.Tip is null)
        {
            return new MainWorktreeStatus(
                MainWorktreeRelation.NoTarget, "main-worktree", head?.Sha, null,
                "No local 'main' branch to compare against; cannot be behind it.");
        }

        if (head is null)
        {
            return new MainWorktreeStatus(
                MainWorktreeRelation.NoTarget, "main-worktree", null, main.Tip.Sha,
                "HEAD is unborn; nothing checked out to be stale.");
        }

        if (string.Equals(head.Sha, main.Tip.Sha, StringComparison.Ordinal))
        {
            return new MainWorktreeStatus(
                MainWorktreeRelation.Current, "main-worktree", head.Sha, main.Tip.Sha,
                "Main working copy is at 'main'.");
        }

        Commit? mergeBase = repo.ObjectDatabase.FindMergeBase(head, main.Tip);

        // HEAD is a proper ancestor of main -> strictly behind: exactly the drift this gate exists for.
        if (mergeBase is not null && string.Equals(mergeBase.Sha, head.Sha, StringComparison.Ordinal))
        {
            return new MainWorktreeStatus(
                MainWorktreeRelation.Behind, "main-worktree", head.Sha, main.Tip.Sha,
                $"Main working copy {Short(head.Sha)} is behind 'main' {Short(main.Tip.Sha)}; "
                    + "its sources do not include the published main.");
        }

        // main is an ancestor of HEAD -> already included (ahead); otherwise the two diverged. Neither is
        // the main-copy drift, so neither blocks a legitimate topic/PR checkout.
        if (mergeBase is not null && string.Equals(mergeBase.Sha, main.Tip.Sha, StringComparison.Ordinal))
        {
            return new MainWorktreeStatus(
                MainWorktreeRelation.Ahead, "main-worktree", head.Sha, main.Tip.Sha,
                "Working copy already includes 'main' (it is ahead).");
        }

        return new MainWorktreeStatus(
            MainWorktreeRelation.Diverged, "main-worktree", head.Sha, main.Tip.Sha,
            "Working copy has diverged from 'main' (a topic/PR checkout, not the main-copy drift).");
    }

    /// <summary>
    /// Walk up from <paramref name="baseDirectory"/> to the repository root — the directory holding
    /// <c>SpecDesk.slnx</c> — which is present only in a source checkout, not a published app.
    /// </summary>
    private static string? LocateDevRepoRoot(string baseDirectory)
    {
        for (DirectoryInfo? dir = new(baseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SpecDesk.slnx")))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(CanonicalPath(a), CanonicalPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The physical path with every symbolic-link component resolved and any trailing separator trimmed,
    /// so two spellings of the same directory compare equal. libgit2 reports
    /// <see cref="RepositoryInformation.WorkingDirectory"/> with links already resolved, whereas walking
    /// up from the app's base directory (<see cref="LocateDevRepoRoot"/>) keeps the un-resolved spelling;
    /// on the macOS runner the system temp dir lives under <c>/var</c> (a symlink to <c>/private/var</c>),
    /// so the two forms differed and the actual main working copy was mis-read as an isolated worktree —
    /// wrongly exempting it from the currency check. Both operands of <see cref="SamePath"/> go through
    /// this, so resolution is symmetric and never introduces a one-sided mismatch; on a tree with no
    /// symlinked component (the ordinary Windows and Linux case) it yields exactly
    /// <see cref="Path.GetFullPath(string)"/>, leaving the gate's behaviour byte-for-byte unchanged.
    /// </summary>
    private static string CanonicalPath(string path) =>
        Path.TrimEndingDirectorySeparator(StripExtendedLengthPrefix(ResolveLinks(Path.GetFullPath(path))));

    /// <summary>
    /// Resolve symbolic links component by component, left to right, rebuilding from the already-resolved
    /// prefix: <see cref="FileSystemInfo.ResolveLinkTarget(bool)"/> only follows the leaf's own chain, so
    /// an intermediate directory link (macOS's <c>/var</c>) is caught only by walking every component. Any
    /// resolution failure (a broken or cyclic link, a path that has vanished) degrades to the un-resolved
    /// remainder rather than throwing, so the comparison can never become more brittle than the pre-fix
    /// lexical one.
    /// </summary>
    private static string ResolveLinks(string fullPath)
    {
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        if (root.Length == 0)
        {
            return fullPath;
        }

        string resolved = root;
        foreach (string segment in fullPath[root.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(resolved, segment);
            try
            {
                FileSystemInfo entry =
                    Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
                string? target = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                resolved = target is null
                    ? candidate
                    : Path.IsPathRooted(target) ? target : Path.Combine(resolved, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A broken/cyclic link, or a component that no longer exists: keep the un-resolved
                // candidate and let path comparison fall back to lexical equality — exactly the pre-fix
                // behaviour, so a filesystem oddity can never make the guard throw at startup.
                resolved = candidate;
            }
        }

        return resolved;
    }

    /// <summary>
    /// Drop a Windows extended-length prefix (<c>\\?\</c> or <c>\\?\UNC\</c>) that
    /// <see cref="FileSystemInfo.ResolveLinkTarget(bool)"/> can prepend when it resolves a junction, so a
    /// resolved and an un-resolved spelling of the same directory still compare equal. A no-op on any path
    /// without such a prefix — every non-Windows path, and every Windows path with no junction component.
    /// </summary>
    private static string StripExtendedLengthPrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string dosPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.Ordinal))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        return path.StartsWith(dosPrefix, StringComparison.Ordinal) ? path[dosPrefix.Length..] : path;
    }

    private static string Short(string? sha) =>
        string.IsNullOrEmpty(sha) ? "(none)" : sha[..Math.Min(12, sha.Length)];
}
