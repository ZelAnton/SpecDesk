using LibGit2Sharp;
using SpecDesk.Git;

namespace SpecDesk.Git.Tests;

// Exercises IRepositoryCloner against a LOCAL bare repo standing in for the GitHub remote — local-file
// transport ignores credentials, so cloning works fully offline (the live HTTPS clone is the user's own
// action). Mirrors GitPublishingTests' fixture: a work repo with one committed .md, mirrored into a bare
// repo the cloner then clones from. The host chooses the exact destination folder (owner_name), so these
// tests pass a full destination path, not a parent directory.
[TestFixture]
public sealed class LibGit2RepositoryClonerTests
{
    private string _root = string.Empty;
    private string _work = string.Empty;
    private string _bare = string.Empty;
    private string _dest = string.Empty;
    private LibGit2RepositoryCloner _cloner = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "specdesk-clone-" + Guid.NewGuid().ToString("N"));
        _work = Path.Combine(_root, "work");
        _bare = Path.Combine(_root, "remote.git");
        // The exact folder the clone lands in — the host namespaces this by owner and name.
        _dest = Path.Combine(_root, "repos", "acme_specs");
        Directory.CreateDirectory(_work);
        File.WriteAllText(Path.Combine(_work, "spec.md"), "# Version one");

        LibGit2DocumentVersioning versioning = new();
        versioning.Initialize(_work, "Seed");
        // Mirror the work repo into a bare repo whose HEAD points at the committed branch, so a subsequent
        // clone checks out the file (a freshly Repository.Init'd bare would leave HEAD on a non-existent
        // default branch and clone an empty tree).
        Repository.Clone(_work, _bare, new CloneOptions { IsBare = true });

        _cloner = new LibGit2RepositoryCloner();
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
    public void CloneOrReuse_ClonesIntoTheGivenFolderAndChecksOutTheFile()
    {
        string local = _cloner.CloneOrReuse(_bare, _dest, accessToken: null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(local, Is.EqualTo(_dest));
            Assert.That(Repository.IsValid(local), Is.True);
            Assert.That(File.Exists(Path.Combine(local, "spec.md")), Is.True);
        });
    }

    [Test]
    public void IsCloned_IsTrueForAValidCloneAndFalseForAMissingOrNonRepoFolder()
    {
        Assert.That(_cloner.IsCloned(_dest), Is.False, "not cloned yet");

        // A leftover directory that is NOT a git working tree must read as "not cloned" (so it's re-cloned,
        // not opened as a broken workspace) — the very debris a force-killed clone would leave.
        Directory.CreateDirectory(_dest);
        File.WriteAllText(Path.Combine(_dest, "junk.txt"), "debris");
        Assert.That(_cloner.IsCloned(_dest), Is.False, "a non-repo folder is not a clone");

        _cloner.CloneOrReuse(_bare, _dest, accessToken: null, CancellationToken.None);
        Assert.That(_cloner.IsCloned(_dest), Is.True, "a valid working tree is a clone");
    }

    [Test]
    public void CloneOrReuse_OverPartialDebris_ClearsItAndClonesFresh()
    {
        // Simulate a previous clone that faulted mid-transfer: a non-empty, non-repo directory at the target.
        Directory.CreateDirectory(_dest);
        File.WriteAllText(Path.Combine(_dest, "partial.pack"), "debris");

        string local = _cloner.CloneOrReuse(_bare, _dest, accessToken: null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(Repository.IsValid(local), Is.True);
            Assert.That(File.Exists(Path.Combine(local, "spec.md")), Is.True);
            Assert.That(File.Exists(Path.Combine(local, "partial.pack")), Is.False, "debris cleared");
        });
    }

    [Test]
    public void CloneOrReuse_CalledAgain_ReusesTheExistingCloneWithoutRecloning()
    {
        string first = _cloner.CloneOrReuse(_bare, _dest, accessToken: null, CancellationToken.None);
        // A file the clone did NOT contain: a re-clone would wipe the whole tree (or fail on a non-empty
        // target), so its survival proves the second call reused the existing working tree.
        string sentinel = Path.Combine(first, "local-only.txt");
        File.WriteAllText(sentinel, "kept");

        string second = _cloner.CloneOrReuse(_bare, _dest, accessToken: null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.EqualTo(first));
            Assert.That(File.Exists(sentinel), Is.True, "the existing clone must be reused, not re-cloned");
        });
    }

    [Test]
    public void CloneOrReuse_AnEmptyUrl_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => _cloner.CloneOrReuse(string.Empty, _dest, null, CancellationToken.None));
    }
}
