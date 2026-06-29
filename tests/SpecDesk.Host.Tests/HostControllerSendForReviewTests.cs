using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;
using SpecDesk.GitHub;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

// The "Send for review" round-trip: a draft pushes its branch and opens a pull request via the injected
// IGitPublishing + IGitHubReview (the network is faked), then the document moves to In review. Gating —
// a connected account and a GitHub remote — is exercised here too.
[TestFixture]
public sealed class HostControllerSendForReviewTests
{
    private sealed class NoDialogs : IFileDialogs
    {
        public string? PickOpenFile() => null;

        public string? PickSaveFile(string? suggestedPath) => null;
    }

    private static Renderer.RenderResult StubRender(string docDir, string text) => new(string.Empty, []);

    // A minimal IGitHubAuth: signed-in state + a token handed transiently to WithAccessTokenAsync. The
    // device-flow members are unused here (the sign-in UX has its own tests).
    private sealed class StubAuth(bool signedIn, string accessToken = "gho_test") : IGitHubAuth
    {
        public Task<DeviceCodePrompt> StartSignInAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SignInResult> AwaitAuthorizationAsync(
            DeviceCodePrompt prompt, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public bool IsSignedIn() => signedIn;

        public string? SignedInLogin() => signedIn ? "octocat" : null;

        public Task<T> WithAccessTokenAsync<T>(
            Func<string, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(use);
            if (!signedIn)
            {
                throw new InvalidOperationException("Not signed in to GitHub.");
            }

            return use(accessToken, cancellationToken);
        }

        public void SignOut()
        {
        }
    }

    // Records the OpenPullRequestAsync call and returns a canned PR (or throws, to exercise the failure path).
    private sealed class FakeGitHubReview : IGitHubReview
    {
        public int Calls { get; private set; }

        public string? Token { get; private set; }

        public string? Owner { get; private set; }

        public string? Repo { get; private set; }

        public string? Head { get; private set; }

        public string? Base { get; private set; }

        public string? Title { get; private set; }

        public string? Body { get; private set; }

        public bool ThrowOnOpen { get; init; }

        /// <summary>When set, the call blocks (after recording its arguments) until released — so a test
        /// can keep one round-trip in flight and assert a concurrent send is single-flighted away.</summary>
        public ManualResetEventSlim? ReleaseGate { get; init; }

        public Task<PullRequest> OpenPullRequestAsync(
            string accessToken, string owner, string repo, string head, string baseBranch,
            string title, string body, CancellationToken cancellationToken = default)
        {
            Calls++;
            Token = accessToken;
            Owner = owner;
            Repo = repo;
            Head = head;
            Base = baseBranch;
            Title = title;
            Body = body;
            if (ThrowOnOpen)
            {
                throw new HttpRequestException("GitHub rejected the pull-request create (HTTP 422).");
            }

            // Block in flight until the test releases it (bounded so a wiring bug fails fast, not hangs).
            ReleaseGate?.Wait(TimeSpan.FromSeconds(10), cancellationToken);
            return Task.FromResult(new PullRequest(42, $"https://github.com/{owner}/{repo}/pull/42"));
        }
    }

    private string _tempDir = string.Empty;
    private string _docPath = string.Empty;
    private readonly List<string> _sent = [];
    private readonly object _gate = new();

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "specdesk-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _docPath = Path.Combine(_tempDir, "billing.md");
        File.WriteAllText(_docPath, "# Billing");
        lock (_gate)
        {
            _sent.Clear();
        }
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    // Builds a controller over the temp doc with the given fakes, loads the doc (Ready), and — unless
    // asked not to — starts a draft (Edit) so the document is in the Draft state ready to send.
    private HostController Build(
        FakeVersioning versioning, IGitHubAuth auth, FakeGitHubReview review, bool startDraft = true)
    {
        void Send(string json)
        {
            lock (_gate)
            {
                _sent.Add(json);
            }
        }

        HostController controller = new(
            StubRender, Send, new NoDialogs(), (_, _, _, _, _) => null,
            versioning, NullLogger<HostController>.Instance, _docPath,
            auth: auth, publishing: versioning, review: review);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
        if (startDraft)
        {
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));
        }

        return controller;
    }

    [Test]
    public void SendForReview_pushes_the_branch_opens_a_pr_and_moves_to_in_review()
    {
        FakeVersioning versioning = new();
        FakeGitHubReview review = new();
        using HostController controller = Build(versioning, new StubAuth(signedIn: true), review);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));

        Assert.That(WaitForStatusState("inReview"), Is.True, "the document should reach In review");
        Assert.Multiple(() =>
        {
            Assert.That(versioning.PushBranchCalls, Is.EqualTo(1));
            Assert.That(versioning.PushedToken, Is.EqualTo("gho_test"));
            Assert.That(review.Calls, Is.EqualTo(1));
            Assert.That(review.Owner, Is.EqualTo("octo"));
            Assert.That(review.Repo, Is.EqualTo("spec-repo"));
            Assert.That(review.Head, Does.StartWith("spec/billing-"));
            Assert.That(review.Base, Is.EqualTo("main"));
            // The PR title is seeded from the author's last version note.
            Assert.That(review.Title, Is.EqualTo("Clarify the refund window"));
            Assert.That(review.Body, Does.Contain("billing.md"));
            // The branch pushed is the one the PR is opened from.
            Assert.That(versioning.PushedBranch, Is.EqualTo(review.Head));
        });
    }

    [Test]
    public void SendForReview_falls_back_to_a_generated_title_without_a_version_note()
    {
        FakeVersioning versioning = new() { LastNoteValue = null };
        FakeGitHubReview review = new();
        using HostController controller = Build(versioning, new StubAuth(signedIn: true), review);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));

        Assert.That(WaitForStatusState("inReview"), Is.True);
        Assert.That(review.Title, Is.EqualTo("Review: billing.md"));
    }

    [Test]
    public void SendForReview_without_a_saved_version_asks_to_save_first_and_does_not_push()
    {
        // The draft is level with its base (nothing committed to review). The author gets actionable
        // guidance — not a misleading network error — and nothing is pushed.
        FakeVersioning versioning = new() { HasCommitsValue = false };
        FakeGitHubReview review = new();
        using HostController controller = Build(versioning, new StubAuth(signedIn: true), review);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));

        IpcMessage? error = WaitForKind(MessageKinds.Error);
        Assert.That(error, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(error!.GetPayload<ErrorPayload>()!.Message, Does.Contain("Save a version"));
            Assert.That(versioning.PushBranchCalls, Is.EqualTo(0));
            Assert.That(review.Calls, Is.EqualTo(0));
            Assert.That(LatestStatus()?.State, Is.EqualTo("draft"));
        });
    }

    [Test]
    public void SendForReview_while_signed_out_reports_an_error_and_does_not_push()
    {
        FakeVersioning versioning = new();
        FakeGitHubReview review = new();
        using HostController controller = Build(versioning, new StubAuth(signedIn: false), review);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));

        Assert.That(WaitForKind(MessageKinds.Error), Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(versioning.PushBranchCalls, Is.EqualTo(0));
            Assert.That(review.Calls, Is.EqualTo(0));
            Assert.That(LatestStatus()?.State, Is.EqualTo("draft"));
        });
    }

    [Test]
    public void SendForReview_on_a_non_github_remote_reports_an_error_and_does_not_push()
    {
        FakeVersioning versioning = new() { RemoteUrlValue = "https://gitlab.com/octo/spec-repo.git" };
        FakeGitHubReview review = new();
        using HostController controller = Build(versioning, new StubAuth(signedIn: true), review);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));

        Assert.That(WaitForKind(MessageKinds.Error), Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(versioning.PushBranchCalls, Is.EqualTo(0));
            Assert.That(review.Calls, Is.EqualTo(0));
            Assert.That(LatestStatus()?.State, Is.EqualTo("draft"));
        });
    }

    [Test]
    public void SendForReview_when_the_pr_create_fails_reports_an_error_and_stays_in_draft()
    {
        FakeVersioning versioning = new();
        FakeGitHubReview review = new() { ThrowOnOpen = true };
        using HostController controller = Build(versioning, new StubAuth(signedIn: true), review);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));

        Assert.That(WaitForKind(MessageKinds.Error), Is.Not.Null);
        Assert.Multiple(() =>
        {
            // The push happened, but the PR create failed — the document must NOT advance to In review.
            Assert.That(review.Calls, Is.EqualTo(1));
            Assert.That(LatestStatus()?.State, Is.EqualTo("draft"));
        });
    }

    [Test]
    public void SendForReview_recovers_after_a_repo_read_fault_and_is_not_wedged()
    {
        FakeVersioning versioning = new() { ThrowOnRemoteUrl = true };
        FakeGitHubReview review = new();
        using HostController controller = Build(versioning, new StubAuth(signedIn: true), review);

        // First attempt: the synchronous repo read throws — the author sees an error, no PR opens.
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));
        Assert.That(WaitForKind(MessageKinds.Error), Is.Not.Null);
        Assert.That(review.Calls, Is.EqualTo(0));

        // The fault must NOT have wedged the single-flight claim: a retry once the read recovers succeeds.
        versioning.ThrowOnRemoteUrl = false;
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));

        Assert.That(WaitForStatusState("inReview"), Is.True, "a retry after the fault should still work");
        Assert.That(review.Calls, Is.EqualTo(1));
    }

    [Test]
    public void SendForReview_single_flights_a_concurrent_second_request()
    {
        using ManualResetEventSlim gate = new(initialState: false);
        FakeVersioning versioning = new();
        FakeGitHubReview review = new() { ReleaseGate = gate };
        using HostController controller = Build(versioning, new StubAuth(signedIn: true), review);

        // First send: reaches the (blocked) PR call and stays in flight.
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));
        Assert.That(WaitUntil(() => review.Calls == 1), Is.True, "the first send should reach the PR call");

        // Second send while the first is in flight must be dropped — no second push, no second PR.
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));
        Thread.Sleep(60);
        Assert.Multiple(() =>
        {
            Assert.That(review.Calls, Is.EqualTo(1));
            Assert.That(versioning.PushBranchCalls, Is.EqualTo(1));
        });

        gate.Set();
        Assert.That(WaitForStatusState("inReview"), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(review.Calls, Is.EqualTo(1));
            Assert.That(versioning.PushBranchCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public void SendForReview_from_published_is_ignored()
    {
        FakeVersioning versioning = new();
        FakeGitHubReview review = new();
        // No draft started → the document is Published, where Send for review is not a legal transition.
        using HostController controller = Build(versioning, new StubAuth(signedIn: true), review, startDraft: false);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));

        Assert.Multiple(() =>
        {
            Assert.That(versioning.PushBranchCalls, Is.EqualTo(0));
            Assert.That(review.Calls, Is.EqualTo(0));
        });
    }

    private bool WaitForStatusState(string state) => WaitUntil(() => LatestStatus()?.State == state);

    private static bool WaitUntil(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    private IpcMessage? WaitForKind(string kind)
    {
        for (int attempt = 0; attempt < 200; attempt++)
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

            Thread.Sleep(20);
        }

        return null;
    }

    private StatusPayload? LatestStatus()
    {
        lock (_gate)
        {
            for (int i = _sent.Count - 1; i >= 0; i--)
            {
                IpcMessage? message = IpcSerializer.TryDeserialize(_sent[i]);
                if (message is not null && message.Kind == MessageKinds.Status)
                {
                    return message.GetPayload<StatusPayload>();
                }
            }
        }

        return null;
    }
}
