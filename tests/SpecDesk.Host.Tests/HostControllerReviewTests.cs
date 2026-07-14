using System.Diagnostics;
using System.Reflection;
using System.Threading;
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
        public string? PickOpenFolder() => null;

        public string? PickSaveFile(string? suggestedPath) => null;
    }

    // Lets a test drive a genuine reload (DocOpen picking the same path back up) instead of relying on
    // "ready" firing twice — OnReady only auto-loads the initial document once (see M-15 / T-028).
    private sealed class ReopenSamePathDialogs(string path) : IFileDialogs
    {
        public string? PickOpenFile() => path;
        public string? PickOpenFolder() => null;

        public string? PickSaveFile(string? suggestedPath) => null;
    }

    private static Renderer.RenderResult StubRender(string docDir, string text) => new(string.Empty, []);

    // The reviewer handles the configured-reviewers test expects to reach the GitHub layer, verbatim.
    private static readonly string[] ExpectedReviewers = ["@alice", "@octo/team"];

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
        FakeVersioning versioning,
        IGitHubAuth auth,
        FakeGitHubReview review,
        bool startDraft = true,
        IFileDialogs? dialogs = null)
    {
        void Send(string json)
        {
            lock (_gate)
            {
                _sent.Add(json);
            }
        }

        HostController controller = new(
            StubRender, Send, dialogs ?? new NoDialogs(), (_, _, _, _, _) => null,
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
			Assert.That(
				versioning.PushedExpectedRepositoryUrl,
				Is.EqualTo("https://github.com/octo/spec-repo.git"));
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
	public void SendForReview_remote_replaced_after_readiness_refuses_without_push_or_pr()
	{
		const string original = "https://github.com/octo/spec-repo.git";
		const string replacement = "https://github.com/other/private-repo.git";
		FakeVersioning versioning = new();
		versioning.BeforePush = () =>
		{
			versioning.RemoteUrlValue = replacement;
			versioning.PushUrlValue = replacement;
		};
		FakeGitHubReview review = new();
		using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));

		Assert.That(WaitForKind(MessageKinds.Error), Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(versioning.AttemptedExpectedRepositoryUrl, Is.EqualTo(original));
			Assert.That(versioning.RemoteUrlValue, Is.EqualTo(replacement));
			Assert.That(versioning.PushUrlValue, Is.EqualTo(replacement));
			Assert.That(versioning.PushBranchCalls, Is.Zero);
			Assert.That(review.Calls, Is.Zero);
			Assert.That(LatestStatus()?.State, Is.EqualTo("draft"));
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

    // M-10: OpenPullRequestAsync is bounded by two nested timeouts — GitHubReviewClient's own 30s
    // per-request cap, and RunReviewPublish's overall 120s SendForReviewTimeout wrapping the whole push +
    // PR-open round-trip. Either can be the one that actually fires (the 120s budget can be mostly spent
    // by an earlier, unbounded-except-by-OS-socket push before the PR call even starts), and both surface
    // as OperationCanceledException. Confirm the two are never told apart into different, confusing error
    // text — RunReviewPublish's catch is a single broad `catch (Exception ex)` reporting one fixed message
    // regardless of which timer won the race — and that the cancellation does not wedge the single-flight
    // claim (a retry once the transient condition clears still succeeds).
    [Test]
    public void SendForReview_when_the_pr_open_call_times_out_reports_the_same_generic_error_and_recovers()
    {
        FakeVersioning versioning = new();
        FakeGitHubReview review = new() { ThrowTimeoutOnOpen = true };
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));

        IpcMessage? error = WaitForKind(MessageKinds.Error);
        Assert.That(error, Is.Not.Null);
        Assert.Multiple(() =>
        {
            // The same plain message as any other send failure (HttpRequestException, a repo fault, …) —
            // never a raw "The operation was canceled." or a message that varies by which timeout fired.
            Assert.That(
                error!.GetPayload<ErrorPayload>()!.Message,
                Is.EqualTo("Couldn't send this for review. Check your connection and try again."));
            Assert.That(LatestStatus()?.State, Is.EqualTo("draft"));
        });

        // The cancellation must not have wedged the single-flight claim: a retry once the timeout clears
        // still reaches In review.
        review.ThrowTimeoutOnOpen = false;
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));
        Assert.That(WaitForStatusState("inReview"), Is.True, "a retry after the timeout should still work");
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

    // M-13: TryAdvanceReview used to key only on (state, branch name) — both of which a recreated
    // same-named draft can coincidentally reproduce (branch names are date-deterministic, so discarding
    // and starting a new draft the same day regenerates the identical name). LoadFile (triggered here by
    // re-opening the same path — see OnOpen) resets the draft fields WITHOUT checking _publishInFlight,
    // unlike Discard, which refuses outright while a publish is in flight — so a stale Send-for-review
    // push still resolving in the background can land on a brand-new, never-sent draft that merely
    // happens to share the old one's branch name, wrongly jumping it straight to "In review" and
    // stamping its own unrelated version as already shared.
    [Test]
    public void SendForReview_does_not_stamp_a_same_named_draft_recreated_while_the_old_push_is_still_resolving()
    {
        using ManualResetEventSlim gate = new(initialState: false);
        FakeVersioning versioning = new();
        FakeGitHubReview review = new() { ReleaseGate = gate };
        using HostController controller =
            Build(versioning, new FakeGitHubAuth(signedIn: true), review, startDraft: false,
                dialogs: new ReopenSamePathDialogs(_docPath));

        // The first draft, explicitly named so the recreated draft below can reuse the exact same name —
        // in production this coincidence comes from the branch name being date-deterministic, not an
        // explicit author choice.
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit, new EditPayload("spec/reused")));
        SaveAVersion(controller);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));
        Assert.That(
            WaitUntil(() => review.Calls == 1), Is.True, "the first send should reach the PR call and block there");

        // While that push is still resolving in the background, the document is reloaded (re-opening the
        // same path through DocOpen — OnReady itself only auto-loads once, see M-15 / T-028) and a
        // brand-new draft is started, reusing the exact same branch name.
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen));
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit, new EditPayload("spec/reused")));
        Assert.Multiple(() =>
        {
            Assert.That(
                versioning.BeginEditCalls,
                Is.EqualTo(2),
                "reloading the document must retire only the old identity's publish claim");
            Assert.That(
                LatestStatus()?.State,
                Is.EqualTo("draft"),
                "the replacement identity must be editable before the old push settles");
        });
        SaveAVersion(controller);

        // Let the stale push settle (successfully or not) before asserting anything about its effect —
        // RunReviewPublish always calls SendLifecycleStatus once it resolves, so a new message is
        // guaranteed regardless of which branch (advance / not-advance) it takes.
        int sentBeforeRelease;
        lock (_gate)
        {
            sentBeforeRelease = _sent.Count;
        }

        gate.Set();
        Assert.That(
            WaitUntil(() =>
            {
                lock (_gate)
                {
                    return _sent.Count > sentBeforeRelease;
                }
            }),
            Is.True, "the stale push should settle after being released");

        // The recreated draft never sent anything itself — it must NOT have been jumped to In review.
        Assert.That(LatestStatus()?.State, Is.EqualTo("draft"));

        // Confirm its own version is genuinely still unshared: the recreated draft's own Send for review
        // must still push and open its own pull request rather than silently no-op'ing (the false
        // negative this bug caused would instead have already stamped it as shared).
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));
        Assert.That(WaitForStatusState("inReview"), Is.True, "the recreated draft's own send should still work");
        Assert.That(review.Calls, Is.EqualTo(2));
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

    // Write a [review] reviewers list into the temp repo's .spectool.toml (the doc's repo root), so a
    // send resolves reviewers from it. Call before Build so the root resolves to the temp dir.
    private void WriteReviewersConfig(string reviewersArrayBody) =>
        File.WriteAllText(
            Path.Combine(_tempDir, ".spectool.toml"), $"[review]\nreviewers = [{reviewersArrayBody}]\n");

    [Test]
    public void SendForReview_requests_the_configured_reviewers_on_the_new_pr()
    {
        WriteReviewersConfig("\"@alice\", \"@octo/team\"");
        FakeVersioning versioning = new();
        FakeGitHubReview review = new();
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));

        Assert.That(WaitForStatusState("inReview"), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(review.RequestReviewersCalls, Is.EqualTo(1));
            // FakeGitHubReview opens PR #42; the reviewers are passed through untouched (the client splits
            // them into users/teams).
            Assert.That(review.RequestedOnPull, Is.EqualTo(42));
            Assert.That(review.RequestedReviewers, Is.EqualTo(ExpectedReviewers));
        });
    }

    [Test]
    public void SendForReview_requests_no_reviewers_when_none_are_configured()
    {
        // No .spectool.toml [review] reviewers (a "codeowners"-only list would be the same) → nothing
        // explicit to request; GitHub's own CODEOWNERS auto-request, if any, is left to it.
        FakeVersioning versioning = new();
        FakeGitHubReview review = new();
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));

        Assert.That(WaitForStatusState("inReview"), Is.True);
        Assert.That(review.RequestReviewersCalls, Is.EqualTo(0));
    }

    [Test]
    public void SendForReview_still_reaches_in_review_when_the_reviewer_request_fails()
    {
        WriteReviewersConfig("\"@alice\"");
        FakeVersioning versioning = new();
        FakeGitHubReview review = new() { ThrowOnRequestReviewers = true };
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));

        // Reviewer assignment is best-effort: the PR opened, the request was attempted and failed, but the
        // document still reaches In review rather than being stranded in Draft.
        Assert.That(WaitForStatusState("inReview"), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(review.Calls, Is.EqualTo(1));
            Assert.That(review.RequestReviewersCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public void SendForReview_uses_the_authors_confirmed_title_and_body()
    {
        FakeVersioning versioning = new();
        FakeGitHubReview review = new();
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.DocSendForReview, new SendForReviewPayload("My review title", "My review body")));

        Assert.That(WaitForStatusState("inReview"), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(review.Title, Is.EqualTo("My review title"));
            Assert.That(review.Body, Is.EqualTo("My review body"));
        });
    }

    [Test]
    public void SendForReview_falls_back_to_the_generated_title_but_honours_a_cleared_body()
    {
        // FakeVersioning's LastNoteValue ("Clarify the refund window") is the generated title. A blank title
        // falls back to it (GitHub rejects an empty title); a body the author cleared is honoured as empty
        // (the description is optional).
        FakeVersioning versioning = new();
        FakeGitHubReview review = new();
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.DocSendForReview, new SendForReviewPayload("   ", "")));

        Assert.That(WaitForStatusState("inReview"), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(review.Title, Is.EqualTo("Clarify the refund window"));
            Assert.That(review.Body, Is.EqualTo(string.Empty));
        });
    }

    [Test]
    public void SuggestPrText_replies_with_the_generated_title_and_body_echoing_the_request_id()
    {
        FakeVersioning versioning = new();
        FakeGitHubReview review = new();
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.PrSuggestedRequest, id: "req-1"));

        IpcMessage? reply = WaitForKind(MessageKinds.PrSuggested);
        Assert.That(reply, Is.Not.Null);
        PrSuggestedPayload? payload = reply!.GetPayload<PrSuggestedPayload>();
        Assert.Multiple(() =>
        {
            Assert.That(reply!.Id, Is.EqualTo("req-1"));
            Assert.That(payload!.Blocked, Is.Null); // ready to send
            Assert.That(payload!.Title, Is.EqualTo("Clarify the refund window"));
            Assert.That(payload!.Body, Does.Contain("billing.md"));
        });
    }

    [Test]
    public void SuggestPrText_when_not_signed_in_replies_blocked_so_the_prompt_stays_closed()
    {
        FakeVersioning versioning = new();
        using HostController controller =
            Build(versioning, new FakeGitHubAuth(signedIn: false), new FakeGitHubReview());

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.PrSuggestedRequest, id: "r"));

        IpcMessage? reply = WaitForKind(MessageKinds.PrSuggested);
        Assert.That(reply, Is.Not.Null);
        Assert.That(reply!.GetPayload<PrSuggestedPayload>()!.Blocked, Does.Contain("Connect"));
    }

    [Test]
    public void SuggestPrText_while_a_send_is_in_flight_replies_blocked_so_the_prompt_stays_closed()
    {
        using ManualResetEventSlim gate = new(initialState: false);
        FakeVersioning versioning = new();
        FakeGitHubReview review = new() { ReleaseGate = gate };
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

        // A send reaches the (blocked) PR call and stays in flight, so _publishInFlight is set.
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));
        Assert.That(WaitUntil(() => review.Calls == 1), Is.True, "the send should reach the PR call");

        // Asking for a suggestion now is blocked — the prompt must not open to compose a doomed second send.
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.PrSuggestedRequest, id: "r"));
        IpcMessage? reply = WaitForKind(MessageKinds.PrSuggested);
        Assert.That(reply, Is.Not.Null);
        Assert.That(reply!.GetPayload<PrSuggestedPayload>()!.Blocked, Does.Contain("already being sent"));

        gate.Set();
        Assert.That(WaitForStatusState("inReview"), Is.True);
    }

    [Test]
    public void SuggestPrText_without_a_saved_version_replies_blocked()
    {
        FakeVersioning versioning = new() { HasCommitsValue = false };
        using HostController controller =
            Build(versioning, new FakeGitHubAuth(signedIn: true), new FakeGitHubReview());

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.PrSuggestedRequest, id: "r"));

        IpcMessage? reply = WaitForKind(MessageKinds.PrSuggested);
        Assert.That(reply, Is.Not.Null);
        Assert.That(reply!.GetPayload<PrSuggestedPayload>()!.Blocked, Does.Contain("Save a version"));
    }

    [Test]
    public void Discard_during_an_in_flight_send_is_ignored_so_the_open_pr_is_not_orphaned()
    {
        using ManualResetEventSlim gate = new(initialState: false);
        FakeVersioning versioning = new();
        FakeGitHubReview review = new() { ReleaseGate = gate };
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

        // Send reaches the (blocked) PR call and stays in flight.
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));
        Assert.That(WaitUntil(() => review.Calls == 1), Is.True, "the send should reach the PR call");

        // Discard while the send is publishing must be ignored — deleting the draft branch now would orphan
        // the pull request the push is opening on GitHub.
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocDiscard));
        Thread.Sleep(60);
        Assert.That(versioning.DiscardCalled, Is.False, "discard must not run while a send is in flight");

        gate.Set();
        Assert.That(WaitForStatusState("inReview"), Is.True);
        Assert.That(versioning.DiscardCalled, Is.False);
    }

    [Test]
    public void Discard_reads_the_lifecycle_state_and_the_publish_flag_atomically()
    {
        // Regression guard for S-06: OnDiscard used to compute its lifecycle-transition check
        // (Lifecycle.tryStep) from an UNLOCKED read of _state, then separately re-enter _sync only to
        // check _publishInFlight. A send that fully settles (TryAdvanceReview advances _state to
        // In review, then ClearPublishInFlight clears the flag — two further, separate lock(_sync)
        // acquisitions of its own) between those two reads would leave a stale but still-valid `next`
        // unrechecked: the flag-only check would find it already false and let Discard through, deleting
        // the local branch a just-opened PR now depends on. The fix re-derives tryStep inside the SAME
        // lock(_sync) as the flag check, so it always sees a state consistent with that check.
        //
        // Reproduced deterministically (no timing luck) by holding _sync itself via reflection: this
        // blocks OnDiscard's entire gate check (now one critical section) before it can read anything,
        // then flips _state/_publishInFlight to look exactly like a send that has just fully settled,
        // then releases _sync. With the fix, OnDiscard's check runs AFTER that change and sees it; with
        // the old, split-read code, the (now-untestable) unlocked read would have already captured the
        // stale Draft-derived `next` before ever reaching this lock.
        FakeVersioning versioning = new();
        FakeGitHubReview review = new();
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

        object sync = typeof(HostController)
            .GetField("_sync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(controller)!;
        // The six lifecycle fields (state / branch / … ) are consolidated into the immutable _session
        // snapshot; poke its State the same way this used to poke the standalone _state field.
        FieldInfo sessionField = typeof(HostController).GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo publishInFlightField =
            typeof(HostController).GetField("_publishInFlight", BindingFlags.NonPublic | BindingFlags.Instance)!;
        string State() => ((HostController.DraftSession)sessionField.GetValue(controller)!).State;

        Task discard;
        Monitor.Enter(sync);
        try
        {
            Assert.That(State(), Is.EqualTo("draft"), "the draft should still be Draft here");

            discard = Task.Run(() => controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocDiscard)));
            Assert.That(
                SpinUntil(() => discard.Status == TaskStatus.Running, TimeSpan.FromSeconds(2)),
                Is.True);
            Thread.Sleep(50);

            // Stand in for a send that has JUST fully settled: state advanced to In review (where
            // "discard" is not a legal transition), flag cleared — while OnDiscard's whole check is still
            // blocked waiting for _sync. Swap in a session snapshot with State advanced, exactly as
            // TryAdvanceReview does under _sync.
            sessionField.SetValue(
                controller,
                ((HostController.DraftSession)sessionField.GetValue(controller)!) with { State = "inReview" });
            publishInFlightField.SetValue(controller, false);
        }
        finally
        {
            Monitor.Exit(sync);
        }

        Assert.That(discard.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(versioning.DiscardCalled, Is.False, "discard is not a legal transition from In review");
            Assert.That(State(), Is.EqualTo("inReview"));
        });
    }

    private static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }

    [Test]
    public void SaveVersion_completing_during_a_send_does_not_revert_the_In_review_transition()
    {
        // Regression guard for the OnSaveVersion state-clobber race: a "Save a version" that reads _state
        // while still Draft, then commits while a concurrent Send for review advances the document to In
        // review, must NOT write its stale state back and revert In review → Draft.
        using ManualResetEventSlim pushGate = new(initialState: false);
        using ManualResetEventSlim saveGate = new(initialState: false);
        FakeVersioning versioning = new() { PushGate = pushGate, SaveGate = saveGate };
        FakeGitHubReview review = new();
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

        // Send is in flight, blocked inside PushBranch while holding _repoGate; the document is still Draft.
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSendForReview));
        Assert.That(WaitUntil(() => versioning.PushBranchCalls == 1), Is.True, "the send should reach the push");

        // Save a version on another thread. OnSaveVersion reads _state (Draft) up front, then blocks
        // acquiring _repoGate (held by the push). The short pause lets it capture that stale Draft read
        // before the send advances the document's state.
        Task saver = Task.Run(() => SaveAVersion(controller));
        Thread.Sleep(100);

        // Let the send finish: it releases _repoGate, opens the PR, and advances Draft → In review. The save
        // then acquires _repoGate and reaches its commit, where SaveGate holds it mid-flight.
        pushGate.Set();
        Assert.That(WaitForStatusState("inReview"), Is.True, "the send should reach In review");
        Assert.That(WaitUntil(() => versioning.SaveVersionCalls == 1), Is.True, "the save should reach its commit");

        // Release the held commit. With the fix OnSaveVersion does not write its stale Draft state, so the
        // document stays In review; a regression would revert it to Draft right here.
        saveGate.Set();
        Assert.That(saver.Wait(TimeSpan.FromSeconds(10)), Is.True, "the save should complete");
        Assert.That(LatestStatus()?.State, Is.EqualTo("inReview"));
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
	public void UpdateReview_remote_replaced_after_readiness_refuses_without_push()
	{
		const string original = "https://github.com/octo/spec-repo.git";
		const string replacement = "https://github.com/other/private-repo.git";
		FakeVersioning versioning = new();
		FakeGitHubReview review = new();
		using HostController controller = BuildInReview(versioning, review);
		SaveAVersion(controller);
		versioning.BeforePush = () =>
		{
			versioning.RemoteUrlValue = replacement;
			versioning.PushUrlValue = replacement;
		};

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocUpdateReview));

		Assert.That(WaitForKind(MessageKinds.Error), Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(versioning.AttemptedExpectedRepositoryUrl, Is.EqualTo(original));
			Assert.That(versioning.RemoteUrlValue, Is.EqualTo(replacement));
			Assert.That(versioning.PushUrlValue, Is.EqualTo(replacement));
			Assert.That(versioning.PushBranchCalls, Is.EqualTo(1));
			Assert.That(review.Calls, Is.EqualTo(1));
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

    // M-10 counterpart for the update path: RunReviewPublish's 120s SendForReviewTimeout is the only
    // caller-side bound on PushBranch (the connect/handshake phase is bounded only by the OS socket timeout
    // — see PushBranch's own note). Confirm a timed-out push is reported through the exact same generic
    // "couldn't update" message as any other push fault, not a confusing distinct one, and that the
    // cancellation does not wedge the single-flight claim.
    [Test]
    public void UpdateReview_when_the_push_times_out_reports_the_same_generic_error_and_recovers()
    {
        FakeVersioning versioning = new();
        using HostController controller = BuildInReview(versioning, new FakeGitHubReview());
        SaveAVersion(controller);

        versioning.ThrowTimeoutOnPush = true;
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocUpdateReview));

        IpcMessage? error = WaitForKind(MessageKinds.Error);
        Assert.That(error, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(
                error!.GetPayload<ErrorPayload>()!.Message,
                Is.EqualTo("Couldn't update the review. Check your connection and try again."));
            Assert.That(LatestStatus()?.State, Is.EqualTo("inReview"));
        });

        // Not wedged: a retry once the timeout clears still pushes the still-unshared version.
        versioning.ThrowTimeoutOnPush = false;
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocUpdateReview));
        Assert.That(
            WaitUntil(() => versioning.PushBranchCalls == 2), Is.True, "a retry after the timeout should push");
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

    [Test]
    public void RefreshReviewStatus_reflects_the_GitHub_review_decision()
    {
        FakeVersioning versioning = new();
        FakeGitHubReview review = new();
        using HostController controller = BuildInReview(versioning, review);

        // A reviewer approved on GitHub; a window-focus refresh picks it up.
        review.ReviewStatusValue = new ReviewStatus(ReviewDecision.Approved, 42, PullRequestState.Open);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.ReviewRefresh));

        Assert.That(WaitForStatusState("approved"), Is.True);
    }

    [Test]
    public void RefreshReviewStatus_moves_to_changes_requested()
    {
        FakeVersioning versioning = new();
        FakeGitHubReview review = new();
        using HostController controller = BuildInReview(versioning, review);

        review.ReviewStatusValue = new ReviewStatus(ReviewDecision.ChangesRequested, 42, PullRequestState.Open);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.ReviewRefresh));

        Assert.That(WaitForStatusState("changesRequested"), Is.True);
    }

    [Test]
    public void RefreshReviewStatus_leaves_the_status_unchanged_when_the_pr_is_gone_and_keeps_recovering()
    {
        FakeVersioning versioning = new();
        FakeGitHubReview review = new();
        using HostController controller = BuildInReview(versioning, review);

        // The PR reads merged. SpecDesk must NOT force the doc to Published from a background read (that
        // could strand uncommitted edits) — it leaves the last-known status. It does NOT latch a permanent
        // pause either: a later open read (e.g. a reopened PR) still recovers the live status.
        review.ReviewStatusValue = new ReviewStatus(ReviewDecision.InReview, 1, PullRequestState.Merged);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.ReviewRefresh));
        Assert.That(WaitUntil(() => review.GetReviewStatusCalls == 1), Is.True);
        Assert.That(LatestStatus()?.State, Is.EqualTo("inReview"));

        // The PR is open again and approved — the next refresh still queries and picks it up (no freeze).
        review.ReviewStatusValue = new ReviewStatus(ReviewDecision.Approved, 1, PullRequestState.Open);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.ReviewRefresh));
        Assert.That(WaitForStatusState("approved"), Is.True, "a gone read must not permanently freeze the review");
    }

    [Test]
    public void RefreshReviewStatus_with_no_open_pr_leaves_the_status_unchanged()
    {
        FakeVersioning versioning = new();
        FakeGitHubReview review = new();
        using HostController controller = BuildInReview(versioning, review);

        // No open PR (e.g. merged/closed elsewhere) — the last-known In review status stands.
        review.ReviewStatusValue = null;
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.ReviewRefresh));

        Assert.That(WaitUntil(() => review.GetReviewStatusCalls == 1), Is.True, "the refresh should query GitHub");
        Assert.That(LatestStatus()?.State, Is.EqualTo("inReview"));
    }

    [Test]
    public void RefreshReviewStatus_from_a_non_review_state_does_not_query_GitHub()
    {
        FakeVersioning versioning = new();
        FakeGitHubReview review = new();
        // A plain draft (never sent) — there's no open review to refresh.
        using HostController controller = Build(versioning, new FakeGitHubAuth(signedIn: true), review);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.ReviewRefresh));

        Thread.Sleep(50);
        Assert.That(review.GetReviewStatusCalls, Is.EqualTo(0));
    }

    [Test]
    public void ListReviews_replies_with_the_users_open_reviews_mapped_to_plain_terms()
    {
        FakeVersioning versioning = new();
        FakeGitHubReview review = new()
        {
            ReviewsValue =
            [
                new ReviewSummary(
                    42, "Clarify refunds", "https://github.com/o/r/pull/42", "o/r",
                    ReviewRole.Author, ReviewDecision.ChangesRequested),
            ],
        };
        using HostController controller =
            Build(versioning, new FakeGitHubAuth(signedIn: true), review, startDraft: false);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.PrListRequest, id: "r"));

        IpcMessage? reply = WaitForKind(MessageKinds.PrList);
        Assert.That(reply, Is.Not.Null);
        PrListPayload? payload = reply!.GetPayload<PrListPayload>();
        Assert.That(payload, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(reply!.Id, Is.EqualTo("r"));
            Assert.That(payload!.Error, Is.Null);
            Assert.That(payload!.Items, Has.Count.EqualTo(1));
            Assert.That(payload!.Items[0].Role, Is.EqualTo("author"));
            Assert.That(payload!.Items[0].Status, Is.EqualTo("changesRequested"));
            Assert.That(payload!.Items[0].Label, Is.EqualTo("Changes requested"));
            Assert.That(payload!.Items[0].Repo, Is.EqualTo("o/r"));
        });
    }

    [Test]
    public void ListReviews_when_signed_out_replies_with_a_reason_and_no_items()
    {
        FakeVersioning versioning = new();
        FakeGitHubReview review = new();
        using HostController controller =
            Build(versioning, new FakeGitHubAuth(signedIn: false), review, startDraft: false);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.PrListRequest, id: "r"));

        IpcMessage? reply = WaitForKind(MessageKinds.PrList);
        PrListPayload? payload = reply?.GetPayload<PrListPayload>();
        Assert.That(payload, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(payload!.Error, Does.Contain("Connect"));
            Assert.That(payload!.Items, Is.Empty);
        });
    }

    [Test]
    public void ListReviews_on_a_failure_replies_with_a_plain_reason()
    {
        FakeVersioning versioning = new();
        FakeGitHubReview review = new() { ThrowOnListReviews = true };
        using HostController controller =
            Build(versioning, new FakeGitHubAuth(signedIn: true), review, startDraft: false);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.PrListRequest, id: "r"));

        IpcMessage? reply = WaitForKind(MessageKinds.PrList);
        PrListPayload? payload = reply?.GetPayload<PrListPayload>();
        Assert.That(payload, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(payload!.Error, Does.Contain("Couldn't load"));
            Assert.That(payload!.Items, Is.Empty);
        });
    }

    [Test]
    public void ListPullRequests_uses_the_scoped_request_and_returns_both_relationships()
    {
        FakeVersioning versioning = new();
        FakeGitHubReview review = new()
        {
            PullRequestsValue =
            [
                new ReviewSummary(
                    1, "Mine", "https://github.com/o/r/pull/1", "o/r",
                    ReviewRole.Author, ReviewDecision.InReview),
                new ReviewSummary(
                    2, "Joined", "https://github.com/o/x/pull/2", "o/x",
                    ReviewRole.Reviewer, ReviewDecision.InReview),
            ],
        };
        using HostController controller =
            Build(versioning, new FakeGitHubAuth(signedIn: true), review, startDraft: false);

        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.PrListRequest, new PrListRequestPayload("pullRequests"), id: "pull-list"));

        IpcMessage? reply = WaitForKind(MessageKinds.PrList);
        PrListPayload? payload = reply?.GetPayload<PrListPayload>();
        Assert.That(payload, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(reply!.Id, Is.EqualTo("pull-list"));
            Assert.That(review.ListPullRequestsCalls, Is.EqualTo(1));
            Assert.That(
                payload!.Items.Select(item => item.Role),
                Is.EqualTo(new List<string> { "author", "reviewer" }));
        });
    }

    [Test]
    public void PullRequestDetails_are_correlated_and_mapped_for_the_in_app_document()
    {
        FakeGitHubReview review = new()
        {
            DetailsValue = new PullRequestDetails(
                42, "octo/spec", "Clarify refunds", "Description", "https://github.com/octo/spec/pull/42",
                "open", false, "alex", "avatar", "main", "spec/refunds",
                [new PullRequestParticipant("sam", "avatar", "user")],
				[new PullRequestComment(9, "conversation", "", "sam", "avatar", "Please clarify", DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch, false)],
                [new PullRequestCommit("abcdef", "abcdef0", "Clarify", DateTimeOffset.UnixEpoch, "success")],
				true, true),
        };
        using HostController controller =
            Build(new FakeVersioning(), new FakeGitHubAuth(signedIn: true), review, startDraft: false);

        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.PrDetailsRequest, new PrDetailsRequestPayload("octo/spec", 42), id: "details"));

        IpcMessage? reply = WaitForKind(MessageKinds.PrDetails);
        PrDetailsPayload? payload = reply?.GetPayload<PrDetailsPayload>();
        Assert.Multiple(() =>
        {
            Assert.That(reply?.Id, Is.EqualTo("details"));
            Assert.That(review.GetPullRequestDetailsCalls, Is.EqualTo(1));
            Assert.That(payload?.Title, Is.EqualTo("Clarify refunds"));
            Assert.That(payload?.Comments.Single().Id, Is.EqualTo(9));
            Assert.That(payload?.Commits.Single().CheckState, Is.EqualTo("success"));
			Assert.That(payload?.CommentsIncomplete, Is.True);
			Assert.That(payload?.CommitsIncomplete, Is.True);
        });
    }

	[Test]
	public void PullRequestReviewers_normalizes_bounded_handles_before_calling_GitHub()
	{
		FakeGitHubReview review = new();
		using HostController controller =
			Build(new FakeVersioning(), new FakeGitHubAuth(signedIn: true), review, startDraft: false);

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.PrReviewersRequest,
			new PrReviewersRequestPayload("octo/spec", 42, [" @Alice ", "octo/team", "alice"]),
			id: "reviewers"));

		IpcMessage? reply = WaitForKind(MessageKinds.PrMutationCompleted);
		Assert.Multiple(() =>
		{
			Assert.That(reply?.GetPayload<PrMutationCompletedPayload>()?.Succeeded, Is.True);
			Assert.That(review.RequestReviewersCalls, Is.EqualTo(1));
			Assert.That(review.RequestedReviewers, Has.Count.EqualTo(2));
			Assert.That(review.RequestedReviewers, Does.Contain("Alice"));
			Assert.That(review.RequestedReviewers, Does.Contain("octo/team"));
		});
	}

	[Test]
	public void PullRequestReviewers_rejects_unbounded_or_malformed_lists_before_calling_GitHub()
	{
		FakeGitHubReview review = new();
		using HostController controller =
			Build(new FakeVersioning(), new FakeGitHubAuth(signedIn: true), review, startDraft: false);

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.PrReviewersRequest,
			new PrReviewersRequestPayload("octo/spec", 42, [.. Enumerable.Repeat("reviewer", 16)]),
			id: "too-many"));
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.PrReviewersRequest,
			new PrReviewersRequestPayload("octo/spec", 42, ["org/team/extra"]),
			id: "malformed"));

		Assert.Multiple(() =>
		{
			Assert.That(review.RequestReviewersCalls, Is.Zero);
			Assert.That(_sent.Count(json =>
				IpcSerializer.TryDeserialize(json)?.Kind == MessageKinds.PrMutationCompleted), Is.EqualTo(2));
		});
	}

    [Test]
    public void PullRequestComment_reply_mentions_the_selected_author_and_acknowledges_the_mutation()
    {
        FakeGitHubReview review = new();
        using HostController controller =
            Build(new FakeVersioning(), new FakeGitHubAuth(signedIn: true), review, startDraft: false);

        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.PrCommentReply,
            new PrCommentReplyPayload("octo/spec", 42, 9, "conversation", "@sam", "Thanks"),
            id: "reply"));

        IpcMessage? reply = WaitForKind(MessageKinds.PrMutationCompleted);
        Assert.Multiple(() =>
        {
            Assert.That(reply?.Id, Is.EqualTo("reply"));
            Assert.That(reply?.GetPayload<PrMutationCompletedPayload>()?.Succeeded, Is.True);
            Assert.That(review.CreatedComments.Single(), Is.EqualTo("@sam Thanks"));
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
