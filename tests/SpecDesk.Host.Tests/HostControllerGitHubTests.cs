using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;
using SpecDesk.Git;
using SpecDesk.GitHub;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class HostControllerGitHubTests
{
	private static readonly string[] AuthorizedOrganizations = ["acme", "octo-labs"];
	private static readonly string[] AccessibleRepositoryNames = ["acme/specs", "octocat/notes"];

    private sealed class NoDialogs(string? openFolder = null) : IFileDialogs
    {
        public string? PickOpenFile() => null;
        public string? PickOpenFolder() => openFolder;

        public string? PickSaveFile(string? suggestedPath) => null;
    }

    private sealed class CreatingCloner(string destination) : IRepositoryCloner
    {
        public int CloneCalls { get; private set; }
		public bool IsCloned(string destinationPath) => Directory.Exists(destinationPath);
		public bool IsCloneOf(string destinationPath, string url) => IsCloned(destinationPath);
		public bool IsCloneOfAtBranch(
			string destinationPath, string url, string? expectedCurrentBranch) => IsCloneOf(destinationPath, url);

        public string CloneOrReuse(
            string url, string destinationPath, string? accessToken, CancellationToken cancellationToken)
        {
            CloneCalls++;
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "README.md"), "# Authorized clone");
            return destination;
        }
    }

	private sealed class AccountCatalog : IGitHubRepositoryCatalog
	{
		public Task<string?> GetAvatarUrlAsync(
			string accessToken, CancellationToken cancellationToken = default) =>
			Task.FromResult<string?>("https://avatars.githubusercontent.com/u/583231?v=4");

		public Task<IReadOnlyList<string>> GetOrganizationsAsync(
			string accessToken, CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<string>>(["acme", "octo-labs"]);

		public Task<IReadOnlyList<GitHubRepositoryOption>> GetRepositoriesAsync(
			string accessToken, CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<GitHubRepositoryOption>>(
			[
				new("acme/specs", "Product specifications"),
				new("octocat/notes", null),
			]);

		public Task<GitHubRepositoryMetadata> GetMetadataAsync(
			string owner, string name, string accessToken, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task<IReadOnlyList<GitHubRepositoryEntry>> GetTreeAsync(
			string owner, string name, string branch, string accessToken,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<string> GetFileAsync(
			string owner, string name, string branch, string path, string accessToken,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class ApprovingAccountCatalog : IGitHubRepositoryCatalog
	{
		private int _organizationRequests;
		private int _repositoryRequests;

		public int OrganizationRequests => Volatile.Read(ref _organizationRequests);
		public int RepositoryRequests => Volatile.Read(ref _repositoryRequests);

		public Task<string?> GetAvatarUrlAsync(
			string accessToken, CancellationToken cancellationToken = default) =>
			Task.FromResult<string?>(null);

		public Task<IReadOnlyList<string>> GetOrganizationsAsync(
			string accessToken, CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<string>>(
				Interlocked.Increment(ref _organizationRequests) == 1 ? [] : ["approved-org"]);

		public Task<IReadOnlyList<GitHubRepositoryOption>> GetRepositoriesAsync(
			string accessToken, CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<GitHubRepositoryOption>>(
				Interlocked.Increment(ref _repositoryRequests) == 1
					? [new("octocat/notes", null)]
					: [new("approved-org/specs", "Newly approved organization repository")]);

		public Task<GitHubRepositoryMetadata> GetMetadataAsync(
			string owner, string name, string accessToken, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task<IReadOnlyList<GitHubRepositoryEntry>> GetTreeAsync(
			string owner, string name, string branch, string accessToken,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<string> GetFileAsync(
			string owner, string name, string branch, string path, string accessToken,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class FailingAccountCatalog : IGitHubRepositoryCatalog
	{
		public Task<IReadOnlyList<string>> GetOrganizationsAsync(
			string accessToken, CancellationToken cancellationToken = default) =>
			Task.FromException<IReadOnlyList<string>>(new HttpRequestException("offline"));

		public Task<GitHubRepositoryMetadata> GetMetadataAsync(
			string owner, string name, string accessToken, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task<IReadOnlyList<GitHubRepositoryEntry>> GetTreeAsync(
			string owner, string name, string branch, string accessToken,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<string> GetFileAsync(
			string owner, string name, string branch, string path, string accessToken,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class BlockingMetadataCatalog : IGitHubRepositoryCatalog
	{
		public TaskCompletionSource Started { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<IReadOnlyList<string>> GetOrganizationsAsync(
			string accessToken, CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<string>>([]);

		public async Task<GitHubRepositoryMetadata> GetMetadataAsync(
			string owner, string name, string accessToken, CancellationToken cancellationToken = default)
		{
			Started.SetResult();
			// Deliberately ignore cancellation: a remote provider can complete after local removal.
			await Release.Task;
			return new GitHubRepositoryMetadata("main", "Delayed metadata");
		}

		public Task<IReadOnlyList<GitHubRepositoryEntry>> GetTreeAsync(
			string owner, string name, string branch, string accessToken,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<string> GetFileAsync(
			string owner, string name, string branch, string path, string accessToken,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

	private sealed class RacingAccountCatalog : IGitHubRepositoryCatalog
	{
		private int _repositoryRequests;

		public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<IReadOnlyList<string>> GetOrganizationsAsync(
			string accessToken, CancellationToken cancellationToken = default) =>
			Task.FromResult<IReadOnlyList<string>>([]);

		public async Task<IReadOnlyList<GitHubRepositoryOption>> GetRepositoriesAsync(
			string accessToken, CancellationToken cancellationToken = default)
		{
			if (Interlocked.Increment(ref _repositoryRequests) == 1)
			{
				FirstStarted.SetResult();
				await ReleaseFirst.Task;
				return [new("stale/old", null)];
			}
			return [new("fresh/current", null)];
		}

		public Task<GitHubRepositoryMetadata> GetMetadataAsync(
			string owner, string name, string accessToken, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task<IReadOnlyList<GitHubRepositoryEntry>> GetTreeAsync(
			string owner, string name, string branch, string accessToken,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();

		public Task<string> GetFileAsync(
			string owner, string name, string branch, string path, string accessToken,
			CancellationToken cancellationToken = default) => throw new NotSupportedException();
	}

    private static Renderer.RenderResult StubRender(string docDir, string text) => new(string.Empty, []);

    // A scripted IGitHubAuth: a fixed prompt + result, controllable signed-in state, and an optional
    // "never authorizes" mode (awaits cancellation) so the cancel path can be exercised.
    private sealed class FakeGitHubAuth(SignInResult result) : IGitHubAuth
    {
        private static readonly DeviceCodePrompt Prompt = new(
            "WXYZ-1234", new Uri("https://github.com/login/device"),
            TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5), "device-code");

        public bool SignedIn { get; set; }

        public string? Login { get; set; }

		public int SignOutCalls { get; private set; }
		public int StartCalls { get; private set; }
		public Exception? SignOutException { get; init; }

        /// <summary>When true, AwaitAuthorizationAsync blocks until cancelled (the user never authorizes).</summary>
        public bool BlockUntilCancelled { get; init; }

		public Task<DeviceCodePrompt> StartSignInAsync(CancellationToken cancellationToken = default)
		{
			StartCalls++;
			return Task.FromResult(Prompt);
		}

        public async Task<SignInResult> AwaitAuthorizationAsync(
            DeviceCodePrompt prompt, CancellationToken cancellationToken = default)
        {
            if (BlockUntilCancelled)
            {
                // Mirror the real library: once polling, a cancel is folded into TimedOut, not thrown.
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return SignInResult.TimedOut();
                }
            }
            if (result.Outcome == SignInOutcome.Authorized)
            {
                SignedIn = true;
                Login = result.Login;
            }
            return result;
        }

        public bool IsSignedIn() => SignedIn;

        public string? SignedInLogin() => Login;

        public Task<T> WithAccessTokenAsync<T>(
            Func<string, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(use);
            if (!SignedIn)
            {
                throw new InvalidOperationException("Not signed in to GitHub.");
            }

            return use("gho_test", cancellationToken);
        }

        public void SignOut()
        {
			SignOutCalls++;
			SignedIn = false;
			Login = null;
			if (SignOutException is not null)
			{
				throw SignOutException;
			}
		}
    }

    // A fake IGitHubAuth whose device code is unique per StartSignInAsync call (CODE-1, CODE-2, ...), so a
    // test can tell which flow's frame is which. Its first flow blocks until cancelled (mirroring
    // FakeGitHubAuth.BlockUntilCancelled); every later flow authorizes immediately — this lets a test cancel
    // flow 1, start flow 2, and check that flow 1's stale cancellation fallout never surfaces after that.
    private sealed class SequencedGitHubAuth : IGitHubAuth
    {
        private int _callIndex;

        public Task<DeviceCodePrompt> StartSignInAsync(CancellationToken cancellationToken = default)
        {
            int index = Interlocked.Increment(ref _callIndex);
            return Task.FromResult(new DeviceCodePrompt(
                $"CODE-{index}", new Uri("https://github.com/login/device"),
                TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5), $"device-code-{index}"));
        }

        public async Task<SignInResult> AwaitAuthorizationAsync(
            DeviceCodePrompt prompt, CancellationToken cancellationToken = default)
        {
            if (prompt.UserCode == "CODE-1")
            {
                // Mirror the real library: once polling, a cancel is folded into TimedOut, not thrown.
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return SignInResult.TimedOut();
                }
            }

            return SignInResult.Authorized("octocat");
        }

        public bool IsSignedIn() => false;

        public string? SignedInLogin() => null;

        public Task<T> WithAccessTokenAsync<T>(
            Func<string, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by this test.");

        public void SignOut()
        {
        }
    }

	private sealed class AuthorizedThenBlockingGitHubAuth : IGitHubAuth
	{
		private int _flow;
		public TaskCompletionSource SecondFlowStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<DeviceCodePrompt> StartSignInAsync(CancellationToken cancellationToken = default)
		{
			int flow = Interlocked.Increment(ref _flow);
			return Task.FromResult(new DeviceCodePrompt(
				$"CODE-{flow}", new Uri("https://github.com/login/device"),
				TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5), $"device-{flow}"));
		}

		public async Task<SignInResult> AwaitAuthorizationAsync(
			DeviceCodePrompt prompt, CancellationToken cancellationToken = default)
		{
			if (prompt.UserCode == "CODE-1")
			{
				return SignInResult.Authorized("old-user");
			}
			SecondFlowStarted.SetResult();
			try
			{
				await Task.Delay(Timeout.Infinite, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				return SignInResult.TimedOut();
			}
			return SignInResult.TimedOut();
		}

		public bool IsSignedIn() => false;
		public string? SignedInLogin() => null;
		public Task<T> WithAccessTokenAsync<T>(
			Func<string, CancellationToken, Task<T>> use,
			CancellationToken cancellationToken = default) =>
			use("gho_test", cancellationToken);
		public void SignOut()
		{
		}
	}

    private sealed class RacingStartGitHubAuth : IGitHubAuth
    {
        private int _startCount;
        private bool _signedIn;

        public TaskCompletionSource FirstStartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstStart { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DeviceCodePrompt> StartSignInAsync(CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _startCount);
            if (call == 1)
            {
                FirstStartEntered.SetResult();
                await ReleaseFirstStart.Task;
                throw new HttpRequestException("stale start failed");
            }

            return new DeviceCodePrompt(
                "NEW-CODE", new Uri("https://github.com/login/device"),
                TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5), "new-device-code");
        }

        public Task<SignInResult> AwaitAuthorizationAsync(
            DeviceCodePrompt prompt, CancellationToken cancellationToken = default)
        {
            _signedIn = true;
            return Task.FromResult(SignInResult.Authorized("octocat"));
        }

        public bool IsSignedIn() => _signedIn;
        public string? SignedInLogin() => _signedIn ? "octocat" : null;

        public Task<T> WithAccessTokenAsync<T>(
            Func<string, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default) =>
            use("gho_test", cancellationToken);

        public void SignOut() => _signedIn = false;
    }

    private enum RaceStage
    {
        Prompt,
        Terminal,
    }

    private sealed class OrdinaryRaceGitHubAuth(RaceStage stage) : IGitHubAuth
    {
        private int _calls;
        public TaskCompletionSource FirstPaused { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DeviceCodePrompt> StartSignInAsync(CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _calls);
            if (call == 1 && stage == RaceStage.Prompt)
            {
                FirstPaused.SetResult();
                await ReleaseFirst.Task;
            }
            return new DeviceCodePrompt(
                call == 1 ? "OLD-CODE" : "NEW-CODE",
                new Uri("https://github.com/login/device"),
                TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5), $"device-{call}");
        }

        public async Task<SignInResult> AwaitAuthorizationAsync(
            DeviceCodePrompt prompt, CancellationToken cancellationToken = default)
        {
            if (prompt.UserCode == "OLD-CODE" && stage == RaceStage.Terminal)
            {
                FirstPaused.SetResult();
                await ReleaseFirst.Task;
            }
            return SignInResult.Authorized(prompt.UserCode == "OLD-CODE" ? "old-user" : "new-user");
        }

        public bool IsSignedIn() => false;
        public string? SignedInLogin() => null;
        public Task<T> WithAccessTokenAsync<T>(
            Func<string, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default) =>
            use("gho_test", cancellationToken);
        public void SignOut() { }
    }

	private static (HostController Controller, List<string> Sent, object Gate) Build(
		IGitHubAuth? auth,
		WorkspaceStore? workspace = null,
		IRepositoryCloner? cloner = null,
		Action<string>? beforeSend = null,
		IGitHubRepositoryCatalog? repositoryCatalog = null,
		IFileDialogs? dialogs = null,
		bool acknowledgeAccount = true)
    {
		List<string> sent = [];
		object gate = new();
		HostController? controller = null;
		void Send(string json)
		{
			beforeSend?.Invoke(json);
			lock (gate)
			{
				sent.Add(json);
			}
			IpcMessage? message = IpcSerializer.TryDeserialize(json);
			string? publicationId = message?.Kind == MessageKinds.GitHubAccount
				? message.GetPayload<GitHubAccountPayload>()?.PublicationId
				: null;
			if (acknowledgeAccount && publicationId is not null)
			{
				controller!.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.GitHubAccountApplied,
					new GitHubAccountAppliedPayload(publicationId)));
			}
		}

		controller = new HostController(
            StubRender, Send, dialogs ?? new NoDialogs(), (_, _, _, _, _) => null,
            new FakeVersioning(), NullLogger<HostController>.Instance,
            auth: auth, workspace: workspace, cloner: cloner, repositoryCatalog: repositoryCatalog);
		return (controller, sent, gate);
	}

	[Test]
	public void PendingRepositoryActionWaitsForTheMatchingAppliedAccountBoundary()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-account-ack-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			FakeGitHubAuth auth = new(SignInResult.Authorized("octocat"));
			WorkspaceStore workspace = new(Path.Combine(root, "workspace.json"));
			(HostController controller, List<string> sent, object gate) = Build(
				auth, workspace, acknowledgeAccount: false);
			using (controller)
			{
				controller.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.RepoRegister, new RegisterRepoPayload("octo/specs")));
				GitHubAccountPayload account = WaitForKind(sent, gate, MessageKinds.GitHubAccount)!
					.GetPayload<GitHubAccountPayload>()!;
				Assert.That(account.PublicationId, Is.Not.Null.And.Not.Empty);
				Assert.That(workspace.FindRepo("octo/specs"), Is.Null);

				controller.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.GitHubAccountApplied,
					new GitHubAccountAppliedPayload("wrong-publication")));
				Assert.That(workspace.FindRepo("octo/specs"), Is.Null);

				controller.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.GitHubAccountApplied,
					new GitHubAccountAppliedPayload(account.PublicationId!)));
				Assert.That(
					SpinWait.SpinUntil(
						() => workspace.FindRepo("octo/specs") is not null,
						TimeSpan.FromSeconds(2)),
					Is.True);
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

    // Handlers reply from a background task; poll briefly for the expected message.
    private static IpcMessage? WaitForKind(List<string> sent, object gate, string kind)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            lock (gate)
            {
                foreach (string json in sent)
                {
                    IpcMessage? message = IpcSerializer.TryDeserialize(json);
                    if (message is not null && message.Kind == kind)
                    {
                        return message;
                    }
                }
            }

            Thread.Sleep(20);
        }

        return null;
    }

    [Test]
	public void SignIn_emits_the_code_then_the_authorized_account()
    {
        (HostController controller, List<string> sent, object gate) = Build(new FakeGitHubAuth(SignInResult.Authorized("octocat")));
        using (controller)
        {
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignIn));

            IpcMessage? code = WaitForKind(sent, gate, MessageKinds.GitHubCode);
			IpcMessage? account = WaitForKind(sent, gate, MessageKinds.GitHubAccount);

            Assert.That(code, Is.Not.Null);
            Assert.That(account, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(code!.GetPayload<GitHubCodePayload>()!.UserCode, Is.EqualTo("WXYZ-1234"));
                GitHubAccountPayload payload = account!.GetPayload<GitHubAccountPayload>()!;
                Assert.That(payload.SignedIn, Is.True);
                Assert.That(payload.Login, Is.EqualTo("octocat"));
                Assert.That(payload.Available, Is.True);
            });
        }
    }

    [Test]
	public void A_non_authorized_signIn_emits_a_plain_language_message()
    {
        (HostController controller, List<string> sent, object gate) = Build(new FakeGitHubAuth(SignInResult.Expired()));
        using (controller)
        {
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignIn));

			IpcMessage? account = WaitForKind(sent, gate, MessageKinds.GitHubAccount);

            Assert.That(account, Is.Not.Null);
            GitHubAccountPayload payload = account!.GetPayload<GitHubAccountPayload>()!;
            Assert.Multiple(() =>
            {
                Assert.That(payload.SignedIn, Is.False);
                Assert.That(payload.Message, Does.Contain("expired"));
            });
        }
    }

    [Test]
	public void SignOut_clears_the_account()
    {
        FakeGitHubAuth auth = new(SignInResult.Authorized("octocat")) { SignedIn = true, Login = "octocat" };
        (HostController controller, List<string> sent, object gate) = Build(auth);
        using (controller)
        {
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignOut));

            IpcMessage? account = WaitForKind(sent, gate, MessageKinds.GitHubAccount);

            Assert.That(account, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(auth.SignOutCalls, Is.EqualTo(1));
                Assert.That(account!.GetPayload<GitHubAccountPayload>()!.SignedIn, Is.False);
            });
        }
    }

    [Test]
    public void Ready_emits_the_current_account()
    {
        FakeGitHubAuth auth = new(SignInResult.Authorized("octocat")) { SignedIn = true, Login = "octocat" };
        (HostController controller, List<string> sent, object gate) = Build(auth);
        using (controller)
        {
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));

            IpcMessage? account = WaitForKind(sent, gate, MessageKinds.GitHubAccount);

            Assert.That(account, Is.Not.Null);
            GitHubAccountPayload payload = account!.GetPayload<GitHubAccountPayload>()!;
            Assert.Multiple(() =>
            {
                Assert.That(payload.Available, Is.True);
                Assert.That(payload.SignedIn, Is.True);
                Assert.That(payload.Login, Is.EqualTo("octocat"));
            });
        }
    }

	[Test]
	public void Ready_refreshes_the_authorized_organizations()
	{
		FakeGitHubAuth auth = new(SignInResult.Authorized("octocat"))
		{
			SignedIn = true,
			Login = "octocat",
		};
		(HostController controller, List<string> sent, object gate) =
			Build(auth, repositoryCatalog: new AccountCatalog());
		using (controller)
		{
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));

			GitHubAccountPayload? account = null;
			for (int attempt = 0; attempt < 100 && account?.Organizations is null; attempt++)
			{
				lock (gate)
				{
					account = sent.Select(IpcSerializer.TryDeserialize)
						.Where(message => message?.Kind == MessageKinds.GitHubAccount)
						.Select(message => message!.GetPayload<GitHubAccountPayload>())
						.LastOrDefault(payload => payload?.Organizations is not null);
				}
				Thread.Sleep(20);
			}

			Assert.Multiple(() =>
			{
				Assert.That(account?.Organizations, Is.EqualTo(AuthorizedOrganizations));
				Assert.That(account?.AvatarUrl,
					Is.EqualTo("https://avatars.githubusercontent.com/u/583231?v=4"));
			});
		}
	}

	[Test]
	public void Account_refresh_discovers_organizations_approved_after_sign_in()
	{
		FakeGitHubAuth auth = new(SignInResult.Authorized("octocat"))
		{
			SignedIn = true,
			Login = "octocat",
		};
		ApprovingAccountCatalog catalog = new();
		(HostController controller, List<string> sent, object gate) =
			Build(auth, repositoryCatalog: catalog);
		using (controller)
		{
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
			Assert.That(
				SpinWait.SpinUntil(
					() => catalog.OrganizationRequests == 1 && catalog.RepositoryRequests == 1,
					TimeSpan.FromSeconds(2)),
				Is.True);
			lock (gate)
			{
				sent.Clear();
			}

			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubAccountRefresh));

			Assert.That(
				SpinWait.SpinUntil(() =>
				{
					lock (gate)
					{
						bool organizationPublished = sent.Select(IpcSerializer.TryDeserialize)
							.Where(message => message?.Kind == MessageKinds.GitHubAccount)
							.Select(message => message!.GetPayload<GitHubAccountPayload>())
							.Any(payload => payload?.Organizations?.SequenceEqual(["approved-org"]) == true);
						bool repositoryPublished = sent.Select(IpcSerializer.TryDeserialize)
							.Where(message => message?.Kind == MessageKinds.GitHubRepositories)
							.Select(message => message!.GetPayload<GitHubRepositoriesPayload>())
							.Any(payload => payload?.Repositories
								.Any(repository => repository.FullName == "approved-org/specs") == true);
						return organizationPublished && repositoryPublished;
					}
				}, TimeSpan.FromSeconds(2)),
				Is.True);

			Assert.Multiple(() =>
			{
				Assert.That(catalog.OrganizationRequests, Is.EqualTo(2));
				Assert.That(catalog.RepositoryRequests, Is.EqualTo(2));
			});
		}
	}

	[Test]
	public void SignOut_storage_failure_still_publishes_the_disconnected_account()
	{
		FakeGitHubAuth auth = new(SignInResult.Authorized("octocat"))
		{
			SignedIn = true,
			Login = "octocat",
			SignOutException = new IOException("token file is locked"),
		};
		(HostController controller, List<string> sent, object gate) = Build(auth);
		using (controller)
		{
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignOut));

			GitHubAccountPayload? account = WaitForKind(sent, gate, MessageKinds.GitHubAccount)
				?.GetPayload<GitHubAccountPayload>();

			Assert.Multiple(() =>
			{
				Assert.That(auth.SignedIn, Is.False);
				Assert.That(account?.SignedIn, Is.False);
				Assert.That(account?.Message, Does.Contain("saved GitHub authorization"));
			});
		}
	}

	[Test]
	public void Ready_publishes_accessible_repositories_for_autocomplete()
	{
		FakeGitHubAuth auth = new(SignInResult.Authorized("octocat"))
		{
			SignedIn = true,
			Login = "octocat",
		};
		(HostController controller, List<string> sent, object gate) =
			Build(auth, repositoryCatalog: new AccountCatalog());
		using (controller)
		{
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
			IpcMessage? message = WaitForKind(sent, gate, MessageKinds.GitHubRepositories);
			GitHubRepositoriesPayload? payload = message?.GetPayload<GitHubRepositoriesPayload>();
			Assert.That(payload?.Repositories.Select(repository => repository.FullName),
				Is.EqualTo(AccessibleRepositoryNames));
		}
	}

	[Test]
	public void A_stale_repository_catalog_result_cannot_replace_a_newer_refresh()
	{
		FakeGitHubAuth auth = new(SignInResult.Authorized("octocat"))
		{
			SignedIn = true,
			Login = "octocat",
		};
		RacingAccountCatalog catalog = new();
		(HostController controller, List<string> sent, object gate) = Build(auth, repositoryCatalog: catalog);
		using (controller)
		{
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
			Assert.That(catalog.FirstStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
			IpcMessage? current = WaitForKind(sent, gate, MessageKinds.GitHubRepositories);
			Assert.That(current?.GetPayload<GitHubRepositoriesPayload>()?.Repositories[0].FullName,
				Is.EqualTo("fresh/current"));

			catalog.ReleaseFirst.SetResult();
			Thread.Sleep(100);
			lock (gate)
			{
				string[] names = sent.Select(IpcSerializer.TryDeserialize)
					.Where(message => message?.Kind == MessageKinds.GitHubRepositories)
					.SelectMany(message => message!.GetPayload<GitHubRepositoriesPayload>()!.Repositories)
					.Select(repository => repository.FullName)
					.ToArray();
				Assert.That(names, Has.Length.EqualTo(1));
				Assert.That(names[0], Is.EqualTo("fresh/current"));
			}
		}
	}

	[Test]
	public void Organization_refresh_failure_replaces_loading_with_actionable_status()
	{
		FakeGitHubAuth auth = new(SignInResult.Authorized("octocat"))
		{
			SignedIn = true,
			Login = "octocat",
		};
		(HostController controller, List<string> sent, object gate) =
			Build(auth, repositoryCatalog: new FailingAccountCatalog());
		using (controller)
		{
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));

			GitHubAccountPayload? account = null;
			for (int attempt = 0; attempt < 100 && account?.Message is null; attempt++)
			{
				lock (gate)
				{
					account = sent.Select(IpcSerializer.TryDeserialize)
						.Where(message => message?.Kind == MessageKinds.GitHubAccount)
						.Select(message => message!.GetPayload<GitHubAccountPayload>())
						.LastOrDefault(payload => payload?.Message is not null);
				}
				Thread.Sleep(20);
			}

			Assert.Multiple(() =>
			{
				Assert.That(account?.SignedIn, Is.True);
				Assert.That(account?.Message, Does.Contain("refresh GitHub access"));
				Assert.That(account?.Organizations, Is.Empty);
			});
		}
	}

    [Test]
    public void An_unconfigured_host_reports_the_account_unavailable()
    {
        (HostController controller, List<string> sent, object gate) = Build(auth: null);
        using (controller)
        {
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
            // A sign-in request on an unconfigured host must not crash, and re-reports unavailable.
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignIn));

            IpcMessage? account = WaitForKind(sent, gate, MessageKinds.GitHubAccount);

            Assert.That(account, Is.Not.Null);
            Assert.That(account!.GetPayload<GitHubAccountPayload>()!.Available, Is.False);
        }
    }

    [Test]
    public void Cancelling_a_signIn_falls_back_to_signed_out()
    {
        FakeGitHubAuth auth = new(SignInResult.Authorized("octocat")) { BlockUntilCancelled = true };
        (HostController controller, List<string> sent, object gate) = Build(auth);
        using (controller)
        {
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignIn));
            // Wait for the code to appear (the flow is now blocked awaiting authorization), then cancel.
            Assert.That(WaitForKind(sent, gate, MessageKinds.GitHubCode), Is.Not.Null);

            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignInCancel));

            IpcMessage? account = WaitForKind(sent, gate, MessageKinds.GitHubAccount);
            Assert.That(account, Is.Not.Null);
            GitHubAccountPayload payload = account!.GetPayload<GitHubAccountPayload>()!;
            Assert.Multiple(() =>
            {
                Assert.That(payload.SignedIn, Is.False);
                // A user cancel must NOT surface the "code expired" message (the host folds TimedOut from
                // its own cancellation back to a plain signed-out state).
                Assert.That(payload.Message, Is.Null);
            });

            Thread.Sleep(100);
            lock (gate)
            {
                Assert.That(sent.Count(json =>
                    IpcSerializer.TryDeserialize(json)?.Kind == MessageKinds.GitHubAccount), Is.EqualTo(1));
            }
        }
    }

	[Test]
	public void Cancelling_a_signIn_reports_durable_cleanup_failure_but_stays_disconnected()
	{
		FakeGitHubAuth auth = new(SignInResult.Authorized("octocat"))
		{
			BlockUntilCancelled = true,
			SignOutException = new IOException("token marker is locked"),
		};
		(HostController controller, List<string> sent, object gate) = Build(auth);
		using (controller)
		{
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignIn));
			Assert.That(WaitForKind(sent, gate, MessageKinds.GitHubCode), Is.Not.Null);

			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignInCancel));

			GitHubAccountPayload payload = WaitForKind(sent, gate, MessageKinds.GitHubAccount)!
				.GetPayload<GitHubAccountPayload>()!;
			Assert.Multiple(() =>
			{
				Assert.That(auth.SignOutCalls, Is.EqualTo(1));
				Assert.That(payload.SignedIn, Is.False);
				Assert.That(payload.Login, Is.Null);
				Assert.That(payload.Message, Does.Contain("couldn't update the saved GitHub authorization"));
			});
		}
	}

    // Poll briefly for at least `count` messages of the given kind, returning the last one seen. Used where
    // a scenario emits the same kind more than once (a second sign-in's own GitHubCode) and the test must
    // look past the first occurrence rather than stop at it (WaitForKind always returns the first match).
    private static IpcMessage? WaitForNthKind(List<string> sent, object gate, string kind, int count)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            lock (gate)
            {
                IpcMessage? last = null;
                int seen = 0;
                foreach (string json in sent)
                {
                    IpcMessage? message = IpcSerializer.TryDeserialize(json);
                    if (message is not null && message.Kind == kind)
                    {
                        seen++;
                        last = message;
                    }
                }

                if (seen >= count)
                {
                    return last;
                }
            }

            Thread.Sleep(20);
        }

        return null;
    }

    [Test]
	public void A_cancelled_signIns_fallback_does_not_close_a_newer_signIns_code_prompt()
    {
        // Regression for M-14: start a sign-in, cancel it, then start a newer one before the cancelled
        // flow's background task unwinds. The stale flow's "signed out" fallback must stay quiet — only
        // the newer flow's own outcome may reach the webview.
        SequencedGitHubAuth auth = new();
        (HostController controller, List<string> sent, object gate) = Build(auth);
        using (controller)
        {
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignIn));
            Assert.That(WaitForKind(sent, gate, MessageKinds.GitHubCode), Is.Not.Null);

            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignInCancel));
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignIn));

            IpcMessage? newerCode = WaitForNthKind(sent, gate, MessageKinds.GitHubCode, count: 2);
            Assert.That(newerCode, Is.Not.Null);
            Assert.That(newerCode!.GetPayload<GitHubCodePayload>()!.UserCode, Is.EqualTo("CODE-2"));

			IpcMessage? account = WaitForNthKind(sent, gate, MessageKinds.GitHubAccount, count: 2);
            Assert.That(account, Is.Not.Null);
            GitHubAccountPayload payload = account!.GetPayload<GitHubAccountPayload>()!;
            Assert.Multiple(() =>
            {
				// The newer flow's own outcome must follow the cancel action's single terminal frame.
                Assert.That(payload.SignedIn, Is.True);
                Assert.That(payload.Login, Is.EqualTo("octocat"));
            });

			// Give the stale flow's own background task a further beat to (mis)behave. There must be exactly
			// two terminal account frames: one owned by Cancel and one owned by the newer successful flow.
            Thread.Sleep(100);
            int accountFrames;
            lock (gate)
            {
				accountFrames = sent
					.Select(IpcSerializer.TryDeserialize)
					.Where(message => message?.Kind == MessageKinds.GitHubAccount)
					.Select(message => message!.GetPayload<GitHubAccountPayload>())
					.Count(payload => payload?.Organizations is null);
            }

			Assert.That(accountFrames, Is.EqualTo(2));
        }
    }

    [Test]
    public void RegisteringWhileSignedOut_ContinuesAfterAuthorization()
    {
        string root = Path.Combine(Path.GetTempPath(), "specdesk-auth-register-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            FakeGitHubAuth auth = new(SignInResult.Authorized("octocat"));
            WorkspaceStore workspace = new(Path.Combine(root, "workspace.json"));
            (HostController controller, List<string> sent, object gate) = Build(auth, workspace);
            using (controller)
            {
                controller.OnMessage(IpcSerializer.SerializeEvent(
                    MessageKinds.RepoRegister, new RegisterRepoPayload("octo/specs")));

                Assert.That(WaitForKind(sent, gate, MessageKinds.GitHubCode), Is.Not.Null);
                Assert.That(WaitForKind(sent, gate, MessageKinds.GitHubAccount), Is.Not.Null);
                IpcMessage? state = WaitForKind(sent, gate, MessageKinds.WorkspaceState);
                Assert.That(state?.GetPayload<WorkspaceStatePayload>()?.Repositories.Select(repo => repo.Id),
                    Has.Member("octo/specs"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

	[Test]
	public void UnregisterBeforeAuthorizationTakesPendingRegistrationPreventsLaterRegistration()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-auth-unregister-before-take-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			OrdinaryRaceGitHubAuth auth = new(RaceStage.Terminal);
			WorkspaceStore workspace = new(Path.Combine(root, "workspace.json"));
			(HostController controller, _, _) = Build(auth, workspace);
			using (controller)
			using (ManualResetEventSlim resumed = new(false))
			{
				controller.PendingRepoActionsResumedForTest = resumed;
				controller.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.RepoRegister, new RegisterRepoPayload("octo/specs")));
				Assert.That(auth.FirstPaused.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
				controller.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.RepoUnregister, new UnregisterRepoPayload("octo/specs")));
				auth.ReleaseFirst.SetResult();
				Assert.That(resumed.Wait(TimeSpan.FromSeconds(2)), Is.True);

				Assert.That(workspace.FindRepo("octo/specs"), Is.Null);
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void UnregisterAfterAuthorizationTakesPendingRegistrationPreventsResumeFromRegistering()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-auth-unregister-after-take-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			FakeGitHubAuth auth = new(SignInResult.Authorized("octocat"));
			WorkspaceStore workspace = new(Path.Combine(root, "workspace.json"));
			(HostController controller, _, _) = Build(auth, workspace);
			using (controller)
			using (ManualResetEventSlim taken = new(false))
			using (ManualResetEventSlim release = new(false))
			using (ManualResetEventSlim resumed = new(false))
			{
				controller.PendingRepoActionsTakenForTest = taken;
				controller.PendingRepoActionsResumeForTest = release;
				controller.PendingRepoActionsResumedForTest = resumed;
				try
				{
					controller.OnMessage(IpcSerializer.SerializeEvent(
						MessageKinds.RepoRegister, new RegisterRepoPayload("octo/specs")));
					Assert.That(taken.Wait(TimeSpan.FromSeconds(2)), Is.True);
					controller.OnMessage(IpcSerializer.SerializeEvent(
						MessageKinds.RepoUnregister, new UnregisterRepoPayload("octo/specs")));
				}
				finally
				{
					release.Set();
				}
				Assert.That(resumed.Wait(TimeSpan.FromSeconds(2)), Is.True);

				Assert.That(workspace.FindRepo("octo/specs"), Is.Null);
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void SignOutAfterAuthorizationTakesPendingActionPreventsStaleAccountPublication()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-auth-signout-after-take-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			FakeGitHubAuth auth = new(SignInResult.Authorized("octocat"));
			WorkspaceStore workspace = new(Path.Combine(root, "workspace.json"));
			(HostController controller, List<string> sent, object gate) = Build(auth, workspace);
			using (controller)
			using (ManualResetEventSlim taken = new(false))
			using (ManualResetEventSlim release = new(false))
			{
				controller.PendingRepoActionsTakenForTest = taken;
				controller.PendingRepoActionsResumeForTest = release;
				try
				{
					controller.OnMessage(IpcSerializer.SerializeEvent(
						MessageKinds.RepoRegister, new RegisterRepoPayload("octo/specs")));
					Assert.That(taken.Wait(TimeSpan.FromSeconds(2)), Is.True);
					controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignOut));
				}
				finally
				{
					release.Set();
				}

				Assert.That(SpinWait.SpinUntil(() =>
				{
					lock (gate)
					{
						return sent.Any(json =>
							IpcSerializer.TryDeserialize(json)?.Kind == MessageKinds.GitHubAccount);
					}
				}, TimeSpan.FromSeconds(2)), Is.True);
				Thread.Sleep(100);
				lock (gate)
				{
					GitHubAccountPayload[] accounts = sent
						.Select(IpcSerializer.TryDeserialize)
						.Where(message => message?.Kind == MessageKinds.GitHubAccount)
						.Select(message => message!.GetPayload<GitHubAccountPayload>()!)
						.ToArray();
					Assert.That(accounts, Is.Not.Empty);
					Assert.That(accounts[^1].SignedIn, Is.False);
					Assert.That(accounts.Any(account => account.SignedIn), Is.False);
				}
				Assert.That(workspace.FindRepo("octo/specs"), Is.Null);
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void CancelingANewerSignInRetiresAnOlderClaimedAccountPublication()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-auth-newer-cancel-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			AuthorizedThenBlockingGitHubAuth auth = new();
			WorkspaceStore workspace = new(Path.Combine(root, "workspace.json"));
			(HostController controller, List<string> sent, object gate) = Build(auth, workspace);
			using (controller)
			using (ManualResetEventSlim taken = new(false))
			using (ManualResetEventSlim release = new(false))
			{
				controller.PendingRepoActionsTakenForTest = taken;
				controller.PendingRepoActionsResumeForTest = release;
				try
				{
					controller.OnMessage(IpcSerializer.SerializeEvent(
						MessageKinds.RepoRegister, new RegisterRepoPayload("octo/specs")));
					Assert.That(taken.Wait(TimeSpan.FromSeconds(2)), Is.True);

					controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignIn));
					Assert.That(auth.SecondFlowStarted.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
					controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignInCancel));
				}
				finally
				{
					release.Set();
				}

				Thread.Sleep(150);
				lock (gate)
				{
					GitHubAccountPayload[] accounts = sent
						.Select(IpcSerializer.TryDeserialize)
						.Where(message => message?.Kind == MessageKinds.GitHubAccount)
						.Select(message => message!.GetPayload<GitHubAccountPayload>()!)
						.ToArray();
					Assert.That(accounts, Is.Not.Empty);
					Assert.That(accounts.Any(account => account.SignedIn), Is.False);
				}
				Assert.That(workspace.FindRepo("octo/specs"), Is.Null);
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[TestCase("open")]
	[TestCase("clone")]
	[TestCase("cloneToFolder")]
	public void UnregisterBeforeAuthorizationTakesPendingRepositoryNavigationPreventsResurrection(
		string action)
	{
		string root = Path.Combine(
			Path.GetTempPath(), "specdesk-auth-nav-unregister-before-take-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			const string id = "octo/specs";
			OrdinaryRaceGitHubAuth auth = new(RaceStage.Terminal);
			WorkspaceStore workspace = new(Path.Combine(root, "workspace.json"));
			workspace.RegisterRepo(new RegisteredRepo(id, id, $"https://github.com/{id}", "main", []));
			CreatingCloner cloner = new(Path.Combine(root, "cloned"));
			(HostController controller, _, _) = Build(
				auth, workspace, cloner, dialogs: new NoDialogs(root));
			using (controller)
			using (ManualResetEventSlim resumed = new(false))
			{
				controller.PendingRepoActionsResumedForTest = resumed;
				SendPendingRepositoryNavigation(controller, action, id);
				Assert.That(auth.FirstPaused.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);

				controller.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.RepoUnregister, new UnregisterRepoPayload(id)));
				auth.ReleaseFirst.SetResult();
				Assert.That(resumed.Wait(TimeSpan.FromSeconds(2)), Is.True);
				Thread.Sleep(100);

				Assert.Multiple(() =>
				{
					Assert.That(workspace.FindRepo(id), Is.Null);
					Assert.That(cloner.CloneCalls, Is.Zero);
				});
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[TestCase("open")]
	[TestCase("clone")]
	[TestCase("cloneToFolder")]
	public void UnregisterAfterAuthorizationTakesPendingRepositoryNavigationPreventsResurrection(
		string action)
	{
		string root = Path.Combine(
			Path.GetTempPath(), "specdesk-auth-nav-unregister-after-take-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			const string id = "octo/specs";
			FakeGitHubAuth auth = new(SignInResult.Authorized("octocat"));
			WorkspaceStore workspace = new(Path.Combine(root, "workspace.json"));
			workspace.RegisterRepo(new RegisteredRepo(id, id, $"https://github.com/{id}", "main", []));
			CreatingCloner cloner = new(Path.Combine(root, "cloned"));
			(HostController controller, _, _) = Build(
				auth, workspace, cloner, dialogs: new NoDialogs(root));
			using (controller)
			using (ManualResetEventSlim taken = new(false))
			using (ManualResetEventSlim release = new(false))
			using (ManualResetEventSlim resumed = new(false))
			{
				controller.PendingRepoActionsTakenForTest = taken;
				controller.PendingRepoActionsResumeForTest = release;
				controller.PendingRepoActionsResumedForTest = resumed;
				try
				{
					SendPendingRepositoryNavigation(controller, action, id);
					Assert.That(taken.Wait(TimeSpan.FromSeconds(2)), Is.True);
					controller.OnMessage(IpcSerializer.SerializeEvent(
						MessageKinds.RepoUnregister, new UnregisterRepoPayload(id)));
				}
				finally
				{
					release.Set();
				}
				Assert.That(resumed.Wait(TimeSpan.FromSeconds(2)), Is.True);
				Thread.Sleep(100);

				Assert.Multiple(() =>
				{
					Assert.That(workspace.FindRepo(id), Is.Null);
					Assert.That(cloner.CloneCalls, Is.Zero);
				});
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private static void SendPendingRepositoryNavigation(
		HostController controller, string action, string id)
	{
		string message = action switch
		{
			"open" => IpcSerializer.SerializeEvent(
				MessageKinds.RepoOpen, new RepoOpenPayload(id)),
			"clone" => IpcSerializer.SerializeEvent(
				MessageKinds.RepoClone, new RepoClonePayload(id)),
			"cloneToFolder" => IpcSerializer.SerializeEvent(
				MessageKinds.RepoCloneToFolder, new RepoCloneToFolderPayload(id, "specs-copy")),
			_ => throw new ArgumentOutOfRangeException(nameof(action)),
		};
		controller.OnMessage(message);
	}

	[Test]
	public void UnregisterWhileMetadataIsLoadingPreventsDelayedMetadataFromRestoringRegistration()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-auth-unregister-metadata-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			FakeGitHubAuth auth = new(SignInResult.Authorized("octocat"))
			{
				SignedIn = true,
				Login = "octocat",
			};
			BlockingMetadataCatalog catalog = new();
			WorkspaceStore workspace = new(Path.Combine(root, "workspace.json"));
			(HostController controller, _, _) = Build(
				auth, workspace, repositoryCatalog: catalog);
			using (controller)
			{
				controller.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.RepoRegister, new RegisterRepoPayload("octo/specs")));
				Assert.That(catalog.Started.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
				Assert.That(workspace.FindRepo("octo/specs"), Is.Not.Null);

				controller.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.RepoUnregister, new UnregisterRepoPayload("octo/specs")));
				catalog.Release.SetResult();

				Assert.That(SpinWait.SpinUntil(
					() => MetadataLookupCount(controller) == 0,
					TimeSpan.FromSeconds(2)), Is.True);
				Assert.That(workspace.FindRepo("octo/specs"), Is.Null);
			}
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void SignOutBetweenImmediateRegistrationAndMetadataLeaseRollsBackTheNewDescriptor()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-auth-register-boundary-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		using ManualResetEventSlim registrationPublished = new(false);
		using ManualResetEventSlim releaseRegistration = new(false);
		HostController? controller = null;
		try
		{
			FakeGitHubAuth auth = new(SignInResult.Authorized("octocat"))
			{
				SignedIn = true,
				Login = "octocat",
			};
			WorkspaceStore workspace = new(Path.Combine(root, "workspace.json"));
			BlockingMetadataCatalog catalog = new();
			(HostController built, _, _) = Build(
				auth,
				workspace,
				repositoryCatalog: catalog);
			controller = built;
			controller.RepoRegistrationPublishedForTest = registrationPublished;
			controller.RepoRegistrationResumeForTest = releaseRegistration;
			Task registration = Task.Run(() => controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoRegister, new RegisterRepoPayload("octo/specs"))));
			Assert.That(registrationPublished.Wait(TimeSpan.FromSeconds(2)), Is.True);

			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignOut));
			releaseRegistration.Set();

			Assert.That(registration.Wait(TimeSpan.FromSeconds(2)), Is.True);
			Assert.Multiple(() =>
			{
				Assert.That(workspace.FindRepo("octo/specs"), Is.Null);
				Assert.That(catalog.Started.Task.IsCompleted, Is.False);
			});
		}
		finally
		{
			releaseRegistration.Set();
			controller?.Dispose();
			Directory.Delete(root, recursive: true);
		}
	}

	private static int MetadataLookupCount(HostController controller)
	{
		System.Reflection.FieldInfo? field = typeof(HostController).GetField(
			"_repoMetadataLookups",
			System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
		return ((System.Collections.ICollection?)field?.GetValue(controller))?.Count ?? -1;
	}

    [Test]
    public void OpeningWhileSignedOut_ClonesAndOpensAfterAuthorization()
    {
        string root = Path.Combine(Path.GetTempPath(), "specdesk-auth-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            FakeGitHubAuth auth = new(SignInResult.Authorized("octocat"));
            WorkspaceStore workspace = new(Path.Combine(root, "workspace.json"));
            CreatingCloner cloner = new(Path.Combine(root, "clone"));
            (HostController controller, List<string> sent, object gate) = Build(auth, workspace, cloner);
            using (controller)
            {
                controller.OnMessage(IpcSerializer.SerializeEvent(
                    MessageKinds.RepoOpen, new RepoOpenPayload("octo/specs")));

                Assert.That(WaitForKind(sent, gate, MessageKinds.GitHubCode), Is.Not.Null);
                Assert.That(WaitForKind(sent, gate, MessageKinds.GitHubAccount), Is.Not.Null);
                Assert.That(WaitForKind(sent, gate, MessageKinds.Tree), Is.Not.Null);
                Assert.That(cloner.CloneCalls, Is.EqualTo(1));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void AStaleStartFailure_DoesNotClearOrOverwriteANewerAuthorization()
    {
        string root = Path.Combine(Path.GetTempPath(), "specdesk-auth-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RacingStartGitHubAuth auth = new();
            WorkspaceStore workspace = new(Path.Combine(root, "workspace.json"));
            (HostController controller, List<string> sent, object gate) = Build(auth, workspace);
            using (controller)
            {
                controller.OnMessage(IpcSerializer.SerializeEvent(
                    MessageKinds.RepoRegister, new RegisterRepoPayload("octo/specs")));
                Assert.That(auth.FirstStartEntered.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);

                controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignIn));
                Assert.That(WaitForKind(sent, gate, MessageKinds.GitHubAccount), Is.Not.Null);
                auth.ReleaseFirstStart.SetResult();
                Thread.Sleep(100);

                lock (gate)
                {
                    GitHubAccountPayload[] accounts = sent
                        .Select(IpcSerializer.TryDeserialize)
                        .Where(message => message?.Kind == MessageKinds.GitHubAccount)
                        .Select(message => message!.GetPayload<GitHubAccountPayload>()!)
						.Where(payload => payload.Organizations is null)
                        .ToArray();
                    Assert.That(accounts, Has.Length.EqualTo(1));
                    Assert.That(accounts[0].SignedIn, Is.True);
                }

                Assert.That(workspace.State().Repositories.Select(repo => repo.Id), Has.Member("octo/specs"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void AStalePrompt_IsNeverPublishedAfterANewerFlowStarts()
    {
        OrdinaryRaceGitHubAuth auth = new(RaceStage.Prompt);
        (HostController controller, List<string> sent, object gate) = Build(auth);
        using (controller)
        {
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignIn));
            Assert.That(auth.FirstPaused.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignIn));
            IpcMessage? newer = WaitForKind(sent, gate, MessageKinds.GitHubCode);
            Assert.That(newer?.GetPayload<GitHubCodePayload>()?.UserCode, Is.EqualTo("NEW-CODE"));
            auth.ReleaseFirst.SetResult();
            Thread.Sleep(100);

            lock (gate)
            {
                string[] codes = sent
                    .Select(IpcSerializer.TryDeserialize)
                    .Where(message => message?.Kind == MessageKinds.GitHubCode)
                    .Select(message => message!.GetPayload<GitHubCodePayload>()!.UserCode)
                    .ToArray();
				Assert.That(codes, Has.Length.EqualTo(1));
				Assert.That(codes[0], Is.EqualTo("NEW-CODE"));
            }
        }
    }

    [Test]
	public void AStaleOrdinaryAuthorizedResult_IsSilentAfterANewerFlowWins()
    {
        OrdinaryRaceGitHubAuth auth = new(RaceStage.Terminal);
        (HostController controller, List<string> sent, object gate) = Build(auth);
        using (controller)
        {
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignIn));
            Assert.That(auth.FirstPaused.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignIn));
            IpcMessage? account = WaitForKind(sent, gate, MessageKinds.GitHubAccount);
            Assert.That(account?.GetPayload<GitHubAccountPayload>()?.Login, Is.EqualTo("new-user"));
            auth.ReleaseFirst.SetResult();
            Thread.Sleep(100);

            lock (gate)
            {
                GitHubAccountPayload[] accounts = sent
                    .Select(IpcSerializer.TryDeserialize)
                    .Where(message => message?.Kind == MessageKinds.GitHubAccount)
                    .Select(message => message!.GetPayload<GitHubAccountPayload>()!)
					.Where(payload => payload.Organizations is null)
                    .ToArray();
                Assert.That(accounts, Has.Length.EqualTo(1));
                Assert.That(accounts[0].Login, Is.EqualTo("new-user"));
			}
		}
	}

	[Test]
	public void Dispose_WaitsForClaimedTerminalPublicationAndNoWorkContinuesAfterReturn()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-auth-dispose-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			using ManualResetEventSlim terminalEntered = new();
			using ManualResetEventSlim releaseTerminal = new();
			FakeGitHubAuth auth = new(SignInResult.Authorized("octocat"));
			WorkspaceStore workspace = new(Path.Combine(root, "workspace.json"));
			(HostController controller, List<string> sent, object gate) = Build(
				auth,
				workspace,
				beforeSend: json =>
				{
					IpcMessage? message = IpcSerializer.TryDeserialize(json);
					if (message?.Kind == MessageKinds.GitHubAccount
						&& message.GetPayload<GitHubAccountPayload>()?.SignedIn == true)
					{
						terminalEntered.Set();
						releaseTerminal.Wait(TimeSpan.FromSeconds(2));
					}
				});

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoRegister,
				new RegisterRepoPayload("octo/specs")));
			Assert.That(terminalEntered.Wait(TimeSpan.FromSeconds(2)), Is.True);

			Task dispose = Task.Run(controller.Dispose);
			Assert.That(dispose.Wait(TimeSpan.FromMilliseconds(100)), Is.False,
				"Dispose must wait for the terminal publication gate");
			releaseTerminal.Set();
			Assert.That(dispose.Wait(TimeSpan.FromSeconds(2)), Is.True);

			Assert.That(workspace.State().Repositories.Any(repo => repo.Id == "octo/specs"), Is.True);
			int sentAfterDispose;
			lock (gate)
			{
				sentAfterDispose = sent.Count;
			}
			Thread.Sleep(100);
			lock (gate)
			{
				Assert.That(sent, Has.Count.EqualTo(sentAfterDispose));
			}
			Assert.That(workspace.State().Repositories, Has.Count.EqualTo(1));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void MessagesAfterDispose_CannotStartAuthorizationOrRepositoryWork()
	{
		string root = Path.Combine(Path.GetTempPath(), "specdesk-auth-after-dispose-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			FakeGitHubAuth auth = new(SignInResult.Authorized("octocat"));
			WorkspaceStore workspace = new(Path.Combine(root, "workspace.json"));
			(HostController controller, List<string> sent, object gate) = Build(auth, workspace);
			controller.Dispose();

			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignIn));
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.RepoRegister,
				new RegisterRepoPayload("octo/specs")));
			Thread.Sleep(100);

			Assert.That(auth.StartCalls, Is.Zero);
			Assert.That(workspace.State().Repositories, Is.Empty);
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
}
