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

        _versioning.PushBranch(_work, "spec/draft", "x-access-token");

        using Repository remote = new(_remote);
        Assert.That(remote.Branches["spec/draft"], Is.Not.Null);
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
    public void PushBranch_throws_for_a_missing_remote()
    {
        Assert.Throws<InvalidOperationException>(
            () => _versioning.PushBranch(_work, "main", "x-access-token", "upstream"));
    }

    [Test]
    public void PushBranch_throws_for_a_missing_branch()
    {
        Assert.Throws<InvalidOperationException>(
            () => _versioning.PushBranch(_work, "no-such-branch", "x-access-token"));
    }
}
