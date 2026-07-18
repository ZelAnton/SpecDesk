namespace SpecDesk.Git;

/// <summary>The outcome of a "Save a version" commit attempt.</summary>
/// <param name="Committed">Whether a commit was actually created (false when nothing changed).</param>
/// <param name="Sha">The new commit's SHA, or <c>null</c> when nothing was committed.</param>
/// <param name="When">When the attempt completed.</param>
public sealed record CommitResult(bool Committed, string? Sha, DateTimeOffset When);

/// <summary>One saved version of a selected document, newest first.</summary>
public sealed record DocumentVersion(
    string Id, string Note, string Author, DateTimeOffset When, string Summary = "Document updated");

/// <summary>The branches involved in a started edit.</summary>
/// <param name="Branch">The checked-out working branch.</param>
/// <param name="BaseBranch">The published branch it was forked from (and returns to on discard).</param>
public sealed record EditSession(string Branch, string BaseBranch);

/// <summary>The checked-out branch identity, distinguishing a deliberate detached checkout from a branch
/// name that could not be read. Read failures are surfaced by the caller catching the thrown exception.</summary>
public sealed record CurrentBranchInfo(string? Name, bool IsDetached);

/// <summary>Thrown by <see cref="IDocumentVersioning.BeginEdit"/> when the working tree has uncommitted
/// changes that belong to a branch other than the one being started. A forced checkout resets the whole
/// working tree, so proceeding would silently discard another document's unsaved autosaved draft; the
/// caller should ask the author to finish or discard that other draft first.</summary>
/// <param name="dirtyBranch">The branch (or commit SHA, if detached) whose uncommitted changes would
/// otherwise have been lost.</param>
public sealed class DirtyWorkingTreeException(string dirtyBranch)
    : InvalidOperationException($"The working tree has uncommitted changes on '{dirtyBranch}'.")
{
    /// <summary>The branch (or commit SHA, if detached) whose uncommitted changes blocked the edit.</summary>
    public string DirtyBranch { get; } = dirtyBranch;
}

/// <summary>A local-only file or directory would be overwritten by a working-line checkout.</summary>
public sealed class ProtectedLocalFileException(string filePath)
    : InvalidOperationException("A protected local file is in the way of changing working lines.")
{
    public string FilePath { get; } = filePath;
}

/// <summary>
/// The local git operations behind the document lifecycle (docs/design/04-git-workflow.md), kept
/// behind an interface so the host is testable without a real repository and so no LibGit2Sharp
/// types leak into <c>SpecDesk.Host</c>. All paths are working-tree roots / absolute file paths.
/// PoC-4 is entirely local — no remotes, push, or fetch. Committing happens only on the author's
/// explicit "Save a version"; plain autosave (writing the working copy to disk) is the host's job
/// and never reaches this layer.
/// </summary>
public interface IDocumentVersioning
{
    /// <summary>Whether <paramref name="repoRoot"/> is itself the root of a git working tree.</summary>
    bool IsVersioned(string repoRoot);

    /// <summary>Read a file's content as of the current branch's HEAD commit (the last committed
    /// version) — the base for a local "what changed in this draft" diff. <paramref name="repoRelativePath"/>
    /// is the file path relative to <paramref name="repoRoot"/> (forward slashes). Returns <c>null</c>
    /// when the repository has no commits yet, or the file is not tracked at HEAD.</summary>
    string? ReadHeadContent(string repoRoot, string repoRelativePath);

    /// <summary>Read a file's content as of the tip of local branch <paramref name="branch"/> — the
    /// published baseline for a "compare against <c>main</c>" lens (PoC-7 Part C), independent of whatever
    /// draft is currently checked out. <paramref name="repoRelativePath"/> is the file path relative to
    /// <paramref name="repoRoot"/> (forward slashes). Returns <c>null</c> when the branch does not exist
    /// locally, has no commits, or the file is not tracked at that tip.</summary>
    string? ReadBranchContent(string repoRoot, string branch, string repoRelativePath);

    /// <summary>Return the commits that changed the selected document, newest first and bounded.</summary>
    IReadOnlyList<DocumentVersion> GetDocumentVersions(
        string repoRoot,
        string repoRelativePath,
        int maxCount = 50,
        CancellationToken cancellationToken = default);

    /// <summary>Initialize a new repository at <paramref name="repoRoot"/> (default branch
    /// <c>main</c>) and make an initial commit of everything already present.</summary>
    void Initialize(string repoRoot, string commitMessage);

    /// <summary>The friendly name of the currently checked-out branch, or <c>null</c> if unknown.</summary>
    string? CurrentBranch(string repoRoot);

    /// <summary>The current branch with an explicit detached state. Implementations throw when the
    /// repository cannot be read, so callers can distinguish unavailable from detached.</summary>
    CurrentBranchInfo DescribeCurrentBranch(string repoRoot);

    /// <summary>The repository's actual default branch. The remote HEAD wins when available;
    /// <paramref name="preferredBranch"/> is then used only when that local branch exists, followed by
    /// conventional main/master branches and the current branch. Returns <c>null</c> for a branchless repo.</summary>
    string? DefaultBranch(string repoRoot, string? preferredBranch);

    /// <summary>Create (if absent) and check out the working branch <paramref name="branchName"/>,
    /// forking it from <paramref name="preferredBase"/> when that branch exists (else from whatever
    /// is currently checked out — a previous session may have left a working branch active).
    /// Returns the working and resolved base branch names. Throws if the repo has no commits yet, or —
    /// as <see cref="DirtyWorkingTreeException"/> — if the working tree has uncommitted changes that
    /// belong to a different branch than <paramref name="branchName"/> (the forced checkout this performs
    /// would otherwise silently discard that other, unrelated draft's unsaved autosave).
    /// <see cref="ProtectedLocalFileException"/> is thrown before mutation when a local-only path would be
    /// overwritten. Re-entering the already-current branch preserves its working tree.
    /// <paramref name="onMutationStarting"/> runs after validation and immediately before creating or checking
    /// out the working branch (or publishing a same-branch resume).</summary>
    EditSession BeginEdit(
        string repoRoot,
        string branchName,
        string preferredBase,
        Action? onMutationStarting = null);

    /// <summary>Save a version: stage every working-tree change (the document and any assets such
    /// as pasted images) and commit them with <paramref name="message"/> (the author's version
    /// note). A no-op (returns <c>Committed = false</c>) when nothing has changed since the last
    /// version.</summary>
    CommitResult SaveVersion(string repoRoot, string message);

    /// <summary>Start abandoning a draft by switching to <paramref name="baseBranch"/>, while retaining
    /// the working branch until the caller has successfully reloaded the published document.</summary>
    void BeginDiscard(string repoRoot, string workingBranch, string baseBranch);

    /// <summary>Finish abandoning a draft by deleting the retained working branch after the caller has
    /// successfully reloaded the published document.</summary>
    void CompleteDiscard(string repoRoot, string workingBranch, string baseBranch);
}
