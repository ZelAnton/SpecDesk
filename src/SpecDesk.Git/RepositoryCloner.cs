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
	/// True only when <paramref name="destinationPath"/> is the root of a valid working tree whose canonical
	/// <c>origin</c> identifies the repository at <paramref name="url"/>. This is a read-only local check: it
	/// never fetches, clones, or changes repository configuration.
	/// </summary>
	bool IsCloneOf(string destinationPath, string url);

	/// <summary>
	/// True only when <paramref name="destinationPath"/> is the exact root of the repository at
	/// <paramref name="url"/> and its HEAD still matches <paramref name="expectedCurrentBranch"/>. A null
	/// expected branch requires a detached HEAD. This is a fail-closed, read-only local probe.
	/// </summary>
	bool IsCloneOfAtBranch(string destinationPath, string url, string? expectedCurrentBranch);

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

/// <summary>
/// The requested clone destination was claimed by a different filesystem entry or repository before the
/// clone could publish. The caller can surface its existing-copy conflict flow without treating the entry as
/// the requested repository.
/// </summary>
public sealed class RepositoryDestinationConflictException : IOException
{
	public RepositoryDestinationConflictException(string destinationPath)
		: base("The repository destination is already in use.")
	{
		DestinationPath = destinationPath;
	}

	public string DestinationPath { get; }
}

/// <summary>
/// The local working tree no longer belongs to the repository selected by the caller.
/// </summary>
public sealed class RepositoryIdentityMismatchException : InvalidOperationException
{
	public RepositoryIdentityMismatchException()
		: base("The local copy belongs to a different repository.")
	{
	}

	internal RepositoryIdentityMismatchException(string message)
		: base(message)
	{
	}
}

public sealed record LocalRepositoryStatus(
	int Ahead,
	int Behind,
	bool HasUncommitted,
	int StashCount,
	bool HasConflicts);

public sealed record LocalBranchInfo(
	string Name,
	LocalRepositoryStatus Status,
	bool CanDelete = false,
	bool CanRename = false);

public sealed record LocalRepositoryInfo(
	string DefaultBranch,
	string? CurrentBranch,
	IReadOnlyList<LocalBranchInfo> Branches,
	LocalRepositoryStatus Status)
{
	public LocalRepositoryInfo(string defaultBranch, IReadOnlyList<string> branches)
		: this(
			defaultBranch,
			null,
			branches.Select(branch => new LocalBranchInfo(
				branch,
				new LocalRepositoryStatus(0, 0, false, 0, false))).ToArray(),
			new LocalRepositoryStatus(0, 0, false, 0, false))
	{
	}
}

public interface ILocalRepositoryInspector
{
	LocalRepositoryInfo Inspect(string repositoryPath, string knownDefaultBranch);
}

public sealed record BranchSwitchResult(
	string CurrentBranch,
	bool CreatedSafetyCopy,
	bool RestoredSafetyCopy,
	bool HasConflicts);

public sealed record CloneRenameResult(string Path, LocalRepositoryInfo Repository);

public sealed record RepositoryDeletionRisks(
	bool HasUncommitted,
	bool HasUnpushed,
	int StashCount,
	string ConfirmationToken,
	IReadOnlyList<LinkedWorktreeDeletionRisk>? LinkedWorktrees = null);

public sealed record LinkedWorktreeDeletionRisk(
	string Name,
	string Path,
	string? CurrentBranch,
	bool HasUncommitted,
	bool HasUnpushed,
	int StashCount,
	bool HasConflicts,
	bool InspectionFailed = false);

public sealed class RepositoryHasLinkedWorktreesException(
	IReadOnlyList<LinkedWorktreeDeletionRisk> linkedWorktrees) : InvalidOperationException(
	"Remove linked local working copies before deleting this local copy.")
{
	public IReadOnlyList<LinkedWorktreeDeletionRisk> LinkedWorktrees { get; } = [.. linkedWorktrees];
}

public sealed record BranchDeletionResult(
	bool CleanupComplete,
	bool SwitchedCurrentBranch,
	LocalRepositoryInfo Repository);

public sealed class RepositoryStateChangedException : InvalidOperationException
{
	public RepositoryStateChangedException()
		: base("The local repository changed after confirmation.")
	{
	}
}

/// <summary>
/// The local copy was detached from its registered path, a post-move validation failed, and the original
/// path could not be restored because another filesystem entry claimed it. The preserved local files remain
/// recoverable at <see cref="QuarantinePath"/>; callers must stop using the original path.
/// </summary>
public sealed class RepositoryQuarantinedCloneException : InvalidOperationException
{
	public RepositoryQuarantinedCloneException(string quarantinePath, Exception? innerException = null)
		: base("The local copy was preserved at a quarantine path after its registered path changed.", innerException)
	{
		QuarantinePath = quarantinePath;
	}

	public string QuarantinePath { get; }
}

/// <summary>Local repository actions used by the manager-facing repository picker.</summary>
public interface ILocalRepositoryManager : ILocalRepositoryInspector
{
	/// <summary>Validate the expected repository identity and inspect it from the same open handle.</summary>
	LocalRepositoryInfo InspectExpected(
		string repositoryPath,
		string expectedRepositoryUrl,
		string knownDefaultBranch);

	/// <summary>Validate that the local copy belongs to <paramref name="expectedRepositoryUrl"/>, capture that
	/// exact fetch endpoint, fetch updated remote references without reading mutable remote configuration again,
	/// and return its inspected state from the same open repository handle. A supplied GitHub token is released
	/// only to an HTTPS <c>github.com</c> endpoint; other remotes can still fetch anonymously.</summary>
	LocalRepositoryInfo Fetch(
		string repositoryPath,
		string expectedRepositoryUrl,
		string knownDefaultBranch,
		string? accessToken,
		CancellationToken ct);

	/// <summary>Validate and capture the expected repository's fetch endpoint, fetch and fast-forward the expected
	/// current working line without reading mutable remote configuration again, then inspect it on the same open
	/// repository handle. <paramref name="beforeMutation"/> runs only after that handle's identity and current
	/// working line are verified; <paramref name="onMutationStarting"/> runs immediately before the fast-forward,
	/// so the caller treats any later failure as potentially post-mutation. Refuses unfinished files, conflicts,
	/// divergence, or a current-line change instead of overwriting local work.</summary>
	LocalRepositoryInfo PullFastForward(
		string repositoryPath,
		string expectedRepositoryUrl,
		string knownDefaultBranch,
		string expectedBranch,
		string? accessToken,
		CancellationToken ct,
		Action? beforeMutation = null,
		Action? onMutationStarting = null);

	/// <summary>Validate and capture the expected repository's fetch and effective push endpoints, share the
	/// expected current working line without reading mutable remote configuration again, then inspect it on the
	/// same open repository handle. Refuses when GitHub has versions not present locally or when the current line
	/// changed.</summary>
	LocalRepositoryInfo PushBranchSafely(
		string repositoryPath,
		string expectedRepositoryUrl,
		string knownDefaultBranch,
		string expectedBranch,
		string accessToken,
		CancellationToken ct);

	/// <summary>Validate that the local copy belongs to <paramref name="expectedRepositoryUrl"/> and remains on
	/// <paramref name="expectedCurrentBranch"/>, protect unfinished work, switch to <paramref name="branch"/>,
	/// then restore the most recent SpecDesk safety copy that belongs to that branch.
	/// <paramref name="beforeMutation"/> runs only after both validations on the same handle;
	/// <paramref name="onMutationStarting"/> runs immediately before checkout, so the caller treats any later
	/// failure as potentially post-mutation. A restored copy is removed only after a clean apply.</summary>
	BranchSwitchResult SwitchBranchSafely(
		string repositoryPath,
		string expectedRepositoryUrl,
		string expectedCurrentBranch,
		string branch,
		Action? beforeMutation = null,
		Action? onMutationStarting = null);

	/// <summary>Create and check out a new local working line at the current commit without publishing it.</summary>
	LocalRepositoryInfo CreateBranch(
		string repositoryPath,
		string expectedRepositoryUrl,
		string expectedCurrentBranch,
		string branch,
		Action? beforeMutation = null,
		Action? onMutationStarting = null);

	/// <summary>Rename a local working line. The GitHub branch is never changed or deleted.</summary>
	LocalRepositoryInfo RenameBranch(
		string repositoryPath,
		string expectedRepositoryUrl,
		string expectedCurrentBranch,
		string branch,
		string newBranch,
		string defaultBranch,
		Action? beforeMutation = null,
		Action? onMutationStarting = null);

	/// <summary>Move one exact local copy to a sibling folder with a new display/folder name.</summary>
	CloneRenameResult RenameClone(
		string repositoryPath,
		string expectedRepositoryUrl,
		string knownDefaultBranch,
		string localName,
		Action? beforeMutation = null,
		Action? onMutationStarting = null);

	RepositoryDeletionRisks InspectDeletionRisks(
		string repositoryPath,
		string expectedRepositoryUrl,
		string? expectedCurrentBranch,
		string? branch = null,
		Action? beforeInspect = null);

	BranchDeletionResult DeleteBranch(
		string repositoryPath,
		string expectedRepositoryUrl,
		string branch,
		string defaultBranch,
		string confirmationToken,
		Action? onCurrentBranchChangeStarting = null);

	/// <summary>Atomically detach the exactly confirmed local copy from its registered path, verify the moved
	/// tree, and remove only that local tree. <paramref name="onMutationStarting"/> runs after all pre-move
	/// validation and immediately before the filesystem move, so any later failure must be treated as
	/// potentially post-mutation.</summary>
	bool DeleteClone(
		string repositoryPath,
		string expectedRepositoryUrl,
		string confirmationToken,
		Action? onMutationStarting = null);
}
