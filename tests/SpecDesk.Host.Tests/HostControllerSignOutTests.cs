using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Ai;
using SpecDesk.Contracts;
using SpecDesk.Git;
using SpecDesk.GitHub;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class HostControllerSignOutTests
{
	private sealed class NoDialogs : IFileDialogs
	{
		public string? PickOpenFile() => null;
		public string? PickOpenFolder() => null;
		public string? PickSaveFile(string? suggestedPath) => null;
	}

	private sealed class TrackingAuth : IGitHubAuth
	{
		private readonly object _gate = new();
		private readonly List<CancellationToken> _tokens = [];
		private bool _signedIn = true;

		public int TokensAtSignOut { get; private set; }
		public bool AllTokensCancelledAtSignOut { get; private set; }

		public Task<DeviceCodePrompt> StartSignInAsync(CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task<SignInResult> AwaitAuthorizationAsync(
			DeviceCodePrompt prompt, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public bool IsSignedIn() => _signedIn;
		public string? SignedInLogin() => _signedIn ? "octocat" : null;

		public Task<T> WithAccessTokenAsync<T>(
			Func<string, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default)
		{
			lock (_gate)
			{
				_tokens.Add(cancellationToken);
			}
			return use("private-token", cancellationToken);
		}

		public void SignOut()
		{
			lock (_gate)
			{
				TokensAtSignOut = _tokens.Count;
				AllTokensCancelledAtSignOut = _tokens.All(token => token.IsCancellationRequested);
				_signedIn = false;
			}
		}
	}

	private sealed class BlockingCatalog : IGitHubRepositoryCatalog
	{
		public TaskCompletionSource OrganizationsStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource RepositoriesStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource RegistrationStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource DescriptionStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource TreeStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource FileStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async Task<IReadOnlyList<string>> GetOrganizationsAsync(
			string accessToken, CancellationToken cancellationToken = default)
		{
			OrganizationsStarted.SetResult();
			await Release.Task;
			return ["private-org"];
		}

		public async Task<IReadOnlyList<GitHubRepositoryOption>> GetRepositoriesAsync(
			string accessToken, CancellationToken cancellationToken = default)
		{
			RepositoriesStarted.SetResult();
			await Release.Task;
			return [new("private-org/private-repo", "private list result")];
		}

		public async Task<GitHubRepositoryMetadata> GetMetadataAsync(
			string owner, string name, string accessToken, CancellationToken cancellationToken = default)
		{
			(name == "register" ? RegistrationStarted : DescriptionStarted).SetResult();
			await Release.Task;
			return new GitHubRepositoryMetadata("secret", "private description", IsPrivate: true);
		}

		public async Task<IReadOnlyList<GitHubRepositoryEntry>> GetTreeAsync(
			string owner, string name, string branch, string accessToken,
			CancellationToken cancellationToken = default)
		{
			TreeStarted.SetResult();
			await Release.Task;
			return [new("secret.md", false, 12)];
		}

		public async Task<string> GetFileAsync(
			string owner, string name, string branch, string path, string accessToken,
			CancellationToken cancellationToken = default)
		{
			FileStarted.SetResult();
			await Release.Task;
			return "# private document";
		}
	}

	private sealed class BlockingCloner(string clonePath) : IRepositoryCloner
	{
		public ManualResetEventSlim Started { get; } = new(false);
		public ManualResetEventSlim Release { get; } = new(false);

		public bool IsCloned(string destinationPath) => false;
		public bool IsCloneOf(string destinationPath, string url) => false;
		public bool IsCloneOfAtBranch(
			string destinationPath, string url, string? expectedCurrentBranch) => false;

		public string CloneOrReuse(
			string url, string destinationPath, string? accessToken, CancellationToken cancellationToken)
		{
			Started.Set();
			Release.Wait(TimeSpan.FromSeconds(5), CancellationToken.None);
			Directory.CreateDirectory(clonePath);
			File.WriteAllText(Path.Combine(clonePath, "README.md"), "# private clone");
			return clonePath;
		}
	}

	[Test]
	public void SignOutCancelsAndInvalidatesEveryAccountBoundRepositoryOperationBeforeClearingTheToken()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-signout-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			WorkspaceStore store = new(Path.Combine(root, "workspace.json"));
			store.RegisterRepo(new RegisteredRepo(
				"octo/browse", "octo/browse", "https://github.com/octo/browse", "main", []));
			TrackingAuth auth = new();
			BlockingCatalog catalog = new();
			BlockingCloner cloner = new(Path.Combine(root, "late-clone"));
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
				new NoDialogs(),
				(_, _, _, _, _) => null,
				new FakeVersioning(),
				NullLogger<HostController>.Instance,
				auth: auth,
				workspace: store,
				cloner: cloner,
				repositoryCatalog: catalog);

			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoRegister, new RegisterRepoPayload("octo/register")));
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoDescriptionRequest,
				new RepoDescriptionRequestPayload("octo/describe", 42)));
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoBrowse, new RepoBrowsePayload("octo/browse")));
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.DocOpen,
				new DocOpenPayload("github://octo/browse/main/secret.md")));
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoCloneManaged, new RepoCloneManagedPayload("octo/clone")));

			Task[] starts =
			[
				catalog.OrganizationsStarted.Task,
				catalog.RepositoriesStarted.Task,
				catalog.RegistrationStarted.Task,
				catalog.DescriptionStarted.Task,
				catalog.TreeStarted.Task,
				catalog.FileStarted.Task,
			];
			Assert.That(Task.WaitAll(starts, TimeSpan.FromSeconds(2)), Is.True);
			Assert.That(cloner.Started.Wait(TimeSpan.FromSeconds(2)), Is.True);

			lock (gate)
			{
				sent.Clear();
			}
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignOut));

			Assert.Multiple(() =>
			{
				Assert.That(auth.TokensAtSignOut, Is.EqualTo(7));
				Assert.That(auth.AllTokensCancelledAtSignOut, Is.True);
			});

			catalog.Release.SetResult();
			cloner.Release.Set();
			Thread.Sleep(150);

			lock (gate)
			{
				IpcMessage[] messages = sent
					.Select(IpcSerializer.TryDeserialize)
					.Where(message => message is not null)
					.Cast<IpcMessage>()
					.ToArray();
				Assert.Multiple(() =>
				{
					Assert.That(messages.Any(message => message.Kind == MessageKinds.RepoDescription), Is.False);
					Assert.That(messages.Any(message => message.Kind == MessageKinds.DocLoaded), Is.False);
					TreePayload[] treeClears = messages
						.Where(message => message.Kind == MessageKinds.Tree)
						.Select(message => message.GetPayload<TreePayload>()!)
						.ToArray();
					Assert.That(treeClears, Is.Not.Empty);
					Assert.That(treeClears.All(tree => tree.Root.Length == 0 && tree.Nodes.Count == 0), Is.True);
					Assert.That(messages
						.Where(message => message.Kind == MessageKinds.GitHubRepositories)
						.Select(message => message.GetPayload<GitHubRepositoriesPayload>()!)
						.All(payload => payload.Repositories.Count == 0), Is.True);
				});
			}

			WorkspaceStatePayload state = new WorkspaceStore(Path.Combine(root, "workspace.json")).State();
			Assert.Multiple(() =>
			{
				Assert.That(state.Repositories.Any(repo => repo.Id == "octo/browse"), Is.True);
				Assert.That(state.Repositories.Any(repo => repo.Id == "octo/register"), Is.False);
				Assert.That(state.Repositories.Any(repo => repo.Id == "octo/clone"), Is.False);
			});
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private sealed class BlockingChatAgent : IChatAgent
	{
		public TaskCompletionSource Started { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async IAsyncEnumerable<string> StreamAsync(
			string userMessage,
			[EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			Started.SetResult();
			await Release.Task;
			yield return "private late reply";
		}
	}

	private sealed class BlockingChatFactory(BlockingChatAgent agent) : IChatAgentFactory
	{
		public IChatAgent Create(string githubAccessToken) => agent;
	}

	private sealed class CancellationAwarePublishing : IGitPublishing
	{
		public ManualResetEventSlim Started { get; } = new(false);
		public ManualResetEventSlim CancellationObserved { get; } = new(false);
		public ManualResetEventSlim Release { get; } = new(false);

		public string? RemoteUrl(string repoRoot, string remoteName = "origin") =>
			"https://github.com/octo/spec-repo.git";

		public string? LastVersionNote(string repoRoot, string branchName) => "Review";

		public bool HasCommitsToReview(string repoRoot, string branchName, string baseBranch) => true;

		public void PushBranch(
			string repoRoot,
			string branchName,
			string expectedRepositoryUrl,
			string accessToken,
			string remoteName = "origin",
			CancellationToken cancellationToken = default)
		{
			using CancellationTokenRegistration registration =
				cancellationToken.Register(CancellationObserved.Set);
			Started.Set();
			Release.Wait(TimeSpan.FromSeconds(5), CancellationToken.None);
		}
	}

	[Test]
	public void DisposeCancelsAReviewPushBeforeWaitingForItsAccountPublicationGate()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-dispose-review-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		string document = Path.Combine(root, "billing.md");
		File.WriteAllText(document, "# Billing");
		CancellationAwarePublishing publishing = new();
		HostController? controller = null;
		Task? dispose = null;
		try
		{
			controller = new HostController(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				_ => { },
				new NoDialogs(),
				(_, _, _, _, _) => null,
				new FakeVersioning(),
				NullLogger<HostController>.Instance,
				initialDocPath: document,
				auth: new TrackingAuth(),
				publishing: publishing,
				review: new FakeGitHubReview());

			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit, new EditPayload("edit")));
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));
			Assert.That(publishing.Started.Wait(TimeSpan.FromSeconds(2)), Is.True);

			dispose = Task.Run(controller.Dispose);
			Assert.That(
				publishing.CancellationObserved.Wait(TimeSpan.FromSeconds(2)),
				Is.True,
				"Dispose waited for the account gate before cancelling the review push");
			Assert.That(dispose.IsCompleted, Is.False, "the fake push still owns the account gate");

			publishing.Release.Set();
			Assert.That(dispose.Wait(TimeSpan.FromSeconds(2)), Is.True);
			Assert.DoesNotThrow(controller.Dispose, "Dispose must remain idempotent after shutdown completes");
			controller = null;
		}
		finally
		{
			publishing.Release.Set();
			dispose?.Wait(TimeSpan.FromSeconds(2));
			controller?.Dispose();
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void SignOutSuppressesLateReviewListAndDocumentComments()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-account-late-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		string document = Path.Combine(root, "billing.md");
		File.WriteAllText(document, "# Billing");
		TaskCompletionSource listStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource listRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource commentsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource commentsRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
		try
		{
			TrackingAuth auth = new();
			FakeGitHubReview review = new()
			{
				ReviewsValue = [new ReviewSummary(7, "Private review", "https://example.invalid", "secret/repo", ReviewRole.Author, ReviewDecision.InReview)],
				ListReviewsStarted = listStarted,
				ListReviewsGate = listRelease,
				ReviewStatusValue = new ReviewStatus(ReviewDecision.InReview, 7, PullRequestState.Open),
				GetReviewStatusStarted = commentsStarted,
				GetReviewStatusGate = commentsRelease,
				CommentsValue = [new ReviewComment("1", "billing.md", "reviewer", "private comment", DateTimeOffset.UnixEpoch)],
			};
			List<string> sent = [];
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json => { lock (sent) { sent.Add(json); } },
				new NoDialogs(), (_, _, _, _, _) => null, new FakeVersioning(),
				NullLogger<HostController>.Instance, initialDocPath: document,
				auth: auth, publishing: new FakeVersioning(), review: review);

			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit, new EditPayload("edit")));
			controller.OnMessage(IpcSerializer.Serialize(new IpcMessage(MessageKinds.PrListRequest, Id: "list")));
			controller.OnMessage(IpcSerializer.Serialize(new IpcMessage(MessageKinds.DocumentActivityRequest, Id: "activity")));
			Assert.That(listStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
			Assert.That(commentsStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);

			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignOut));
			lock (sent) { sent.Clear(); }
			listRelease.SetResult();
			commentsRelease.SetResult();
			Thread.Sleep(150);

			lock (sent)
			{
				IpcMessage[] late = sent.Select(IpcSerializer.TryDeserialize).OfType<IpcMessage>().ToArray();
				Assert.Multiple(() =>
				{
					Assert.That(late.Any(message => message.Kind == MessageKinds.PrList), Is.False);
					Assert.That(late.Any(message => message.Kind == MessageKinds.DocumentActivity), Is.False);
					Assert.That(review.ListReviewCommentsCalls, Is.Zero,
						"sign-out after the PR lookup must prevent the following comments request");
				});
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void SignOutSuppressesCancellationIgnoringChatAndStopsReviewPublishAfterPush()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-account-effects-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		string document = Path.Combine(root, "billing.md");
		File.WriteAllText(document, "# Billing");
		using ManualResetEventSlim pushRelease = new(false);
		try
		{
			TrackingAuth auth = new();
			FakeVersioning versioning = new() { PushGate = pushRelease, IgnorePushCancellation = true };
			FakeGitHubReview review = new();
			BlockingChatAgent chat = new();
			List<string> sent = [];
			using HostController controller = new(
				(_, _) => new Renderer.RenderResult(string.Empty, []),
				json => { lock (sent) { sent.Add(json); } },
				new NoDialogs(), (_, _, _, _, _) => null, versioning,
				NullLogger<HostController>.Instance, initialDocPath: document,
				auth: auth, publishing: versioning, review: review,
				chatAgentFactory: new BlockingChatFactory(chat));

			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit, new EditPayload("edit")));
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.DocSaveVersion, new SaveVersionPayload("version")));
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));
			Assert.That(SpinWait.SpinUntil(() => versioning.PushBranchCalls == 1, 2_000), Is.True);
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.ChatSend, new ChatSendPayload("private question", [], "old-turn")));
			Assert.That(chat.Started.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);

			Task signOut = Task.Run(() =>
				controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignOut)));
			Thread.Sleep(50);
			pushRelease.Set();
			Assert.That(signOut.Wait(TimeSpan.FromSeconds(2)), Is.True);
			lock (sent) { sent.Clear(); }
			chat.Release.SetResult();
			Thread.Sleep(200);

			lock (sent)
			{
				IpcMessage[] late = sent.Select(IpcSerializer.TryDeserialize).OfType<IpcMessage>().ToArray();
				Assert.Multiple(() =>
				{
					Assert.That(late.Any(message => message.Kind is MessageKinds.ChatDelta or MessageKinds.ChatDone), Is.False);
					Assert.That(late.Any(message => message.Kind is MessageKinds.Status or MessageKinds.Error), Is.False);
				});
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}
}
