using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;
using SpecDesk.Git;
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
	private sealed class RecordingMetadataCatalog(GitHubRepositoryMetadata metadata)
		: IGitHubRepositoryCatalog
	{
		public string? Owner { get; private set; }
		public string? Name { get; private set; }
		public string? AccessToken { get; private set; }

		public Task<GitHubRepositoryMetadata> GetMetadataAsync(
			string owner, string name, string accessToken, CancellationToken cancellationToken = default)
		{
			Owner = owner;
			Name = name;
			AccessToken = accessToken;
			return Task.FromResult(metadata);
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

	private sealed class RacingMetadataCatalog : IGitHubRepositoryCatalog
	{
		private int _calls;
		public bool ThrowFirst { get; init; }
		public TaskCompletionSource FirstStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseFirst { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource SecondReturned { get; } =
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
			SecondReturned.TrySetResult();
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

	private sealed class DeletionTrapRepositoryManager : ILocalRepositoryManager
	{
		public int InvocationCount { get; private set; }

		private T Unexpected<T>()
		{
			InvocationCount++;
			throw new AssertionException("Top-level unregister must not invoke local repository operations.");
		}

		public LocalRepositoryInfo Inspect(string repositoryPath, string knownDefaultBranch) =>
			Unexpected<LocalRepositoryInfo>();

		public LocalRepositoryInfo InspectExpected(
			string repositoryPath,
			string expectedRepositoryUrl,
			string knownDefaultBranch) => Unexpected<LocalRepositoryInfo>();

		public LocalRepositoryInfo Fetch(
			string repositoryPath,
			string expectedRepositoryUrl,
			string knownDefaultBranch,
			string? accessToken,
			CancellationToken ct) => Unexpected<LocalRepositoryInfo>();

		public LocalRepositoryInfo PullFastForward(
			string repositoryPath,
			string expectedRepositoryUrl,
			string knownDefaultBranch,
			string expectedBranch,
			string? accessToken,
			CancellationToken ct,
			Action? beforeMutation = null,
			Action? onMutationStarting = null) => Unexpected<LocalRepositoryInfo>();

		public LocalRepositoryInfo FetchAndFastForwardCleanLine(
			string repositoryPath,
			string expectedRepositoryUrl,
			string knownDefaultBranch,
			string defaultBranch,
			string? accessToken,
			CancellationToken ct) => Unexpected<LocalRepositoryInfo>();

		public LocalRepositoryInfo PushBranchSafely(
			string repositoryPath,
			string expectedRepositoryUrl,
			string knownDefaultBranch,
			string expectedBranch,
			string accessToken,
			CancellationToken ct) => Unexpected<LocalRepositoryInfo>();

		public BranchSwitchResult SwitchBranchSafely(
			string repositoryPath,
			string expectedRepositoryUrl,
			string expectedCurrentBranch,
			string branch,
			Action? beforeMutation = null,
			Action? onMutationStarting = null) => Unexpected<BranchSwitchResult>();

		public LocalRepositoryInfo CreateBranch(
			string repositoryPath,
			string expectedRepositoryUrl,
			string expectedCurrentBranch,
			string branch,
			Action? beforeMutation = null,
			Action? onMutationStarting = null) => Unexpected<LocalRepositoryInfo>();

		public LocalRepositoryInfo RenameBranch(
			string repositoryPath,
			string expectedRepositoryUrl,
			string expectedCurrentBranch,
			string branch,
			string newBranch,
			string defaultBranch,
			Action? beforeMutation = null,
			Action? onMutationStarting = null) => Unexpected<LocalRepositoryInfo>();

		public CloneRenameResult RenameClone(
			string repositoryPath,
			string expectedRepositoryUrl,
			string knownDefaultBranch,
			string localName,
			Action? beforeMutation = null,
			Action? onMutationStarting = null) => Unexpected<CloneRenameResult>();

		public RepositoryDeletionRisks InspectDeletionRisks(
			string repositoryPath,
			string expectedRepositoryUrl,
			string? expectedCurrentBranch,
			string? branch = null,
			Action? beforeInspect = null) => Unexpected<RepositoryDeletionRisks>();

		public BranchDeletionResult DeleteBranch(
			string repositoryPath,
			string expectedRepositoryUrl,
			string branch,
			string defaultBranch,
			string confirmationToken,
			Action? onCurrentBranchChangeStarting = null) => Unexpected<BranchDeletionResult>();

		public bool DeleteClone(
			string repositoryPath,
			string expectedRepositoryUrl,
			string confirmationToken,
			Action? onMutationStarting = null) => Unexpected<bool>();
	}

	// Records only the background auto-sync entry point; every other member is unreachable on that path.
	private sealed class RecordingAutoSyncManager : ILocalRepositoryManager
	{
		private readonly object _gate = new();
		private readonly List<(string Path, string Url, string DefaultBranch, string? AccessToken)> _calls = [];

		public int CallCount
		{
			get
			{
				lock (_gate)
				{
					return _calls.Count;
				}
			}
		}

		public (string Path, string Url, string DefaultBranch, string? AccessToken)[] Calls()
		{
			lock (_gate)
			{
				return _calls.ToArray();
			}
		}

		public LocalRepositoryInfo FetchAndFastForwardCleanLine(
			string repositoryPath,
			string expectedRepositoryUrl,
			string knownDefaultBranch,
			string defaultBranch,
			string? accessToken,
			CancellationToken ct)
		{
			ct.ThrowIfCancellationRequested();
			lock (_gate)
			{
				_calls.Add((repositoryPath, expectedRepositoryUrl, defaultBranch, accessToken));
			}
			LocalRepositoryStatus clean = new(0, 0, false, 0, false);
			return new LocalRepositoryInfo(
				knownDefaultBranch,
				knownDefaultBranch,
				[new LocalBranchInfo(knownDefaultBranch, clean)],
				clean);
		}

		private static T Unreachable<T>() =>
			throw new AssertionException("Background auto-sync must only call FetchAndFastForwardCleanLine.");

		public LocalRepositoryInfo Inspect(string repositoryPath, string knownDefaultBranch) =>
			Unreachable<LocalRepositoryInfo>();

		public LocalRepositoryInfo InspectExpected(
			string repositoryPath, string expectedRepositoryUrl, string knownDefaultBranch) =>
			Unreachable<LocalRepositoryInfo>();

		public LocalRepositoryInfo Fetch(
			string repositoryPath, string expectedRepositoryUrl, string knownDefaultBranch,
			string? accessToken, CancellationToken ct) => Unreachable<LocalRepositoryInfo>();

		public LocalRepositoryInfo PullFastForward(
			string repositoryPath, string expectedRepositoryUrl, string knownDefaultBranch, string expectedBranch,
			string? accessToken, CancellationToken ct, Action? beforeMutation = null,
			Action? onMutationStarting = null) => Unreachable<LocalRepositoryInfo>();

		public LocalRepositoryInfo PushBranchSafely(
			string repositoryPath, string expectedRepositoryUrl, string knownDefaultBranch, string expectedBranch,
			string accessToken, CancellationToken ct) => Unreachable<LocalRepositoryInfo>();

		public BranchSwitchResult SwitchBranchSafely(
			string repositoryPath, string expectedRepositoryUrl, string expectedCurrentBranch, string branch,
			Action? beforeMutation = null, Action? onMutationStarting = null) => Unreachable<BranchSwitchResult>();

		public LocalRepositoryInfo CreateBranch(
			string repositoryPath, string expectedRepositoryUrl, string expectedCurrentBranch, string branch,
			Action? beforeMutation = null, Action? onMutationStarting = null) => Unreachable<LocalRepositoryInfo>();

		public LocalRepositoryInfo RenameBranch(
			string repositoryPath, string expectedRepositoryUrl, string expectedCurrentBranch, string branch,
			string newBranch, string defaultBranch, Action? beforeMutation = null,
			Action? onMutationStarting = null) => Unreachable<LocalRepositoryInfo>();

		public CloneRenameResult RenameClone(
			string repositoryPath, string expectedRepositoryUrl, string knownDefaultBranch, string localName,
			Action? beforeMutation = null, Action? onMutationStarting = null) => Unreachable<CloneRenameResult>();

		public RepositoryDeletionRisks InspectDeletionRisks(
			string repositoryPath, string expectedRepositoryUrl, string? expectedCurrentBranch,
			string? branch = null, Action? beforeInspect = null) => Unreachable<RepositoryDeletionRisks>();

		public BranchDeletionResult DeleteBranch(
			string repositoryPath, string expectedRepositoryUrl, string branch, string defaultBranch,
			string confirmationToken, Action? onCurrentBranchChangeStarting = null) =>
			Unreachable<BranchDeletionResult>();

		public bool DeleteClone(
			string repositoryPath, string expectedRepositoryUrl, string confirmationToken,
			Action? onMutationStarting = null) => Unreachable<bool>();
	}

	// A registered repository with one local copy on its default line, so an auto-sync has a target to iterate.
	private static void RegisterCopyForAutoSync(WorkspaceStore store, string clonePath)
	{
		store.RegisterRepo(new RegisteredRepo(
			"octo/specs",
			"octo/specs",
			"https://github.com/octo/specs",
			"main",
			[
				new RegisteredClone(
					"spec-copy",
					clonePath,
					"main",
					[new RegisteredBranch("main", RepositoryStatusPayload.Empty)],
					RepositoryStatusPayload.Empty),
			]));
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

	private HostController NewController(
		IGitHubAuth? auth = null,
		IGitHubRepositoryCatalog? repositoryCatalog = null,
		ILocalRepositoryInspector? repositoryInspector = null,
		WorkspaceStore? workspace = null)
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
			auth: auth ?? new FakeGitHubAuth(true),
			workspace: workspace ?? new WorkspaceStore(_wsPath),
			repositoryInspector: repositoryInspector,
			repositoryCatalog: repositoryCatalog);
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

	private WorkspaceStatePayload? WaitForState(Func<WorkspaceStatePayload, bool> predicate)
	{
		WorkspaceStatePayload? match = null;
		bool found = SpinWait.SpinUntil(() =>
		{
			lock (_gate)
			{
				foreach (string json in _sent)
				{
					IpcMessage? message = IpcSerializer.TryDeserialize(json);
					WorkspaceStatePayload? state = message?.Kind == MessageKinds.WorkspaceState
						? message.GetPayload<WorkspaceStatePayload>()
						: null;
					if (state is not null && predicate(state))
					{
						match = state;
						return true;
					}
				}
			}

			return false;
		}, TimeSpan.FromSeconds(5));
		return found ? match : null;
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

	private IpcMessage? WaitFor(string kind)
	{
		SpinWait.SpinUntil(() => Find(kind) is not null, TimeSpan.FromSeconds(2));
		return Find(kind);
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

		WorkspaceStatePayload? state = WaitForState(candidate =>
			candidate.Recent.Count == 0
			&& candidate.Favorites.Count == 0
			&& candidate.Repositories.Count == 0);
		Assert.That(state, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(state!.Recent, Is.Empty);
			Assert.That(state.Favorites, Is.Empty);
			Assert.That(state.Repositories, Is.Empty);
		});
	}

	[Test]
	public void WorkspaceRequest_StaleForcedSnapshotCannotOvertakeANewerMutation()
	{
		string id = $"outside/repo{Guid.NewGuid():N}";
		new WorkspaceStore(_wsPath).RegisterRepo(new RegisteredRepo(
			id, id, $"https://github.com/{id}", "main", []));
		using HostController controller = NewController();
		using ManualResetEventSlim captured = new(false);
		using ManualResetEventSlim release = new(false);
		controller.WorkspaceRequestStateCapturedForTest = () =>
		{
			captured.Set();
			release.Wait(TimeSpan.FromSeconds(5));
		};

		Task request = Task.Run(() => controller.OnMessage(
			IpcSerializer.SerializeEvent(MessageKinds.WorkspaceRequest)));
		Assert.That(captured.Wait(TimeSpan.FromSeconds(2)), Is.True);
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.RepoUnregister, new UnregisterRepoPayload(id)));
		release.Set();
		Assert.That(request.Wait(TimeSpan.FromSeconds(2)), Is.True);
		Assert.That(WaitForState(state => state.Repositories.Count == 0), Is.Not.Null);

		WorkspaceStatePayload[] states;
		lock (_gate)
		{
			states = _sent
				.Select(IpcSerializer.TryDeserialize)
				.Where(message => message?.Kind == MessageKinds.WorkspaceState)
				.Select(message => message!.GetPayload<WorkspaceStatePayload>()!)
				.ToArray();
		}
		Assert.That(states, Is.Not.Empty);
		int firstEmpty = Array.FindIndex(states, state => state.Repositories.Count == 0);
		Assert.Multiple(() =>
		{
			Assert.That(firstEmpty, Is.GreaterThanOrEqualTo(0));
			Assert.That(states.Skip(firstEmpty).All(state => state.Repositories.Count == 0), Is.True,
				"the captured older state must not be emitted after the unregister mutation");
		});
	}
	[TestCase(false, false, RepoDescriptionStates.Found, "")]
	[TestCase(true, true, RepoDescriptionStates.Private, "gho_test")]
	public void RepositoryDescriptionRequest_PublishesMetadataForTheCurrentAuthorization(
		bool signedIn,
		bool isPrivate,
		string expectedState,
		string expectedAccessToken)
	{
		RecordingMetadataCatalog catalog = new(
			new GitHubRepositoryMetadata("main", "Product specifications", isPrivate));
		using HostController controller = NewController(new FakeGitHubAuth(signedIn), catalog);

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.RepoDescriptionRequest,
			new RepoDescriptionRequestPayload("outside/product-specs", 42)));

		RepoDescriptionPayload? payload = WaitFor(MessageKinds.RepoDescription)?
			.GetPayload<RepoDescriptionPayload>();
		Assert.That(payload, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(catalog.Owner, Is.EqualTo("outside"));
			Assert.That(catalog.Name, Is.EqualTo("product-specs"));
			Assert.That(catalog.AccessToken, Is.EqualTo(expectedAccessToken));
			Assert.That(payload!.Url, Is.EqualTo("outside/product-specs"));
			Assert.That(payload.RequestId, Is.EqualTo(42));
			Assert.That(payload.State, Is.EqualTo(expectedState));
			Assert.That(payload.Description, Is.EqualTo("Product specifications"));
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
		WorkspaceStatePayload? initialState = WaitForState(state => state.Recent.Count == 0);
		Assert.That(initialState, Is.Not.Null);
		Assert.That(initialState!.Recent, Is.Empty, "the auto-opened welcome doc must not be a recent");

		// A file the author explicitly opens IS recorded.
		string chosen = WriteDoc("chosen.md");
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload(chosen)));
		string[] expected = [chosen];
		WorkspaceStatePayload? chosenState = WaitForState(state =>
			state.Recent.Count == 1 && state.Recent[0].Path == chosen);
		Assert.That(chosenState, Is.Not.Null);
		Assert.That(chosenState!.Recent.Select(r => r.Path), Is.EqualTo(expected));
	}

	[Test]
	public void OpeningAFile_RecordsItAsARecent()
	{
		using HostController controller = NewController();
		string doc = WriteDoc("spec.md");

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload(doc)));

		WorkspaceStatePayload? state = WaitForState(candidate =>
			candidate.Recent.Count == 1 && candidate.Recent[0].Path == doc);
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

		WorkspaceStatePayload? state = WaitForState(candidate =>
			candidate.Recent.Count == 1
			&& candidate.Recent[0].Path == Path.GetFullPath(folder));
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

		WorkspaceStatePayload? state = WaitForState(candidate =>
			candidate.Recent.Count == 2
			&& candidate.Recent[0].Path == a
			&& candidate.Recent[1].Path == b);
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

		WorkspaceStatePayload? state = WaitForState(candidate =>
			candidate.Recent.Count == 20 && candidate.Recent[0].Label == "f24.md");
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
		WorkspaceStatePayload? added = WaitForState(candidate => candidate.Favorites.Count == 1);
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
		WorkspaceStatePayload? removed = WaitForState(candidate => candidate.Favorites.Count == 0);
		Assert.That(removed, Is.Not.Null);
		Assert.That(removed!.Favorites, Is.Empty);
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

	[Test]
	public void FavoriteToggle_PreservesRegisteredCloneAndBranchIdentity()
	{
		string clonePath = Path.Combine(_root, "spec-copy");
		Directory.CreateDirectory(clonePath);
		WorkspaceStore store = new(_wsPath);
		store.RegisterRepo(new RegisteredRepo(
			"octo/specs",
			"octo/specs",
			"https://github.com/octo/specs",
			"main",
			[
				new RegisteredClone(
					"spec-copy", clonePath, "draft",
					[new RegisteredBranch("main", RepositoryStatusPayload.Empty),
						new RegisteredBranch("draft", RepositoryStatusPayload.Empty)],
					RepositoryStatusPayload.Empty),
			]));
		using HostController controller = NewController();

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.WorkspaceFavorite,
			new WorkspaceFavoritePayload(
				clonePath, true, "clone", RepositoryId: "octo/specs", IsFolder: true)));
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.WorkspaceFavorite,
			new WorkspaceFavoritePayload(
				clonePath, true, "branch", RepositoryId: "octo/specs", Branch: "draft", IsFolder: true)));

		WorkspaceItem[] favorites = new WorkspaceStore(_wsPath).State().Favorites.ToArray();
		Assert.Multiple(() =>
		{
			Assert.That(favorites.Select(item => item.Kind), Is.EqualTo(["clone", "branch"]));
			Assert.That(favorites.Select(item => item.RepositoryId), Is.All.EqualTo("octo/specs"));
			Assert.That(favorites.Single(item => item.Kind == "branch").Branch, Is.EqualTo("draft"));
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

		WorkspaceStatePayload? state = WaitForState(candidate =>
			candidate.Repositories.Count == 1
			&& candidate.Repositories[0].Id == "octo/spec-repo");
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
		Assert.That(catalog.SecondReturned.Task.Wait(TimeSpan.FromSeconds(5)), Is.True,
			"The replacement registration did not start its metadata lookup.");
		Assert.That(SpinWait.SpinUntil(
			() => new WorkspaceStore(_wsPath).State().Repositories
				.SingleOrDefault()?.DefaultBranch == "trunk",
			TimeSpan.FromSeconds(5)), Is.True,
			"The replacement registration did not persist its metadata.");
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
			Assert.That(WaitFor(MessageKinds.Error)?.GetPayload<ErrorPayload>()?.Message, Is.Not.Null.And.Not.Empty);
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
		WorkspaceStatePayload? registered = WaitForState(state =>
			state.Repositories.Count == 1 && state.Repositories[0].Id == "owner/name");
		Assert.That(registered, Is.Not.Null);
		Assert.That(registered!.Repositories, Has.Count.EqualTo(1));

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.RepoUnregister, new UnregisterRepoPayload("owner/name")));

		WorkspaceStatePayload? unregistered = WaitForState(state => state.Repositories.Count == 0);
		Assert.That(unregistered, Is.Not.Null);
		Assert.That(unregistered!.Repositories, Is.Empty);
	}

	[Test]
	public void UnregisterRepo_ForgetsOnlyRegistrationAndFavorites()
	{
		DeletionTrapRepositoryManager manager = new();
		WorkspaceStore store = new(_wsPath);
		using HostController controller = NewController(repositoryInspector: manager, workspace: store);
		string clonePath = Path.Combine(_root, "spec-copy");
		string branchMarker = Path.Combine(clonePath, ".git", "refs", "heads", "draft");
		string sentinelPath = Path.Combine(clonePath, "keep-me.md");
		Directory.CreateDirectory(Path.GetDirectoryName(branchMarker)!);
		File.WriteAllText(branchMarker, "seeded-local-branch");
		File.WriteAllText(sentinelPath, "local work must survive forgetting the registration");
		store.RegisterRepo(new RegisteredRepo(
			"octo/specs",
			"octo/specs",
			"https://github.com/octo/specs",
			"main",
			[
				new RegisteredClone(
					"spec-copy",
					clonePath,
					"draft",
					[
						new RegisteredBranch("main", RepositoryStatusPayload.Empty),
						new RegisteredBranch("draft", RepositoryStatusPayload.Empty, CanDelete: true),
					],
					RepositoryStatusPayload.Empty),
			]));
		store.SetFavorite(new WorkspaceItem(
			"octo/specs", "octo/specs", true, "repository", "octo/specs"), true);
		store.SetFavorite(new WorkspaceItem(
			clonePath, "spec-copy", true, "clone", "octo/specs"), true);
		store.SetFavorite(new WorkspaceItem(
			clonePath, "draft", true, "branch", "octo/specs", "draft"), true);

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.RepoUnregister, new UnregisterRepoPayload("octo/specs")));

		WorkspaceStatePayload? state = WaitForState(candidate =>
			candidate.Repositories.Count == 0 && candidate.Favorites.Count == 0);
		Assert.That(state, Is.Not.Null);
		WorkspaceStatePayload persisted = new WorkspaceStore(_wsPath).State();
		Assert.Multiple(() =>
		{
			Assert.That(persisted.Repositories, Is.Empty);
			Assert.That(persisted.Favorites, Is.Empty);
			Assert.That(Directory.Exists(clonePath), Is.True);
			Assert.That(File.ReadAllText(branchMarker), Is.EqualTo("seeded-local-branch"));
			Assert.That(File.ReadAllText(sentinelPath),
				Is.EqualTo("local work must survive forgetting the registration"));
			Assert.That(manager.InvocationCount, Is.Zero,
				"forgetting a GitHub repository registration must not inspect or delete local work");
		});
	}

	[Test]
	public void UnregisterAfterDispose_DoesNotMutateOrPublishWorkspaceState()
	{
		HostController controller = NewController();
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.RepoRegister,
			new RegisterRepoPayload("octo/specs")));
		Assert.That(WaitForState(state =>
			state.Repositories.Count == 1 && state.Repositories[0].Id == "octo/specs"), Is.Not.Null);
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

	[Test]
	public void AutoSync_FetchesAndFastForwardsEveryRegisteredLocalCopy()
	{
		RecordingAutoSyncManager manager = new();
		WorkspaceStore store = new(_wsPath);
		string clonePath = Path.Combine(_root, "spec-copy");
		RegisterCopyForAutoSync(store, clonePath);
		using HostController controller = NewController(repositoryInspector: manager, workspace: store);

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.RepoAutoSync));

		Assert.That(SpinWait.SpinUntil(() => manager.CallCount >= 1, TimeSpan.FromSeconds(5)), Is.True);
		(string Path, string Url, string DefaultBranch, string? AccessToken) call = manager.Calls()[0];
		Assert.Multiple(() =>
		{
			Assert.That(call.Path, Is.EqualTo(clonePath));
			Assert.That(call.Url, Is.EqualTo("https://github.com/octo/specs"));
			// Auto-sync only ever asks to fast-forward the repository's own default (main) line.
			Assert.That(call.DefaultBranch, Is.EqualTo("main"));
			Assert.That(call.AccessToken, Is.EqualTo("gho_test"));
		});
	}

	[Test]
	public void AutoSync_ThrottlesRapidTriggersToASingleUpstreamSync()
	{
		RecordingAutoSyncManager manager = new();
		WorkspaceStore store = new(_wsPath);
		RegisterCopyForAutoSync(store, Path.Combine(_root, "spec-copy"));
		using HostController controller = NewController(repositoryInspector: manager, workspace: store);

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.RepoAutoSync));
		Assert.That(SpinWait.SpinUntil(() => manager.CallCount >= 1, TimeSpan.FromSeconds(5)), Is.True);

		// A second trigger inside the throttle window is coalesced away — a focus/poll burst cannot hammer upstream.
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.RepoAutoSync));
		Assert.That(SpinWait.SpinUntil(() => manager.CallCount >= 2, TimeSpan.FromMilliseconds(400)), Is.False);
		Assert.That(manager.CallCount, Is.EqualTo(1));
	}

	[Test]
	public void AutoSync_RunsAgainAfterTheThrottleWindowElapses()
	{
		RecordingAutoSyncManager manager = new();
		WorkspaceStore store = new(_wsPath);
		RegisterCopyForAutoSync(store, Path.Combine(_root, "spec-copy"));
		using HostController controller = NewController(repositoryInspector: manager, workspace: store);
		controller.AutoSyncMinInterval = TimeSpan.FromMilliseconds(50);

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.RepoAutoSync));
		Assert.That(SpinWait.SpinUntil(() => manager.CallCount >= 1, TimeSpan.FromSeconds(5)), Is.True);

		// Once the window has elapsed a later trigger is allowed through again.
		Thread.Sleep(200);
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.RepoAutoSync));
		Assert.That(SpinWait.SpinUntil(() => manager.CallCount >= 2, TimeSpan.FromSeconds(5)), Is.True);
	}

	[Test]
	public void AutoSync_WithoutAConnectedAccountDoesNothing()
	{
		RecordingAutoSyncManager manager = new();
		WorkspaceStore store = new(_wsPath);
		RegisterCopyForAutoSync(store, Path.Combine(_root, "spec-copy"));
		using HostController controller = NewController(
			auth: new FakeGitHubAuth(false), repositoryInspector: manager, workspace: store);

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.RepoAutoSync));

		Assert.That(SpinWait.SpinUntil(() => manager.CallCount >= 1, TimeSpan.FromMilliseconds(400)), Is.False);
		Assert.That(manager.CallCount, Is.Zero);
	}

	[Test]
	public void AutoSync_StopsAfterTheGitHubAccountIsDisconnected()
	{
		RecordingAutoSyncManager manager = new();
		FakeGitHubAuth auth = new(true);
		WorkspaceStore store = new(_wsPath);
		RegisterCopyForAutoSync(store, Path.Combine(_root, "spec-copy"));
		using HostController controller = NewController(
			auth: auth, repositoryInspector: manager, workspace: store);

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.RepoAutoSync));
		Assert.That(SpinWait.SpinUntil(() => manager.CallCount >= 1, TimeSpan.FromSeconds(5)), Is.True);

		// The account disconnects; clearing the throttle proves it is the missing account, not the window, that
		// stops the next trigger from doing any background work for a context that no longer exists.
		auth.SignOut();
		controller.AutoSyncMinInterval = TimeSpan.Zero;
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.RepoAutoSync));

		Assert.That(SpinWait.SpinUntil(() => manager.CallCount >= 2, TimeSpan.FromMilliseconds(400)), Is.False);
		Assert.That(manager.CallCount, Is.EqualTo(1));
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
