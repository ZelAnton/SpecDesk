using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;
using SpecDesk.GitHub;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

/// <summary>
/// The workspace-state slice of the controller (HostController.Workspace.cs): serving the persisted state
/// (workspace.request → workspace.state), recording recents as a side effect of opening a file/folder,
/// toggling favorites, and registering/unregistering GitHub repos over the wire — plus the pure GitHub-URL
/// parser the register handler uses. Driven with a real temporary workspace.json.
/// </summary>
[TestFixture]
public sealed class HostControllerWorkspaceTests
{
	private sealed class RacingMetadataCatalog : IGitHubRepositoryCatalog
	{
		private int _calls;
		public bool ThrowFirst { get; init; }
		public TaskCompletionSource FirstStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseFirst { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async Task<GitHubRepositoryMetadata> GetMetadataAsync(
			string owner, string name, string accessToken, CancellationToken cancellationToken = default)
		{
			int call = Interlocked.Increment(ref _calls);
			if (call == 1)
			{
				FirstStarted.SetResult();
				await ReleaseFirst.Task;
				if (ThrowFirst)
				{
					throw new HttpRequestException("stale metadata failure");
				}
				return new GitHubRepositoryMetadata("master");
			}
			return new GitHubRepositoryMetadata("trunk");
		}

		public Task<IReadOnlyList<GitHubRepositoryEntry>> GetTreeAsync(
			string owner, string name, string branch, string accessToken,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Repository browsing is not exercised by this fake.");

		public Task<string> GetFileAsync(
			string owner, string name, string branch, string path, string accessToken,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("Repository browsing is not exercised by this fake.");
	}
	private sealed class NoDialogs : IFileDialogs
	{
		public string? PickOpenFile() => null;
		public string? PickOpenFolder() => null;
		public string? PickSaveFile(string? suggestedPath) => null;
	}

	private static Renderer.RenderResult StubRender(string docDir, string text) => new(string.Empty, []);

	private string _root = string.Empty;
	private string _wsPath = string.Empty;
	private readonly List<string> _sent = [];
	private readonly object _gate = new();

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), "specdesk-wsc-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_root);
		_wsPath = Path.Combine(_root, "workspace.json");
		lock (_gate)
		{
			_sent.Clear();
		}
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	private HostController NewController()
	{
		void Send(string json)
		{
			lock (_gate)
			{
				_sent.Add(json);
			}
		}

		HostController controller = new(
			StubRender,
			Send,
			new NoDialogs(),
			(_, _, _, _, _) => null,
			new FakeVersioning(),
			NullLogger<HostController>.Instance,
			initialDocPath: null,
			auth: new FakeGitHubAuth(true),
			workspace: new WorkspaceStore(_wsPath));
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
		lock (_gate)
		{
			_sent.Clear();
		}
		return controller;
	}

	// The most recently emitted workspace.state payload (mutations re-emit it, so tests want the latest).
	private WorkspaceStatePayload? LatestState()
	{
		lock (_gate)
		{
			WorkspaceStatePayload? latest = null;
			foreach (string json in _sent)
			{
				IpcMessage? message = IpcSerializer.TryDeserialize(json);
				if (message is not null && message.Kind == MessageKinds.WorkspaceState)
				{
					latest = message.GetPayload<WorkspaceStatePayload>();
				}
			}

			return latest;
		}
	}

	private IpcMessage? Find(string kind)
	{
		lock (_gate)
		{
			foreach (string json in _sent)
			{
				IpcMessage? message = IpcSerializer.TryDeserialize(json);
				if (message is not null && message.Kind == kind)
				{
					return message;
				}
			}
		}

		return null;
	}

	private string WriteDoc(string name)
	{
		string path = Path.Combine(_root, name);
		File.WriteAllText(path, "# " + name);
		return path;
	}

	[Test]
	public void WorkspaceRequest_EmitsTheCurrentState()
	{
		using HostController controller = NewController();

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.WorkspaceRequest));

		WorkspaceStatePayload? state = LatestState();
		Assert.That(state, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(state!.Recent, Is.Empty);
			Assert.That(state.Favorites, Is.Empty);
			Assert.That(state.Repositories, Is.Empty);
		});
	}

	[Test]
	public void TheAutoOpenedWelcomeDoc_IsNotRecorded_ButAUserOpenIs()
	{
		string welcome = WriteDoc("welcome.md");
		void Send(string json)
		{
			lock (_gate)
			{
				_sent.Add(json);
			}
		}

		using HostController controller = new(
			StubRender,
			Send,
			new NoDialogs(),
			(_, _, _, _, _) => null,
			new FakeVersioning(),
			NullLogger<HostController>.Instance,
			initialDocPath: welcome,
			workspace: new WorkspaceStore(_wsPath));
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready)); // auto-opens welcome.md

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.WorkspaceRequest));
		Assert.That(LatestState()!.Recent, Is.Empty, "the auto-opened welcome doc must not be a recent");

		// A file the author explicitly opens IS recorded.
		string chosen = WriteDoc("chosen.md");
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload(chosen)));
		string[] expected = [chosen];
		Assert.That(LatestState()!.Recent.Select(r => r.Path), Is.EqualTo(expected));
	}

	[Test]
	public void OpeningAFile_RecordsItAsARecent()
	{
		using HostController controller = NewController();
		string doc = WriteDoc("spec.md");

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload(doc)));

		WorkspaceStatePayload? state = LatestState();
		Assert.That(state, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(state!.Recent, Has.Count.EqualTo(1));
			Assert.That(state.Recent[0].Path, Is.EqualTo(doc));
			Assert.That(state.Recent[0].Label, Is.EqualTo("spec.md"));
			Assert.That(state.Recent[0].IsFolder, Is.False);
		});
	}

	[Test]
	public void OpeningAFolder_RecordsItAsARecentFolder()
	{
		using HostController controller = NewController();
		string folder = Path.Combine(_root, "specs");
		Directory.CreateDirectory(folder);

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.FolderOpen, new FolderOpenPayload(folder)));

		WorkspaceStatePayload? state = LatestState();
		Assert.That(state, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(state!.Recent, Has.Count.EqualTo(1));
			Assert.That(state.Recent[0].Path, Is.EqualTo(Path.GetFullPath(folder)));
			Assert.That(state.Recent[0].IsFolder, Is.True);
		});
	}

	[Test]
	public void OpeningTheSameFileTwice_DedupesToOneRecent()
	{
		using HostController controller = NewController();
		string a = WriteDoc("a.md");
		string b = WriteDoc("b.md");

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload(a)));
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload(b)));
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload(a)));

		WorkspaceStatePayload? state = LatestState();
		Assert.That(state, Is.Not.Null);
		// Re-opening a.md moves it to the front; it is not duplicated. Most-recent first: [a, b].
		string[] expected = [a, b];
		Assert.That(state!.Recent.Select(r => r.Path), Is.EqualTo(expected));
	}

	[Test]
	public void OpeningManyFiles_CapsRecentsAtTwenty()
	{
		using HostController controller = NewController();
		for (int i = 0; i < 25; i++)
		{
			string doc = WriteDoc($"f{i}.md");
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload(doc)));
		}

		WorkspaceStatePayload? state = LatestState();
		Assert.That(state, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(state!.Recent, Has.Count.EqualTo(20));
			// The most recent (f24) is first; the five oldest (f0..f4) fell off the tail.
			Assert.That(state.Recent[0].Label, Is.EqualTo("f24.md"));
			Assert.That(state.Recent.Select(r => r.Label), Has.None.EqualTo("f0.md"));
		});
	}

	[Test]
	public void FavoriteToggle_AddsThenRemoves()
	{
		using HostController controller = NewController();
		string folder = Path.Combine(_root, "faves");
		Directory.CreateDirectory(folder);

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.WorkspaceFavorite, new WorkspaceFavoritePayload(folder, Favorite: true)));
		WorkspaceStatePayload? added = LatestState();
		Assert.That(added, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(added!.Favorites, Has.Count.EqualTo(1));
			Assert.That(added.Favorites[0].Path, Is.EqualTo(folder));
			// The host reconstructs the label and the folder flag from the path.
			Assert.That(added.Favorites[0].Label, Is.EqualTo("faves"));
			Assert.That(added.Favorites[0].IsFolder, Is.True);
		});

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.WorkspaceFavorite, new WorkspaceFavoritePayload(folder, Favorite: false)));
		Assert.That(LatestState()!.Favorites, Is.Empty);
	}

	[Test]
	public void FavoriteToggle_PersistsRepositoryAndStableRemoteFolderIdentity()
	{
		using HostController controller = NewController();
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.RepoRegister, new RegisterRepoPayload("octo/specs")));
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.WorkspaceFavorite,
			new WorkspaceFavoritePayload(
				"octo/specs", true, "repository", RepositoryId: "octo/specs", IsFolder: true)));
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.WorkspaceFavorite,
			new WorkspaceFavoritePayload(
				"docs", true, "remote", RepositoryId: "octo/specs", Branch: "main", IsFolder: true)));

		WorkspaceItem[] favorites = new WorkspaceStore(_wsPath).State().Favorites.ToArray();
		Assert.That(favorites, Has.Length.EqualTo(2));
		Assert.That(favorites.Single(item => item.Kind == "repository").RepositoryId, Is.EqualTo("octo/specs"));
		WorkspaceItem remote = favorites.Single(item => item.Kind == "remote");
		Assert.Multiple(() =>
		{
			Assert.That(remote.Path, Is.EqualTo("docs"));
			Assert.That(remote.Branch, Is.EqualTo("main"));
			Assert.That(remote.IsFolder, Is.True);
		});
	}

	[TestCase(false)]
	[TestCase(true)]
	public void FavoriteToggle_CanRemoveADeletedLocalItem(bool folder)
	{
		using HostController controller = NewController();
		string path = Path.Combine(_root, folder ? "gone-folder" : "gone.md");
		if (folder)
		{
			Directory.CreateDirectory(path);
		}
		else
		{
			File.WriteAllText(path, "gone");
		}
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.WorkspaceFavorite,
			new WorkspaceFavoritePayload(path, true, IsFolder: folder)));
		if (folder)
		{
			Directory.Delete(path);
		}
		else
		{
			File.Delete(path);
		}

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.WorkspaceFavorite,
			new WorkspaceFavoritePayload(path, false, IsFolder: folder)));

		Assert.That(new WorkspaceStore(_wsPath).State().Favorites, Is.Empty);
	}

	[Test]
	public void RegisterRepo_WithAValidUrl_StoresANormalizedEntry()
	{
		using HostController controller = NewController();

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.RepoRegister, new RegisterRepoPayload("https://github.com/octo/spec-repo")));

		WorkspaceStatePayload? state = LatestState();
		Assert.That(state, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(Find(MessageKinds.Error), Is.Null);
			Assert.That(state!.Repositories, Has.Count.EqualTo(1));
			Assert.That(state.Repositories[0].Id, Is.EqualTo("octo/spec-repo"));
			Assert.That(state.Repositories[0].Name, Is.EqualTo("octo/spec-repo"));
			Assert.That(state.Repositories[0].Url, Is.EqualTo("https://github.com/octo/spec-repo"));
		});
	}

	[TestCase(false, false)]
	[TestCase(true, false)]
	[TestCase(true, true)]
	public void StaleMetadataCannotPublishAfterUnregisterReRegisterOrDispose(
		bool throwFirst,
		bool disposeBeforeRelease)
	{
		RacingMetadataCatalog catalog = new() { ThrowFirst = throwFirst };
		void Send(string json)
		{
			lock (_gate)
			{
				_sent.Add(json);
			}
		}
		HostController controller = new(
			StubRender, Send, new NoDialogs(), (_, _, _, _, _) => null,
			new FakeVersioning(), NullLogger<HostController>.Instance,
			auth: new FakeGitHubAuth(true), workspace: new WorkspaceStore(_wsPath),
			repositoryCatalog: catalog);
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.RepoRegister, new RegisterRepoPayload("octo/specs")));
		Assert.That(catalog.FirstStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.RepoUnregister, new UnregisterRepoPayload("octo/specs")));
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.RepoRegister, new RegisterRepoPayload("octo/specs")));
		for (int attempt = 0; attempt < 100; attempt++)
		{
			IReadOnlyList<RegisteredRepo> repositories = new WorkspaceStore(_wsPath).State().Repositories;
			if (repositories.Count > 0 && repositories[0].DefaultBranch == "trunk")
			{
				break;
			}
			Thread.Sleep(20);
		}
		if (disposeBeforeRelease)
		{
			controller.Dispose();
		}
		catalog.ReleaseFirst.SetResult();
		Thread.Sleep(100);
		if (!disposeBeforeRelease)
		{
			controller.Dispose();
		}

		RegisteredRepo saved = new WorkspaceStore(_wsPath).State().Repositories.Single();
		Assert.That(saved.DefaultBranch, Is.EqualTo("trunk"));
		lock (_gate)
		{
			Assert.That(_sent
				.Select(IpcSerializer.TryDeserialize)
				.Where(message => message?.Kind == MessageKinds.Error), Is.Empty);
		}
	}

	[Test]
	public void RegisterRepo_WithAnInvalidUrl_EmitsAnErrorAndStoresNothing()
	{
		using HostController controller = NewController();

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.RepoRegister, new RegisterRepoPayload("just-a-string")));

		Assert.Multiple(() =>
		{
			Assert.That(Find(MessageKinds.Error)?.GetPayload<ErrorPayload>()?.Message, Is.Not.Null.And.Not.Empty);
			// No workspace.state is emitted for a rejected register (nothing changed), so the store stays empty.
			Assert.That(LatestState(), Is.Null);
		});
	}

	[Test]
	public void UnregisterRepo_RemovesAPreviouslyRegisteredRepo()
	{
		using HostController controller = NewController();
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.RepoRegister, new RegisterRepoPayload("owner/name")));
		Assert.That(LatestState()!.Repositories, Has.Count.EqualTo(1));

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.RepoUnregister, new UnregisterRepoPayload("owner/name")));

		Assert.That(LatestState()!.Repositories, Is.Empty);
	}

	[Test]
	public void UnregisterAfterDispose_DoesNotMutateOrPublishWorkspaceState()
	{
		HostController controller = NewController();
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.RepoRegister,
			new RegisterRepoPayload("octo/specs")));
		lock (_gate)
		{
			_sent.Clear();
		}

		controller.Dispose();
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.RepoUnregister,
			new UnregisterRepoPayload("octo/specs")));

		IReadOnlyList<RegisteredRepo> repositories = new WorkspaceStore(_wsPath).State().Repositories;
		Assert.That(repositories, Has.Count.EqualTo(1));
		Assert.That(repositories[0].Id, Is.EqualTo("octo/specs"));
		lock (_gate)
		{
			Assert.That(_sent, Is.Empty);
		}
	}

	[TestCase("https://github.com/octo/spec-repo", "octo", "spec-repo")]
	[TestCase("https://github.com/octo/spec-repo.git", "octo", "spec-repo")]
	[TestCase("https://github.com/octo/spec-repo/", "octo", "spec-repo")]
	[TestCase("octo/spec-repo", "octo", "spec-repo")]
	[TestCase("git@github.com:octo/spec-repo.git", "octo", "spec-repo")]
	[TestCase("  github.com/octo/spec-repo  ", "octo", "spec-repo")]
	public void TryParseGitHubRepo_AcceptsTheSupportedForms(string input, string owner, string name)
	{
		Assert.That(HostController.TryParseGitHubRepo(input, out string parsedOwner, out string parsedName), Is.True);
		Assert.Multiple(() =>
		{
			Assert.That(parsedOwner, Is.EqualTo(owner));
			Assert.That(parsedName, Is.EqualTo(name));
		});
	}

	[TestCase("")]
	[TestCase("   ")]
	[TestCase("just-a-string")]
	[TestCase("https://github.com/octo/spec-repo/tree/main")]
	[TestCase("https://example.com/octo/spec-repo")]
	[TestCase("octo/")]
	[TestCase("/spec-repo")]
	// A non-GitHub host must NOT be mis-read as the owner (the host is anchored to github.com; an owner can't
	// contain the dots a hostname does).
	[TestCase("https://gitlab.com/some-user")]
	[TestCase("http://example.com/owner")]
	[TestCase("https://youtube.com/watch")]
	[TestCase("gitlab.com/some-user")]
	public void TryParseGitHubRepo_RejectsAnythingElse(string input)
	{
		Assert.That(HostController.TryParseGitHubRepo(input, out _, out _), Is.False);
	}
}
