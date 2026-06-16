namespace SpecDesk.Git;

/// <summary>The outcome of a "Save a version" commit attempt.</summary>
/// <param name="Committed">Whether a commit was actually created (false when nothing changed).</param>
/// <param name="Sha">The new commit's SHA, or <c>null</c> when nothing was committed.</param>
/// <param name="When">When the attempt completed.</param>
public sealed record CommitResult(bool Committed, string? Sha, DateTimeOffset When);

/// <summary>The branches involved in a started edit.</summary>
/// <param name="Branch">The checked-out working branch.</param>
/// <param name="BaseBranch">The published branch it was forked from (and returns to on discard).</param>
public sealed record EditSession(string Branch, string BaseBranch);

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

    /// <summary>Initialize a new repository at <paramref name="repoRoot"/> (default branch
    /// <c>main</c>) and make an initial commit of everything already present.</summary>
    void Initialize(string repoRoot, string commitMessage);

    /// <summary>The friendly name of the currently checked-out branch, or <c>null</c> if unknown.</summary>
    string? CurrentBranch(string repoRoot);

    /// <summary>Create (if absent) and check out the working branch <paramref name="branchName"/>,
    /// forking it from <paramref name="preferredBase"/> when that branch exists (else from whatever
    /// is currently checked out — a previous session may have left a working branch active).
    /// Returns the working and resolved base branch names. Throws if the repo has no commits yet.</summary>
    EditSession BeginEdit(string repoRoot, string branchName, string preferredBase);

    /// <summary>Save a version: stage every working-tree change (the document and any assets such
    /// as pasted images) and commit them with <paramref name="message"/> (the author's version
    /// note). A no-op (returns <c>Committed = false</c>) when nothing has changed since the last
    /// version.</summary>
    CommitResult SaveVersion(string repoRoot, string message);

    /// <summary>Abandon a draft: switch back to <paramref name="baseBranch"/> and delete the
    /// working branch. Safe to call when already off the working branch.</summary>
    void Discard(string repoRoot, string workingBranch, string baseBranch);
}
