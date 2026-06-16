using SpecDesk.Git;

namespace SpecDesk.Git.Tests;

[TestFixture]
public sealed class LibGit2DocumentVersioningTests
{
    private string _repo = string.Empty;
    private string _doc = string.Empty;
    private LibGit2DocumentVersioning _versioning = null!;

    [SetUp]
    public void SetUp()
    {
        _repo = Path.Combine(Path.GetTempPath(), "specdesk-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
        _doc = Path.Combine(_repo, "spec.md");
        File.WriteAllText(_doc, "# Version one");
        _versioning = new LibGit2DocumentVersioning();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_repo))
        {
            // Git marks pack files read-only; clear the attribute so the tree can be deleted.
            foreach (string file in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_repo, recursive: true);
        }
    }

    [Test]
    public void Initialize_CreatesAVersionedRepoOnMain()
    {
        Assert.That(_versioning.IsVersioned(_repo), Is.False);

        _versioning.Initialize(_repo, "Seed");

        Assert.Multiple(() =>
        {
            Assert.That(_versioning.IsVersioned(_repo), Is.True);
            Assert.That(_versioning.CurrentBranch(_repo), Is.EqualTo("main"));
        });
    }

    [Test]
    public void BeginEdit_CreatesAndChecksOutTheWorkingBranchFromTheBase()
    {
        _versioning.Initialize(_repo, "Seed");

        EditSession session = _versioning.BeginEdit(_repo, "spec/billing-20260614", "main");

        Assert.Multiple(() =>
        {
            Assert.That(session.Branch, Is.EqualTo("spec/billing-20260614"));
            Assert.That(session.BaseBranch, Is.EqualTo("main"));
            Assert.That(_versioning.CurrentBranch(_repo), Is.EqualTo("spec/billing-20260614"));
        });
    }

    [Test]
    public void BeginEdit_ForksFromTheBaseEvenWhenAnotherWorkingBranchIsCheckedOut()
    {
        _versioning.Initialize(_repo, "Seed");
        // First draft, committed, left checked out (as if a prior session never discarded it).
        _versioning.BeginEdit(_repo, "spec/old", "main");
        File.WriteAllText(_doc, "# Stray draft");
        _versioning.SaveVersion(_repo, "Stray");

        EditSession session = _versioning.BeginEdit(_repo, "spec/new", "main");

        Assert.Multiple(() =>
        {
            Assert.That(session.BaseBranch, Is.EqualTo("main"));
            // The new branch forked from main, so the stray draft's content is gone from the tree.
            Assert.That(File.ReadAllText(_doc), Is.EqualTo("# Version one"));
        });
    }

    [Test]
    public void BeginEdit_SucceedsWhenTheWorkingTreeIsDirty()
    {
        _versioning.Initialize(_repo, "Seed");
        _versioning.BeginEdit(_repo, "spec/x", "main");
        // A prior session autosaved to disk but never saved a version: the working tree is dirty
        // (uncommitted). Starting a new edit must not throw a checkout conflict.
        File.WriteAllText(_doc, "# Uncommitted stray draft");

        Assert.DoesNotThrow(() => _versioning.BeginEdit(_repo, "spec/y", "main"));
        // Forked from main with a forced checkout, so the uncommitted stray text is reset.
        Assert.That(File.ReadAllText(_doc), Is.EqualTo("# Version one"));
    }

    [Test]
    public void SaveVersion_CommitsAChangedDocument()
    {
        _versioning.Initialize(_repo, "Seed");
        _versioning.BeginEdit(_repo, "spec/x", "main");

        File.WriteAllText(_doc, "# Version two");
        CommitResult result = _versioning.SaveVersion(_repo, "Update spec");

        Assert.Multiple(() =>
        {
            Assert.That(result.Committed, Is.True);
            Assert.That(result.Sha, Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public void SaveVersion_AlsoCommitsNewlyAddedAssets()
    {
        _versioning.Initialize(_repo, "Seed");
        _versioning.BeginEdit(_repo, "spec/x", "main");

        // Simulate an image pasted into the editor: a brand-new untracked file in the repo.
        string imageDir = Path.Combine(_repo, "images");
        Directory.CreateDirectory(imageDir);
        File.WriteAllText(Path.Combine(imageDir, "diagram.svg"), "<svg/>");

        CommitResult result = _versioning.SaveVersion(_repo, "Add diagram");

        Assert.That(result.Committed, Is.True, "the pasted asset should be committed, not orphaned");
    }

    [Test]
    public void SaveVersion_IsANoOpWhenNothingChanged()
    {
        _versioning.Initialize(_repo, "Seed");
        _versioning.BeginEdit(_repo, "spec/x", "main");

        // No edit between the seed commit and this save.
        CommitResult result = _versioning.SaveVersion(_repo, "Update spec");

        Assert.Multiple(() =>
        {
            Assert.That(result.Committed, Is.False);
            Assert.That(result.Sha, Is.Null);
        });
    }

    [Test]
    public void Discard_ReturnsToBaseAndRevertsTheDocument()
    {
        _versioning.Initialize(_repo, "Seed");
        _versioning.BeginEdit(_repo, "spec/x", "main");
        File.WriteAllText(_doc, "# Version two");
        _versioning.SaveVersion(_repo, "Update spec");

        _versioning.Discard(_repo, "spec/x", "main");

        Assert.Multiple(() =>
        {
            Assert.That(_versioning.CurrentBranch(_repo), Is.EqualTo("main"));
            Assert.That(File.ReadAllText(_doc), Is.EqualTo("# Version one"));
        });
    }
}
