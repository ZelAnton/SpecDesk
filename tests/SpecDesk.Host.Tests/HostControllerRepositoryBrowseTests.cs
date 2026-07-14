using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;
using SpecDesk.Git;
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

	private sealed class AccountBoundaryLevelCatalog : IGitHubRepositoryCatalog
	{
		public TaskCompletionSource LevelStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseLevel { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<GitHubRepositoryMetadata> GetMetadataAsync(
			string owner, string name, string accessToken, CancellationToken cancellationToken = default) =>
			Task.FromResult(new GitHubRepositoryMetadata("main"));

		public Task<IReadOnlyList<GitHubRepositoryEntry>> GetTreeAsync(
			string owner, string name, string branch, string accessToken,
			CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<GitHubRepositoryEntry>>([new("private", true, 0)]);

		public async Task<IReadOnlyList<GitHubRepositoryEntry>> GetTreeLevelAsync(
			string owner, string name, string branch, string path, string accessToken,
			CancellationToken cancellationToken = default)
		{
			if (path.Length == 0)
			{
				return [new GitHubRepositoryEntry("private", true, 0)];
			}
			LevelStarted.SetResult();
			await ReleaseLevel.Task;
			return [new GitHubRepositoryEntry("private/secret.md", false, 1)];
		}

		public Task<string> GetFileAsync(
			string owner, string name, string branch, string path, string accessToken,
			CancellationToken cancellationToken = default) => Task.FromResult("# Secret");
	}

	private sealed class FailingLevelCatalog : IGitHubRepositoryCatalog
	{
		public Task<GitHubRepositoryMetadata> GetMetadataAsync(
			string owner, string name, string accessToken, CancellationToken cancellationToken = default) =>
			Task.FromResult(new GitHubRepositoryMetadata("main"));

		public Task<IReadOnlyList<GitHubRepositoryEntry>> GetTreeAsync(
			string owner, string name, string branch, string accessToken,
			CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<GitHubRepositoryEntry>>([new("large", true, 0)]);

		public Task<IReadOnlyList<GitHubRepositoryEntry>> GetTreeLevelAsync(
			string owner, string name, string branch, string path, string accessToken,
			CancellationToken cancellationToken = default) =>
			path.Length == 0
				? GetTreeAsync(owner, name, branch, accessToken, cancellationToken)
				: Task.FromException<IReadOnlyList<GitHubRepositoryEntry>>(
					new InvalidDataException("truncated"));

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

	private sealed class NoopCloner : IRepositoryCloner
	{
		public bool IsCloned(string destinationPath) => false;
		public bool IsCloneOf(string destinationPath, string url) => false;
		public bool IsCloneOfAtBranch(
			string destinationPath, string url, string? expectedCurrentBranch) => false;
		public string CloneOrReuse(
			string url, string destinationPath, string? accessToken, CancellationToken ct) =>
			throw new InvalidOperationException("Authorization should remain pending.");
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

	private sealed class SwitchingAccountAuth : IGitHubAuth
	{
		private string _login = "old-user";

		public Task<DeviceCodePrompt> StartSignInAsync(CancellationToken cancellationToken = default) =>
			Task.FromResult(new DeviceCodePrompt(
				"CODE", new Uri("https://github.com/login/device"),
				TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1), "device"));

		public Task<SignInResult> AwaitAuthorizationAsync(
			DeviceCodePrompt prompt, CancellationToken cancellationToken = default)
		{
			_login = "new-user";
			return Task.FromResult(SignInResult.Authorized(_login));
		}

		public bool IsSignedIn() => true;
		public string? SignedInLogin() => _login;
		public Task<T> WithAccessTokenAsync<T>(
			Func<string, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default) =>
			use(_login == "old-user" ? "old-token" : "new-token", cancellationToken);
		public void SignOut() { }
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
		public TaskCompletionSource FileReturned { get; } =
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
			FileReturned.SetResult();
			return "# A";
		}
	}

	private sealed class FailingTreeCatalog : IGitHubRepositoryCatalog
	{
		public Task<GitHubRepositoryMetadata> GetMetadataAsync(
			string owner, string name, string accessToken, CancellationToken cancellationToken = default) =>
			Task.FromResult(new GitHubRepositoryMetadata("main"));

		public Task<IReadOnlyList<GitHubRepositoryEntry>> GetTreeAsync(
			string owner, string name, string branch, string accessToken,
			CancellationToken cancellationToken = default) =>
			Task.FromException<IReadOnlyList<GitHubRepositoryEntry>>(
				new HttpRequestException("Remote tree failed."));

		public Task<string> GetFileAsync(
			string owner, string name, string branch, string path, string accessToken,
			CancellationToken cancellationToken = default) =>
			Task.FromResult("text");
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
			TreePayload tree = WaitForTree(sent, gate, candidate => candidate.Nodes.Count > 0)!;
			string[] expectedTree = ["docs", "README.md"];
			Assert.That(tree.Nodes.Select(node => node.Name), Is.EqualTo(expectedTree));
			WorkspaceContextPayload context = WaitFor<WorkspaceContextPayload>(
				sent, gate, MessageKinds.WorkspaceContext)!;
			Assert.Multiple(() =>
			{
				Assert.That(context.Repository, Is.EqualTo("octo/specs"));
				Assert.That(context.RepositoryRoot, Is.Null);
				Assert.That(context.Branch, Is.EqualTo("master"));
				Assert.That(context.Path, Is.Empty);
			});
			TreeNode docs = tree.Nodes.Single(node => node.Name == "docs");
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.TreeRequest, new TreeRequestPayload(docs.Path, RequestId: 51)));
			TreePayload docsLevel = WaitForTree(
				sent, gate, candidate => candidate.RequestId == 51)!;
			TreeNode json = docsLevel.Nodes.Single();

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
			bool allErrorsPublished = SpinWait.SpinUntil(
				() =>
				{
					lock (gate)
					{
						return sent.Count(json =>
							IpcSerializer.TryDeserialize(json)?.Kind == MessageKinds.Error) == mutations.Length;
					}
				},
				TimeSpan.FromSeconds(2));
			Assert.That(allErrorsPublished, Is.True);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void RemoteBrowse_AccountSwitchSuppressesOldPublishedTreeFromCancellationIgnoringTransport()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-remote-browse-account-switch-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		IsolationCatalog catalog = new();
		try
		{
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", "main", []));
			List<string> sent = [];
			object gate = new();
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json => { lock (gate) { sent.Add(json); } },
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: new SwitchingAccountAuth(), workspace: store, repositoryCatalog: catalog);

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoBrowse, new RepoBrowsePayload("octo/specs")));
			Assert.That(catalog.TreeStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignIn));
			Assert.That(SpinWait.SpinUntil(() =>
			{
				lock (gate)
				{
					return sent.Select(IpcSerializer.TryDeserialize).Any(message =>
						message?.Kind == MessageKinds.GitHubAccount
						&& message.GetPayload<GitHubAccountPayload>()?.Login == "new-user");
				}
			}, TimeSpan.FromSeconds(2)), Is.True);

			catalog.ReleaseTree.SetResult();
			Thread.Sleep(250);
			lock (gate)
			{
				IpcMessage[] messages = sent.Select(IpcSerializer.TryDeserialize)
					.Where(message => message is not null).Select(message => message!).ToArray();
				int accountIndex = Array.FindLastIndex(messages, message =>
					message.Kind == MessageKinds.GitHubAccount
					&& message.GetPayload<GitHubAccountPayload>()?.Login == "new-user");
				Assert.That(messages.Skip(accountIndex + 1).Any(message =>
					message.Kind == MessageKinds.Tree
					&& message.GetPayload<TreePayload>()?.Nodes.Count > 0), Is.False,
					"the previous account's private root tree crossed the identity boundary");
			}
		}
		finally
		{
			catalog.ReleaseTree.TrySetResult();
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void RemoteLevel_FailureCompletesAsRetryableAndSurfacesAnError()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-remote-level-failure-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", "main", []));
			List<string> sent = [];
			object gate = new();
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json => { lock (gate) { sent.Add(json); } },
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: new FakeGitHubAuth(true), workspace: store, repositoryCatalog: new FailingLevelCatalog());

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoBrowse, new RepoBrowsePayload("octo/specs")));
			TreePayload rootTree = WaitForTree(sent, gate, tree => tree.Nodes.Count > 0)!;
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.TreeRequest, new TreeRequestPayload(rootTree.Nodes[0].Path, RequestId: 76)));

			TreePayload failure = WaitForTree(sent, gate, tree => tree.RequestId == 76)!;
			Assert.Multiple(() =>
			{
				Assert.That(failure.Error, Is.EqualTo("Could not read that folder. Try again."));
				Assert.That(failure.Nodes, Is.Empty);
				Assert.That(WaitFor<ErrorPayload>(sent, gate, MessageKinds.Error)?.Message,
					Is.EqualTo("Could not read that folder. Try again."));
			});
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void RemoteTree_SignOutClearsAlreadyPublishedPrivateTreeAndFolderContext()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-published-remote-tree-account-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", "main", []));
			List<string> sent = [];
			object gate = new();
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json => { lock (gate) { sent.Add(json); } },
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: new FakeGitHubAuth(true), workspace: store, repositoryCatalog: new CountingCatalog());

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoBrowse, new RepoBrowsePayload("octo/specs")));
			Assert.That(WaitForTree(sent, gate, tree => tree.Nodes.Count > 0), Is.Not.Null);

			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignOut));

			Assert.That(WaitForTree(sent, gate, tree => tree.Root.Length == 0 && tree.Nodes.Count == 0), Is.Not.Null);
			Assert.That(
				WaitFor<GitHubAccountPayload>(sent, gate, MessageKinds.GitHubAccount)?.SignedIn,
				Is.False);
			lock (gate)
			{
				IpcMessage[] messages = sent.Select(IpcSerializer.TryDeserialize)
					.Where(message => message is not null).Select(message => message!).ToArray();
				int clearIndex = Array.FindLastIndex(messages, message =>
					message.Kind == MessageKinds.Tree
					&& message.GetPayload<TreePayload>() is { Root.Length: 0, Nodes.Count: 0 });
				int accountIndex = Array.FindLastIndex(messages, message =>
					message.Kind == MessageKinds.GitHubAccount
					&& message.GetPayload<GitHubAccountPayload>()?.SignedIn == false);
				Assert.That(clearIndex, Is.GreaterThanOrEqualTo(0));
				Assert.That(accountIndex, Is.GreaterThan(clearIndex),
					"the private Folder tree must disappear before the signed-out account state is published");
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[TestCase(false)]
	[TestCase(true)]
	public void FailedRemoteRoot_AccountBoundaryClearsItsIdentityAndError(bool switchAccount)
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-failed-remote-root-account-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", "main", []));
			List<string> sent = [];
			object gate = new();
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json => { lock (gate) { sent.Add(json); } },
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: switchAccount ? new SwitchingAccountAuth() : new FakeGitHubAuth(true),
				workspace: store, repositoryCatalog: new FailingTreeCatalog());

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoBrowse, new RepoBrowsePayload("octo/specs")));
			TreePayload failed = WaitForTree(sent, gate, tree => tree.Error is not null)!;
			Assert.That(failed.Remote, Is.True);
			lock (gate)
			{
				sent.Clear();
			}

			controller.OnMessage(IpcSerializer.SerializeEvent(
				switchAccount ? MessageKinds.GitHubSignIn : MessageKinds.GitHubSignOut));

			TreePayload? cleared = WaitForTree(sent, gate, tree =>
				tree.Root.Length == 0 && tree.Nodes.Count == 0 && tree.Error is null);
			Assert.Multiple(() =>
			{
				Assert.That(cleared, Is.Not.Null);
				Assert.That(cleared!.Remote, Is.Null);
			});
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void RemoteFile_SignOutClearsAlreadyPublishedPrivateDocumentAndContext()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-published-remote-file-account-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", "main", []));
			List<string> sent = [];
			object gate = new();
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json => { lock (gate) { sent.Add(json); } },
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: new FakeGitHubAuth(true), workspace: store, repositoryCatalog: new CountingCatalog());

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.DocOpen,
				new DocOpenPayload("github://octo/specs/main/private.md", 781)));
			Assert.That(WaitFor<DocLoadedPayload>(sent, gate, MessageKinds.DocLoaded)?.Path,
				Is.EqualTo("github://octo/specs/main/private.md"));

			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignOut));

			Assert.That(SpinWait.SpinUntil(() =>
			{
				lock (gate)
				{
					return sent.Select(IpcSerializer.TryDeserialize).Any(message =>
						message?.Kind == MessageKinds.DocLoaded
						&& message.GetPayload<DocLoadedPayload>()?.Path.Length == 0);
				}
			}, TimeSpan.FromSeconds(2)), Is.True);
			lock (gate)
			{
				IpcMessage[] messages = sent.Select(IpcSerializer.TryDeserialize)
					.Where(message => message is not null).Select(message => message!).ToArray();
				int clearIndex = Array.FindLastIndex(messages, message =>
					message.Kind == MessageKinds.DocLoaded
					&& message.GetPayload<DocLoadedPayload>()?.Path.Length == 0);
				int contextIndex = Array.FindLastIndex(messages, message =>
					message.Kind == MessageKinds.WorkspaceContext
					&& message.GetPayload<WorkspaceContextPayload>() is { Repository: null, Path.Length: 0 });
				int accountIndex = Array.FindLastIndex(messages, message =>
					message.Kind == MessageKinds.GitHubAccount
					&& message.GetPayload<GitHubAccountPayload>()?.SignedIn == false);
				Assert.That(clearIndex, Is.GreaterThanOrEqualTo(0));
				Assert.That(contextIndex, Is.GreaterThan(clearIndex));
				Assert.That(accountIndex, Is.GreaterThan(contextIndex),
					"private document text and context must disappear before sign-out is rendered");
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void RemoteLevel_SignOutSuppressesLatePrivateTreeFromCancellationIgnoringTransport()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-remote-level-account-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		AccountBoundaryLevelCatalog catalog = new();
		try
		{
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", "main", []));
			List<string> sent = [];
			object gate = new();
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json => { lock (gate) { sent.Add(json); } },
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: new FakeGitHubAuth(true), workspace: store, repositoryCatalog: catalog);

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoBrowse, new RepoBrowsePayload("octo/specs")));
			TreePayload rootTree = WaitForTree(sent, gate, tree => tree.Nodes.Count > 0)!;
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.TreeRequest, new TreeRequestPayload(rootTree.Nodes[0].Path, RequestId: 77)));
			Assert.That(catalog.LevelStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);

			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignOut));
			catalog.ReleaseLevel.SetResult();
			Thread.Sleep(250);

			lock (gate)
			{
				Assert.That(sent.Select(IpcSerializer.TryDeserialize).Any(message =>
					message?.Kind == MessageKinds.Tree
					&& message.GetPayload<TreePayload>()?.RequestId == 77), Is.False,
					"a private directory response crossed the completed sign-out boundary");
			}
		}
		finally
		{
			catalog.ReleaseLevel.TrySetResult();
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void RemoteFile_SignOutSuppressesLatePrivateDocumentFromCancellationIgnoringTransport()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-remote-file-account-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		IsolationCatalog catalog = new();
		try
		{
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", "main", []));
			List<string> sent = [];
			object gate = new();
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json => { lock (gate) { sent.Add(json); } },
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: new FakeGitHubAuth(true), workspace: store, repositoryCatalog: catalog);
			const long requestId = 78;

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.DocOpen,
				new DocOpenPayload("github://octo/specs/main/private.md", requestId)));
			Assert.That(catalog.FileStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);

			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignOut));
			catalog.ReleaseFile.SetResult();
			Assert.That(catalog.FileReturned.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
			Thread.Sleep(250);

			lock (gate)
			{
				IpcMessage?[] messages = sent.Select(IpcSerializer.TryDeserialize).ToArray();
				Assert.Multiple(() =>
				{
					Assert.That(messages.Any(message => message?.Kind == MessageKinds.DocLoaded), Is.False,
						"private document text crossed the completed sign-out boundary");
					Assert.That(messages.Any(message =>
						message?.Kind == MessageKinds.DocOpenCompleted
						&& message.GetPayload<DocOpenCompletedPayload>() is
							{ RequestId: requestId, Succeeded: true }), Is.False,
						"the retired private document request was reported as successful");
				});
			}
		}
		finally
		{
			catalog.ReleaseFile.TrySetResult();
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
			_ = WaitForTree(sent, gate, candidate => candidate.Nodes.Count > 0);

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
			TreePayload latest = WaitForTree(sent, gate, candidate => candidate.Nodes.Count > 0)!;
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
				TreePayload[] completedTrees = trees.Where(tree => tree.Nodes.Count > 0).ToArray();
				Assert.That(completedTrees, Has.Length.EqualTo(1));
				Assert.That(completedTrees[0].Nodes.Single().Name, Is.EqualTo("new.md"));
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
				IpcMessage[] messages = sent
					.Select(IpcSerializer.TryDeserialize)
					.Where(message => message is not null)
					.Select(message => message!)
					.ToArray();
				TreePayload[] trees = messages
					.Where(message => message.Kind == MessageKinds.Tree)
					.Select(message => message.GetPayload<TreePayload>()!)
					.ToArray();
				Assert.That(trees, Has.Length.EqualTo(1));
				Assert.That(trees[0].Nodes, Is.Empty);
				Assert.That(messages.Select(message => message.Kind), Has.None.EqualTo(MessageKinds.Error));
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
			_ = WaitForTree(sent, gate, tree => tree.Root == repo.Id && tree.Nodes.Count == 0);
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
				Assert.That(sent
					.Select(IpcSerializer.TryDeserialize)
					.Where(message => message is not null)
					.Select(message => message!.Kind),
					Has.None.EqualTo(MessageKinds.Tree));
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

			TreePayload tree = WaitForTree(sent, gate, candidate => candidate.Nodes.Count > 0)!;
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
			int navigationTriggered = 0;
			HostController? controller = null;
			void Send(string json)
			{
				lock (gate)
				{
					sent.Add(json);
				}
				IpcMessage? message = IpcSerializer.TryDeserialize(json);
				if (message?.Kind == MessageKinds.GitHubAccount
					&& message.GetPayload<GitHubAccountPayload>()?.SignedIn == true
					&& Interlocked.Exchange(ref navigationTriggered, 1) == 0)
				{
					// PublishTerminalIfCurrent has already taken the pending actions before SendAccount.
					// Navigate here so this runs in the exact Take -> navigation -> Resume window.
					controller!.OnMessage(IpcSerializer.SerializeEvent(
						MessageKinds.FolderOpen, new FolderOpenPayload(root)));
				}
				AcknowledgeAccountApplication(controller, message);
			}
			controller = new HostController(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				Send,
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: auth, workspace: store, repositoryCatalog: catalog);
			using (controller)
			{
				using ManualResetEventSlim resumed = new();
				controller.PendingRepoActionsResumedForTest = resumed;

				controller.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.RepoBrowse, new RepoBrowsePayload("octo/specs")));
				Assert.That(auth.AuthorizationStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
				auth.ReleaseAuthorization.SetResult();
				Assert.That(resumed.Wait(TimeSpan.FromSeconds(2)), Is.True);
				string localRoot = Path.GetFullPath(root);
				Assert.That(WaitForTree(sent, gate, tree => tree.Root == localRoot), Is.Not.Null);

				Assert.That(catalog.TreeCalls, Is.Zero);
				lock (gate)
				{
					TreePayload[] trees = sent
						.Select(IpcSerializer.TryDeserialize)
						.Where(message => message?.Kind == MessageKinds.Tree)
						.Select(message => message!.GetPayload<TreePayload>()!)
						.ToArray();
					Assert.That(trees, Has.Length.EqualTo(3));
					Assert.That(trees[0].Root, Is.EqualTo("octo/specs"));
					Assert.That(trees[0].Nodes, Is.Empty);
					Assert.That(trees[1].Root, Is.Empty);
					Assert.That(trees[2].Root, Is.EqualTo(localRoot));
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
			HostController? controller = null;
			controller = new HostController(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json =>
				{
					lock (gate) { sent.Add(json); }
					AcknowledgeAccountApplication(controller, IpcSerializer.TryDeserialize(json));
				},
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: auth, workspace: store, repositoryCatalog: catalog);
			using (controller)
			{
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
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void PendingRepositoryOpenDisplacesQueuedRemoteFileExactlyOnce()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-pending-file-replacement-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", "main", []));
			store.RegisterRepo(new RegisteredRepo(
				"octo/other", "octo/other", "https://github.com/octo/other", "main", []));
			BlockingSignInAuth auth = new();
			CountingCatalog catalog = new();
			List<string> sent = [];
			object gate = new();
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json => { lock (gate) { sent.Add(json); } },
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: auth,
				workspace: store,
				cloner: new NoopCloner(),
				repositoryCatalog: catalog);
			const long requestId = 914;

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.DocOpen,
				new DocOpenPayload("github://octo/specs/main/Docs%2FGuide.md", requestId)));
			Assert.That(auth.AuthorizationStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoOpen, new RepoOpenPayload("octo/other")));

			Assert.That(SpinWait.SpinUntil(() =>
			{
				lock (gate)
				{
					return sent.Count(json =>
					{
						IpcMessage? message = IpcSerializer.TryDeserialize(json);
						return message?.Kind == MessageKinds.DocOpenCompleted
							&& message.GetPayload<DocOpenCompletedPayload>() is
								{ RequestId: requestId, Succeeded: false };
					}) == 1;
				}
			}, TimeSpan.FromSeconds(2)), Is.True);
			Assert.That(catalog.FileCalls, Is.Zero);
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
			int navigationTriggered = 0;
			HostController? controller = null;
			void Send(string json)
			{
				lock (gate)
				{
					sent.Add(json);
				}
				IpcMessage? message = IpcSerializer.TryDeserialize(json);
				if (message?.Kind == MessageKinds.GitHubAccount
					&& message.GetPayload<GitHubAccountPayload>()?.SignedIn == true
					&& Interlocked.Exchange(ref navigationTriggered, 1) == 0)
				{
					controller!.OnMessage(IpcSerializer.SerializeEvent(
						MessageKinds.FolderOpen, new FolderOpenPayload(root)));
				}
				AcknowledgeAccountApplication(controller, message);
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
				Assert.That(SpinWait.SpinUntil(() =>
				{
					lock (gate)
					{
						return sent.Any(json =>
							IpcSerializer.TryDeserialize(json)?.Kind == MessageKinds.Tree);
					}
				}, TimeSpan.FromSeconds(2)), Is.True);

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
			int unregisterTriggered = 0;
			HostController? controller = null;
			void Send(string json)
			{
				lock (gate)
				{
					sent.Add(json);
				}
				IpcMessage? message = IpcSerializer.TryDeserialize(json);
				if (message?.Kind == MessageKinds.GitHubAccount
					&& message.GetPayload<GitHubAccountPayload>()?.SignedIn == true
					&& Interlocked.Exchange(ref unregisterTriggered, 1) == 0)
				{
					string removed = matching ? repoA.Id : repoB.Id;
					controller!.OnMessage(IpcSerializer.SerializeEvent(
						MessageKinds.RepoUnregister, new UnregisterRepoPayload(removed)));
					if (matching)
					{
						store.RegisterRepo(repoA);
					}
				}
				AcknowledgeAccountApplication(controller, message);
			}
			controller = new HostController(
				(_, _) => new Renderer.RenderResult(string.Empty, []), Send,
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: auth, workspace: store, repositoryCatalog: catalog);
			using (controller)
			{
				using ManualResetEventSlim resumed = new();
				controller.PendingRepoActionsResumedForTest = resumed;
				controller.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.RepoBrowse, new RepoBrowsePayload(repoA.Id)));
				Assert.That(auth.AuthorizationStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
				auth.ReleaseAuthorization.SetResult();
				Assert.That(resumed.Wait(TimeSpan.FromSeconds(2)), Is.True);

				if (matching)
				{
					Assert.That(catalog.TreeCalls, Is.Zero);
				}
				else
				{
					TreePayload tree = WaitForTree(sent, gate, candidate => candidate.Nodes.Count > 0)!;
					Assert.That(tree.Nodes.Single().Name, Is.EqualTo("remote.md"));
					Assert.That(catalog.TreeCalls, Is.EqualTo(1));
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
				TreePayload tree = WaitForTree(sent, gate, candidate => candidate.Nodes.Count > 0)!;
				Assert.That(tree.Nodes.Single().Name, Is.EqualTo("A.md"));
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void FailedRemoteBrowse_ReplacesThePreviousLocalTreeWithAnExplicitRemoteError()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-remote-failure-tree-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			File.WriteAllText(Path.Combine(root, "local.md"), "# Local");
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", "main", []));
			List<string> sent = [];
			object gate = new();
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json => { lock (gate) { sent.Add(json); } },
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: new FakeGitHubAuth(true), workspace: store, repositoryCatalog: new FailingTreeCatalog());

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.FolderOpen, new FolderOpenPayload(root)));
			_ = WaitForTree(sent, gate, tree => tree.Root == Path.GetFullPath(root));
			lock (gate)
			{
				sent.Clear();
			}

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoBrowse, new RepoBrowsePayload("octo/specs")));
			Assert.That(WaitFor<ErrorPayload>(sent, gate, MessageKinds.Error), Is.Not.Null);

			lock (gate)
			{
				IpcMessage[] messages = sent
					.Select(IpcSerializer.TryDeserialize)
					.Where(message => message is not null)
					.Select(message => message!)
					.ToArray();
				TreePayload[] trees = messages
					.Where(message => message.Kind == MessageKinds.Tree)
					.Select(message => message.GetPayload<TreePayload>()!)
					.ToArray();
				Assert.That(trees, Has.Length.EqualTo(2));
				Assert.That(trees, Has.All.Property(nameof(TreePayload.Root)).EqualTo("octo/specs"));
				Assert.That(trees, Has.All.Property(nameof(TreePayload.Nodes)).Empty);
				Assert.That(trees[0].Error, Is.Null);
				Assert.That(trees[1].Error,
					Is.EqualTo("Could not read that repository. Check your connection and access, then try again."));
				Assert.That(trees, Has.All.Property(nameof(TreePayload.Remote)).True);
				Assert.That(messages[0].Kind, Is.EqualTo(MessageKinds.Tree));
				Assert.That(messages[^1].Kind, Is.EqualTo(MessageKinds.Error));
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void RemoteTreeTerminalPublication_PrecedesLaterAcceptedLocalNavigation()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-remote-tree-publication-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		using ManualResetEventSlim terminalEntered = new();
		using ManualResetEventSlim releaseTerminal = new();
		try
		{
			File.WriteAllText(Path.Combine(root, "local.md"), "# Local");
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", "main", []));
			List<string> sent = [];
			object gate = new();
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json => { lock (gate) { sent.Add(json); } },
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: new FakeGitHubAuth(true), workspace: store, repositoryCatalog: new Catalog());
			controller.RemoteBrowseTerminalPublishingForTest = () =>
			{
				terminalEntered.Set();
				releaseTerminal.Wait(TimeSpan.FromSeconds(5));
			};

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoBrowse, new RepoBrowsePayload("octo/specs")));
			Assert.That(terminalEntered.Wait(TimeSpan.FromSeconds(2)), Is.True);
			Task localNavigation = Task.Run(() => controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.FolderOpen, new FolderOpenPayload(root))));
			Assert.That(localNavigation.Wait(TimeSpan.FromMilliseconds(100)), Is.False);

			releaseTerminal.Set();
			Assert.That(localNavigation.Wait(TimeSpan.FromSeconds(2)), Is.True);
			_ = WaitForTree(sent, gate, tree => tree.Root == Path.GetFullPath(root));

			lock (gate)
			{
				IpcMessage[] messages = sent
					.Select(IpcSerializer.TryDeserialize)
					.Where(message => message is not null)
					.Select(message => message!)
					.ToArray();
				int localTree = Array.FindLastIndex(messages, message =>
					message.Kind == MessageKinds.Tree
					&& message.GetPayload<TreePayload>()?.Root == Path.GetFullPath(root));
				Assert.That(localTree, Is.GreaterThanOrEqualTo(0));
				Assert.That(messages.Skip(localTree + 1)
					.Where(message => message.Kind == MessageKinds.Tree)
					.Select(message => message.GetPayload<TreePayload>()!)
					.Any(tree => tree.Root == "octo/specs" && tree.Nodes.Count > 0), Is.False);
			}
		}
		finally
		{
			releaseTerminal.Set();
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void RemoteFileTerminalPublication_PrecedesLaterAcceptedLocalNavigation()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-remote-file-publication-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		using ManualResetEventSlim terminalEntered = new();
		using ManualResetEventSlim releaseTerminal = new();
		try
		{
			File.WriteAllText(Path.Combine(root, "local.md"), "# Local");
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/specs", "octo/specs", "https://github.com/octo/specs", "main", []));
			List<string> sent = [];
			object gate = new();
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json => { lock (gate) { sent.Add(json); } },
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: new FakeGitHubAuth(true), workspace: store, repositoryCatalog: new Catalog());
			controller.RemoteFileTerminalPublishingForTest = () =>
			{
				terminalEntered.Set();
				releaseTerminal.Wait(TimeSpan.FromSeconds(5));
			};

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.DocOpen,
				new DocOpenPayload("github://octo/specs/main/README.md", 147)));
			Assert.That(terminalEntered.Wait(TimeSpan.FromSeconds(2)), Is.True);
			Task localNavigation = Task.Run(() => controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.FolderOpen, new FolderOpenPayload(root))));
			Assert.That(localNavigation.Wait(TimeSpan.FromMilliseconds(100)), Is.False);

			releaseTerminal.Set();
			Assert.That(localNavigation.Wait(TimeSpan.FromSeconds(2)), Is.True);
			_ = WaitForTree(sent, gate, tree => tree.Root == Path.GetFullPath(root));

			lock (gate)
			{
				IpcMessage[] messages = sent
					.Select(IpcSerializer.TryDeserialize)
					.Where(message => message is not null)
					.Select(message => message!)
					.ToArray();
				int localTree = Array.FindLastIndex(messages, message =>
					message.Kind == MessageKinds.Tree
					&& message.GetPayload<TreePayload>()?.Root == Path.GetFullPath(root));
				Assert.That(localTree, Is.GreaterThanOrEqualTo(0));
				Assert.That(messages.Skip(localTree + 1).Any(message =>
					message.Kind == MessageKinds.DocLoaded
					&& message.GetPayload<DocLoadedPayload>()?.Repository == "octo/specs"), Is.False);
				Assert.That(messages.Skip(localTree + 1).Any(message =>
					message.Kind == MessageKinds.WorkspaceContext
					&& message.GetPayload<WorkspaceContextPayload>()?.Repository == "octo/specs"), Is.False);
			}
		}
		finally
		{
			releaseTerminal.Set();
			Directory.Delete(root, recursive: true);
		}
	}

	private static TreePayload? WaitForTree(
		List<string> sent, object gate, Func<TreePayload, bool> predicate)
	{
		for (int attempt = 0; attempt < 200; attempt++)
		{
			lock (gate)
			{
				foreach (string json in sent)
				{
					IpcMessage? message = IpcSerializer.TryDeserialize(json);
					TreePayload? tree = message?.Kind == MessageKinds.Tree
						? message.GetPayload<TreePayload>()
						: null;
					if (tree is not null && predicate(tree))
					{
						return tree;
					}
				}
			}
			Thread.Sleep(20);
		}
		return null;
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

	private static void AcknowledgeAccountApplication(HostController? controller, IpcMessage? message)
	{
		string? publicationId = message?.Kind == MessageKinds.GitHubAccount
			? message.GetPayload<GitHubAccountPayload>()?.PublicationId
			: null;
		if (publicationId is not null)
		{
			controller!.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.GitHubAccountApplied,
				new GitHubAccountAppliedPayload(publicationId)));
		}
	}
}
