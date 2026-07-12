namespace SpecDesk.Git;

/// <summary>
/// Clones a GitHub repository into a managed local folder so it can be opened as a workspace. Kept behind
/// an interface (like <see cref="IDocumentVersioning"/>) so the host is testable without performing a real
/// clone and so no LibGit2Sharp types leak into <c>SpecDesk.Host</c>. The caller chooses the exact
/// destination folder (the host namespaces it by owner AND name, so two repos with the same name from
/// different owners never collide).
/// </summary>
public interface IRepositoryCloner
{
    /// <summary>
    /// True when <paramref name="destinationPath"/> already holds a valid clone (a git working tree). The
    /// host uses this to open an existing clone directly instead of cloning again — and, unlike a bare
    /// directory-exists check, it treats partial/faulted debris as "not cloned" so a broken folder is
    /// re-cloned rather than opened as an empty workspace.
    /// </summary>
    bool IsCloned(string destinationPath);

    /// <summary>
    /// Clone the repository at <paramref name="url"/> into <paramref name="destinationPath"/> (the exact
    /// folder the host chose) and return that path.
    /// <para>
    /// Idempotent: if <paramref name="destinationPath"/> already holds a valid git working tree it is
    /// returned as-is WITHOUT re-cloning ("already cloned"); if it holds partial debris (a previous clone
    /// cancelled/faulted mid-transfer) that debris is cleared first and the repo re-cloned. When
    /// <paramref name="accessToken"/> is non-null and non-empty it authenticates a private repo — and,
    /// mirroring the push path, is handed ONLY to an HTTPS github.com host (never a look-alike, an SSH
    /// remote, or a local-file remote), so a re-pointed URL can never capture it; a null/empty token clones
    /// anonymously (public repos). Throws on a clone failure (a <c>LibGit2SharpException</c>, or an
    /// <see cref="System.OperationCanceledException"/> when <paramref name="ct"/> is signalled mid-transfer).
    /// </para>
    /// </summary>
    string CloneOrReuse(string url, string destinationPath, string? accessToken, CancellationToken ct);
}

public sealed record LocalRepositoryInfo(string DefaultBranch, IReadOnlyList<string> Branches);

public interface ILocalRepositoryInspector
{
	LocalRepositoryInfo Inspect(string repositoryPath, string knownDefaultBranch);
}
