using LibGit2Sharp;
using SpecDesk.Git;

namespace SpecDesk.Git.Tests;

[TestFixture]
public sealed class LibGit2DocumentVersioningTests
{
    private static readonly string[] ExpectedDocumentVersionNotes = ["Clarify spec", "Seed"];
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
            Assert.That(_versioning.DescribeCurrentBranch(_repo),
                Is.EqualTo(new CurrentBranchInfo("main", IsDetached: false)));
        });
    }

    // Regression test for R-01 (re-review): the fix translates libgit2's own detached-HEAD placeholder
    // "(no branch)" to null in CurrentBranch (see the comment there), but nothing exercised the real
    // LibGit2Sharp path — only FakeVersioning, which never calls this code at all. A revert of the
    // `repo.Info.IsHeadDetached ? null : repo.Head.FriendlyName` line back to a bare
    // `repo.Head.FriendlyName` would fabricate "(no branch)" again and this test would catch it (it would
    // then assert Is.Null against "(no branch)" and fail).
    [Test]
    public void CurrentBranch_ReturnsNullWhenHeadIsDetached()
    {
        _versioning.Initialize(_repo, "Seed");
        string tipSha;
        using (Repository repo = new(_repo))
        {
            tipSha = repo.Head.Tip.Sha;
            // Checking out a raw SHA (rather than a branch name) detaches HEAD, exactly like an author
            // (or a prior SpecDesk session) landing on a specific commit instead of a branch tip.
            Commands.Checkout(repo, tipSha);
        }

        Assert.Multiple(() =>
        {
            Assert.That(_versioning.CurrentBranch(_repo), Is.Null);
            Assert.That(_versioning.DescribeCurrentBranch(_repo),
                Is.EqualTo(new CurrentBranchInfo(null, IsDetached: true)));
        });
    }

    [Test]
    public void DefaultBranch_FallsBackToMasterWhenConfiguredMainDoesNotExist()
    {
        _versioning.Initialize(_repo, "Seed");
        using (Repository repo = new(_repo))
        {
            repo.Branches.Rename("main", "master");
        }

        Assert.That(_versioning.DefaultBranch(_repo, "main"), Is.EqualTo("master"));
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
    public void BeginEdit_MutationBoundaryRunsImmediatelyBeforeBranchCreationAndCheckout()
    {
        _versioning.Initialize(_repo, "Seed");
        int boundaries = 0;

        EditSession session = _versioning.BeginEdit(
            _repo,
            "spec/mutation-boundary",
            "main",
            onMutationStarting: () =>
            {
                using Repository repository = new(_repo);
                Assert.Multiple(() =>
                {
                    Assert.That(repository.Branches["spec/mutation-boundary"], Is.Null);
                    Assert.That(repository.Head.FriendlyName, Is.EqualTo("main"));
                });
                boundaries++;
            });

        Assert.Multiple(() =>
        {
            Assert.That(boundaries, Is.EqualTo(1));
            Assert.That(session.Branch, Is.EqualTo("spec/mutation-boundary"));
            Assert.That(_versioning.CurrentBranch(_repo), Is.EqualTo("spec/mutation-boundary"));
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
    public void BeginEdit_ResumingTheSameBranchPreservesAllWorkingTreeChanges()
    {
        _versioning.Initialize(_repo, "Seed");
        _versioning.BeginEdit(_repo, "spec/x", "main");
        // A branch name does not identify which document owns local changes. Resuming it must never use
        // a forced checkout that resets tracked content or removes a local-only asset.
        File.WriteAllText(_doc, "# Uncommitted stray draft");
        string asset = Path.Combine(_repo, "diagram.bin");
        byte[] expectedAsset = [0, 1, 2, 255];
        File.WriteAllBytes(asset, expectedAsset);

        Assert.DoesNotThrow(() => _versioning.BeginEdit(_repo, "spec/x", "main"));
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(_doc), Is.EqualTo("# Uncommitted stray draft"));
            Assert.That(File.ReadAllBytes(asset), Is.EqualTo(expectedAsset));
            Assert.That(_versioning.CurrentBranch(_repo), Is.EqualTo("spec/x"));
        });
    }

    [Test]
    public void BeginEdit_RefusesToStartADifferentDraftWhileAnotherBranchsWorkingTreeIsDirty()
    {
        _versioning.Initialize(_repo, "Seed");
        _versioning.BeginEdit(_repo, "spec/a", "main");
        // Document A was autosaved to disk (uncommitted) and left checked out; the author now switches
        // to editing a different document B. A forced checkout to spec/b would reset the whole working
        // tree, silently destroying A's unsaved autosave — BeginEdit must refuse instead.
        File.WriteAllText(_doc, "# Document A, autosaved but not saved as a version");

        DirtyWorkingTreeException ex = Assert.Throws<DirtyWorkingTreeException>(
            () => _versioning.BeginEdit(_repo, "spec/b", "main"))!;

        Assert.Multiple(() =>
        {
            Assert.That(ex.DirtyBranch, Is.EqualTo("spec/a"));
            // Refused before touching the working tree: A's autosaved content survives untouched.
            Assert.That(File.ReadAllText(_doc), Is.EqualTo("# Document A, autosaved but not saved as a version"));
            Assert.That(_versioning.CurrentBranch(_repo), Is.EqualTo("spec/a"));
        });
    }

    [Test]
    public void BeginEdit_SucceedsWhenAnUntrackedFileIsLyingAroundButTheTreeIsOtherwiseClean()
    {
        _versioning.Initialize(_repo, "Seed");
        _versioning.BeginEdit(_repo, "spec/a", "main");
        // An untracked file (e.g. a stray build artifact, or an image pasted but not yet saved as a
        // version) must not be mistaken for another draft's unsaved document text.
        File.WriteAllText(Path.Combine(_repo, "untracked.tmp"), "not part of any draft's tracked content");

        Assert.DoesNotThrow(() => _versioning.BeginEdit(_repo, "spec/b", "main"));
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(Path.Combine(_repo, "untracked.tmp")),
                Is.EqualTo("not part of any draft's tracked content"));
            Assert.That(_versioning.CurrentBranch(_repo), Is.EqualTo("spec/b"));
        });
    }

    [Test]
    public void BeginEdit_RefusesBeforeAnUntrackedFileWouldBeOverwritten()
    {
        _versioning.Initialize(_repo, "Seed");
        CommitFileOnBranch("spec/target", "collision.bin", [9, 8, 7]);
        byte[] localBytes = [1, 3, 5, 7];
        string collision = Path.Combine(_repo, "collision.bin");
        File.WriteAllBytes(collision, localBytes);
        RepositorySnapshot before = SnapshotRepository();
        bool mutationStarted = false;

        ProtectedLocalFileException ex = Assert.Throws<ProtectedLocalFileException>(() =>
            _versioning.BeginEdit(_repo, "spec/target", "main", () => mutationStarted = true))!;

        AssertCheckoutRefusalPreserved(before, collision, localBytes, mutationStarted);
        Assert.That(ex.FilePath.Replace('\\', '/'), Is.EqualTo("collision.bin"));
    }

    [Test]
    public void BeginEdit_RefusesBeforeAnUntrackedDirectoryWouldBeReplacedByATargetFile()
    {
        _versioning.Initialize(_repo, "Seed");
        CommitFileOnBranch("spec/target", "collision-dir", [9, 8, 7]);
        string collisionDir = Path.Combine(_repo, "collision-dir");
        Directory.CreateDirectory(collisionDir);
        byte[] localBytes = [2, 4, 6, 8];
        string localFile = Path.Combine(collisionDir, "local.bin");
        File.WriteAllBytes(localFile, localBytes);
        RepositorySnapshot before = SnapshotRepository();
        bool mutationStarted = false;

        Assert.Throws<ProtectedLocalFileException>(() =>
            _versioning.BeginEdit(_repo, "spec/target", "main", () => mutationStarted = true));

        AssertCheckoutRefusalPreserved(before, localFile, localBytes, mutationStarted);
        Assert.That(Directory.Exists(collisionDir), Is.True);
    }

    [Test]
    public void BeginEdit_RefusesBeforeAnIgnoredFileWouldBeOverwritten()
    {
        _versioning.Initialize(_repo, "Seed");
        CommitFileOnBranch("spec/target", "private/cache.bin", [9, 8, 7]);
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), "private/\n");
        CommitCurrentTree("Protect local cache");
        string privateDir = Path.Combine(_repo, "private");
        Directory.CreateDirectory(privateDir);
        byte[] localBytes = [10, 20, 30, 40];
        string collision = Path.Combine(privateDir, "cache.bin");
        File.WriteAllBytes(collision, localBytes);
        RepositorySnapshot before = SnapshotRepository();
        bool mutationStarted = false;

        Assert.Throws<ProtectedLocalFileException>(() =>
            _versioning.BeginEdit(_repo, "spec/target", "main", () => mutationStarted = true));

        AssertCheckoutRefusalPreserved(before, collision, localBytes, mutationStarted);
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

    // M-12: Commands.Stage(repo, "*") stages everything matching the pathspec, but it does NOT force-add
    // ignored paths unless explicitly told to (StageOptions.IncludeIgnored, left at its default false
    // here) — the same default behaviour as plain `git add -A`. A repository with a build-artifact
    // directory listed in .gitignore must not have it swept into a saved version.
    [Test]
    public void SaveVersion_DoesNotCommitAGitignoredBuildArtifactDirectory()
    {
        File.WriteAllText(Path.Combine(_repo, ".gitignore"), "build/\n");
        _versioning.Initialize(_repo, "Seed");
        _versioning.BeginEdit(_repo, "spec/x", "main");

        string buildDir = Path.Combine(_repo, "build");
        Directory.CreateDirectory(buildDir);
        File.WriteAllText(Path.Combine(buildDir, "artifact.bin"), "not part of any draft's tracked content");
        // A genuine, non-ignored change too, so the save is not a no-op.
        File.WriteAllText(_doc, "# Version two");

        CommitResult result = _versioning.SaveVersion(_repo, "Update spec");

        Assert.Multiple(() =>
        {
            Assert.That(result.Committed, Is.True);
            Assert.That(_versioning.ReadHeadContent(_repo, "build/artifact.bin"), Is.Null,
                "the ignored build directory must not have been committed");
        });
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
    public void ReadHeadContent_ReturnsTheCommittedVersion()
    {
        _versioning.Initialize(_repo, "Seed");

        Assert.That(_versioning.ReadHeadContent(_repo, "spec.md"), Is.EqualTo("# Version one"));
    }

    [Test]
    public void GetDocumentVersions_ReturnsOnlySelectedDocumentHistoryNewestFirst()
    {
        _versioning.Initialize(_repo, "Seed");
        _versioning.BeginEdit(_repo, "spec/x", "main");
        File.WriteAllText(_doc, "# Version two");
        _versioning.SaveVersion(_repo, "Clarify spec");
        File.WriteAllText(Path.Combine(_repo, "other.md"), "Other");
        _versioning.SaveVersion(_repo, "Add unrelated file");

        IReadOnlyList<DocumentVersion> versions = _versioning.GetDocumentVersions(_repo, "spec.md");

        Assert.Multiple(() =>
        {
            Assert.That(versions.Select(item => item.Note), Is.EqualTo(ExpectedDocumentVersionNotes));
            Assert.That(versions[0].Summary, Is.EqualTo("Document updated"));
            Assert.That(versions[1].Summary, Is.EqualTo("Document added"));
        });
    }

    [Test]
    public void ReadHeadContent_ReturnsNullForAFileNotTrackedAtHead()
    {
        _versioning.Initialize(_repo, "Seed");

        Assert.That(_versioning.ReadHeadContent(_repo, "never-committed.md"), Is.Null);
    }

    [Test]
    public void ReadHeadContent_ReturnsThePriorVersionWhenTheWorkingCopyHasUncommittedEdits()
    {
        _versioning.Initialize(_repo, "Seed"); // HEAD has "# Version one"
        File.WriteAllText(_doc, "# Working-copy edit, not committed");

        // The diff base is the last committed version, not the dirty working copy.
        Assert.That(_versioning.ReadHeadContent(_repo, "spec.md"), Is.EqualTo("# Version one"));
    }

    [Test]
    public void Discard_ReturnsToBaseAndRevertsTheDocument()
    {
        _versioning.Initialize(_repo, "Seed");
        _versioning.BeginEdit(_repo, "spec/x", "main");
        File.WriteAllText(_doc, "# Version two");
        _versioning.SaveVersion(_repo, "Update spec");

        _versioning.BeginDiscard(_repo, "spec/x", "main");
        using (Repository repository = new(_repo))
        {
            Assert.That(repository.Branches["spec/x"], Is.Not.Null, "The draft must remain recoverable until reload succeeds.");
        }
        _versioning.CompleteDiscard(_repo, "spec/x", "main");

        using Repository completedRepository = new(_repo);
        Assert.Multiple(() =>
        {
            Assert.That(_versioning.CurrentBranch(_repo), Is.EqualTo("main"));
            Assert.That(File.ReadAllText(_doc), Is.EqualTo("# Version one"));
            Assert.That(completedRepository.Branches["spec/x"], Is.Null);
        });
    }

    [Test]
    public void BeginDiscard_RefusesBeforeAnUntrackedFileWouldBeOverwritten()
    {
        string publishedAsset = Path.Combine(_repo, "published.bin");
        File.WriteAllBytes(publishedAsset, [9, 8, 7]);
        _versioning.Initialize(_repo, "Seed");
        _versioning.BeginEdit(_repo, "spec/x", "main");
        using (Repository repository = new(_repo))
        {
            Commands.Remove(repository, "published.bin");
            Commit(repository, "Remove published asset in draft");
        }
        byte[] localBytes = [1, 2, 3, 4];
        File.WriteAllBytes(publishedAsset, localBytes);
        RepositorySnapshot before = SnapshotRepository();

        Assert.Throws<ProtectedLocalFileException>(() =>
            _versioning.BeginDiscard(_repo, "spec/x", "main"));

        AssertCheckoutRefusalPreserved(before, publishedAsset, localBytes, mutationStarted: false);
    }

    private sealed record RepositorySnapshot(
        string Branch,
        string HeadSha,
        Dictionary<string, string> Refs);

    private void CommitFileOnBranch(string branchName, string relativePath, byte[] content)
    {
        using Repository repository = new(_repo);
        Branch branch = repository.CreateBranch(branchName, repository.Head.Tip);
        Commands.Checkout(repository, branch);
        string path = Path.Combine(_repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllBytes(path, content);
        Commands.Stage(repository, relativePath);
        Commit(repository, $"Add {relativePath}");
        Commands.Checkout(repository, repository.Branches["main"]);
    }

    private void CommitCurrentTree(string message)
    {
        using Repository repository = new(_repo);
        Commands.Stage(repository, "*");
        Commit(repository, message);
    }

    private static void Commit(Repository repository, string message)
    {
        Signature signature = new("SpecDesk Tests", "tests@specdesk.local", DateTimeOffset.Now);
        repository.Commit(message, signature, signature);
    }

    private RepositorySnapshot SnapshotRepository()
    {
        using Repository repository = new(_repo);
        return new RepositorySnapshot(
            repository.Head.FriendlyName,
            repository.Head.Tip.Sha,
            repository.Refs.ToDictionary(
                reference => reference.CanonicalName,
                reference => reference.TargetIdentifier,
                StringComparer.Ordinal));
    }

    private void AssertCheckoutRefusalPreserved(
        RepositorySnapshot before,
        string localPath,
        byte[] expectedBytes,
        bool mutationStarted)
    {
        using Repository repository = new(_repo);
        Dictionary<string, string> refsAfter = repository.Refs.ToDictionary(
            reference => reference.CanonicalName,
            reference => reference.TargetIdentifier,
            StringComparer.Ordinal);
        Assert.Multiple(() =>
        {
            Assert.That(mutationStarted, Is.False);
            Assert.That(File.ReadAllBytes(localPath), Is.EqualTo(expectedBytes));
            Assert.That(repository.Head.FriendlyName, Is.EqualTo(before.Branch));
            Assert.That(repository.Head.Tip.Sha, Is.EqualTo(before.HeadSha));
            Assert.That(refsAfter, Is.EqualTo(before.Refs));
        });
    }
}
