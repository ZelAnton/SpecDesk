using Microsoft.Extensions.Logging;
using SpecDesk.Contracts;
using SpecDesk.Core;
using SpecDesk.GitHub;

namespace SpecDesk.Host;

// The GitHub review-orchestration slice of HostController: send for review, update review, refresh
// status, list reviews, and the PR-text suggestion, plus the shared publish scaffold and helpers.
// The shared fields, locks, constructor, and the IPC router live in HostController.cs.
public sealed partial class HostController
{
	// "Send for review": push the draft branch to GitHub and open a pull request, then move the
	// document to In review. Needs the GitHub feature wired, a connected account, and a GitHub remote;
	// the access token is taken transiently (WithAccessTokenAsync) for the push + API call and is never
	// stored or logged here. The network round-trip runs on a background task — it can take seconds.
	private void OnSendForReview(IpcMessage message)
	{
		// The author-confirmed PR title/body (edited from the suggestion in the send prompt). Absent for a
		// bare send — the publish delegate then falls back to the fully generated text, so the round-trip
		// stays robust whether or not the confirm step ran.
		SendForReviewPayload? prText = SafeGetPayload<SendForReviewPayload>(message);

		// Gate the lifecycle transition AND claim the single-flight slot atomically under _sync, so a
		// stale state read or a double-click can't slip a second round-trip through. fromState/branch are
		// re-checked before the transition is committed (the document may have moved on); seq records how
		// many saved versions this push carries, so a later Update review knows what is already shared.
		// generation additionally guards against a SAME-named branch being discarded and re-created while
		// this push is in flight (M-13): branch names are date-deterministic, so a recreated draft can match
		// both fromState and branchName by coincidence, yet must never be stamped with the old push's seq.
		// See TryAdvanceReview and the _draftGeneration field comment.
		string next;
		string? repoRoot;
		string? branch;
		string? baseBranch;
		string? path;
		string fromState;
		long seq;
		long generation;
		lock (_sync)
		{
			DraftSession session = _session;
			next = Lifecycle.tryStep(session.State, "sendForReview");
			if (next.Length == 0)
			{
				_logger.LogDebug("Send for review ignored from state {State}", session.State);
				return;
			}

			if (_publishInFlight)
			{
				_logger.LogDebug("Send for review ignored: a review publish is already in flight");
				return;
			}

			fromState = session.State;
			repoRoot = _repoRoot;
			branch = session.Branch;
			baseBranch = session.BaseBranch;
			path = _currentPath;
			seq = session.VersionsSaved;
			// The LIVE _draftGeneration (not the session's own Generation token): this snapshots "which
			// checkout am I sending", which TryAdvanceReview later re-compares against the live counter to
			// reject a same-named draft recreated mid-push (M-13).
			generation = Interlocked.Read(ref _draftGeneration);
			_publishInFlight = true;
		}

		// The two pure null checks below can't throw, so it's safe to release the claim and return here.
		// EVERYTHING that can throw (IsSignedIn, the repo read, the push, the API call) runs inside the
		// background task (RunReviewPublish), whose finally always releases the claim — otherwise a single
		// libgit2/IO fault on the synchronous path would leak the claim and wedge the feature for the session.
		if (_auth is null || _publishing is null || _review is null)
		{
			ClearPublishInFlight();
			SendError("Connect a GitHub account to send a document for review.");
			return;
		}

		if (repoRoot is null || branch is null || baseBranch is null || path is null)
		{
			ClearPublishInFlight();
			return;
		}

		// Non-null copies so the background closure below sees them as non-nullable.
		string root = repoRoot;
		string branchName = branch;
		string baseName = baseBranch;
		string docPath = path;

		RunReviewPublish(
			fromState, branchName, next, seq, generation, "Sent for review",
			"Couldn't send this for review. Check your connection and try again.",
			async ct =>
			{
				// Re-check readiness at send time (the prompt already gated on it, but the state could have
				// moved) — one shared policy for the prompt and the send, and it hands back the title seed.
				(string? blocked, GitHubRepo? repo, string? lastNote) = CheckSendReadiness(root, branchName, baseName);
				if (blocked is not null)
				{
					SendError(blocked);
					return false;
				}

				SendTransientStatus("Sending for review…");
				// Use the author's confirmed text. A blank title degrades to the generated seed — GitHub
				// rejects an empty title. The body degrades ONLY when it is absent (a bare send with no
				// payload, or a payload missing the field) — a description the author cleared is honoured as
				// empty (it's optional). The prompt only opens once readiness passes, so a failed suggestion
				// can't reach here with a spuriously-blank body.
				(string genTitle, string genBody) = ReviewRequestContent(lastNote, docPath);
				string title = string.IsNullOrWhiteSpace(prText?.Title) ? genTitle : prText!.Title;
				string body = prText?.Body is null ? genBody : prText.Body;
				// The explicit @user/@team reviewers to request (a plain .spectool.toml read, no repo access);
				// "codeowners" is filtered out upstream and left to GitHub's own auto-request.
				string[] reviewers = WorkflowConfig.reviewersForHost(WorkflowSeeds.TryReadRepoToml(root));

				PullRequest pr = await _auth.WithAccessTokenAsync(
					async (token, innerCt) =>
					{
						// The push is a repo operation AND a network transfer, so _repoGate is held across the
						// whole push to serialize it with every other libgit2 access (libgit2 isn't
						// concurrency-safe). This can block a concurrent repo-gated message-thread handler for
						// the push's duration — see the _repoGate note. The lock is released before the
						// separate PR API call below, which needs no repo access.
						lock (_repoGate)
						{
							_publishing.PushBranch(root, branchName, token, cancellationToken: innerCt);
						}

						PullRequest opened = await _review.OpenPullRequestAsync(
							token, repo!.Owner, repo.Name, branchName, baseName, title, body, innerCt);
						// Assign reviewers within the same token scope, best-effort (never fails the send).
						await RequestReviewersBestEffort(token, repo, opened, reviewers, innerCt);
						return opened;
					},
					ct);

				_logger.LogInformation(
					"Opened pull request #{Number} ({Url}) for {Branch}", pr.Number, pr.Url, branchName);
				return true;
			});
	}

	// "Update review": push the newly-saved versions of a draft that is already under review to its open
	// pull request. The PR tracks the head branch, so pushing is all it takes — no second PR is opened. The
	// lifecycle settles the state on the push: from Approved back to In review (new versions need
	// re-approval), a self-transition from In review / Changes requested (a change request stands until the
	// reviewer re-reviews). The push does NOT read GitHub right away — just after a push its GraphQL can still
	// return the pre-push head (replication lag) and re-stamp a stale decision; the periodic poll / next
	// window focus pick up the settled decision once replication catches up. Needs the GitHub feature wired, a
	// connected account, and a GitHub remote; the token is taken transiently (WithAccessTokenAsync) for the
	// push and never stored or logged. Shares OnSendForReview's single-flight + off-thread scaffold
	// (RunReviewPublish), minus the PR-open step, plus a "nothing new to share" guard.
	private void OnUpdateReview()
	{
		// Gate the transition AND claim the shared single-flight slot atomically under _sync (see
		// OnSendForReview). seq/shared capture how many saved versions exist vs. have been shared, so the
		// "nothing new" guard below and the post-push bookkeeping agree on one snapshot.
		string next;
		string? repoRoot;
		string? branch;
		string fromState;
		long seq;
		long shared;
		long generation;
		lock (_sync)
		{
			DraftSession session = _session;
			next = Lifecycle.tryStep(session.State, "updateReview");
			if (next.Length == 0)
			{
				_logger.LogDebug("Update review ignored from state {State}", session.State);
				return;
			}

			if (_publishInFlight)
			{
				_logger.LogDebug("Update review ignored: a review publish is already in flight");
				return;
			}

			fromState = session.State;
			repoRoot = _repoRoot;
			branch = session.Branch;
			seq = session.VersionsSaved;
			shared = session.VersionsShared;
			// The LIVE _draftGeneration (not the session's own Generation token): see OnSendForReview /
			// the _draftGeneration field comment. Guards TryAdvanceReview against a same-named branch being
			// discarded and re-created while this push is in flight (M-13).
			generation = Interlocked.Read(ref _draftGeneration);
			_publishInFlight = true;
		}

		if (_auth is null || _publishing is null)
		{
			ClearPublishInFlight();
			SendError("Connect a GitHub account to update a review.");
			return;
		}

		if (repoRoot is null || branch is null)
		{
			ClearPublishInFlight();
			return;
		}

		if (seq <= shared)
		{
			// Nothing saved since the review was last shared: a push would be a no-op and re-opening review
			// (or dropping an Approved status) would be misleading. Report it plainly, touch nothing. This is
			// a pure field compare, so it's safe on the synchronous path — no background task is spun up.
			ClearPublishInFlight();
			SendTransientStatus("No new versions to update the review with");
			return;
		}

		// Non-null copies so the background closure below sees them as non-nullable.
		string root = repoRoot;
		string branchName = branch;

		RunReviewPublish(
			fromState, branchName, next, seq, generation, "Updated the review",
			"Couldn't update the review. Check your connection and try again.",
			ct =>
			{
				if (!_auth.IsSignedIn())
				{
					SendError("Connect your GitHub account first, then update the review.");
					return Task.FromResult(false);
				}

				if (ResolveGitHubReviewRepo(root) is null)
				{
					SendError("This document isn't in a GitHub repository, so its review can't be updated.");
					return Task.FromResult(false);
				}

				SendTransientStatus("Updating the review…");

				// Push only — the PR already exists and tracks the branch, so there is no network step to
				// await once the repo-gated push returns.
				return _auth.WithAccessTokenAsync(
					(token, innerCt) =>
					{
						lock (_repoGate)
						{
							_publishing.PushBranch(root, branchName, token, cancellationToken: innerCt);
						}

						return Task.FromResult(true);
					},
					ct);
				});
	}

	// "Refresh review status": while a document is under review, read GitHub's current review decision for
	// its open pull request and reflect it — In review / Changes requested / Approved. This is what makes
	// those states reachable (a reviewer acts on GitHub, out of band); the webview triggers it on window
	// focus. Read-only and best-effort: a failure or a since-closed PR leaves the last-known status. Needs
	// the GitHub feature wired, a connected account, and a GitHub remote; the token is taken transiently.
	private void OnRefreshReviewStatus()
	{
		string? repoRoot;
		string? branch;
		string fromState;
		lock (_sync)
		{
			DraftSession session = _session;
			fromState = session.State;
			repoRoot = _repoRoot;
			branch = session.Branch;
			// Nothing to refresh unless under review with the feature wired (there's an open PR to check), or
			// while a send/update is publishing — that flow authoritatively sets the state, so a concurrent
			// read could clobber it (just after a push its GraphQL can lag; the poll / next focus pick it up).
			if (!IsReviewState(fromState) || _publishInFlight || _auth is null
				|| _publishing is null || _review is null || repoRoot is null || branch is null)
			{
				return;
			}

			// A refresh is already running: the in-flight read may predate what this request wants to see, so
			// queue exactly one follow-up rather than dropping it (a focus refresh must not be lost to a poll).
			if (_refreshingStatus)
			{
				_refreshPending = true;
				return;
			}

			_refreshingStatus = true;
		}

		string root = repoRoot;
		string branchName = branch;
		_ = Task.Run(async () =>
		{
			using CancellationTokenSource timeout = new();
			timeout.CancelAfter(ReviewStatusTimeout);
			try
			{
				if (!_auth.IsSignedIn() || ResolveGitHubReviewRepo(root) is not { } repo)
				{
					return;
				}

				ReviewStatus? status = await _auth.WithAccessTokenAsync(
					(token, ct) => _review.GetReviewStatusAsync(token, repo.Owner, repo.Name, branchName, ct),
					timeout.Token);
				if (status is null)
				{
					// The branch never had a pull request (shouldn't happen once under review) — nothing to do.
					return;
				}

				if (status.PrState != PullRequestState.Open)
				{
					// The PR is merged or closed on GitHub. We deliberately do NOT force a lifecycle change
					// from this background read: flipping to Published (read-only) could strand uncommitted
					// edits, and flipping to Draft would swap in the destructive Discard chrome — both without
					// the author asking. Merging / abandoning is a deliberate step (the Publish flow, PoC-10).
					// Leave the last-known status; the refresh is a no-op. (If the review is done the poll keeps
					// reading it merged until the author moves on — a small, self-correcting cost that avoids the
					// stale-freeze / never-recover bugs a host-side "stop polling" latch kept introducing.)
					_logger.LogDebug(
						"Pull request #{Number} for {Branch} is {State} on GitHub — leaving the last-known status",
						status.Number, branchName, status.PrState);
					return;
				}

				// Map GitHub's live decision straight to the target review state (not through Lifecycle.next,
				// which models the author's local actions) — GitHub is the source of truth while the PR is open.
				// NOTE on a narrow race: a poll/focus refresh landing within GitHub's replication lag right after
				// an Update-review push can briefly read the pre-push head and re-stamp a stale Approved onto
				// just-pushed content. It self-heals on the next refresh once GitHub indexes the push (the head
				// no longer matches the approval's commit → In review). Publish (PoC-10) must do its own
				// head-level freshness check before merging rather than trusting this transient status.
				string mapped = DecisionStateName(status.Decision);

				bool changed = false;
				lock (_sync)
				{
					// Apply the decision only if the document is still the same review draft that we queried
					// for, no publish started meanwhile (its committed state wins), and the decision moved.
					DraftSession session = _session;
					if (session.Branch == branchName && !_publishInFlight && IsReviewState(session.State) && session.State != mapped)
					{
						_session = session with { State = mapped };
						changed = true;
					}
				}

				if (changed)
				{
					_logger.LogInformation(
						"Review status for {Branch} (PR #{Number}) is now {State}", branchName, status.Number, mapped);
					SendLifecycleStatus();
				}
			}
			catch (Exception ex)
			{
				// Best-effort: a status refresh failure must never disturb the author — the last-known status
				// stands. (HttpRequestException / a request timeout / a repo read fault.)
				_logger.LogWarning(ex, "Could not refresh the review status for {Branch}", branchName);
			}
			finally
			{
				bool again;
				lock (_sync)
				{
					_refreshingStatus = false;
					again = _refreshPending;
					_refreshPending = false;
				}

				// A refresh was requested mid-flight — run exactly one more so a focus/poll read that arrived
				// during this one isn't lost (it may have wanted a newer decision than this read saw).
				if (again)
				{
					OnRefreshReviewStatus();
				}
			}
		});
	}

	// Reply to the webview's request for the author's open reviews (the browse list). A best-effort network
	// read on a background thread, correlated to the request by id. The token is taken transiently and never
	// stored or logged; a failure returns a plain reason with an empty list rather than leaving the request
	// unanswered. No git vocabulary reaches the author.
	private void OnListReviews(IpcMessage message)
	{
		if (message.GetPayload<PrListRequestPayload>()?.Scope == "reviewRequests")
		{
			OnListReviewRequests(message);
			return;
		}

		const string connectFirst = "Connect a GitHub account to see your reviews.";
		string? id = message.Id;
		IGitHubAuth? auth = _auth;
		IGitHubReview? review = _review;
		if (auth is null || review is null)
		{
			Emit(IpcSerializer.SerializeEvent(MessageKinds.PrList, new PrListPayload([], connectFirst), id: id));
			return;
		}

		_ = Task.Run(async () =>
		{
			using CancellationTokenSource timeout = new();
			// Bounded below the webview's ipc.request timeout (30s) so the host always replies before the
			// waiter gives up — otherwise a slow-but-successful load would surface as a failure and the real
			// reply would be dropped against an already-abandoned request id.
			timeout.CancelAfter(PrListTimeout);
			PrListPayload payload;
			try
			{
				if (!auth.IsSignedIn())
				{
					payload = new PrListPayload([], connectFirst);
				}
				else
				{
					IReadOnlyList<ReviewSummary> reviews = await auth.WithAccessTokenAsync(
						(token, ct) => review.ListReviewsAsync(token, ct), timeout.Token);
					payload = new PrListPayload([.. reviews.Select(ToListItem)], null);
				}
			}
			catch (Exception ex)
			{
				// Best-effort browse: a failure (HttpRequestException / a request timeout) is reported plainly,
				// never as a token or a stack trace, so the panel shows a reason instead of hanging.
				_logger.LogWarning(ex, "Could not list the user's reviews");
				payload = new PrListPayload([], "Couldn't load your reviews. Check your connection and try again.");
			}

			Emit(IpcSerializer.SerializeEvent(MessageKinds.PrList, payload, id: id));
		});
	}

	// Reply to the left-panel Review mode. This is deliberately separate from the legacy combined "My
	// reviews" list: it contains only requests waiting on the signed-in user, including visible team
	// memberships, and keeps the same correlated payload contract for the webview decoder.
	private void OnListReviewRequests(IpcMessage message)
	{
		const string connectFirst = "Connect a GitHub account to see review requests.";
		string? id = message.Id;
		IGitHubAuth? auth = _auth;
		IGitHubReview? review = _review;
		if (auth is null || review is null)
		{
			Emit(IpcSerializer.SerializeEvent(MessageKinds.PrList, new PrListPayload([], connectFirst), id: id));
			return;
		}

		_ = Task.Run(async () =>
		{
			using CancellationTokenSource timeout = new();
			timeout.CancelAfter(PrListTimeout);
			PrListPayload payload;
			try
			{
				if (!auth.IsSignedIn())
				{
					payload = new PrListPayload([], connectFirst);
				}
				else
				{
					IReadOnlyList<ReviewSummary> reviews = await auth.WithAccessTokenAsync(
						(token, ct) => review.ListReviewRequestsAsync(token, ct), timeout.Token);
					payload = new PrListPayload([.. reviews.Select(ToListItem)], null);
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Could not list review requests");
				payload = new PrListPayload(
					[], "Couldn't load review requests. Check your connection and try again.");
			}

			Emit(IpcSerializer.SerializeEvent(MessageKinds.PrList, payload, id: id));
		});
	}

	// One review-list row: the wire status name for styling and its author-facing label (Lifecycle.labelOf,
	// the same source as the status bar) so the panel never re-implements the state vocabulary.
	private static PrListItemPayload ToListItem(ReviewSummary summary)
	{
		string state = DecisionStateName(summary.Decision);
		return new PrListItemPayload(
			summary.Number, summary.Title, summary.Url, summary.Repo,
			summary.Role == ReviewRole.Author ? "author" : "reviewer", state, Lifecycle.labelOf(state));
	}

	private static string DecisionStateName(ReviewDecision decision) => decision switch
	{
		ReviewDecision.Approved => Lifecycle.stateName(Lifecycle.State.Approved),
		ReviewDecision.ChangesRequested => Lifecycle.stateName(Lifecycle.State.ChangesRequested),
		_ => Lifecycle.stateName(Lifecycle.State.InReview),
	};

	// Whether a wire state name is one of the under-review states (an open PR exists to query / update).
	// Derived from Lifecycle.stateName so a rename of a review state's wire name can't silently desync.
	private static bool IsReviewState(string state) =>
		state == Lifecycle.stateName(Lifecycle.State.InReview)
		|| state == Lifecycle.stateName(Lifecycle.State.ChangesRequested)
		|| state == Lifecycle.stateName(Lifecycle.State.Approved);

	// The background scaffold both review pushes share: bound the round-trip with the timeout, run the
	// caller's <paramref name="publish"/> (its own signed-in / remote checks, guards, and the token-scoped
	// push [+ PR open]), and — only when it reports it actually pushed AND the document is still the same
	// draft — commit the lifecycle transition and record how far the review is now shared. Always releases
	// the single-flight claim. A <paramref name="publish"/> that returns false has already told the author
	// why it bailed, so the lifecycle is left untouched.
	private void RunReviewPublish(
		string fromState,
		string branchName,
		string next,
		long seq,
		long generation,
		string action,
		string errorMessage,
		Func<CancellationToken, Task<bool>> publish)
	{
		_ = Task.Run(async () =>
		{
			// Bound the whole round-trip so a stalled push/API call can't hold _repoGate (and the single-
			// flight claim) indefinitely. The transfer phase honours this; a connect-phase stall is bounded
			// only by the OS socket timeout (see PushBranch).
			using CancellationTokenSource timeout = new();
			timeout.CancelAfter(SendForReviewTimeout);
			try
			{
				if (!await publish(timeout.Token))
				{
					return;
				}

				if (TryAdvanceReview(fromState, branchName, next, seq, generation))
				{
					_logger.LogInformation("{Action}: {Branch}", action, branchName);
				}
				else
				{
					// The document changed during the push (discard / switch, including a same-named draft
					// recreated in place — M-13), so the branch was pushed / the PR opened but this document
					// must NOT be stamped — re-sync the chrome to the real state so the transient "…" label
					// never lingers.
					_logger.LogInformation(
						"{Action} completed, but the document moved on — not advancing: {Branch}", action, branchName);
				}

				// Settle on the lifecycle label — the terminal status frame, so the author is never left
				// staring at the transient "Updating…/Sending…" message. For Update review (a self-transition)
				// the "Updating the review…" transient clearing to the settled state is the confirmation, and
				// a no-op instead shows the distinct "No new versions…" line. GitHub's own decision (if it
				// changed) is picked up by the periodic poll / next window focus.
				SendLifecycleStatus();
			}
			catch (Exception ex)
			{
				// Push / token / API / repo faults (HttpRequestException, LibGit2SharpException,
				// InvalidOperationException, a request timeout) all surface as one plain line — never the
				// token or a stack trace. The document stays where it was so the author can retry.
				_logger.LogError(ex, "Review push failed for {Branch}", branchName);
				SendError(errorMessage);
			}
			finally
			{
				ClearPublishInFlight();
			}
		});
	}

	// Commit a review lifecycle transition iff the document is still the same draft that began the push,
	// and record how far the review has now been shared. seq is the saved-version count captured when the
	// push began. A version saved mid-push is deliberately NOT counted as shared even though the push may
	// actually carry it to the PR (git pushes HEAD): the bias is to UNDER-count, never over-count. Under-
	// counting only costs a later Update review a harmless no-op re-push; over-counting would mark a version
	// shared that never left, so the next Update would report "nothing new" and the reviewer would never see
	// it. Exactly tracking "what HEAD held at push time" would need the counter under _repoGate (the push's
	// lock), which the _sync/_repoGate ordering rule forbids reading here — not worth it for a no-op re-push.
	//
	// M-13: (session State, Branch) alone can't tell "the same draft, untouched" from "a same-named draft that was
	// recreated while this push was in flight" (e.g. via a reload — LoadFile resets the draft fields
	// without checking _publishInFlight, unlike Discard — followed by a fresh BeginEdit) — branch names
	// are date-deterministic, so a same-day recreation reproduces the exact same (state, branch) pair the
	// old push captured, and would otherwise get wrongly stamped with the OLD push's seq, making the
	// recreated draft's next Update review falsely report "no new versions". generation (the caller's
	// snapshot of _draftGeneration, which bumps on every BeginEdit/Discard checkout change) rules this
	// out: any recreation bumps it at least once more, so a stale snapshot can never match the live value
	// even when state and branch coincide.
	// Returns whether the transition was applied.
	private bool TryAdvanceReview(string fromState, string branchName, string next, long seq, long generation)
	{
		lock (_sync)
		{
			DraftSession session = _session;
			if (session.State != fromState || session.Branch != branchName || Interlocked.Read(ref _draftGeneration) != generation)
			{
				return false;
			}

			_session = session with { State = next, VersionsShared = seq };
			return true;
		}
	}

	// Resolve the GitHub owner/repo the current remote points at (a repo-gated read of the remote URL, then
	// the strict github.com parse), or null when there is no GitHub remote to host a review. Callers have
	// already established _publishing is non-null on the synchronous path.
	private GitHubRepo? ResolveGitHubReviewRepo(string root)
	{
		string? remoteUrl;
		lock (_repoGate)
		{
			remoteUrl = _publishing!.RemoteUrl(root);
		}

		return GitHubRemote.TryParse(remoteUrl);
	}

	// The single readiness policy shared by the pre-send prompt (OnSuggestPrText) and the send itself:
	// whether a review can be sent for this draft right now (signed in, a GitHub remote, at least one saved
	// version), as plain-language checks over local git/store reads (no network). Returns the blocking
	// reason and a null repo when not ready, or (null, the parsed repo) when ready — so the prompt never
	// opens for a send that would be rejected, and both paths speak the same words. Callers guarantee
	// _auth/_publishing are non-null.
	private (string? Blocked, GitHubRepo? Repo, string? LastNote) CheckSendReadiness(
		string root, string branch, string baseBranch)
	{
		if (!_auth!.IsSignedIn())
		{
			return ("Connect your GitHub account first, then send for review.", null, null);
		}

		// Resolve the GitHub remote first so a non-GitHub repo returns its specific message without a wasted
		// (and possibly throwing) has-commits/last-note read. Then the remaining two local-git reads batch
		// under one lock. (Two lock acquisitions on the GitHub path, one on the non-GitHub path; the only
		// concurrent _repoGate contender during a prompt-open is the quick disk autosave.)
		if (ResolveGitHubReviewRepo(root) is not { } repo)
		{
			return ("This document isn't in a GitHub repository, so it can't be sent for review.", null, null);
		}

		bool hasCommits;
		string? lastNote;
		lock (_repoGate)
		{
			hasCommits = _publishing!.HasCommitsToReview(root, branch, baseBranch);
			lastNote = _publishing.LastVersionNote(root, branch);
		}

		if (!hasCommits)
		{
			// The draft is level with its base (no saved version) — GitHub would reject the PR as "no commits
			// between base and head"; ask the author to save a version rather than surfacing that raw.
			return ("Save a version before sending it for review.", null, null);
		}

		return (null, repo, lastNote);
	}

	// Release the shared single-flight claim taken by OnSendForReview / OnUpdateReview (success, failure,
	// or an early gate exit).
	private void ClearPublishInFlight()
	{
		lock (_sync)
		{
			_publishInFlight = false;
		}
	}

	// Compose the pull-request title and body for a review request: the title is the author's last
	// version note (falling back to the document name when there is none), and the body is a short,
	// plain-language line naming the document. PR content is reviewer-facing on GitHub, so it may name
	// the file — but it stays free of internal git vocabulary.
	private static (string Title, string Body) ReviewRequestContent(string? lastNote, string docPath)
	{
		string docName = Path.GetFileName(docPath);
		string title = !string.IsNullOrWhiteSpace(lastNote) ? lastNote! : $"Review: {docName}";
		string body = $"Review requested for {docName} via SpecDesk.";
		return (title, body);
	}

	// Request the configured reviewers on the freshly-opened PR, best-effort. The PR is already open (the
	// author is In review), so a reviewer-request failure — a handle that isn't a collaborator, a team that
	// needs read:org, a network blip — is logged and swallowed, never failing the send; the author can add
	// reviewers on GitHub. Skipped when there are no explicit reviewers, or the PR number is unknown (a 2xx
	// create with an unparseable body). Runs inside the caller's token scope.
	private async Task RequestReviewersBestEffort(
		string token, GitHubRepo repo, PullRequest pr, string[] reviewers, CancellationToken ct)
	{
		if (reviewers.Length == 0 || _review is null)
		{
			return;
		}

		if (pr.Number == 0)
		{
			// The PR opened but its number couldn't be read (a 2xx create with an unparseable body), so the
			// reviewers endpoint can't be targeted. Say so rather than skipping silently — the author may
			// need to add the configured reviewers on GitHub.
			_logger.LogWarning(
				"Opened a pull request with an unknown number; could not request {Count} configured reviewer(s)",
				reviewers.Length);
			return;
		}

		try
		{
			int requested = await _review.RequestReviewersAsync(token, repo.Owner, repo.Name, pr.Number, reviewers, ct);
			if (requested > 0)
			{
				_logger.LogInformation("Requested {Count} reviewer(s) on pull request #{Number}", requested, pr.Number);
			}
			else
			{
				// The configured entries resolved to nothing GitHub could be asked for (all filtered or
				// malformed) — report honestly rather than claiming an assignment that didn't happen.
				_logger.LogWarning(
					"Configured reviewers resolved to none that could be requested on pull request #{Number}",
					pr.Number);
			}
		}
		catch (Exception ex)
		{
			// Best-effort: assigning reviewers must never undo an opened PR. Swallow the fault (an API
			// rejection, a request timeout, an unexpected error) with a diagnostic, and stay In review.
			_logger.LogWarning(ex, "Could not request reviewers on pull request #{Number}", pr.Number);
		}
	}

	// Reply to the webview's request for the suggested PR title/body to prefill the "send for review"
	// confirm prompt. The title seeds from the branch's last version note (the last commit message),
	// falling back to the document name; the body is a short plain-language line. Correlated by the
	// request id (the webview awaits it). Mirrors the send flow's own generation, so the prompt shows
	// exactly what a bare send would use.
	private void OnSuggestPrText(IpcMessage message)
	{
		string? id = message.Id;
		string? repoRoot;
		string? branch;
		string? baseBranch;
		string? path;
		bool sendLegal;
		bool publishInFlight;
		lock (_sync)
		{
			DraftSession session = _session;
			repoRoot = _repoRoot;
			branch = session.Branch;
			baseBranch = session.BaseBranch;
			path = _currentPath;
			sendLegal = Lifecycle.tryStep(session.State, "sendForReview").Length > 0;
			publishInFlight = _publishInFlight;
		}

		string title = string.Empty;
		string body = string.Empty;
		string? blocked;
		try
		{
			if (_auth is null || _publishing is null || _review is null)
			{
				blocked = "Connect a GitHub account to send a document for review.";
			}
			else if (repoRoot is null || branch is null || baseBranch is null || path is null || !sendLegal)
			{
				// No draft to send (or the document already moved past Draft). The Send button is draft-only,
				// so this is a defensive reply rather than a reachable UI path.
				blocked = "Start a draft before sending it for review.";
			}
			else if (publishInFlight)
			{
				// A send is already publishing this draft — don't open the prompt to compose text that a
				// second in-flight send would just drop.
				blocked = "This document is already being sent for review.";
			}
			else
			{
				(blocked, GitHubRepo? repo, string? lastNote) = CheckSendReadiness(repoRoot, branch, baseBranch);
				if (repo is not null)
				{
					// Ready — seed the prompt with the same text a bare send would generate.
					(title, body) = ReviewRequestContent(lastNote, path);
				}
			}
		}
		catch (Exception ex)
		{
			// Whatever the readiness read faulted with (a libgit2 error, an I/O or permission fault, an
			// unexpected edge), we MUST still reply — an unanswered request hangs the prompt for the full IPC
			// timeout. So this deliberately catches broadly: reply with a blocking message; the author retries.
			_logger.LogError(ex, "Could not prepare the review suggestion");
			blocked = "Couldn't prepare the review. Try again.";
		}

		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.PrSuggested,
			new PrSuggestedPayload(title, body, blocked),
			id: id));
	}
}
