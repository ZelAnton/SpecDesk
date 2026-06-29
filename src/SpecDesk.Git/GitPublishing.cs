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

    /// <summary>Push a local branch to the remote over HTTPS, authenticating with the GitHub access
    /// token. Throws when the remote or branch is missing, or on a transport / auth failure.</summary>
    void PushBranch(string repoRoot, string branchName, string accessToken, string remoteName = "origin");
}
