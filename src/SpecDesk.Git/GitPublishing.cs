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
    /// authentication, which reflects the remote's <c>pushurl</c>, not merely the fetch URL — so a remote
    /// re-pointed at a look-alike host cannot exfiltrate it. Cancellation aborts a stalled transfer. Throws
    /// when the remote or branch is missing, on a transport / auth failure, or when the remote rejects the
    /// ref update (non-fast-forward, a protected branch, a rejecting pre-receive hook) — this never returns
    /// successfully while leaving the remote unchanged.</summary>
    void PushBranch(
        string repoRoot,
        string branchName,
        string accessToken,
        string remoteName = "origin",
        CancellationToken cancellationToken = default);
}
