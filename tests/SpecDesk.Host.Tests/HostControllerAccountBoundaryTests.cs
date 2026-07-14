using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Ai;
using SpecDesk.Contracts;
using SpecDesk.Git;
using SpecDesk.GitHub;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class HostControllerAccountBoundaryTests
{
	private sealed class NoDialogs : IFileDialogs
	{
		public string? PickOpenFile() => null;
		public string? PickOpenFolder() => null;
		public string? PickSaveFile(string? suggestedPath) => null;
	}

	private sealed class BoundaryAuth : IGitHubAuth
	{
		private int _blockNextCheck;
		private bool _signedIn = true;

		public bool PauseTokenUse { get; set; }
		public ManualResetEventSlim TokenUseReady { get; } = new(false);
		public ManualResetEventSlim TokenUseRelease { get; } = new(false);
		public ManualResetEventSlim TokenUseFinished { get; } = new(false);
		public ManualResetEventSlim CheckEntered { get; } = new(false);
		public ManualResetEventSlim CheckRelease { get; } = new(false);
		public Sequence? Ordering { get; init; }
		public int SignOutOrder { get; private set; }

		public Task<DeviceCodePrompt> StartSignInAsync(CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
		public Task<SignInResult> AwaitAuthorizationAsync(
			DeviceCodePrompt prompt, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public bool IsSignedIn()
		{
			bool result = _signedIn;
			if (Interlocked.Exchange(ref _blockNextCheck, 0) == 1)
			{
				CheckEntered.Set();
				CheckRelease.Wait(TimeSpan.FromSeconds(2));
			}
			return result;
		}

		public string? SignedInLogin() => _signedIn ? "octocat" : null;
		public void BlockNextSessionCheck() => Interlocked.Exchange(ref _blockNextCheck, 1);
		public void SignInAgain() => _signedIn = true;

		public async Task<T> WithAccessTokenAsync<T>(
			Func<string, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default)
		{
			if (PauseTokenUse)
			{
				TokenUseReady.Set();
				TokenUseRelease.Wait(TimeSpan.FromSeconds(2), CancellationToken.None);
			}
			try
			{
				return await use("private-token", CancellationToken.None);
			}
			finally
			{
				TokenUseFinished.Set();
			}
		}

		public void SignOut()
		{
			SignOutOrder = Ordering?.Next() ?? 0;
			_signedIn = false;
		}
	}

	private sealed class BlockingReview : IGitHubReview
	{
		private int _openCalls;
		public int OpenCalls => Volatile.Read(ref _openCalls);
		public TaskCompletionSource OpenStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<PullRequest>? FirstOpenGate { get; init; }
		public TaskCompletionSource ListStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<IReadOnlyList<ReviewSummary>> ListRelease { get; } = new();
		public TaskCompletionSource CommentsStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<IReadOnlyList<ReviewComment>> CommentsRelease { get; } = new();

		public Task<PullRequest> OpenPullRequestAsync(
			string accessToken, string owner, string repo, string head, string baseBranch,
			string title, string body, CancellationToken cancellationToken = default)
		{
			int call = Interlocked.Increment(ref _openCalls);
			OpenStarted.TrySetResult();
			return call == 1 && FirstOpenGate is not null
				? FirstOpenGate.Task
				: Task.FromResult(new PullRequest(42, "https://github.com/octo/spec/pull/42"));
		}
		public Task<int> RequestReviewersAsync(
			string accessToken, string owner, string repo, int pullNumber,
			IReadOnlyList<string> reviewers, CancellationToken cancellationToken = default) =>
			Task.FromResult(0);
		public Task<ReviewStatus?> GetReviewStatusAsync(
			string accessToken, string owner, string repo, string branch,
			CancellationToken cancellationToken = default) =>
			Task.FromResult<ReviewStatus?>(new ReviewStatus(
				ReviewDecision.InReview, 42, PullRequestState.Open));
		public async Task<IReadOnlyList<ReviewSummary>> ListReviewsAsync(
			string accessToken, CancellationToken cancellationToken = default)
		{
			ListStarted.SetResult();
			return await ListRelease.Task;
		}
		public Task<IReadOnlyList<ReviewSummary>> ListReviewRequestsAsync(
			string accessToken, CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<ReviewSummary>>([]);
		public Task<IReadOnlyList<ReviewSummary>> ListPullRequestsAsync(
			string accessToken, CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<ReviewSummary>>([]);
		public async Task<IReadOnlyList<ReviewComment>> ListReviewCommentsAsync(
			string accessToken, string owner, string repo, int pullNumber,
			CancellationToken cancellationToken = default)
		{
			CommentsStarted.SetResult();
			return await CommentsRelease.Task;
		}
	}

	private sealed class IgnoringChatAgent : IChatAgent
	{
		public TaskCompletionSource Started { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new();
		public TaskCompletionSource Finished { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async IAsyncEnumerable<string> StreamAsync(
			string userMessage,
			[EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			try
			{
				Started.SetResult();
				await Release.Task;
				yield return "private late delta";
			}
			finally
			{
				Finished.SetResult();
			}
		}
	}

	private sealed class OneAgentFactory(IChatAgent agent) : IChatAgentFactory
	{
		public IChatAgent Create(string githubAccessToken) => agent;
	}

	private sealed class Sequence
	{
		private int _value;
		public int Next() => Interlocked.Increment(ref _value);
	}

	private sealed class OrderingPublishing(Sequence ordering) : IGitPublishing
	{
		public int PushOrder { get; private set; }
		public string? RemoteUrl(string repoRoot, string remoteName = "origin") =>
			"https://github.com/octo/spec-repo.git";
		public string? LastVersionNote(string repoRoot, string branchName) => "Review";
		public bool HasCommitsToReview(string repoRoot, string branchName, string baseBranch) => true;
		public void PushBranch(
			string repoRoot, string branchName, string expectedRepositoryUrl, string accessToken,
			string remoteName = "origin",
			CancellationToken cancellationToken = default) => PushOrder = ordering.Next();
	}

	[Test]
	public void SignOutSuppressesLateReviewListFromCancellationIgnoringTransport()
	{
		BoundaryAuth auth = new();
		BlockingReview review = new();
		List<string> sent = [];
		using HostController controller = Build(sent, auth, review: review);

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.PrListRequest, id: "late-list"));
		Assert.That(review.ListStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
		Clear(sent);
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignOut));
		review.ListRelease.SetResult(
			[new ReviewSummary(7, "Private PR", "https://github.com/o/r/pull/7", "o/r",
				ReviewRole.Author, ReviewDecision.InReview)]);

		AssertNoKindFor(sent, MessageKinds.PrList);
	}

	[Test]
	public void SignOutSuppressesLatePrivateDocumentCommentsFromCancellationIgnoringTransport()
	{
		string root = NewDocument(out string document);
		try
		{
			BoundaryAuth auth = new();
			BlockingReview review = new();
			FakeVersioning versioning = new() { Branch = "spec/billing" };
			List<string> sent = [];
			using HostController controller = Build(
				sent, auth, versioning, review, document, publishing: versioning);
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.DocumentActivityRequest, id: "late-comments"));
			Assert.That(review.CommentsStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
			Clear(sent);
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignOut));
			review.CommentsRelease.SetResult(
				[new ReviewComment("secret", "billing.md", "reviewer", "private", DateTimeOffset.UnixEpoch)]);

			AssertNoKindFor(sent, MessageKinds.DocumentActivity);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void SignOutSuppressesLateChatDeltaAndDoneFromCancellationIgnoringAgent()
	{
		BoundaryAuth auth = new();
		IgnoringChatAgent agent = new();
		List<string> sent = [];
		using HostController controller = Build(
			sent, auth, agentFactory: new OneAgentFactory(agent));

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.ChatSend, new ChatSendPayload("private prompt", [], "late-chat")));
		Assert.That(agent.Started.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
		Clear(sent);
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignOut));
		agent.Release.SetResult();
		Assert.That(agent.Finished.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);

		AssertNoKindFor(sent, MessageKinds.ChatDelta, MessageKinds.ChatDone);
	}

	[Test]
	public void SignOutRetiresAReviewClaimWhoseProviderNeverCompletes()
	{
		string root = NewDocument(out string document);
		try
		{
			BoundaryAuth auth = new();
			TaskCompletionSource<PullRequest> firstOpen =
				new(TaskCreationOptions.RunContinuationsAsynchronously);
			BlockingReview review = new() { FirstOpenGate = firstOpen };
			FakeVersioning versioning = new();
			List<string> sent = [];
			using HostController controller = Build(
				sent, auth, versioning, review, document, versioning);
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));
			Assert.That(review.OpenStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);

			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignOut));
			auth.SignInAgain();
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));

			Assert.That(
				SpinWait.SpinUntil(() => review.OpenCalls >= 2, TimeSpan.FromSeconds(2)),
				Is.True,
				"the retired provider claim blocked review publishing after reconnecting");
			firstOpen.SetResult(new PullRequest(41, "https://github.com/octo/spec/pull/41"));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void ReviewDispatchAcquiresTheAccountGuardBeforeTheRemoteMutationGuard()
	{
		string root = NewDocument(out string document);
		try
		{
			BoundaryAuth auth = new();
			FakeVersioning versioning = new();
			List<string> sent = [];
			using HostController controller = Build(
				sent, auth, versioning, new BlockingReview(), document, versioning);
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));

			object signInGuard = typeof(HostController)
				.GetField("_signInPublishSync", BindingFlags.Instance | BindingFlags.NonPublic)!
				.GetValue(controller)!;
			object remoteGuard = typeof(HostController)
				.GetField("_remotePublishSync", BindingFlags.Instance | BindingFlags.NonPublic)!
				.GetValue(controller)!;
			Thread dispatch = new(() => controller.OnMessage(
				IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview)));

			Monitor.Enter(signInGuard);
			try
			{
				dispatch.Start();
				Assert.That(
					SpinWait.SpinUntil(
						() => (dispatch.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
						TimeSpan.FromSeconds(2)),
					Is.True,
					"review dispatch did not reach the guarded section");
				Assert.That(Monitor.TryEnter(remoteGuard), Is.True,
					"review dispatch took the remote guard while waiting for the account guard");
				Monitor.Exit(remoteGuard);
			}
			finally
			{
				Monitor.Exit(signInGuard);
			}

			Assert.That(dispatch.Join(TimeSpan.FromSeconds(2)), Is.True);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void SignOutOrdersReviewPushStartBeforeInvalidationOrPreventsItWhenQueued()
	{
		string root = NewDocument(out string document);
		try
		{
			Sequence ordering = new();
			BoundaryAuth auth = new() { PauseTokenUse = true, Ordering = ordering };
			OrderingPublishing publishing = new(ordering);
			List<string> sent = [];
			using HostController controller = Build(
				sent, auth, new FakeVersioning(), new BlockingReview(), document, publishing);
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));
			Assert.That(auth.TokenUseReady.Wait(TimeSpan.FromSeconds(2)), Is.True);

			object repoGate = typeof(HostController)
				.GetField("_repoGate", BindingFlags.Instance | BindingFlags.NonPublic)!
				.GetValue(controller)!;
			Monitor.Enter(repoGate);
			try
			{
				auth.BlockNextSessionCheck();
				auth.TokenUseRelease.Set();
				Assert.That(auth.CheckEntered.Wait(TimeSpan.FromSeconds(2)), Is.True);
				TaskCompletionSource signOutEntered =
					new(TaskCreationOptions.RunContinuationsAsynchronously);
				Task signOut = Task.Run(() =>
				{
					signOutEntered.SetResult();
					controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignOut));
				});
				Assert.That(signOutEntered.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
				auth.CheckRelease.Set();
				Task.WhenAny(signOut, Task.Delay(150)).GetAwaiter().GetResult();
				Monitor.Exit(repoGate);
				repoGate = null!;
				Assert.That(signOut.Wait(TimeSpan.FromSeconds(2)), Is.True);
			}
			finally
			{
				if (repoGate is not null)
				{
					Monitor.Exit(repoGate);
				}
			}
			Assert.That(auth.TokenUseFinished.Wait(TimeSpan.FromSeconds(2)), Is.True);
			Assert.That(publishing.PushOrder == 0 || publishing.PushOrder < auth.SignOutOrder, Is.True,
				"a push started after GitHubSignOut had invalidated the account session");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private static HostController Build(
		List<string> sent,
		IGitHubAuth auth,
		IDocumentVersioning? versioning = null,
		IGitHubReview? review = null,
		string? document = null,
		IGitPublishing? publishing = null,
		IChatAgentFactory? agentFactory = null) =>
		new(
			(_, _) => new Renderer.RenderResult(string.Empty, []),
			json =>
			{
				lock (sent)
				{
					sent.Add(json);
				}
			},
			new NoDialogs(),
			(_, _, _, _, _) => null,
			versioning ?? new FakeVersioning(),
			NullLogger<HostController>.Instance,
			initialDocPath: document,
			auth: auth,
			publishing: publishing,
			review: review,
			chatAgentFactory: agentFactory);

	private static string NewDocument(out string document)
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-account-boundary-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		document = Path.Combine(root, "billing.md");
		File.WriteAllText(document, "# Billing");
		return root;
	}

	private static void Clear(List<string> sent)
	{
		lock (sent)
		{
			sent.Clear();
		}
	}

	private static void AssertNoKindFor(List<string> sent, params string[] kinds)
	{
		Stopwatch wait = Stopwatch.StartNew();
		while (wait.Elapsed < TimeSpan.FromMilliseconds(150))
		{
			lock (sent)
			{
				Assert.That(sent.Select(IpcSerializer.TryDeserialize)
					.Any(message => message is not null && kinds.Contains(message.Kind)), Is.False);
			}
			Thread.Yield();
		}
	}
}
