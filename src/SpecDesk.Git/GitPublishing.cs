namespace SpecDesk.Git;

/// <summary>
/// The git operations a GitHub round-trip needs — reading the remote URL, reading a branch's last
/// version note (for the pull-request title), and pushing a branch — kept SEPARATE from
/// <see cref="IDocumentVersioning"/> so that interface's deliberately local-only ("no remotes, push, or
/// fetch") contract stays honest. The GitHub access token is passed in as a plain parameter (the Git
/// layer must not depend on <c>SpecDesk.GitHub</c>); it is used only as the push credential and is never
/// logged, stored, or placed in a URL.
/// </summary>
public interface IGitPublishing
{
    /// <summary>The URL of the named remote (default <c>origin</c>), or <c>null</c> when it is absent.</summary>
    string? RemoteUrl(string repoRoot, string remoteName = "origin");

    /// <summary>The first line of the branch tip's commit message — the author's last version note, used
    /// to seed the pull-request title. <c>null</c> when the branch is missing or has no commit.</summary>
    string? LastVersionNote(string repoRoot, string branchName);

    /// <summary>Whether the branch has at least one commit its base doesn't — i.e. there is something to
    /// review. <c>false</c> when the branch is level with (or behind) its base, so opening a pull request
    /// would be rejected as "no commits between base and head"; the host uses this to ask the author to
    /// save a version first rather than surfacing that as a network error.</summary>
    bool HasCommitsToReview(string repoRoot, string branchName, string baseBranch);

    /// <summary>Push a local branch to the remote over HTTPS, authenticating with the GitHub access
    /// token. The token is handed only to an HTTPS <c>github.com</c> target — the URL libgit2 presents for
    /// authentication, which reflects the remote's <c>pushurl</c>, not merely the fetch URL. The caller
    /// supplies the repository URL it resolved before starting the operation; the reopened working tree's
    /// fetch and effective push URLs must both still identify it, so neither a replaced GitHub repository
    /// nor a look-alike host can receive the push; any other target (a look-alike host, an SSH
    /// remote, a local-file remote) is refused outright rather than falling back to the current user's
    /// Windows credentials. Cancellation aborts a stalled transfer. Throws when the remote or branch is
    /// missing, when authentication is refused for a non-GitHub target, on a transport / auth failure, or
    /// when the remote rejects the ref update (non-fast-forward, a protected branch, a rejecting
    /// pre-receive hook) — this never returns successfully while leaving the remote unchanged.</summary>
    void PushBranch(
        string repoRoot,
        string branchName,
        string expectedRepositoryUrl,
        string accessToken,
        string remoteName = "origin",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check whether sharing the draft would collide with a competing change to <paramref
    /// name="repoRelativePath"/> on the latest published version (docs/design/04-git-workflow.md, "Rebase on
    /// send/update"). Fetches the base so the check is against the truly-current published version, then does
    /// an IN-MEMORY three-way merge of the draft branch and that base — it never modifies the working tree,
    /// the index, or any branch, so it is safe to run before a push and to leave un-acted-upon. The token is
    /// the fetch credential only (handed solely to an HTTPS github.com host, never logged or stored); the
    /// caller supplies the repository URL it resolved before starting, so a replaced remote can't redirect the
    /// fetch. Returns the conflicting document's both-sides content — with NO git conflict markers — when the
    /// document collides, or <c>null</c> when the share is clean (nothing new upstream, the base is already an
    /// ancestor, the merge is conflict-free, or only a non-document path collides). Throws when the remote or
    /// the draft branch is missing, or on a transport / auth failure.
    /// </summary>
    ReviewShareConflict? DetectShareConflict(
        string repoRoot,
        string branchName,
        string baseBranch,
        string repoRelativePath,
        string expectedRepositoryUrl,
        string accessToken,
        string remoteName = "origin",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconcile a detected conflict by folding the latest fetched base into the draft branch, keeping the
    /// author's (<see cref="ConflictResolution.KeepMine"/>) or the base's (<see
    /// cref="ConflictResolution.KeepTheirs"/>) whole version of <paramref name="repoRelativePath"/>, and
    /// updating the working copy to the reconciled content. NEVER writes git conflict markers into the working
    /// copy or the commit. Purely local: it reconciles against the base already fetched into the remote-
    /// tracking ref (no network, no token). Requires the draft branch to be the current checkout and the
    /// working tree to be clean — it throws <see cref="DirtyWorkingTreeException"/> rather than force past
    /// unsaved edits, so the author never silently loses in-progress typing. Returns the reconciled document
    /// content the editor should now show.
    /// </summary>
    string ReconcileShareConflict(
        string repoRoot,
        string branchName,
        string baseBranch,
        string repoRelativePath,
        ConflictResolution resolution,
        string remoteName = "origin",
        CancellationToken cancellationToken = default);
}
