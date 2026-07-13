using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;
using SpecDesk.GitHub;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class HostControllerRepositoryBrowseTests
{
	private sealed class NoDialogs : IFileDialogs
	{
		public string? PickOpenFile() => null;
		public string? PickOpenFolder() => null;
		public string? PickSaveFile(string? suggestedPath) => null;
	}

	private sealed class Catalog : IGitHubRepositoryCatalog
	{
		public string? LastTreeBranch { get; private set; }

		public Task<GitHubRepositoryMetadata> GetMetadataAsync(
			string owner, string name, string accessToken, CancellationToken cancellationToken = default) =>
			Task.FromResult(new GitHubRepositoryMetadata("master"));

		public Task<IReadOnlyList<GitHubRepositoryEntry>> GetTreeAsync(
			string owner, string name, string branch, string accessToken,
			CancellationToken cancellationToken = default)
		{
			LastTreeBranch = branch;
			return Task.FromResult<IReadOnlyList<GitHubRepositoryEntry>>([
				new("docs", true, 0),
				new("docs/config.json", false, 12),
				new("README.md", false, 8),
			]);
		}

		public Task<string> GetFileAsync(
			string owner, string name, string branch, string path, string accessToken,
			CancellationToken cancellationToken = default) =>
			Task.FromResult(path.EndsWith(".json", StringComparison.Ordinal) ? "{\"ok\":true}" : "# Remote");
	}

	private sealed class RacingCatalog : IGitHubRepositoryCatalog
	{
		private int _treeCalls;
		public TaskCompletionSource FirstTreeStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseFirstTree { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<GitHubRepositoryMetadata> GetMetadataAsync(
			string owner, string name, string accessToken, CancellationToken cancellationToken = default) =>
			Task.FromResult(new GitHubRepositoryMetadata("main"));

		public async Task<IReadOnlyList<GitHubRepositoryEntry>> GetTreeAsync(
			string owner, string name, string branch, string accessToken,
			CancellationToken cancellationToken = default)
		{
			int call = Interlocked.Increment(ref _treeCalls);
			if (call == 1)
			{
				FirstTreeStarted.SetResult();
				await ReleaseFirstTree.Task;
				return [new GitHubRepositoryEntry("old.md", false, 1)];
			}
			return [new GitHubRepositoryEntry("new.md", false, 1)];
		}

		public Task<string> GetFileAsync(
			string owner, string name, string branch, string path, string accessToken,
			CancellationToken cancellationToken = default) => Task.FromResult("text");
	}

	private sealed class MetadataRacingCatalog : IGitHubRepositoryCatalog
	{
		public TaskCompletionSource MetadataStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseMetadata { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async Task<GitHubRepositoryMetadata> GetMetadataAsync(
			string owner, string name, string accessToken, CancellationToken cancellationToken = default)
		{
			MetadataStarted.SetResult();
			await ReleaseMetadata.Task;
			return new GitHubRepositoryMetadata("trunk");
		}

		public Task<IReadOnlyList<GitHubRepositoryEntry>> GetTreeAsync(
			string owner, string name, string branch, string accessToken,
			CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<GitHubRepositoryEntry>>([new("README.md", false, 1)]);

		public Task<string> GetFileAsync(
			string owner, string name, string branch, string path, string accessToken,
			CancellationToken cancellationToken = default) => Task.FromResult("text");
	}

	private sealed class BlockingSignInAuth : IGitHubAuth
	{
		private bool _signedIn;
		public TaskCompletionSource AuthorizationStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseAuthorization { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<DeviceCodePrompt> StartSignInAsync(CancellationToken cancellationToken = default) =>
			Task.FromResult(new DeviceCodePrompt(
				"CODE", new Uri("https://github.com/login/device"),
				TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1), "device"));

		public async Task<SignInResult> AwaitAuthorizationAsync(
			DeviceCodePrompt prompt, CancellationToken cancellationToken = default)
		{
			AuthorizationStarted.SetResult();
			await ReleaseAuthorization.Task;
			_signedIn = true;
			return SignInResult.Authorized("octocat");
		}

		public bool IsSignedIn() => _signedIn;
		public string? SignedInLogin() => _signedIn ? "octocat" : null;
		public Task<T> WithAccessTokenAsync<T>(
			Func<string, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default) =>
			use("token", cancellationToken);
		public void SignOut() => _signedIn = false;
	}

	private sealed class CountingCatalog : IGitHubRepositoryCatalog
	{
		public int TreeCalls { get; private set; }
		public int FileCalls { get; private set; }
		public Task<GitHubRepositoryMetadata> GetMetadataAsync(
			string owner, string name, string accessToken, CancellationToken cancellationToken = default) =>
			Task.FromResult(new GitHubRepositoryMetadata("main"));
		public Task<IReadOnlyList<GitHubRepositoryEntry>> GetTreeAsync(
			string owner, string name, string branch, string accessToken,
			CancellationToken cancellationToken = default)
		{
			TreeCalls++;
			return Task.FromResult<IReadOnlyList<GitHubRepositoryEntry>>([new("remote.md", false, 1)]);
		}
		public Task<string> GetFileAsync(
			string owner, string name, string branch, string path, string accessToken,
			CancellationToken cancellationToken = default)
		{
			FileCalls++;
			return Task.FromResult("text");
		}
	}

	private sealed class IsolationCatalog : IGitHubRepositoryCatalog
	{
		public TaskCompletionSource TreeStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseTree { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource FileStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseFile { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<GitHubRepositoryMetadata> GetMetadataAsync(
			string owner, string name, string accessToken, CancellationToken cancellationToken = default) =>
			Task.FromResult(new GitHubRepositoryMetadata("main"));

		public async Task<IReadOnlyList<GitHubRepositoryEntry>> GetTreeAsync(
			string owner, string name, string branch, string accessToken,
			CancellationToken cancellationToken = default)
		{
			TreeStarted.SetResult();
			await ReleaseTree.Task;
			return [new GitHubRepositoryEntry("A.md", false, 1)];
		}

		public async Task<string> GetFileAsync(
			string owner, string name, string branch, string path, string accessToken,
			CancellationToken cancellationToken = default)
		{
			FileStarted.SetResult();
			await ReleaseFile.Task;
			return "# A";
		}
	}

	[Test]
	public void RemoteTree_PreservesCaseDistinctGitHubPaths()
	{
		TreePayload tree = HostController.BuildRemoteTree(
			"octo",
			"specs",
			"main",
			[
				new GitHubRepositoryEntry("Foo.md", false, 1),
				new GitHubRepositoryEntry("foo.md", false, 1),
				new GitHubRepositoryEntry("Docs/Guide.md", false, 1),
				new GitHubRepositoryEntry("docs/guide.md", false, 1),
			]);

		Assert.That(tree.Nodes, Has.Count.EqualTo(4));
		string[] expected = ["Docs", "docs", "Foo.md", "foo.md"];
		Assert.That(tree.Nodes.Select(node => node.Name),
			Is.EquivalentTo(expected));
		Assert.That(tree.Nodes.Select(node => node.Path).Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(4));
	}

	[Test]
	public void RemoteTree_AcceptsTheMaximumSupportedDepth()
	{
		string path = string.Join('/', Enumerable.Range(0, 64).Select(index => $"d{index}"));

		TreePayload tree = HostController.BuildRemoteTree(
			"octo", "specs", "main", [new GitHubRepositoryEntry(path, false, 1)]);

		Assert.That(tree.Nodes, Has.Count.EqualTo(1));
	}

	[Test]
	public void RemoteTree_RejectsExcessiveDepthPathLengthAndNodeExpansion()
	{
		string tooDeep = string.Join('/', Enumerable.Range(0, 65).Select(index => $"d{index}"));
		string tooLong = new('a', 4097);
		GitHubRepositoryEntry[] expanding = Enumerable.Range(0, 5000)
			.Select(index => new GitHubRepositoryEntry($"r{index}/a/b/c/file.md", false, 1))
			.ToArray();

		Assert.Multiple(() =>
		{
			Assert.Throws<InvalidDataException>(() => HostController.BuildRemoteTree(
				"octo", "specs", "main", [new GitHubRepositoryEntry(tooDeep, false, 1)]));
			Assert.Throws<InvalidDataException>(() => HostController.BuildRemoteTree(
				"octo", "specs", "main", [new GitHubRepositoryEntry(tooLong, false, 1)]));
			Assert.Throws<InvalidDataException>(() => HostController.BuildRemoteTree(
				"octo", "specs", "main", expanding));
		});
	}

	[Test]
	public void RegisteredRemoteRepository_BrowsesAllFilesAndPreviewsReadOnly()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-remote-browse-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", "master", []));
			List<string> sent = [];
			object gate = new();
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json =>
				{
					lock (gate)
					{
						sent.Add(json);
					}
				},
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: new FakeGitHubAuth(true), workspace: store, repositoryCatalog: new Catalog());

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoBrowse, new RepoBrowsePayload("octo/specs")));
			TreePayload tree = WaitFor<TreePayload>(sent, gate, MessageKinds.Tree)!;
			string[] expectedTree = ["docs", "README.md"];
			Assert.That(tree.Nodes.Select(node => node.Name), Is.EqualTo(expectedTree));
			TreeNode json = tree.Nodes.Single(node => node.Name == "docs").Children.Single();

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.DocOpen, new DocOpenPayload(json.Path)));
			DocLoadedPayload loaded = WaitFor<DocLoadedPayload>(sent, gate, MessageKinds.DocLoaded)!;
			Assert.Multiple(() =>
			{
				Assert.That(loaded.Text, Is.EqualTo("{\"ok\":true}"));
				Assert.That(loaded.ReadOnly, Is.True);
				Assert.That(loaded.Path, Does.StartWith("github://octo/specs/"));
				Assert.That(loaded.Repository, Is.EqualTo("octo/specs"));
				Assert.That(loaded.Branch, Is.EqualTo("master"));
				Assert.That(loaded.RepositoryPath, Is.EqualTo("docs/config.json"));
			});

			lock (gate)
			{
				sent.Clear();
			}
			string[] mutations =
			[
				MessageKinds.EditorChanged,
				MessageKinds.DocSave,
				MessageKinds.DocEdit,
				MessageKinds.DocSaveVersion,
				MessageKinds.DocSendForReview,
				MessageKinds.DocUpdateReview,
				MessageKinds.DocDiscard,
				MessageKinds.ImagePaste,
			];
			foreach (string mutation in mutations)
			{
				controller.OnMessage(IpcSerializer.SerializeEvent(mutation));
			}
			lock (gate)
			{
				Assert.That(sent.Count(json => IpcSerializer.TryDeserialize(json)?.Kind == MessageKinds.Error),
					Is.EqualTo(mutations.Length));
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void RemoteFavorite_ReopensItsStoredBranchInsteadOfTheRepositoryDefault()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-favorite-branch-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", "main", []));
			Catalog catalog = new();
			List<string> sent = [];
			object gate = new();
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json =>
				{
					lock (gate)
					{
						sent.Add(json);
					}
				},
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: new FakeGitHubAuth(true), workspace: store, repositoryCatalog: catalog);

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoBrowse, new RepoBrowsePayload("octo/specs", "feature/Case-Sensitive")));
			_ = WaitFor<TreePayload>(sent, gate, MessageKinds.Tree);

			Assert.That(catalog.LastTreeBranch, Is.EqualTo("feature/Case-Sensitive"));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void AStaleRemoteTreeCannotOverwriteNewerNavigation()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-remote-race-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", "main", []));
			RacingCatalog catalog = new();
			List<string> sent = [];
			object gate = new();
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json =>
				{
					lock (gate)
					{
						sent.Add(json);
					}
				},
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: new FakeGitHubAuth(true), workspace: store, repositoryCatalog: catalog);

			string browse = IpcSerializer.SerializeEvent(
				MessageKinds.RepoBrowse, new RepoBrowsePayload("octo/specs"));
			controller.OnMessage(browse);
			Assert.That(catalog.FirstTreeStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
			controller.OnMessage(browse);
			TreePayload latest = WaitFor<TreePayload>(sent, gate, MessageKinds.Tree)!;
			Assert.That(latest.Nodes.Single().Name, Is.EqualTo("new.md"));
			catalog.ReleaseFirstTree.SetResult();
			Thread.Sleep(100);

			lock (gate)
			{
				TreePayload[] trees = sent
					.Select(IpcSerializer.TryDeserialize)
					.Where(message => message?.Kind == MessageKinds.Tree)
					.Select(message => message!.GetPayload<TreePayload>()!)
					.ToArray();
				Assert.That(trees, Has.Length.EqualTo(1));
				Assert.That(trees[0].Nodes.Single().Name, Is.EqualTo("new.md"));
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void UnregisterDuringDefaultBranchLookup_CannotResurrectOrPublishTheRepository()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-remote-metadata-race-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", string.Empty, []));
			MetadataRacingCatalog catalog = new();
			List<string> sent = [];
			object gate = new();
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json =>
				{
					lock (gate)
					{
						sent.Add(json);
					}
				},
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: new FakeGitHubAuth(true), workspace: store, repositoryCatalog: catalog);

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoBrowse, new RepoBrowsePayload("octo/specs")));
			Assert.That(catalog.MetadataStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoUnregister, new UnregisterRepoPayload("octo/specs")));
			catalog.ReleaseMetadata.SetResult();
			Thread.Sleep(100);

			Assert.That(new WorkspaceStore(Path.Combine(root, "workspace.json")).State().Repositories, Is.Empty);
			lock (gate)
			{
				Assert.That(sent.Any(json => IpcSerializer.TryDeserialize(json)?.Kind is MessageKinds.Tree or MessageKinds.Error),
					Is.False);
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[TestCase(false)]
	[TestCase(true)]
	public void UnregisterDuringTreeLookup_InvalidatesTheOldRegistration(bool reRegister)
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-remote-tree-unregister-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			RegisteredRepo repo = new(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", "main", []);
			store.RegisterRepo(repo);
			RacingCatalog catalog = new();
			List<string> sent = [];
			object gate = new();
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json => { lock (gate) { sent.Add(json); } },
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: new FakeGitHubAuth(true), workspace: store, repositoryCatalog: catalog);

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoBrowse, new RepoBrowsePayload(repo.Id)));
			Assert.That(catalog.FirstTreeStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoUnregister, new UnregisterRepoPayload(repo.Id)));
			if (reRegister)
			{
				store.RegisterRepo(repo);
			}
			lock (gate)
			{
				sent.Clear();
			}
			catalog.ReleaseFirstTree.SetResult();
			Thread.Sleep(100);

			lock (gate)
			{
				Assert.That(sent, Is.Empty);
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void SameRegistrationCloneUpdate_DoesNotInvalidateRemoteTreeLookup()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-remote-tree-update-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			RegisteredRepo repo = new(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", "main", []);
			store.RegisterRepo(repo);
			RacingCatalog catalog = new();
			List<string> sent = [];
			object gate = new();
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json => { lock (gate) { sent.Add(json); } },
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: new FakeGitHubAuth(true), workspace: store, repositoryCatalog: catalog);

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoBrowse, new RepoBrowsePayload(repo.Id)));
			Assert.That(catalog.FirstTreeStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
			store.UpsertRepoClone(repo, new RegisteredClone("copy", Path.Combine(root, "copy"), []), "main");
			catalog.ReleaseFirstTree.SetResult();

			TreePayload tree = WaitFor<TreePayload>(sent, gate, MessageKinds.Tree)!;
			Assert.That(tree.Nodes.Single().Name, Is.EqualTo("old.md"));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void LocalNavigationRetiresABrowseQueuedBehindAuthorization()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-pending-remote-browse-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			File.WriteAllText(Path.Combine(root, "local.md"), "# Local");
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", "main", []));
			BlockingSignInAuth auth = new();
			CountingCatalog catalog = new();
			List<string> sent = [];
			object gate = new();
			HostController? controller = null;
			void Send(string json)
			{
				lock (gate)
				{
					sent.Add(json);
				}
				IpcMessage? message = IpcSerializer.TryDeserialize(json);
				if (message?.Kind == MessageKinds.GitHubAccount
					&& message.GetPayload<GitHubAccountPayload>()?.SignedIn == true)
				{
					// PublishTerminalIfCurrent has already taken the pending actions before SendAccount.
					// Navigate here so this runs in the exact Take -> navigation -> Resume window.
					controller!.OnMessage(IpcSerializer.SerializeEvent(
						MessageKinds.FolderOpen, new FolderOpenPayload(root)));
				}
			}
			controller = new HostController(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				Send,
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: auth, workspace: store, repositoryCatalog: catalog);
			using (controller)
			{

				controller.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.RepoBrowse, new RepoBrowsePayload("octo/specs")));
				Assert.That(auth.AuthorizationStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
				auth.ReleaseAuthorization.SetResult();
				Thread.Sleep(100);

				Assert.That(catalog.TreeCalls, Is.Zero);
				lock (gate)
				{
					TreePayload[] trees = sent
						.Select(IpcSerializer.TryDeserialize)
						.Where(message => message?.Kind == MessageKinds.Tree)
						.Select(message => message!.GetPayload<TreePayload>()!)
						.ToArray();
					Assert.That(trees, Has.Length.EqualTo(1));
					Assert.That(trees[0].Root, Is.EqualTo(Path.GetFullPath(root)));
				}
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void SignedOutRemoteFileFavorite_AuthorizesThenResumesTheExactFile()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-pending-remote-file-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", "main", []));
			BlockingSignInAuth auth = new();
			CountingCatalog catalog = new();
			List<string> sent = [];
			object gate = new();
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json => { lock (gate) { sent.Add(json); } },
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: auth, workspace: store, repositoryCatalog: catalog);
			string wirePath = "github://octo/specs/feature%2FDocs/Docs%2FGuide.md";

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.DocOpen, new DocOpenPayload(wirePath)));
			Assert.That(auth.AuthorizationStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
			Assert.That(catalog.FileCalls, Is.Zero);
			auth.ReleaseAuthorization.SetResult();
			DocLoadedPayload loaded = WaitFor<DocLoadedPayload>(sent, gate, MessageKinds.DocLoaded)!;

			Assert.Multiple(() =>
			{
				Assert.That(catalog.FileCalls, Is.EqualTo(1));
				Assert.That(loaded.Path, Is.EqualTo(wirePath));
				Assert.That(loaded.Repository, Is.EqualTo("octo/specs"));
				Assert.That(loaded.Branch, Is.EqualTo("feature/Docs"));
				Assert.That(loaded.RepositoryPath, Is.EqualTo("Docs/Guide.md"));
			});
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void LocalNavigationRetiresARemoteFileQueuedBehindAuthorization()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-pending-file-navigation-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			File.WriteAllText(Path.Combine(root, "local.md"), "# Local");
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", "main", []));
			BlockingSignInAuth auth = new();
			CountingCatalog catalog = new();
			List<string> sent = [];
			object gate = new();
			HostController? controller = null;
			void Send(string json)
			{
				lock (gate)
				{
					sent.Add(json);
				}
				IpcMessage? message = IpcSerializer.TryDeserialize(json);
				if (message?.Kind == MessageKinds.GitHubAccount
					&& message.GetPayload<GitHubAccountPayload>()?.SignedIn == true)
				{
					controller!.OnMessage(IpcSerializer.SerializeEvent(
						MessageKinds.FolderOpen, new FolderOpenPayload(root)));
				}
			}
			controller = new HostController(
				(_, _) => new Renderer.RenderResult(string.Empty, []), Send,
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: auth, workspace: store, repositoryCatalog: catalog);
			using (controller)
			{
				controller.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.DocOpen,
					new DocOpenPayload("github://octo/specs/main/Docs%2FGuide.md")));
				Assert.That(auth.AuthorizationStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
				auth.ReleaseAuthorization.SetResult();
				Thread.Sleep(100);

				Assert.That(catalog.FileCalls, Is.Zero);
				lock (gate)
				{
					Assert.That(sent.Any(json =>
						IpcSerializer.TryDeserialize(json)?.Kind == MessageKinds.DocLoaded), Is.False);
				}
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[TestCase(false)]
	[TestCase(true)]
	public void UnregisterInTerminalReplayWindow_InvalidatesOnlyTheMatchingBrowseIntent(bool matching)
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-pending-browse-unregister-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			RegisteredRepo repoA = new(
				"octo/a", "octo/a", "https://github.com/octo/a", "main", []);
			RegisteredRepo repoB = new(
				"octo/b", "octo/b", "https://github.com/octo/b", "main", []);
			store.RegisterRepo(repoA);
			store.RegisterRepo(repoB);
			BlockingSignInAuth auth = new();
			CountingCatalog catalog = new();
			List<string> sent = [];
			object gate = new();
			HostController? controller = null;
			void Send(string json)
			{
				lock (gate)
				{
					sent.Add(json);
				}
				IpcMessage? message = IpcSerializer.TryDeserialize(json);
				if (message?.Kind == MessageKinds.GitHubAccount
					&& message.GetPayload<GitHubAccountPayload>()?.SignedIn == true)
				{
					string removed = matching ? repoA.Id : repoB.Id;
					controller!.OnMessage(IpcSerializer.SerializeEvent(
						MessageKinds.RepoUnregister, new UnregisterRepoPayload(removed)));
					if (matching)
					{
						store.RegisterRepo(repoA);
					}
				}
			}
			controller = new HostController(
				(_, _) => new Renderer.RenderResult(string.Empty, []), Send,
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: auth, workspace: store, repositoryCatalog: catalog);
			using (controller)
			{
				controller.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.RepoBrowse, new RepoBrowsePayload(repoA.Id)));
				Assert.That(auth.AuthorizationStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
				auth.ReleaseAuthorization.SetResult();
				Thread.Sleep(100);

				Assert.That(catalog.TreeCalls, Is.EqualTo(matching ? 0 : 1));
				if (!matching)
				{
					TreePayload tree = WaitFor<TreePayload>(sent, gate, MessageKinds.Tree)!;
					Assert.That(tree.Nodes.Single().Name, Is.EqualTo("remote.md"));
				}
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[TestCase(false)]
	[TestCase(true)]
	public void UnregisteringAnotherRepository_DoesNotCancelRepoATreeOrFile(bool file)
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-remote-isolation-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/a", "octo/a", "https://github.com/octo/a", "main", []));
			store.RegisterRepo(new RegisteredRepo(
				"octo/b", "octo/b", "https://github.com/octo/b", "main", []));
			IsolationCatalog catalog = new();
			List<string> sent = [];
			object gate = new();
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json => { lock (gate) { sent.Add(json); } },
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: new FakeGitHubAuth(true), workspace: store, repositoryCatalog: catalog);

			if (file)
			{
				controller.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.DocOpen,
					new DocOpenPayload("github://octo/a/main/A.md")));
				Assert.That(catalog.FileStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
			}
			else
			{
				controller.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.RepoBrowse, new RepoBrowsePayload("octo/a")));
				Assert.That(catalog.TreeStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
			}

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoUnregister, new UnregisterRepoPayload("octo/b")));
			catalog.ReleaseTree.TrySetResult();
			catalog.ReleaseFile.TrySetResult();

			if (file)
			{
				DocLoadedPayload loaded = WaitFor<DocLoadedPayload>(sent, gate, MessageKinds.DocLoaded)!;
				Assert.That(loaded.Repository, Is.EqualTo("octo/a"));
			}
			else
			{
				TreePayload tree = WaitFor<TreePayload>(sent, gate, MessageKinds.Tree)!;
				Assert.That(tree.Nodes.Single().Name, Is.EqualTo("A.md"));
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private static T? WaitFor<T>(List<string> sent, object gate, string kind)
	{
		for (int attempt = 0; attempt < 200; attempt++)
		{
			lock (gate)
			{
				foreach (string json in sent)
				{
					IpcMessage? message = IpcSerializer.TryDeserialize(json);
					if (message?.Kind == kind)
					{
						return message.GetPayload<T>();
					}
				}
			}
			Thread.Sleep(20);
		}
		return default;
	}
}
