using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;
using SpecDesk.GitHub;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

// The GitHub review round-trips, over the injected IGitPublishing + IGitHubReview (the network is faked):
//   • Send for review — a draft pushes its branch and opens a pull request, then moves to In review.
//   • Update review — a draft already under review pushes its newly-saved versions to the open PR (no
//     second PR), re-settling at In review.
// Gating — a connected account and a GitHub remote — and single-flighting are exercised for both.
[TestFixture]
public sealed class HostControllerReviewTests
{
    private sealed class NoDialogs : IFileDialogs
    {
        public string? PickOpenFile() => null;

        public string? PickSaveFile(string? suggestedPath) => null;
    }

    private static Renderer.RenderResult StubRender(string docDir, string text) => new(string.Empty, []);

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
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

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
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

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
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

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
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: false), review);

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
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

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
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

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
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

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
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

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
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review, startDraft: false);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));

        Assert.Multiple(() =>
        {
            Assert.That(versioning.PushBranchCalls, Is.EqualTo(0));
            Assert.That(review.Calls, Is.EqualTo(0));
        });
    }

    // Drive a freshly-built draft all the way to In review (Send for review succeeds), so an Update
    // review test starts from an open pull request (one push + one PR already recorded). Returns once
    // the status has settled at In review.
    private HostController BuildInReview(
        FakeVersioning versioning, FakeGitHubReview review, FakeGitHubAuth? auth = null)
    {
        HostController controller = Build(versioning, auth ?? new FakeGitHubAuth(signedIn: true), review);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));
        Assert.That(WaitForStatusState("inReview"), Is.True, "setup: the draft should reach In review");
        return controller;
    }

    // Save a new version through the host (a committed Save a version), so the draft has a version not yet
    // shared with the review — otherwise Update review's "nothing new" guard short-circuits. OnSaveVersion
    // runs synchronously, so no wait is needed.
    private static void SaveAVersion(HostController controller) =>
        controller.OnMessage(
            IpcSerializer.SerializeEvent(MessageKinds.DocSaveVersion, new SaveVersionPayload("More edits")));

    [Test]
    public void UpdateReview_pushes_the_branch_again_to_the_open_pr_and_stays_in_review()
    {
        FakeVersioning versioning = new();
        FakeGitHubReview review = new();
        using HostController controller = BuildInReview(versioning, review);
        // The setup send did exactly one push and opened exactly one pull request.
        Assert.That(versioning.PushBranchCalls, Is.EqualTo(1));

        SaveAVersion(controller);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocUpdateReview));

        Assert.That(
            WaitUntil(() => versioning.PushBranchCalls == 2), Is.True, "Update review should push the branch again");
        Assert.Multiple(() =>
        {
            Assert.That(versioning.PushedToken, Is.EqualTo("gho_test"));
            // No second pull request is opened — the existing PR already tracks the head branch.
            Assert.That(review.Calls, Is.EqualTo(1));
            Assert.That(LatestStatus()?.State, Is.EqualTo("inReview"));
        });
    }

    [Test]
    public void UpdateReview_while_signed_out_reports_an_error_and_does_not_push()
    {
        FakeVersioning versioning = new();
        FakeGitHubAuth auth = new(signedIn: true);
        using HostController controller = BuildInReview(versioning, new FakeGitHubReview(), auth);

        // A new version is waiting to share, but the account is disconnected before the update.
        SaveAVersion(controller);
        auth.SignedIn = false;
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocUpdateReview));

        Assert.That(WaitForKind(MessageKinds.Error), Is.Not.Null);
        Assert.Multiple(() =>
        {
            // Only the setup send pushed; the update pushed nothing and left the state untouched.
            Assert.That(versioning.PushBranchCalls, Is.EqualTo(1));
            Assert.That(LatestStatus()?.State, Is.EqualTo("inReview"));
        });
    }

    [Test]
    public void UpdateReview_on_a_non_github_remote_reports_an_error_and_does_not_push()
    {
        FakeVersioning versioning = new();
        using HostController controller = BuildInReview(versioning, new FakeGitHubReview());

        // A new version is waiting, but the remote is re-pointed off GitHub after review opened.
        SaveAVersion(controller);
        versioning.RemoteUrlValue = "https://gitlab.com/octo/spec-repo.git";
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocUpdateReview));

        Assert.That(WaitForKind(MessageKinds.Error), Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(versioning.PushBranchCalls, Is.EqualTo(1));
            Assert.That(LatestStatus()?.State, Is.EqualTo("inReview"));
        });
    }

    [Test]
    public void UpdateReview_single_flights_a_concurrent_second_request()
    {
        using ManualResetEventSlim gate = new(initialState: false);
        FakeVersioning versioning = new();
        using HostController controller = BuildInReview(versioning, new FakeGitHubReview());

        // A version to share, then gate only the update's push (the setup send already completed its push).
        SaveAVersion(controller);
        versioning.PushGate = gate;
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocUpdateReview));
        Assert.That(WaitUntil(() => versioning.PushBranchCalls == 2), Is.True, "the first update should reach the push");

        // Second update while the first is in flight must be dropped — no third push.
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocUpdateReview));
        Thread.Sleep(60);
        Assert.That(versioning.PushBranchCalls, Is.EqualTo(2));

        gate.Set();
        Assert.That(WaitForStatusState("inReview"), Is.True);
        Assert.That(versioning.PushBranchCalls, Is.EqualTo(2));
    }

    [Test]
    public void UpdateReview_when_the_push_fails_reports_an_error_and_is_not_wedged()
    {
        FakeVersioning versioning = new();
        using HostController controller = BuildInReview(versioning, new FakeGitHubReview());
        SaveAVersion(controller);

        // First attempt: the push throws — the author sees an error and the document stays In review.
        versioning.ThrowOnPush = true;
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocUpdateReview));
        Assert.That(WaitForKind(MessageKinds.Error), Is.Not.Null);
        Assert.That(LatestStatus()?.State, Is.EqualTo("inReview"));

        // The fault must NOT have wedged the single-flight claim (the version is still unshared, so a
        // retry once the push recovers pushes it and settles back on In review).
        versioning.ThrowOnPush = false;
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocUpdateReview));
        Assert.That(
            WaitUntil(() => versioning.PushBranchCalls == 2), Is.True, "a retry after the fault should push");
        Assert.That(WaitForStatusState("inReview"), Is.True);
    }

    [Test]
    public void UpdateReview_does_not_treat_a_no_op_save_as_a_new_version()
    {
        FakeVersioning versioning = new() { SaveCommits = false };
        using HostController controller = BuildInReview(versioning, new FakeGitHubReview());

        // A "Save a version" that committed nothing (no changes) must not make Update review believe there
        // is a new version to share.
        SaveAVersion(controller);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocUpdateReview));

        Assert.That(
            WaitUntil(() => LatestStatus()?.Label?.Contains("No new versions") == true),
            Is.True,
            "a no-op save leaves nothing new to update");
        Assert.That(versioning.PushBranchCalls, Is.EqualTo(1));
    }

    [Test]
    public void UpdateReview_with_no_new_versions_says_so_and_does_not_push()
    {
        FakeVersioning versioning = new();
        using HostController controller = BuildInReview(versioning, new FakeGitHubReview());

        // No version saved since the review was sent — Update review has nothing to share.
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocUpdateReview));

        Assert.That(
            WaitUntil(() => LatestStatus()?.Label?.Contains("No new versions") == true),
            Is.True,
            "the author should be told there is nothing new to update");
        Assert.Multiple(() =>
        {
            // Only the setup send pushed; the no-op update pushed nothing and stayed In review.
            Assert.That(versioning.PushBranchCalls, Is.EqualTo(1));
            Assert.That(LatestStatus()?.State, Is.EqualTo("inReview"));
        });
    }

    [Test]
    public void UpdateReview_from_draft_is_ignored()
    {
        FakeVersioning versioning = new();
        FakeGitHubReview review = new();
        // A plain draft (never sent) — Update review is not a legal transition until a review is open.
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocUpdateReview));

        Thread.Sleep(60);
        Assert.Multiple(() =>
        {
            Assert.That(versioning.PushBranchCalls, Is.EqualTo(0));
            Assert.That(review.Calls, Is.EqualTo(0));
            Assert.That(LatestStatus()?.State, Is.EqualTo("draft"));
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
