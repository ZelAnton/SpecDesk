using LibGit2Sharp;
using SpecDesk.Git;

namespace SpecDesk.Git.Tests;

// Exercises IGitPublishing against a LOCAL bare repo standing in for the GitHub remote — local-file
// transport ignores credentials, so push works without a real GitHub session (the live HTTPS push is the
// user's manual step). The real auth-against-GitHub round-trip stays out of CI.
[TestFixture]
public sealed class GitPublishingTests
{
    private string _root = string.Empty;
    private string _work = string.Empty;
    private string _remote = string.Empty;
    private LibGit2DocumentVersioning _versioning = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "specdesk-pub-" + Guid.NewGuid().ToString("N"));
        _work = Path.Combine(_root, "work");
        _remote = Path.Combine(_root, "remote.git");
        Directory.CreateDirectory(_work);
        File.WriteAllText(Path.Combine(_work, "spec.md"), "# Version one");

        _versioning = new LibGit2DocumentVersioning();
        _versioning.Initialize(_work, "Seed");

        Repository.Init(_remote, isBare: true);
        using Repository repo = new(_work);
        repo.Network.Remotes.Add("origin", _remote);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
        {
            // Git marks pack files read-only; clear the attribute so the tree can be deleted.
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public void RemoteUrl_returns_the_origin_url()
    {
        Assert.That(_versioning.RemoteUrl(_work), Is.EqualTo(_remote));
    }

    [Test]
    public void RemoteUrl_is_null_for_a_missing_remote()
    {
        Assert.That(_versioning.RemoteUrl(_work, "upstream"), Is.Null);
    }

    [Test]
    public void PushBranch_publishes_the_draft_branch_to_the_remote()
    {
        _versioning.BeginEdit(_work, "spec/draft", "main");
        File.WriteAllText(Path.Combine(_work, "spec.md"), "# Version two");
        _versioning.SaveVersion(_work, "Draft change");

        _versioning.PushBranch(_work, "spec/draft", _remote, "x-access-token");

        using Repository remote = new(_remote);
        Assert.That(remote.Branches["spec/draft"], Is.Not.Null);
    }

    [Test]
    public void PushBranch_cancelled_before_authentication_does_not_publish()
    {
        _versioning.BeginEdit(_work, "spec/draft", "main");
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            _versioning.PushBranch(
                _work, "spec/draft", _remote, "x-access-token", cancellationToken: cts.Token));

        using Repository remote = new(_remote);
        Assert.That(remote.Branches["spec/draft"], Is.Null);
    }

    [Test]
    public void PushBranch_different_github_pushurl_fails_before_credentials_or_remote_mutation()
    {
        using (Repository repo = new(_work))
        {
            repo.Config.Set("remote.origin.url", "https://github.com/acme/specs.git");
            repo.Config.Set("remote.origin.pushurl", "https://github.com/other/specs.git");
        }

        Assert.Throws<RepositoryIdentityMismatchException>(
            () => _versioning.PushBranch(
                _work, "main", "https://github.com/acme/specs.git", "x-access-token"));

        using Repository remote = new(_remote);
        Assert.That(remote.Branches, Is.Empty);
    }

	[Test]
	public void PushBranch_remote_replaced_after_readiness_fails_before_credentials_or_remote_mutation()
	{
		const string expectedUrl = "https://github.com/acme/specs.git";
		using (Repository repo = new(_work))
		{
			repo.Config.Set("remote.origin.url", "https://github.com/other/private-repo.git");
			repo.Config.Set("remote.origin.pushurl", "https://github.com/other/private-repo.git");
		}

		Assert.Throws<RepositoryIdentityMismatchException>(
			() => _versioning.PushBranch(_work, "main", expectedUrl, "x-access-token"));

		using Repository remote = new(_remote);
		Assert.That(remote.Branches, Is.Empty);
	}

	[Test]
	public void PushBranch_nested_path_fails_without_pushing_the_parent_repository()
	{
		string nested = Path.Combine(_work, "nested");
		Directory.CreateDirectory(nested);

		Assert.Throws<RepositoryNotFoundException>(
			() => _versioning.PushBranch(nested, "main", _remote, "x-access-token"));

		using Repository remote = new(_remote);
		Assert.That(remote.Branches, Is.Empty);
	}

    [Test]
    public void LastVersionNote_returns_the_branch_tip_subject()
    {
        _versioning.BeginEdit(_work, "spec/draft", "main");
        File.WriteAllText(Path.Combine(_work, "spec.md"), "# Version two");
        _versioning.SaveVersion(_work, "Clarify the refund window\n\nMore detail in the body.");

        Assert.That(_versioning.LastVersionNote(_work, "spec/draft"), Is.EqualTo("Clarify the refund window"));
    }

    [Test]
    public void LastVersionNote_is_null_for_a_missing_branch()
    {
        Assert.That(_versioning.LastVersionNote(_work, "no-such-branch"), Is.Null);
    }

    [Test]
    public void HasCommitsToReview_is_false_for_a_fresh_branch_level_with_its_base()
    {
        // A draft just forked from main, with no saved version yet, has nothing to review.
        _versioning.BeginEdit(_work, "spec/draft", "main");

        Assert.That(_versioning.HasCommitsToReview(_work, "spec/draft", "main"), Is.False);
    }

    [Test]
    public void HasCommitsToReview_is_true_once_the_draft_has_a_saved_version()
    {
        _versioning.BeginEdit(_work, "spec/draft", "main");
        File.WriteAllText(Path.Combine(_work, "spec.md"), "# Version two");
        _versioning.SaveVersion(_work, "Draft change");

        Assert.That(_versioning.HasCommitsToReview(_work, "spec/draft", "main"), Is.True);
    }

    [Test]
    public void HasCommitsToReview_is_false_for_a_missing_branch()
    {
        Assert.That(_versioning.HasCommitsToReview(_work, "no-such-branch", "main"), Is.False);
    }

    [Test]
    public void PushBranch_throws_for_a_missing_remote()
    {
        Assert.Throws<InvalidOperationException>(
            () => _versioning.PushBranch(_work, "main", _remote, "x-access-token", "upstream"));
    }

    [Test]
    public void PushBranch_throws_for_a_missing_branch()
    {
        Assert.Throws<InvalidOperationException>(
            () => _versioning.PushBranch(_work, "no-such-branch", _remote, "x-access-token"));
    }

    [Test]
    public void PushBranch_throws_instead_of_silently_succeeding_when_local_history_has_diverged()
    {
        // Publish spec/draft once so the remote has a tip commit to diverge from.
        _versioning.BeginEdit(_work, "spec/draft", "main");
        File.WriteAllText(Path.Combine(_work, "spec.md"), "# Version two");
        _versioning.SaveVersion(_work, "Draft change");
        _versioning.PushBranch(_work, "spec/draft", _remote, "x-access-token");

        // Rewrite local history on top of the same base: reset spec/draft back to its parent and commit a
        // different change, so its new tip is not a descendant of what the remote already has on
        // spec/draft. Pushing it is then a non-fast-forward update, which libgit2 refuses client-side
        // (LibGit2SharpException, not InvalidOperationException) — this path was never silently
        // successful. The scenario this task is about — the remote's *server-side* rejection (a protected
        // branch, a refusing pre-receive hook) reported only via `OnPushStatusError` while `Network.Push`
        // itself returns normally — cannot be reproduced against a local bare repo: libgit2's local
        // transport neither runs hooks nor accepts a non-bare target to model branch-protection-style
        // policy. `ThrowIfRejected` below is the unit-testable seam for that path.
        using (Repository repo = new(_work))
        {
            Commit divergedParent = repo.Branches["spec/draft"].Tip.Parents.Single();
            repo.Refs.UpdateTarget(repo.Refs["refs/heads/spec/draft"], divergedParent.Id);
            Commands.Checkout(repo, "spec/draft", new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
        }

        File.WriteAllText(Path.Combine(_work, "spec.md"), "# Version two, diverged");
        _versioning.SaveVersion(_work, "Diverged draft change");

        // Assert.Catch (not Assert.Throws) because the concrete type is NonFastForwardException, a
        // subclass of LibGit2SharpException.
        Assert.Catch<LibGit2SharpException>(
            () => _versioning.PushBranch(_work, "spec/draft", _remote, "x-access-token"));
    }

    [Test]
    public void ThrowIfRejected_throws_with_the_reference_and_remote_message_when_the_remote_rejected_the_push()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => LibGit2DocumentVersioning.ThrowIfRejected(
                "refs/heads/spec/draft", "protected branch hook declined"))!;

        Assert.That(ex.Message, Does.Contain("refs/heads/spec/draft"));
        Assert.That(ex.Message, Does.Contain("protected branch hook declined"));
    }

    [Test]
    public void ThrowIfRejected_does_nothing_when_no_reference_was_rejected()
    {
        Assert.DoesNotThrow(() => LibGit2DocumentVersioning.ThrowIfRejected(null, null));
    }

    [Test]
    public void ResolveCredentials_returns_the_access_token_for_an_https_github_com_url()
    {
        Credentials credentials = LibGit2DocumentVersioning.ResolveCredentials(
            "https://github.com/owner/repo.git/info/refs?service=git-receive-pack", "the-token");

        UsernamePasswordCredentials userPass = AsUsernamePassword(credentials);
        Assert.That(userPass.Username, Is.EqualTo("x-access-token"));
        Assert.That(userPass.Password, Is.EqualTo("the-token"));
    }

    [Test]
    public void ResolveCredentials_refuses_a_look_alike_host()
    {
        // A pushurl re-pointed at a host that merely contains "github.com" must not receive the token, nor
        // fall back to the current user's Windows credentials.
        Assert.Throws<InvalidOperationException>(
            () => LibGit2DocumentVersioning.ResolveCredentials("https://github.com.evil.example/x", "the-token"));
    }

    [Test]
    public void ResolveCredentials_refuses_a_plain_http_github_com_url()
    {
        Assert.Throws<InvalidOperationException>(
            () => LibGit2DocumentVersioning.ResolveCredentials("http://github.com/owner/repo.git", "the-token"));
    }

    [Test]
    public void ResolveCredentials_refuses_a_non_github_host_instead_of_falling_back_to_windows_credentials()
    {
        // Before the fix this returned `new DefaultCredentials()` (GIT_CREDENTIAL_DEFAULT), silently
        // handing the current Windows user's Negotiate/NTLM session to whatever host the remote's pushurl
        // names. It must now refuse outright instead.
        Assert.Throws<InvalidOperationException>(
            () => LibGit2DocumentVersioning.ResolveCredentials("https://attacker.example/repo.git", "the-token"));
    }

    [Test]
    public void ResolveCredentials_refuses_a_non_http_url()
    {
        Assert.Throws<InvalidOperationException>(
            () => LibGit2DocumentVersioning.ResolveCredentials("ssh://git@github.com/owner/repo.git", "the-token"));
    }

    [Test]
    public void ResolveCredentials_refuses_a_null_url()
    {
        Assert.Throws<InvalidOperationException>(
            () => LibGit2DocumentVersioning.ResolveCredentials(null, "the-token"));
    }

    private static UsernamePasswordCredentials AsUsernamePassword(Credentials credentials)
    {
        Assert.That(credentials, Is.InstanceOf<UsernamePasswordCredentials>());
        return (UsernamePasswordCredentials)credentials;
    }
}
