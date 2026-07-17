using Microsoft.Extensions.Logging;
using SpecDesk.Ai;
using SpecDesk.Contracts;
using SpecDesk.Core;
using SpecDesk.GitHub;

namespace SpecDesk.Host;

// The GitHub review-orchestration slice of HostController: send for review, update review, refresh
// status, list reviews, and the PR-text suggestion, plus the shared publish scaffold and helpers.
// The shared fields, locks, constructor, and the IPC router live in HostController.cs.
public sealed partial class HostController
{
	// Review-publishing and PR-suggestion / listing kinds (see the central RegisterMessageHandlers).
	private void RegisterReviewHandlers()
	{
		_messageHandlers.Register(MessageKinds.DocSendForReview, OnSendForReview);
		_messageHandlers.Register(MessageKinds.PrSuggestedRequest, OnSuggestPrText);
		_messageHandlers.Register(MessageKinds.DocUpdateReview, OnUpdateReview);
		_messageHandlers.Register(MessageKinds.DocPublish, OnPublish);
		_messageHandlers.Register(MessageKinds.ReviewRefresh, OnRefreshReviewStatus);
		_messageHandlers.Register(MessageKinds.PrListRequest, OnListReviews);
	}

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
		long publishClaim;
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
			publishClaim = ++_publishClaimCounter;
			_activePublishClaim = publishClaim;
		}

		// The two pure null checks below can't throw, so it's safe to release the claim and return here.
		// EVERYTHING that can throw (IsSignedIn, the repo read, the push, the API call) runs inside the
		// background task (RunReviewPublish), whose finally always releases the claim — otherwise a single
		// libgit2/IO fault on the synchronous path would leak the claim and wedge the feature for the session.
		if (_auth is null || _publishing is null || _review is null
			|| !TryCaptureAccountSession(out AccountSession accountSession))
		{
			ClearPublishInFlight(publishClaim);
			SendError("Connect a GitHub account to send a document for review.");
			return;
		}

		if (repoRoot is null || branch is null || baseBranch is null || path is null)
		{
			ClearPublishInFlight(publishClaim);
			return;
		}

		// Non-null copies so the background closure below sees them as non-nullable.
		string root = repoRoot;
		string branchName = branch;
		string baseName = baseBranch;
		string docPath = path;


		RunReviewPublish(
			accountSession, publishClaim, fromState, branchName, next, seq, generation, "Sent for review",
			"Couldn't send this for review. Check your connection and try again.",
			async ct =>
			{
				// Re-check readiness at send time (the prompt already gated on it, but the state could have
				// moved) — one shared policy for the prompt and the send, and it hands back the title seed.
				if (!IsAccountSessionCurrent(accountSession))
				{
					return false;
				}
				(string? blocked, GitHubRepo? repo, string? expectedRepositoryUrl, string? lastNote) =
					CheckSendReadiness(root, branchName, baseName);
				if (blocked is not null)
				{
					PublishForAccountSession(accountSession, () => SendError(blocked));
					return false;
				}

				if (!PublishForAccountSession(
					accountSession, () => SendTransientStatus("Sending for review…")))
				{
					return false;
				}
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
						if (!StartForAccountSession(accountSession, () =>
						{
							lock (_repoGate)
							{
								_publishing.PushBranch(
									root, branchName, expectedRepositoryUrl!, token, cancellationToken: innerCt);
							}
						}))
						{
							throw new OperationCanceledException(innerCt);
						}

						Task<PullRequest>? openOperation = null;
						if (!StartForAccountSession(accountSession, () =>
							openOperation = _review.OpenPullRequestAsync(
								token, repo!.Owner, repo.Name, branchName, baseName, title, body, innerCt)))
						{
							throw new OperationCanceledException(innerCt);
						}
						PullRequest opened = await openOperation!;
						// Assign reviewers within the same token scope, best-effort (never fails the send).
						await RequestReviewersBestEffort(
							accountSession, token, repo!, opened, reviewers, innerCt);
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
		long publishClaim;
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
			publishClaim = ++_publishClaimCounter;
			_activePublishClaim = publishClaim;
		}

		if (_auth is null || _publishing is null
			|| !TryCaptureAccountSession(out AccountSession accountSession))
		{
			ClearPublishInFlight(publishClaim);
			SendError("Connect a GitHub account to update a review.");
			return;
		}

		if (repoRoot is null || branch is null)
		{
			ClearPublishInFlight(publishClaim);
			return;
		}

		if (seq <= shared)
		{
			// Nothing saved since the review was last shared: a push would be a no-op and re-opening review
			// (or dropping an Approved status) would be misleading. Report it plainly, touch nothing. This is
			// a pure field compare, so it's safe on the synchronous path — no background task is spun up.
			ClearPublishInFlight(publishClaim);
			SendTransientStatus("No new versions to update the review with");
			return;
		}

		// Non-null copies so the background closure below sees them as non-nullable.
		string root = repoRoot;
		string branchName = branch;


		RunReviewPublish(
			accountSession, publishClaim, fromState, branchName, next, seq, generation, "Updated the review",
			"Couldn't update the review. Check your connection and try again.",
			ct =>
			{
				if (!IsAccountSessionCurrent(accountSession))
				{
					return Task.FromResult(false);
				}

				if (ResolveGitHubReviewTarget(root) is not { } target)
				{
					PublishForAccountSession(accountSession, () =>
						SendError("This document isn't in a GitHub repository, so its review can't be updated."));
					return Task.FromResult(false);
				}

				if (!PublishForAccountSession(
					accountSession, () => SendTransientStatus("Updating the review…")))
				{
					return Task.FromResult(false);
				}

				// Push only — the PR already exists and tracks the branch, so there is no network step to
				// await once the repo-gated push returns.
				return _auth.WithAccessTokenAsync(
					(token, innerCt) =>
					{
						if (!StartForAccountSession(accountSession, () =>
						{
							lock (_repoGate)
							{
								_publishing.PushBranch(
									root, branchName, target.RemoteUrl, token, cancellationToken: innerCt);
							}
						}))
						{
							throw new OperationCanceledException(innerCt);
						}

						return Task.FromResult(IsAccountSessionCurrent(accountSession));
					},
					ct);
				});
	}

	// "Publish": merge the approved document's open pull request, remove its draft branch, and move the
	// document to Published — the final author step that ships an approved spec. Gated three ways, all
	// fail-closed: the lifecycle must allow Approved → Publish (only Approved does), the repo's
	// `[review] allow-author-publish` policy must permit it (re-read here, authoritative — the webview's
	// shown/hidden button is only UX), and GitHub must re-confirm the open PR is still approved against its
	// current head right before the irreversible merge (the periodic status can trail replication — see
	// OnRefreshReviewStatus). Shares OnSendForReview's single-flight + off-thread scaffold (RunReviewPublish):
	// the merge is the one network step whose success advances the lifecycle; the branch delete is best-
	// effort cleanup that never undoes a completed publish. Needs the GitHub feature wired, a connected
	// account, and a GitHub remote; the token is taken transiently and never stored or logged.
	private void OnPublish()
	{
		// Gate the transition AND claim the shared single-flight slot atomically under _sync (see
		// OnSendForReview / OnUpdateReview). generation snapshots the checkout so a same-named draft recreated
		// mid-publish can't be stamped Published by this run (M-13, via TryAdvanceReview).
		string next;
		string? repoRoot;
		string? branch;
		string fromState;
		long shared;
		long generation;
		long publishClaim;
		lock (_sync)
		{
			DraftSession session = _session;
			next = Lifecycle.tryStep(session.State, "publish");
			if (next.Length == 0)
			{
				_logger.LogDebug("Publish ignored from state {State}", session.State);
				return;
			}

			if (_publishInFlight)
			{
				_logger.LogDebug("Publish ignored: a review publish is already in flight");
				return;
			}

			fromState = session.State;
			repoRoot = _repoRoot;
			branch = session.Branch;
			// Publishing shares no NEW versions, so the terminal Published state records the same
			// VersionsShared the review already had (carried through TryAdvanceReview unchanged).
			shared = session.VersionsShared;
			generation = Interlocked.Read(ref _draftGeneration);
			_publishInFlight = true;
			publishClaim = ++_publishClaimCounter;
			_activePublishClaim = publishClaim;
		}

		if (_auth is null || _publishing is null || _review is null
			|| !TryCaptureAccountSession(out AccountSession accountSession))
		{
			ClearPublishInFlight(publishClaim);
			SendError("Connect a GitHub account to publish this document.");
			return;
		}

		if (repoRoot is null || branch is null)
		{
			ClearPublishInFlight(publishClaim);
			return;
		}

		// The authoritative author-publish gate (fail-closed): re-read the repo policy here rather than
		// trusting the webview's shown/hidden button, so a stale UI — or a hand-crafted message — can never
		// drive an unpermitted publish. A plain local .spectool.toml read (no network; TryReadRepoToml guards
		// its own IO), so it is safe on the synchronous path.
		if (!WorkflowConfig.allowAuthorPublishForHost(WorkflowSeeds.TryReadRepoToml(repoRoot)))
		{
			ClearPublishInFlight(publishClaim);
			SendError("Publishing a document yourself isn't turned on for this workspace.");
			return;
		}

		// Non-null copies so the background closure below sees them as non-nullable.
		string root = repoRoot;
		string branchName = branch;

		RunReviewPublish(
			accountSession, publishClaim, fromState, branchName, next, shared, generation, "Published",
			"Couldn't publish this document. Check your connection and try again.",
			async ct =>
			{
				if (!IsAccountSessionCurrent(accountSession))
				{
					return false;
				}

				if (ResolveGitHubReviewRepo(root) is not { } repo)
				{
					PublishForAccountSession(accountSession, () =>
						SendError("This document isn't in a GitHub repository, so it can't be published."));
					return false;
				}

				if (!PublishForAccountSession(accountSession, () => SendTransientStatus("Publishing…")))
				{
					return false;
				}

				return await _auth.WithAccessTokenAsync(
					async (token, innerCt) =>
					{
						// Re-confirm the review is still open AND approved against its CURRENT head, right
						// before the irreversible merge: the periodic status can trail GitHub's replication,
						// and a version pushed after approval must force another review rather than publish
						// unseen content. GitHub also re-checks the head at merge time (we pass the sha), so
						// this is defence in depth, not the sole guard.
						Task<ReviewStatus?>? statusOperation = null;
						if (!StartForAccountSession(accountSession, () =>
							statusOperation = _review.GetReviewStatusAsync(
								token, repo.Owner, repo.Name, branchName, innerCt)))
						{
							return false;
						}
						ReviewStatus? status = await statusOperation!;
						if (status is null || status.PrState != PullRequestState.Open
							|| status.Decision != ReviewDecision.Approved)
						{
							PublishForAccountSession(accountSession, () => SendError(
								"This document needs an up-to-date approval before it can be published."));
							return false;
						}

						Task? mergeOperation = null;
						if (!StartForAccountSession(accountSession, () =>
							mergeOperation = _review.MergePullRequestAsync(
								token, repo.Owner, repo.Name, status.Number, status.HeadSha, innerCt)))
						{
							return false;
						}
						await mergeOperation!;

						// The merge published the document. Remove the merged draft branch as cleanup — never
						// let a delete fault undo the publish (the doc must still reach Published), so this is
						// fully best-effort and swallows every fault, including a cancellation.
						await DeleteMergedBranchBestEffort(accountSession, token, repo, branchName, innerCt);
						return true;
					},
					ct);
			});
	}

	// Delete the just-merged draft branch on GitHub, best-effort. The merge already published the document,
	// so a cleanup failure — a protected branch, an already-deleted ref, a transport blip, even a sign-out
	// cancelling the request — must NOT prevent the document reaching Published. Every fault is logged and
	// swallowed; the author can delete a lingering branch on GitHub.
	private async Task DeleteMergedBranchBestEffort(
		AccountSession accountSession, string token, GitHubRepo repo, string branchName, CancellationToken ct)
	{
		if (_review is null)
		{
			return;
		}

		try
		{
			Task? deleteOperation = null;
			if (!StartForAccountSession(accountSession, () =>
				deleteOperation = _review.DeleteBranchAsync(token, repo.Owner, repo.Name, branchName, ct)))
			{
				return;
			}
			await deleteOperation!;
			_logger.LogInformation("Removed the merged draft branch {Branch}", branchName);
		}
		catch (Exception ex)
		{
			// Best-effort cleanup: the publish (merge) already succeeded, so nothing here may fail it. A
			// protected-branch refusal, an already-gone ref, a transport fault, or a cancellation all just
			// leave the branch in place — the document is published either way.
			_logger.LogWarning(ex, "Could not remove the merged draft branch {Branch}", branchName);
		}
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
		if (!TryCaptureAccountSession(out AccountSession accountSession))
		{
			lock (_sync)
			{
				_refreshingStatus = false;
				_refreshPending = false;
			}
			return;
		}
		_ = Task.Run(async () =>
		{
			using CancellationTokenSource timeout =
				CancellationTokenSource.CreateLinkedTokenSource(accountSession.CancellationToken);
			timeout.CancelAfter(ReviewStatusTimeout);
			try
			{
				if (!_auth.IsSignedIn() || ResolveGitHubReviewRepo(root) is not { } repo)
				{
					return;
				}

				Task<ReviewStatus?>? statusOperation = null;
				if (!StartForAccountSession(accountSession, () =>
					statusOperation = _auth.WithAccessTokenAsync(
						(token, ct) => _review.GetReviewStatusAsync(
							token, repo.Owner, repo.Name, branchName, ct),
						timeout.Token)))
				{
					return;
				}
				ReviewStatus? status = await statusOperation!;
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

				PublishForAccountSession(accountSession, () =>
				{
					bool changed = false;
					lock (_sync)
					{
						DraftSession session = _session;
						if (session.Branch == branchName && !_publishInFlight
							&& IsReviewState(session.State) && session.State != mapped)
						{
							_session = session with { State = mapped };
							changed = true;
						}
					}
					if (changed)
					{
						_logger.LogInformation(
							"Review status for {Branch} (PR #{Number}) is now {State}",
							branchName, status.Number, mapped);
						SendLifecycleStatus();
					}
				});
			}
			catch (OperationCanceledException) when (
				accountSession.CancellationToken.IsCancellationRequested)
			{
				// Sign-out retired the account that owned this refresh.
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
				if (again && IsAccountSessionCurrent(accountSession))
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
		// A single parse (a malformed scope — e.g. a number or an array payload — must not throw and leave
		// this correlated request unanswered; see SafeGetPayload). scope is null both when the field is
		// absent and when the whole payload didn't parse, which correctly falls through to the legacy list.
		string? scope = SafeGetPayload<PrListRequestPayload>(message)?.Scope;
		if (scope == "reviewRequests")
		{
			OnListReviewRequests(message);
			return;
		}
		if (scope == "pullRequests")
		{
			OnListPullRequests(message);
			return;
		}

		const string connectFirst = "Connect a GitHub account to see your reviews.";
		RunReviewList(
			message.Id,
			connectFirst,
			"Could not list the user's reviews",
			"Couldn't load your reviews. Check your connection and try again.",
			(token, ct) => _review!.ListReviewsAsync(token, ct));
	}

	// Reply to the left-panel Review mode. This is deliberately separate from the legacy combined "My
	// reviews" list: it contains only requests waiting on the signed-in user, including visible team
	// memberships, and keeps the same correlated payload contract for the webview decoder.
	private void OnListReviewRequests(IpcMessage message)
	{
		const string connectFirst = "Connect a GitHub account to see review requests.";
		RunReviewList(
			message.Id,
			connectFirst,
			"Could not list review requests",
			"Couldn't load review requests. Check your connection and try again.",
			(token, ct) => _review!.ListReviewRequestsAsync(token, ct));
	}

	private void OnListPullRequests(IpcMessage message)
	{
		const string connectFirst = "Connect a GitHub account to see change requests.";
		RunReviewList(
			message.Id,
			connectFirst,
			"Could not list pull requests",
			"Couldn't load change requests. Check your connection and try again.",
			(token, ct) => _review!.ListPullRequestsAsync(token, ct));
	}

	private void RunReviewList(
		string? id,
		string connectFirst,
		string logMessage,
		string errorMessage,
		Func<string, CancellationToken, Task<IReadOnlyList<ReviewSummary>>> list)
	{
		if (_auth is null || _review is null || !TryCaptureAccountSession(out AccountSession accountSession))
		{
			Emit(IpcSerializer.SerializeEvent(
				MessageKinds.PrList, new PrListPayload([], connectFirst), id: id));
			return;
		}

		_ = Task.Run(async () =>
		{
			using CancellationTokenSource timeout =
				CancellationTokenSource.CreateLinkedTokenSource(accountSession.CancellationToken);
			timeout.CancelAfter(PrListTimeout);
			PrListPayload payload;
			try
			{
				Task<IReadOnlyList<ReviewSummary>>? operation = null;
				if (!StartForAccountSession(accountSession, () =>
					operation = _auth.WithAccessTokenAsync(list, timeout.Token)))
				{
					return;
				}
				IReadOnlyList<ReviewSummary> reviews = await operation!;
				payload = new PrListPayload([.. reviews.Select(ToListItem)], null);
			}
			catch (OperationCanceledException) when (accountSession.CancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "GitHub review list failed: {Operation}", logMessage);
				payload = new PrListPayload([], errorMessage);
			}

			PublishForAccountSession(accountSession, () => Emit(IpcSerializer.SerializeEvent(
				MessageKinds.PrList, payload, id: id)));
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
		AccountSession accountSession,
		long publishClaim,
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
			bool clearInFinally = true;
			// Bound the whole round-trip so a stalled push/API call can't hold _repoGate (and the single-
			// flight claim) indefinitely. The transfer phase honours this; a connect-phase stall is bounded
			// only by the OS socket timeout (see PushBranch).
			using CancellationTokenSource timeout =
				CancellationTokenSource.CreateLinkedTokenSource(accountSession.CancellationToken);
			timeout.CancelAfter(SendForReviewTimeout);
			try
			{
				if (!await publish(timeout.Token))
				{
					return;
				}

				PublishForAccountSession(accountSession, () =>
				{
					if (TryAdvanceReview(fromState, branchName, next, seq, generation))
					{
						_logger.LogInformation("{Action}: {Branch}", action, branchName);
					}
					else
					{
						_logger.LogInformation(
							"{Action} completed, but the document moved on — not advancing: {Branch}",
							action, branchName);
					}
					// Publish the terminal status only after releasing the single-flight claim. The terminal
					// frame is the observable completion boundary, so a retry triggered by it must not be dropped.
					ClearPublishInFlight(publishClaim);
					clearInFinally = false;
					SendLifecycleStatus();
				});
			}
			catch (OperationCanceledException) when (
				accountSession.CancellationToken.IsCancellationRequested)
			{
				// Sign-out owns the UI now; a cancellation-ignoring provider is still blocked by generation.
			}
			catch (Exception ex)
			{
				// Push / token / API / repo faults (HttpRequestException, LibGit2SharpException,
				// InvalidOperationException, a request timeout) all surface as one plain line — never the
				// token or a stack trace. The document stays where it was so the author can retry.
				_logger.LogError(ex, "Review push failed for {Branch}", branchName);
				ClearPublishInFlight(publishClaim);
				clearInFinally = false;
				PublishForAccountSession(accountSession, () => SendError(errorMessage));
			}
			finally
			{
				if (clearInFinally)
				{
					ClearPublishInFlight(publishClaim);
				}
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
	private GitHubRepo? ResolveGitHubReviewRepo(string root) => ResolveGitHubReviewTarget(root)?.Repo;

	private (GitHubRepo Repo, string RemoteUrl)? ResolveGitHubReviewTarget(string root)
	{
		string? remoteUrl;
		lock (_repoGate)
		{
			remoteUrl = _publishing!.RemoteUrl(root);
		}

		return GitHubRemote.TryParse(remoteUrl) is { } repo && remoteUrl is not null
			? (repo, remoteUrl)
			: null;
	}

	// The single readiness policy shared by the pre-send prompt (OnSuggestPrText) and the send itself:
	// whether a review can be sent for this draft right now (signed in, a GitHub remote, at least one saved
	// version), as plain-language checks over local git/store reads (no network). Returns the blocking
	// reason and null repository data when not ready, or the parsed repo plus the exact URL snapshot when
	// ready. The snapshot binds the later push to this readiness decision, so replacing the working tree or
	// its remote cannot redirect the operation. Callers guarantee _auth/_publishing are non-null.
	private (string? Blocked, GitHubRepo? Repo, string? ExpectedRepositoryUrl, string? LastNote) CheckSendReadiness(
		string root, string branch, string baseBranch)
	{
		if (!_auth!.IsSignedIn())
		{
			return ("Connect your GitHub account first, then send for review.", null, null, null);
		}

		// Resolve the GitHub remote first so a non-GitHub repo returns its specific message without a wasted
		// (and possibly throwing) has-commits/last-note read. Then the remaining two local-git reads batch
		// under one lock. (Two lock acquisitions on the GitHub path, one on the non-GitHub path; the only
		// concurrent _repoGate contender during a prompt-open is the quick disk autosave.)
		if (ResolveGitHubReviewTarget(root) is not { } target)
		{
			return ("This document isn't in a GitHub repository, so it can't be sent for review.", null, null, null);
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
			return ("Save a version before sending it for review.", null, null, null);
		}

		return (null, target.Repo, target.RemoteUrl, lastNote);
	}

	// Release the shared single-flight claim taken by OnSendForReview / OnUpdateReview (success, failure,
	// or an early gate exit).
	private void ClearPublishInFlight(long publishClaim)
	{
		lock (_sync)
		{
			if (_activePublishClaim == publishClaim)
			{
				_publishInFlight = false;
				_activePublishClaim = 0;
			}
		}
	}

	private void RetirePublishInFlightLocked()
	{
		_publishInFlight = false;
		_activePublishClaim = 0;
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
		AccountSession accountSession,
		string token,
		GitHubRepo repo,
		PullRequest pr,
		string[] reviewers,
		CancellationToken ct)
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
			Task<int>? requestOperation = null;
			if (!StartForAccountSession(accountSession, () =>
				requestOperation = _review.RequestReviewersAsync(
					token, repo.Owner, repo.Name, pr.Number, reviewers, ct)))
			{
				return;
			}
			int requested = await requestOperation!;
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
		string text;
		bool sendLegal;
		bool publishInFlight;
		lock (_sync)
		{
			DraftSession session = _session;
			repoRoot = _repoRoot;
			branch = session.Branch;
			baseBranch = session.BaseBranch;
			path = _currentPath;
			text = _text;
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
				(blocked, GitHubRepo? repo, _, string? lastNote) =
					CheckSendReadiness(repoRoot, branch, baseBranch);
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

		// Ready to send with an AI provider configured: draft the title/body from the read-only document tools
		// off the message thread (bounded), then fall back to the deterministic template. Any failure or
		// timeout keeps the generated text, so the prompt still opens with usable content.
		if (blocked is null && _suggestionAgent is not null && repoRoot is not null && path is not null)
		{
			string templateTitle = title;
			string templateBody = body;
			string documentText = text;
			string? draftBranch = branch;
			string? draftBaseBranch = baseBranch;
			_ = Task.Run(async () =>
			{
				string finalTitle = templateTitle;
				string finalBody = templateBody;
				try
				{
					IReadOnlyDocumentTools tools =
						BuildDocumentToolset(repoRoot, path, documentText, draftBranch, draftBaseBranch);
					using CancellationTokenSource cts = new(SuggestionTimeout);
					PrDescription? suggested = await _suggestionAgent.SuggestPrDescriptionAsync(tools, cts.Token);
					if (suggested is not null && !string.IsNullOrWhiteSpace(suggested.Title))
					{
						finalTitle = suggested.Title;
						finalBody = suggested.Body ?? string.Empty;
					}
				}
				catch (Exception ex)
				{
					// Best-effort, like the version note: any fault falls back to the generated text so the
					// prompt still opens. The base "Send for review" flow never depends on the AI provider.
					_logger.LogWarning(ex, "AI PR-description suggestion failed; using the generated text");
				}
				EmitPrSuggested(id, finalTitle, finalBody, null);
			});
			return;
		}

		EmitPrSuggested(id, title, body, blocked);
	}

	private void EmitPrSuggested(string? id, string title, string body, string? blocked) =>
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.PrSuggested,
			new PrSuggestedPayload(title, body, blocked),
			id: id));
}
