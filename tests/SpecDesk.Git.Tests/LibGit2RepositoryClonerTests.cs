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

		Directory.Delete(_dest, recursive: true);
		_cloner.CloneOrReuse(_bare, _dest, accessToken: null, CancellationToken.None);
        Assert.That(_cloner.IsCloned(_dest), Is.True, "a valid working tree is a clone");
    }

	[Test]
	public void IsCloneOf_ValidatesOriginWithoutChangingTheWorkingTree()
	{
		_cloner.CloneOrReuse(_bare, _dest, accessToken: null, CancellationToken.None);
		string sentinel = Path.Combine(_dest, "local-only.txt");
		File.WriteAllText(sentinel, "kept");

		Assert.Multiple(() =>
		{
			Assert.That(_cloner.IsCloneOf(_dest, _bare), Is.True);
			Assert.That(_cloner.IsCloneOf(_dest, Path.Combine(_root, "different.git")), Is.False);
			Assert.That(File.ReadAllText(sentinel), Is.EqualTo("kept"));
		});
	}

	[Test]
	public void RepositoryUrlsMatch_TreatsEquivalentGitHubFormsAsTheSameRepository()
	{
		Assert.Multiple(() =>
		{
			Assert.That(LibGit2RepositoryCloner.RepositoryUrlsMatch(
				"https://github.com/Acme/Specs.git",
				"git@github.com:acme/specs.git"), Is.True);
			Assert.That(LibGit2RepositoryCloner.RepositoryUrlsMatch(
				"https://github.com/acme/specs/",
				"https://GITHUB.COM/ACME/SPECS"), Is.True);
			Assert.That(LibGit2RepositoryCloner.RepositoryUrlsMatch(
				"https://github.com/acme/specs.git",
				"https://github.com/other/specs.git"), Is.False);
		});
	}

	[TestCase("https://github.com/acme/specs.git", true)]
	[TestCase("https://GITHUB.COM/ACME/SPECS", true)]
	[TestCase("git@github.com:acme/specs.git", true)]
	[TestCase("ssh://git@github.com/acme/specs.git", true)]
	[TestCase("ssh://git@github.com:22/acme/specs.git", true)]
	[TestCase("ssh://github.com/acme/specs.git", false)]
	[TestCase("ssh://attacker@github.com/acme/specs.git", false)]
	[TestCase("ssh://git@github.com:2222/acme/specs.git", false)]
	[TestCase("http://github.com/acme/specs.git", false)]
	[TestCase("file://github.com/acme/specs.git", false)]
	[TestCase("git://github.com/acme/specs.git", false)]
	[TestCase("ftp://github.com/acme/specs.git", false)]
	[TestCase("https://github.com.evil.example/acme/specs.git", false)]
	[TestCase("https://github.com@evil.example/acme/specs.git", false)]
	[TestCase("https://attacker@github.com/acme/specs.git", false)]
	[TestCase("https://github.com:444/acme/specs.git", false)]
	[TestCase("https://github.com/acme/specs.git?redirect=evil", false)]
	public void RepositoryUrlsMatch_AcceptsOnlyCanonicalGitHubTransports(
		string candidate,
		bool expected)
	{
		Assert.That(
			LibGit2RepositoryCloner.RepositoryUrlsMatch(
				"https://github.com/acme/specs.git",
				candidate),
			Is.EqualTo(expected));
	}

	[TestCase("http://github.com/acme/specs.git")]
	[TestCase("file://github.com/acme/specs.git")]
	[TestCase("git://github.com/acme/specs.git")]
	[TestCase("ftp://github.com/acme/specs.git")]
	public void RepositoryUrlsMatch_RejectsMatchingInsecureGitHubAuthorities(string url)
	{
		Assert.That(LibGit2RepositoryCloner.RepositoryUrlsMatch(url, url), Is.False);
	}

	[Test]
	public void IsCloneOfAtBranch_RequiresTheExactNamedOrDetachedHead()
	{
		_cloner.CloneOrReuse(_bare, _dest, accessToken: null, CancellationToken.None);
		string currentBranch;
		using (Repository repository = new(_dest))
		{
			currentBranch = repository.Head.FriendlyName;
		}

		Assert.Multiple(() =>
		{
			Assert.That(_cloner.IsCloneOfAtBranch(_dest, _bare, currentBranch), Is.True);
			Assert.That(_cloner.IsCloneOfAtBranch(_dest, _bare, "different"), Is.False);
			Assert.That(_cloner.IsCloneOfAtBranch(_dest, _bare, expectedCurrentBranch: null), Is.False);
		});

		using (Repository repository = new(_dest))
		{
			Commands.Checkout(repository, repository.Head.Tip);
		}

		Assert.Multiple(() =>
		{
			Assert.That(_cloner.IsCloneOfAtBranch(_dest, _bare, expectedCurrentBranch: null), Is.True);
			Assert.That(_cloner.IsCloneOfAtBranch(_dest, _bare, currentBranch), Is.False);
		});
	}

	[Test]
	public void IsCloneOfAtBranch_FailsClosedForInvalidRootOrOrigin()
	{
		_cloner.CloneOrReuse(_bare, _dest, accessToken: null, CancellationToken.None);
		string currentBranch;
		using (Repository repository = new(_dest))
		{
			currentBranch = repository.Head.FriendlyName;
		}
		string nonRepository = Path.Combine(_root, "not-a-repository");
		Directory.CreateDirectory(nonRepository);

		Assert.Multiple(() =>
		{
			Assert.That(_cloner.IsCloneOfAtBranch(Path.Combine(_dest, "nested"), _bare, currentBranch), Is.False);
			Assert.That(_cloner.IsCloneOfAtBranch(_dest, Path.Combine(_root, "different.git"), currentBranch), Is.False);
			Assert.That(_cloner.IsCloneOfAtBranch(nonRepository, _bare, currentBranch), Is.False);
		});
	}

    [Test]
	public void CloneOrReuse_PreExistingNonRepositoryDestination_FailsClosedAndPreservesIt()
    {
        // Simulate a previous clone that faulted mid-transfer: a non-empty, non-repo directory at the target.
        Directory.CreateDirectory(_dest);
        File.WriteAllText(Path.Combine(_dest, "partial.pack"), "debris");

		Assert.Throws<RepositoryDestinationConflictException>(
			() => _cloner.CloneOrReuse(_bare, _dest, accessToken: null, CancellationToken.None));
		Assert.That(File.ReadAllText(Path.Combine(_dest, "partial.pack")), Is.EqualTo("debris"));
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
	public void CloneOrReuse_ExistingCloneWithDifferentOrigin_FailsClosedAndPreservesIt()
	{
		_cloner.CloneOrReuse(_bare, _dest, accessToken: null, CancellationToken.None);
		string sentinel = Path.Combine(_dest, "local-only.txt");
		File.WriteAllText(sentinel, "kept");
		string differentRemote = Path.Combine(_root, "different-remote.git");

		RepositoryDestinationConflictException error = Assert.Throws<RepositoryDestinationConflictException>(
			() => _cloner.CloneOrReuse(differentRemote, _dest, accessToken: null, CancellationToken.None))!;

		Assert.Multiple(() =>
		{
			Assert.That(error.DestinationPath, Is.EqualTo(_dest));
			Assert.That(File.ReadAllText(sentinel), Is.EqualTo("kept"));
			using Repository existing = new(_dest);
			Assert.That(existing.Network.Remotes["origin"]?.Url, Is.EqualTo(_bare));
		});
	}

    [Test]
    public void CloneOrReuse_AnEmptyUrl_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => _cloner.CloneOrReuse(string.Empty, _dest, null, CancellationToken.None));
    }

    [Test]
    public void Inspect_InfersMasterAndIncludesDefaultCurrentAndRemoteLines()
    {
        _cloner.CloneOrReuse(_bare, _dest, accessToken: null, CancellationToken.None);
        using (Repository repository = new(_dest))
        {
            repository.Branches.Rename(repository.Head, "master");
            repository.CreateBranch("draft");
        }

        LocalRepositoryInfo info = _cloner.Inspect(_dest, knownDefaultBranch: string.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(info.DefaultBranch, Is.EqualTo("master"));
            Assert.That(info.CurrentBranch, Is.EqualTo("master"));
            Assert.That(info.Branches.Select(branch => branch.Name), Does.Contain("draft"));
            Assert.That(info.Branches.Select(branch => branch.Name), Does.Contain("master"));
        });
    }

    [Test]
    public void Inspect_PreservesAKnownCustomDefaultAndIncludesIt()
    {
        _cloner.CloneOrReuse(_bare, _dest, accessToken: null, CancellationToken.None);
        using (Repository repository = new(_dest))
        {
            repository.CreateBranch("trunk");
            repository.CreateBranch("draft");
        }

        LocalRepositoryInfo info = _cloner.Inspect(_dest, knownDefaultBranch: "trunk");

        Assert.Multiple(() =>
        {
            Assert.That(info.DefaultBranch, Is.EqualTo("trunk"));
            Assert.That(info.Branches.Select(branch => branch.Name), Does.Contain("draft"));
            Assert.That(info.Branches.Select(branch => branch.Name), Does.Contain("trunk"));
        });
    }
}
