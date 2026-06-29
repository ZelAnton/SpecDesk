namespace SpecDesk.GitHub.Tests;

[TestFixture]
public sealed class GitHubRemoteTests
{
    [TestCase("https://github.com/octo/spec-repo.git", "octo", "spec-repo")]
    [TestCase("https://github.com/octo/spec-repo", "octo", "spec-repo")]
    [TestCase("https://github.com/octo/spec-repo/", "octo", "spec-repo")]
    [TestCase("git@github.com:octo/spec-repo.git", "octo", "spec-repo")]
    [TestCase("git@github.com:octo/spec-repo", "octo", "spec-repo")]
    [TestCase("https://github.com/My-Org/my.repo.git", "My-Org", "my.repo")]
    public void TryParse_extracts_owner_and_repo(string url, string owner, string repo)
    {
        Assert.That(GitHubRemote.TryParse(url), Is.EqualTo(new GitHubRepo(owner, repo)));
    }

    [TestCase("ssh://git@github.com/octo/spec-repo.git", "octo", "spec-repo")]
    [TestCase("HTTPS://GitHub.com/octo/spec-repo", "octo", "spec-repo")]
    public void TryParse_accepts_the_ssh_url_and_is_case_insensitive(string url, string owner, string repo)
    {
        Assert.That(GitHubRemote.TryParse(url), Is.EqualTo(new GitHubRepo(owner, repo)));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("https://gitlab.com/octo/spec-repo.git")]
    [TestCase("https://example.com/octo/repo")]
    [TestCase("not a url")]
    // Look-alike hosts must be rejected — this parse gates where the OAuth token is pushed.
    [TestCase("https://evilgithub.com/octo/spec-repo.git")]
    [TestCase("https://github.com.evil.com/octo/spec-repo")]
    [TestCase("https://evil.com/github.com/octo/spec-repo")]
    [TestCase("https://github.com@evil.com/octo/spec-repo")]
    [TestCase("https://sub.github.com/octo/spec-repo")]
    public void TryParse_returns_null_for_a_non_github_remote(string? url)
    {
        Assert.That(GitHubRemote.TryParse(url), Is.Null);
    }
}
