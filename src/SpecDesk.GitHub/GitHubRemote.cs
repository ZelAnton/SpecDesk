using System.Text.RegularExpressions;

namespace SpecDesk.GitHub;

/// <summary>A GitHub repository's <see cref="Owner"/> and <see cref="Name"/>, parsed from a git remote URL.</summary>
public sealed record GitHubRepo(string Owner, string Name);

/// <summary>Parses a git remote URL into its GitHub owner/repo coordinates.</summary>
public static partial class GitHubRemote
{
    // Matches the HTTPS (github.com/owner/repo[.git]) and SSH (git@github.com:owner/repo[.git]) forms;
    // owner is one path segment, repo the next, with an optional .git suffix and trailing slash. The host
    // is ANCHORED: an optional scheme then an optional `user@` then EXACTLY `github.com` — so a look-alike
    // host (evilgithub.com, github.com.evil.com, a `github.com@evil.com` userinfo spoof, or a subdomain)
    // can't pass. This parse is the gate that decides the push/PR target, so a loose host match would let
    // the OAuth token be pushed to an attacker-controlled remote.
    [GeneratedRegex(
        @"^(?:(?:https?|ssh|git)://)?(?:[^@/]+@)?github\.com[/:](?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex RemotePattern();

    /// <summary>The owner/repo for a github.com remote URL, or <c>null</c> when it isn't one (no remote, a
    /// non-GitHub host, or an unparseable URL).</summary>
    public static GitHubRepo? TryParse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        Match match = RemotePattern().Match(url);
        if (!match.Success)
        {
            return null;
        }

        string owner = match.Groups["owner"].Value;
        string repo = match.Groups["repo"].Value;
        return owner.Length > 0 && repo.Length > 0 ? new GitHubRepo(owner, repo) : null;
    }
}
