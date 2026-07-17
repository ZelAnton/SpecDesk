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
    private string _baseBranch = "main";
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
        // The seeded default line (main under a normal init, but the environment's init.defaultBranch may
        // differ) — used as the published base the draft forks from and reconciles against.
        _baseBranch = _versioning.CurrentBranch(_work) ?? "main";

        Repository.Init(_remote, isBare: true);
        using Repository repo = new(_work);
        repo.Network.Remotes.Add("origin", _remote);
        // Pin line endings so the reconciliation tests can assert exact multi-line content regardless of the
        // machine's global core.autocrlf (git's default warns about LF→CRLF rewrites).
        repo.Config.Set("core.autocrlf", false);
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


    // —— Share-conflict detection + reconciliation (PoC-10, "Someone else changed this too") ——————————————

    // Publish the base to the remote, then land a COMPETING change to the same file on the remote's base via a
    // throwaway second clone — the "someone else edited this too" precondition.
    private void SeedCompetingBasePublish(string competingContent)
    {
        _versioning.PushBranch(_work, _baseBranch, _remote, "x-access-token");

        string other = Path.Combine(_root, "other");
        Repository.Clone(_remote, other);
        using (Repository clone = new(other))
        {
            clone.Config.Set("core.autocrlf", false);
            // Check out the base explicitly — the bare remote's HEAD may not name it, so a plain clone can
            // land detached with no local base branch to commit onto.
            Branch local = clone.Branches[_baseBranch]
                ?? clone.CreateBranch(_baseBranch, clone.Branches[$"origin/{_baseBranch}"].Tip);
            Commands.Checkout(clone, local);
            File.WriteAllText(Path.Combine(other, "spec.md"), competingContent);
            Signature who = new("Someone Else", "else@example.invalid", DateTimeOffset.Now);
            Commands.Stage(clone, "*");
            clone.Commit("Competing edit", who, who);
        }

        _versioning.PushBranch(other, _baseBranch, _remote, "x-access-token");
    }

    // Start a draft off main and commit the author's OWN edit to the same file.
    private void SeedAuthorDraft(string myContent)
    {
        _versioning.BeginEdit(_work, "spec/draft", _baseBranch);
        File.WriteAllText(Path.Combine(_work, "spec.md"), myContent);
        _versioning.SaveVersion(_work, "My change");
    }

    [Test]
    public void DetectShareConflict_reports_both_clean_sides_when_the_same_document_was_changed_upstream()
    {
        SeedCompetingBasePublish("# Version one\n\nTheir new sentence.\n");
        SeedAuthorDraft("# Version one\n\nMy new sentence.\n");

        ReviewShareConflict? conflict = _versioning.DetectShareConflict(
            _work, "spec/draft", _baseBranch, "spec.md", _remote, "x-access-token");

        Assert.That(conflict, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(conflict!.RepoRelativePath, Is.EqualTo("spec.md"));
            Assert.That(conflict.Mine, Is.EqualTo("# Version one\n\nMy new sentence.\n"));
            Assert.That(conflict.Theirs, Is.EqualTo("# Version one\n\nTheir new sentence.\n"));
            // Neither side ever carries git conflict markers — that is the whole point.
            Assert.That(conflict.Mine, Does.Not.Contain("<<<<<<<"));
            Assert.That(conflict.Theirs, Does.Not.Contain(">>>>>>>"));
        });
    }

    [Test]
    public void DetectShareConflict_is_clean_when_the_upstream_change_touches_a_different_file()
    {
        _versioning.PushBranch(_work, _baseBranch, _remote, "x-access-token");
        string other = Path.Combine(_root, "other");
        Repository.Clone(_remote, other);
        using (Repository clone = new(other))
        {
            clone.Config.Set("core.autocrlf", false);
            Branch local = clone.Branches[_baseBranch]
                ?? clone.CreateBranch(_baseBranch, clone.Branches[$"origin/{_baseBranch}"].Tip);
            Commands.Checkout(clone, local);
            File.WriteAllText(Path.Combine(other, "unrelated.md"), "# Unrelated\n");
            Signature who = new("Someone Else", "else@example.invalid", DateTimeOffset.Now);
            Commands.Stage(clone, "*");
            clone.Commit("Add an unrelated file", who, who);
        }
        _versioning.PushBranch(other, _baseBranch, _remote, "x-access-token");

        SeedAuthorDraft("# Version one\n\nMy new sentence.\n");

        ReviewShareConflict? conflict = _versioning.DetectShareConflict(
            _work, "spec/draft", _baseBranch, "spec.md", _remote, "x-access-token");

        Assert.That(conflict, Is.Null);
    }

    [Test]
    public void DetectShareConflict_is_clean_when_nothing_changed_upstream()
    {
        SeedAuthorDraft("# Version one\n\nMy new sentence.\n");

        ReviewShareConflict? conflict = _versioning.DetectShareConflict(
            _work, "spec/draft", _baseBranch, "spec.md", _remote, "x-access-token");

        Assert.That(conflict, Is.Null);
    }

    [Test]
    public void ReconcileShareConflict_keep_mine_keeps_the_authors_document_and_makes_the_base_an_ancestor()
    {
        SeedCompetingBasePublish("# Version one\n\nTheir new sentence.\n");
        SeedAuthorDraft("# Version one\n\nMy new sentence.\n");
        // The detect fetches the competing base into the remote-tracking ref the reconcile reuses.
        _versioning.DetectShareConflict(_work, "spec/draft", _baseBranch, "spec.md", _remote, "x-access-token");

        string resolved = _versioning.ReconcileShareConflict(
            _work, "spec/draft", _baseBranch, "spec.md", ConflictResolution.KeepMine);

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.EqualTo("# Version one\n\nMy new sentence.\n"));
            Assert.That(File.ReadAllText(Path.Combine(_work, "spec.md")),
                Is.EqualTo("# Version one\n\nMy new sentence.\n"));
            Assert.That(resolved, Does.Not.Contain("<<<<<<<"));
        });

        // The base is now an ancestor of the draft (the reconciliation merged it in), so a re-check is clean.
        ReviewShareConflict? recheck = _versioning.DetectShareConflict(
            _work, "spec/draft", _baseBranch, "spec.md", _remote, "x-access-token");
        Assert.That(recheck, Is.Null);
    }

    [Test]
    public void ReconcileShareConflict_keep_theirs_takes_the_published_document()
    {
        SeedCompetingBasePublish("# Version one\n\nTheir new sentence.\n");
        SeedAuthorDraft("# Version one\n\nMy new sentence.\n");
        _versioning.DetectShareConflict(_work, "spec/draft", _baseBranch, "spec.md", _remote, "x-access-token");

        string resolved = _versioning.ReconcileShareConflict(
            _work, "spec/draft", _baseBranch, "spec.md", ConflictResolution.KeepTheirs);

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.EqualTo("# Version one\n\nTheir new sentence.\n"));
            Assert.That(File.ReadAllText(Path.Combine(_work, "spec.md")),
                Is.EqualTo("# Version one\n\nTheir new sentence.\n"));
            Assert.That(resolved, Does.Not.Contain(">>>>>>>"));
        });
    }

    [Test]
    public void ReconcileShareConflict_refuses_to_discard_unsaved_edits()
    {
        SeedCompetingBasePublish("# Version one\n\nTheir new sentence.\n");
        SeedAuthorDraft("# Version one\n\nMy new sentence.\n");
        _versioning.DetectShareConflict(_work, "spec/draft", _baseBranch, "spec.md", _remote, "x-access-token");
        // Unsaved autosaved typing beyond the last saved version — a hard reset would lose it.
        File.WriteAllText(Path.Combine(_work, "spec.md"), "# Version one\n\nStill typing…\n");

        Assert.Throws<DirtyWorkingTreeException>(() => _versioning.ReconcileShareConflict(
            _work, "spec/draft", _baseBranch, "spec.md", ConflictResolution.KeepMine));

        // The unsaved edit is left exactly as it was — nothing was discarded.
        Assert.That(File.ReadAllText(Path.Combine(_work, "spec.md")),
            Is.EqualTo("# Version one\n\nStill typing…\n"));
    }
}
