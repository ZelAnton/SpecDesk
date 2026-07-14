using SpecDesk.Contracts;

namespace SpecDesk.Host.Tests;

/// <summary>
/// The persisted workspace store (A4): recents (dedup + most-recent-first order + a cap of 20), favorites
/// (add / remove / dedup), registered repos (dedup on register, remove on unregister), a real round-trip
/// through a temp file, and the corruption-tolerant load (missing / corrupt → empty).
/// </summary>
[TestFixture]
public sealed class WorkspaceStoreTests
{
	private string _dir = string.Empty;
	private string _path = string.Empty;

	[SetUp]
	public void SetUp()
	{
		_dir = Path.Combine(Path.GetTempPath(), "specdesk-ws-" + Guid.NewGuid().ToString("N"));
		_path = Path.Combine(_dir, "workspace.json");
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(_dir))
		{
			Directory.Delete(_dir, recursive: true);
		}
	}

	private static WorkspaceItem File(string path) => new(path, Path.GetFileName(path), IsFolder: false);

	[Test]
	public void AddRecent_MovesADuplicateToTheFront_KeepingMostRecentFirst()
	{
		WorkspaceStore store = new(_path);
		store.AddRecent(File("/a.md"));
		store.AddRecent(File("/b.md"));
		// Re-opening a.md must move it to the front, not add a second entry.
		store.AddRecent(File("/a.md"));

		string[] expected = ["/a.md", "/b.md"];
		Assert.That(store.State().Recent.Select(r => r.Path), Is.EqualTo(expected));
	}

	[Test]
	public void AddRecent_DedupesCaseInsensitively_ForWindowsPaths()
	{
		WorkspaceStore store = new(_path);
		store.AddRecent(File(@"C:\Docs\A.md"));
		// The same file reached via a differently-cased path (dialog vs tree click) must not duplicate.
		store.AddRecent(File(@"c:\docs\a.md"));

		IReadOnlyList<WorkspaceItem> recent = store.State().Recent;
		Assert.That(recent, Has.Count.EqualTo(1));
		Assert.That(recent[0].Path, Is.EqualTo(@"c:\docs\a.md")); // the newest casing wins
	}

	[Test]
	public void AddRecent_CapsTheListAtTwenty_DroppingTheOldest()
	{
		WorkspaceStore store = new(_path);
		for (int i = 0; i < 25; i++)
		{
			store.AddRecent(File($"/f{i}.md"));
		}

		IReadOnlyList<WorkspaceItem> recent = store.State().Recent;
		Assert.Multiple(() =>
		{
			Assert.That(recent, Has.Count.EqualTo(20));
			// Most-recent-first: the last opened (f24) is at the front, and the 5 oldest (f0..f4) fell off.
			Assert.That(recent[0].Path, Is.EqualTo("/f24.md"));
			Assert.That(recent[^1].Path, Is.EqualTo("/f5.md"));
			Assert.That(recent.Select(r => r.Path), Has.None.EqualTo("/f0.md"));
		});
	}

	[Test]
	public void SetFavorite_AddsThenRemoves_AndDedupesOnAdd()
	{
		WorkspaceStore store = new(_path);
		WorkspaceItem folder = new(Path.Combine(_dir, "specs"), "specs", IsFolder: true);

		store.SetFavorite(folder, favorite: true);
		// Favoriting the same path again is a no-op (dedup by path), not a second entry.
		store.SetFavorite(folder, favorite: true);
		Assert.That(store.State().Favorites, Has.Count.EqualTo(1));
		Assert.That(store.State().Favorites[0].IsFolder, Is.True);

		store.SetFavorite(folder, favorite: false);
		Assert.That(store.State().Favorites, Is.Empty);
	}

	[Test]
	public void TypedRemoteAndRepositoryFavorites_PersistCaseSensitiveIdentityAndFollowUnregister()
	{
		WorkspaceStore store = new(_path);
		RegisteredRepo repo = new("octo/spec", "octo/spec", "https://github.com/octo/spec", "main", []);
		store.RegisterRepo(repo);
		store.SetFavorite(new WorkspaceItem("octo/spec", "octo/spec", true, "repository", repo.Id), true);
		store.SetFavorite(new WorkspaceItem("Docs/Guide.md", "Guide.md", false, "remote", repo.Id, "main"), true);
		store.SetFavorite(new WorkspaceItem("docs/guide.md", "guide.md", false, "remote", repo.Id, "main"), true);
		store.SetFavorite(new WorkspaceItem(@"docs/name\with-backslash.md", @"name\with-backslash.md", false, "remote", repo.Id, "main"), true);

		WorkspaceStatePayload reloaded = new WorkspaceStore(_path).State();
		Assert.That(reloaded.Favorites, Has.Count.EqualTo(4));
		Assert.That(reloaded.Favorites.Select(item => item.Path),
			Does.Contain("Docs/Guide.md").And.Contain("docs/guide.md").And.Contain(@"docs/name\with-backslash.md"));

		store.UnregisterRepo(repo.Id);
		Assert.That(new WorkspaceStore(_path).State().Favorites, Is.Empty);
	}

	[Test]
	public void CloneAndBranchFavorites_PersistWithDistinctRepositoryIdentity()
	{
		WorkspaceStore store = new(_path);
		string clonePath = Path.Combine(_dir, "spec-copy");
		RegisteredRepo repo = new(
			"octo/spec",
			"octo/spec",
			"https://github.com/octo/spec",
			"main",
			[
				new RegisteredClone(
					"spec-copy", clonePath, "draft",
					[new RegisteredBranch("main", RepositoryStatusPayload.Empty),
						new RegisteredBranch("draft", RepositoryStatusPayload.Empty)],
					RepositoryStatusPayload.Empty),
			]);
		store.RegisterRepo(repo);
		store.SetFavorite(new WorkspaceItem(clonePath, "spec-copy", true, "clone", repo.Id), true);
		store.SetFavorite(new WorkspaceItem(clonePath, "spec-copy · main", true, "branch", repo.Id, "main"), true);
		store.SetFavorite(new WorkspaceItem(clonePath, "spec-copy · draft", true, "branch", repo.Id, "draft"), true);

		WorkspaceItem[] favorites = new WorkspaceStore(_path).State().Favorites.ToArray();
		Assert.Multiple(() =>
		{
			Assert.That(favorites.Select(item => item.Kind), Is.EqualTo(["clone", "branch", "branch"]));
			Assert.That(favorites.Select(item => item.RepositoryId), Is.All.EqualTo(repo.Id));
			Assert.That(favorites.Where(item => item.Kind == "branch").Select(item => item.Branch),
				Is.EqualTo(["main", "draft"]));
		});
	}

	[Test]
	public void RenameCloneAndBranch_AtomicallyRemapRegistrationFavoritesAndRecents()
	{
		WorkspaceStore store = new(_path);
		string oldPath = Path.Combine(_dir, "spec-copy");
		string newPath = Path.Combine(_dir, "quarterly-specs");
		RegisteredClone clone = new(
			"spec-copy",
			oldPath,
			"draft",
			[new RegisteredBranch("main", RepositoryStatusPayload.Empty),
				new RegisteredBranch("draft", RepositoryStatusPayload.Empty)],
			RepositoryStatusPayload.Empty);
		RegisteredRepo repo = new(
			"octo/spec", "octo/spec", "https://github.com/octo/spec", "main", [clone]);
		store.RegisterRepo(repo);
		store.SetFavorite(new WorkspaceItem(oldPath, "spec-copy", true, "clone", repo.Id), true);
		store.SetFavorite(new WorkspaceItem(oldPath, "draft", true, "branch", repo.Id, "draft"), true);
		store.SetFavorite(new WorkspaceItem(
			Path.Combine(oldPath, "docs"), "docs", true), true);
		store.SetFavorite(new WorkspaceItem(
			Path.Combine(oldPath, "docs", "a.md"), "a.md", false), true);
		store.AddRecent(new WorkspaceItem(oldPath, "spec-copy", true));
		store.AddRecent(new WorkspaceItem(Path.Combine(oldPath, "docs", "a.md"), "a.md", false));

		RegisteredClone moved = clone with { Id = "quarterly-specs", Path = newPath };
		Assert.That(store.TryRenameRepoClone(
			repo.Id, repo.Url, oldPath, clone.Id, moved, "main"), Is.True);
		RegisteredClone renamedBranch = moved with
		{
			CurrentBranch = "approved-draft",
			Branches = [new RegisteredBranch("main", RepositoryStatusPayload.Empty),
				new RegisteredBranch("approved-draft", RepositoryStatusPayload.Empty)],
		};
		Assert.That(store.TryRenameRepoBranch(
			repo.Id, repo.Url, newPath, moved.Id, renamedBranch,
			"draft", "approved-draft", "main"), Is.True);

		WorkspaceStatePayload state = new WorkspaceStore(_path).State();
		string[] expectedLocalPaths =
		[
			Path.Combine(newPath, "docs"),
			Path.Combine(newPath, "docs", "a.md"),
		];
		string[] expectedLocalLabels = ["docs", "a.md"];
		Assert.Multiple(() =>
		{
			Assert.That(state.Repositories.Single().Clones.Single().Id, Is.EqualTo("quarterly-specs"));
			Assert.That(state.Repositories.Single().Clones.Single().Path, Is.EqualTo(newPath));
			Assert.That(state.Favorites.Single(item => item.Kind == "clone").Path, Is.EqualTo(newPath));
			Assert.That(state.Favorites.Single(item => item.Kind == "clone").Label, Is.EqualTo("quarterly-specs"));
			Assert.That(state.Favorites.Single(item => item.Kind == "branch").Branch,
				Is.EqualTo("approved-draft"));
			Assert.That(state.Favorites.Single(item => item.Kind == "branch").Label,
				Is.EqualTo("quarterly-specs · approved-draft"));
			Assert.That(state.Favorites.Where(item => item.Kind == "local").Select(item => item.Path),
				Is.EquivalentTo(expectedLocalPaths));
			Assert.That(state.Favorites.Where(item => item.Kind == "local").Select(item => item.Label),
				Is.EquivalentTo(expectedLocalLabels));
			Assert.That(state.Recent[0].Path,
				Is.EqualTo(Path.Combine(newPath, "docs", "a.md")));
			Assert.That(state.Recent.Single(item => item.IsFolder).Path, Is.EqualTo(newPath));
			Assert.That(state.Recent.Single(item => item.IsFolder).Label, Is.EqualTo("quarterly-specs"));
		});
	}

	[Test]
	public void RenameClone_PersistenceFailureRestoresTheOriginalInMemoryAndDurableState()
	{
		WorkspaceStore store = new(_path);
		string oldPath = Path.Combine(_dir, "spec-copy");
		string newPath = Path.Combine(_dir, "quarterly-specs");
		RegisteredClone clone = new("spec-copy", oldPath, ["main"]);
		RegisteredRepo repo = new(
			"octo/spec", "octo/spec", "https://github.com/octo/spec", "main", [clone]);
		store.RegisterRepo(repo);
		store.SetFavorite(new WorkspaceItem(oldPath, "spec-copy", true, "clone", repo.Id), true);
		store.AddRecent(new WorkspaceItem(Path.Combine(oldPath, "README.md"), "README.md", false));
		Directory.CreateDirectory(_path + ".tmp");

		bool renamed = store.TryRenameRepoClone(
			repo.Id,
			repo.Url,
			oldPath,
			clone.Id,
			clone with { Id = "quarterly-specs", Path = newPath },
			"main");

		WorkspaceStatePayload memory = store.State();
		WorkspaceStatePayload durable = new WorkspaceStore(_path).State();
		Assert.Multiple(() =>
		{
			Assert.That(renamed, Is.False);
			Assert.That(memory.Repositories.Single().Clones.Single().Path, Is.EqualTo(oldPath));
			Assert.That(memory.Favorites.Single().Path, Is.EqualTo(oldPath));
			Assert.That(memory.Recent.Single().Path, Is.EqualTo(Path.Combine(oldPath, "README.md")));
			Assert.That(durable.Repositories.Single().Clones.Single().Path, Is.EqualTo(oldPath));
			Assert.That(durable.Favorites.Single().Path, Is.EqualTo(oldPath));
			Assert.That(durable.Recent.Single().Path, Is.EqualTo(Path.Combine(oldPath, "README.md")));
		});
	}

	[Test]
	public void RenameBranch_PersistenceFailureRestoresTheOriginalRegistrationAndFavorite()
	{
		WorkspaceStore store = new(_path);
		string clonePath = Path.Combine(_dir, "spec-copy");
		RegisteredClone clone = new(
			"spec-copy",
			clonePath,
			"draft",
			[new RegisteredBranch("main", RepositoryStatusPayload.Empty),
				new RegisteredBranch("draft", RepositoryStatusPayload.Empty)],
			RepositoryStatusPayload.Empty);
		RegisteredRepo repo = new(
			"octo/spec", "octo/spec", "https://github.com/octo/spec", "main", [clone]);
		store.RegisterRepo(repo);
		store.SetFavorite(new WorkspaceItem(clonePath, "draft", true, "branch", repo.Id, "draft"), true);
		Directory.CreateDirectory(_path + ".tmp");
		RegisteredClone renamed = clone with
		{
			CurrentBranch = "approved-draft",
			Branches = [new RegisteredBranch("main", RepositoryStatusPayload.Empty),
				new RegisteredBranch("approved-draft", RepositoryStatusPayload.Empty)],
		};

		bool committed = store.TryRenameRepoBranch(
			repo.Id, repo.Url, clonePath, clone.Id, renamed,
			"draft", "approved-draft", "main");

		WorkspaceStatePayload memory = store.State();
		WorkspaceStatePayload durable = new WorkspaceStore(_path).State();
		Assert.Multiple(() =>
		{
			Assert.That(committed, Is.False);
			Assert.That(memory.Repositories.Single().Clones.Single().CurrentBranch, Is.EqualTo("draft"));
			Assert.That(memory.Favorites.Single().Branch, Is.EqualTo("draft"));
			Assert.That(durable.Repositories.Single().Clones.Single().CurrentBranch, Is.EqualTo("draft"));
			Assert.That(durable.Favorites.Single().Branch, Is.EqualTo("draft"));
		});
	}

	[Test]
	public void RegisterRepo_DedupesById_AndUnregisterRemovesIt()
	{
		WorkspaceStore store = new(_path);
		RegisteredRepo repo = new("octo/spec", "octo/spec", "https://github.com/octo/spec", "main", []);

		store.RegisterRepo(repo);
		// Same id → no second entry.
		store.RegisterRepo(new RegisteredRepo("octo/spec", "octo/spec", "https://github.com/octo/spec", "main", []));
		store.RegisterRepo(new RegisteredRepo("octo/other", "octo/other", "https://github.com/octo/other", "main", []));
		Assert.That(store.State().Repositories, Has.Count.EqualTo(2));

		store.UnregisterRepo("octo/spec");
		string[] remaining = ["octo/other"];
		Assert.That(store.State().Repositories.Select(r => r.Id), Is.EqualTo(remaining));
	}

	[Test]
	public void RepositoryRegistrationSnapshot_RejectsAbsentAndReregisteredAbaCommits()
	{
		WorkspaceStore store = new(_path);
		RegisteredRepo repo = new("octo/spec", "octo/spec", "https://github.com/octo/spec", "main", []);
		RegisteredClone clone = new("copy", Path.Combine(_dir, "copy"), ["main"]);
		WorkspaceStore.RepositoryRegistrationSnapshot absent = store.CaptureRepoRegistration(repo.Id);

		store.UnregisterRepo(repo.Id);
		bool committedAfterForget = store.TryCommitRepoClone(
			absent, requirePresent: false, repo, clone, "main", out _);
		store.RegisterRepo(repo);
		WorkspaceStore.RepositoryRegistrationSnapshot registered = store.CaptureRepoRegistration(repo.Id);
		store.UnregisterRepo(repo.Id);
		bool metadataRestoredRegistration = store.TrySetRepoDefaultBranch(
			registered, "trunk", out _);
		store.RegisterRepo(repo);
		bool committedAfterReregister = store.TryCommitRepoClone(
			registered, requirePresent: true, repo, clone, "main", out _);

		Assert.Multiple(() =>
		{
			Assert.That(committedAfterForget, Is.False);
			Assert.That(metadataRestoredRegistration, Is.False);
			Assert.That(committedAfterReregister, Is.False);
			Assert.That(store.FindRepo(repo.Id)?.Clones, Is.Empty);
		});
	}

	[Test]
	public void RepositoryRegistrationSnapshot_RejectsCloneAndBranchResurrection()
	{
		WorkspaceStore store = new(_path);
		string clonePath = Path.Combine(_dir, "copy");
		RegisteredClone clone = new(
			"copy",
			clonePath,
			"main",
			[new RegisteredBranch("main", RepositoryStatusPayload.Empty),
				new RegisteredBranch("draft", RepositoryStatusPayload.Empty)],
			RepositoryStatusPayload.Empty);
		RegisteredRepo repo = new(
			"octo/spec", "octo/spec", "https://github.com/octo/spec", "main", [clone]);
		store.RegisterRepo(repo);
		WorkspaceStore.RepositoryRegistrationSnapshot beforeBranchDelete =
			store.CaptureRepoRegistration(repo.Id);

		store.RemoveRepoBranch(repo.Id, clonePath, "draft");
		bool restoredBranch = store.TryCommitRepoClone(
			beforeBranchDelete, requirePresent: true, repo, clone, "main", out _);
		WorkspaceStore.RepositoryRegistrationSnapshot beforeCloneDelete =
			store.CaptureRepoRegistration(repo.Id);
		RegisteredClone currentClone = store.FindRepo(repo.Id)!.Clones.Single();
		Assert.That(store.TryRemoveRepoClone(
			repo.Id, repo.Url, currentClone.Path, currentClone.Id), Is.True);
		bool restoredClone = store.TryCommitRepoClone(
			beforeCloneDelete, requirePresent: true, repo, currentClone, "main", out _);

		Assert.Multiple(() =>
		{
			Assert.That(restoredBranch, Is.False);
			Assert.That(restoredClone, Is.False);
			Assert.That(store.FindRepo(repo.Id)?.Clones, Is.Empty);
		});
	}

	[Test]
	public void DefaultBranchAndCloneMutations_AreAtomicAndPreserveEachOther()
	{
		WorkspaceStore store = new(_path);
		RegisteredRepo seed = new("octo/spec", "octo/spec", "https://github.com/octo/spec", string.Empty, []);
		RegisteredClone clone = new("copy-1", Path.Combine(_dir, "copy-1"), ["draft"]);
		using ManualResetEventSlim start = new(false);

		Task metadata = Task.Run(() =>
		{
			start.Wait();
			for (int index = 0; index < 100; index++)
			{
				store.SetRepoDefaultBranch(seed, "trunk");
			}
		});
		Task localCopy = Task.Run(() =>
		{
			start.Wait();
			for (int index = 0; index < 100; index++)
			{
				store.UpsertRepoClone(seed, clone, "master");
			}
		});
		start.Set();
		Task.WaitAll(metadata, localCopy);

		RegisteredRepo saved = store.State().Repositories.Single();
		Assert.Multiple(() =>
		{
			Assert.That(saved.DefaultBranch, Is.EqualTo("trunk"));
			Assert.That(saved.Clones, Is.EqualTo(new[] { clone }));
		});
	}

	[Test]
	public void TryRemoveRepoClone_DoesNotRemoveAReplacementRegistrationAtTheSamePath()
	{
		WorkspaceStore store = new(_path);
		RegisteredRepo repo = new(
			"octo/spec", "octo/spec", "https://github.com/octo/spec", "main", []);
		string clonePath = Path.Combine(_dir, "copy");
		store.RegisterRepo(repo);
		store.UpsertRepoClone(repo, new RegisteredClone("original", clonePath, ["main"]), "main");
		store.UpsertRepoClone(repo, new RegisteredClone("replacement", clonePath, ["main"]), "main");

		bool removedOriginal = store.TryRemoveRepoClone(
			repo.Id, repo.Url, clonePath, "original");

		Assert.Multiple(() =>
		{
			Assert.That(removedOriginal, Is.False);
			Assert.That(store.State().Repositories.Single().Clones.Single().Id, Is.EqualTo("replacement"));
		});
		Assert.That(
			store.TryRemoveRepoClone(repo.Id, repo.Url, clonePath, "replacement"),
			Is.True);
		Assert.That(store.State().Repositories.Single().Clones, Is.Empty);
	}
	[Test]
	public void Save_ThenAFreshStore_RoundTripsThroughTheFile()
	{
		WorkspaceStore store = new(_path);
		string recentPath = Path.Combine(_dir, "a.md");
		store.AddRecent(File(recentPath));
		string favoritePath = Path.Combine(_dir, "specs");
		store.SetFavorite(new WorkspaceItem(favoritePath, "specs", IsFolder: true), favorite: true);
		store.RegisterRepo(new RegisteredRepo("octo/spec", "octo/spec", "https://github.com/octo/spec", "main", []));

		// A brand-new store over the same path must Load exactly what the first one persisted.
		WorkspaceStatePayload reloaded = new WorkspaceStore(_path).State();
		string[] expectedRecent = [recentPath];
		Assert.Multiple(() =>
		{
			Assert.That(System.IO.File.Exists(_path), Is.True, "Save must create the file (and its directory)");
			Assert.That(reloaded.Recent.Select(r => r.Path), Is.EqualTo(expectedRecent));
			Assert.That(reloaded.Favorites[0].Path, Is.EqualTo(favoritePath));
			Assert.That(reloaded.Favorites[0].IsFolder, Is.True);
			Assert.That(reloaded.Repositories[0].Url, Is.EqualTo("https://github.com/octo/spec"));
		});
	}

	[Test]
	public void Load_WhenTheFileIsMissing_IsAnEmptyState()
	{
		WorkspaceStatePayload state = new WorkspaceStore(_path).State();
		Assert.Multiple(() =>
		{
			Assert.That(state.Recent, Is.Empty);
			Assert.That(state.Favorites, Is.Empty);
			Assert.That(state.Repositories, Is.Empty);
		});
	}

	[Test]
	public void Load_WhenTheFileIsCorrupt_IsAnEmptyStateRatherThanThrowing()
	{
		Directory.CreateDirectory(_dir);
		System.IO.File.WriteAllText(_path, "{ this is not valid json ]");

		WorkspaceStatePayload state = new WorkspaceStore(_path).State();
		Assert.That(state.Recent, Is.Empty);
	}

	[Test]
	public void Load_WhenAListIsExplicitlyNull_CoalescesToEmptyRatherThanThrowing()
	{
		Directory.CreateDirectory(_dir);
		// A hand-edited file with a null list and the others absent must still load as a usable, empty store.
		System.IO.File.WriteAllText(_path, """{ "recent": null }""");

		WorkspaceStore store = new(_path);
		Assert.DoesNotThrow(() => store.AddRecent(File("/a.md")));
		Assert.That(store.State().Recent, Has.Count.EqualTo(1));
	}

	[Test]
	public void Load_NormalizesLegacyFavoritesAndDropsMalformedTypedFavorites()
	{
		Directory.CreateDirectory(_dir);
		System.IO.File.WriteAllText(_path, """
			{
			  "favorites": [
			    { "path": "C:\\specs\\legacy.md", "label": "legacy.md", "isFolder": false },
			    { "path": "unsafe", "label": "unsafe", "isFolder": false, "kind": "future-kind" },
			    { "path": "docs/guide.md", "label": "guide.md", "isFolder": false, "kind": "remote" },
			    { "path": "../escape.md", "label": "escape.md", "isFolder": false, "kind": "remote", "repositoryId": "octo/spec", "branch": "main" },
			    { "path": "octo/other", "label": "octo/spec", "isFolder": true, "kind": "repository", "repositoryId": "octo/spec" },
			    { "path": "C:\\specs\\mixed.md", "label": "mixed.md", "isFolder": false, "kind": "local", "repositoryId": "octo/spec" }
			    ,{ "path": "relative.md", "label": "relative.md", "isFolder": false, "kind": "local" }
			    ,{ "path": "bad owner/spec", "label": "bad", "isFolder": true, "kind": "repository", "repositoryId": "bad owner/spec" }
			    ,{ "path": "-owner/spec", "label": "bad", "isFolder": true, "kind": "repository", "repositoryId": "-owner/spec" }
			    ,{ "path": "octo/spec:bad", "label": "bad", "isFolder": true, "kind": "repository", "repositoryId": "octo/spec:bad" }
			  ]
			}
			""");

		WorkspaceItem favorite = new WorkspaceStore(_path).State().Favorites.Single();
		Assert.Multiple(() =>
		{
			Assert.That(favorite.Path, Is.EqualTo(@"C:\specs\legacy.md"));
			Assert.That(favorite.Kind, Is.EqualTo("local"));
		});
	}
}
