using Microsoft.Extensions.Logging;
using SpecDesk.Contracts;
using SpecDesk.GitHub;

namespace SpecDesk.Host;

// The inline-comment ⇄ GitHub review-comment bridge (PoC-8). The document's inline comment threads live
// locally in the webview; this slice projects the open pull request's review comments onto the open
// document and posts a local thread back as a GitHub review comment when it lands inside a diff hunk. Both
// handlers resolve the repository, branch, PR, and repository-relative path from the host's OWN current-
// document state — never from the webview — so a transiently-taken access token only ever reads/writes the
// document actually open. The token is never stored or logged here.
public sealed partial class HostController
{
	// Inline review-comment sync / publish kinds (see the central RegisterMessageHandlers).
	private void RegisterReviewCommentHandlers()
	{
		_messageHandlers.Register(MessageKinds.ReviewCommentSyncRequest, OnReviewCommentSync);
		_messageHandlers.Register(MessageKinds.ReviewCommentPublish, OnReviewCommentPublish);
	}

	// One inline-comment sync round-trip touches three GitHub reads (status → files → comments), so it gets
	// a slightly wider budget than a single-document read; still well under the webview's IPC wait.
	private static readonly TimeSpan ReviewCommentSyncTimeout = TimeSpan.FromSeconds(35);

	// The resolved projection for one document, assembled inside the token scope so the emit can run after it.
	private sealed record ReviewCommentSyncResult(
		int Number,
		string HeadCommitId,
		IReadOnlyList<int> CommentableLines,
		IReadOnlyList<ReviewCommentAnchor> Comments);

	// "Sync review comments": project the open PR's inline review comments onto the open document and report
	// which of its head-side lines are inside a diff hunk (postable). Read-only and best-effort — a failure,
	// or a document that has no open PR, replies with an empty projection so the webview settles every local
	// thread to "not yet on GitHub" rather than leaving the correlated request unanswered.
	private void OnReviewCommentSync(IpcMessage message)
	{
		// Parse once through SafeGetPayload: a malformed scope must not throw and strand this correlated
		// request. documentKey is echoed back unread so the webview can drop a response that arrives after
		// the author navigated away.
		string documentKey = SafeGetPayload<ReviewCommentSyncRequestPayload>(message)?.DocumentKey ?? string.Empty;

		string? repoRoot;
		string? branch;
		string? path;
		string state;
		lock (_sync)
		{
			DraftSession session = _session;
			state = session.State;
			repoRoot = _repoRoot;
			branch = session.Branch;
			path = _currentPath;
		}

		// Inline-comment sync only applies to a document under review (an open PR to read) with the feature
		// wired, a connected account, and a GitHub remote. Otherwise reply with an empty projection (no PR).
		if (!IsReviewState(state) || _auth is null || _review is null || _publishing is null
			|| repoRoot is null || branch is null || path is null
			|| !TryCaptureAccountSession(out AccountSession accountSession))
		{
			EmitReviewCommentSync(message.Id, documentKey, 0, string.Empty, string.Empty, [], [], null);
			return;
		}

		string root = repoRoot;
		string branchName = branch;
		string relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');

		_ = Task.Run(async () =>
		{
			using CancellationTokenSource timeout =
				CancellationTokenSource.CreateLinkedTokenSource(accountSession.CancellationToken);
			timeout.CancelAfter(ReviewCommentSyncTimeout);
			try
			{
				if (!_auth.IsSignedIn() || ResolveGitHubReviewRepo(root) is not { } repo)
				{
					PublishForAccountSession(accountSession, () => EmitReviewCommentSync(
						message.Id, documentKey, 0, string.Empty, relativePath, [], [], null));
					return;
				}

				Task<ReviewCommentSyncResult>? operation = null;
				if (!StartForAccountSession(accountSession, () =>
					operation = _auth.WithAccessTokenAsync(
						async (token, ct) =>
						{
							// The PR number comes from the branch's live review status (an open PR is required);
							// a merged/closed/absent PR yields the empty projection above's semantics.
							ReviewStatus? status = await _review.GetReviewStatusAsync(
								token, repo.Owner, repo.Name, branchName, ct);
							if (status is null || status.PrState != PullRequestState.Open || status.Number <= 0)
							{
								return new ReviewCommentSyncResult(0, string.Empty, [], []);
							}

							ReviewSyncSnapshot snapshot = await _review.GetReviewSyncAsync(
								token, repo.Owner, repo.Name, status.Number, relativePath, ct);
							return new ReviewCommentSyncResult(
								status.Number, snapshot.HeadCommitId, snapshot.CommentableLines, snapshot.Comments);
						},
						timeout.Token)))
				{
					return;
				}

				ReviewCommentSyncResult result = await operation!;
				PublishForAccountSession(accountSession, () => EmitReviewCommentSync(
					message.Id, documentKey, result.Number, result.HeadCommitId, relativePath,
					result.CommentableLines, result.Comments, null));
			}
			catch (OperationCanceledException) when (accountSession.CancellationToken.IsCancellationRequested)
			{
				// Sign-out retired the account that owned this sync; its review data must not reach the next.
			}
			catch (Exception ex)
			{
				// Best-effort: a sync failure leaves the webview's last projection untouched. (An API
				// rejection, a request timeout, a repo read fault.) Never surfaces the token or a stack trace.
				_logger.LogWarning(ex, "Could not sync review comments for {Branch}", branchName);
				PublishForAccountSession(accountSession, () => EmitReviewCommentSync(
					message.Id, documentKey, 0, string.Empty, relativePath, [], [],
					"Couldn't sync review comments. Check your connection and try again."));
			}
		});
	}

	// "Post to review": publish one local inline comment as a GitHub review comment on the open PR. The
	// caller already established (via the last sync's commentable lines) that the line is inside a diff hunk;
	// GitHub still enforces this and a rejection surfaces as a plain reason. The comment posts from the
	// author's account, so this is the one mutating call — bounded, single round-trip, token never stored.
	private void OnReviewCommentPublish(IpcMessage message)
	{
		ReviewCommentPublishPayload? payload = SafeGetPayload<ReviewCommentPublishPayload>(message);
		if (payload is null || string.IsNullOrEmpty(payload.LocalId))
		{
			EmitReviewCommentPublished(
				message.Id, payload?.LocalId ?? string.Empty, 0, false, "This comment can't be posted right now.");
			return;
		}

		string localId = payload.LocalId;
		if (payload.Number <= 0 || string.IsNullOrWhiteSpace(payload.CommitId) || payload.Line <= 0
			|| string.IsNullOrWhiteSpace(payload.Body) || payload.Body.Length > 65_536)
		{
			EmitReviewCommentPublished(message.Id, localId, 0, false, "This comment can't be posted to the review.");
			return;
		}

		string side = string.Equals(payload.Side, "LEFT", StringComparison.OrdinalIgnoreCase) ? "LEFT" : "RIGHT";

		string? repoRoot;
		string? path;
		string state;
		lock (_sync)
		{
			DraftSession session = _session;
			state = session.State;
			repoRoot = _repoRoot;
			path = _currentPath;
		}

		if (!IsReviewState(state) || _auth is null || _review is null || _publishing is null
			|| repoRoot is null || path is null
			|| !TryCaptureAccountSession(out AccountSession accountSession))
		{
			EmitReviewCommentPublished(
				message.Id, localId, 0, false, "Connect a GitHub account and open this document for review first.");
			return;
		}

		string root = repoRoot;
		// The path is re-resolved from the host's current document, not taken from the webview, so the token
		// can only ever post against the file actually open.
		string relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
		int number = payload.Number;
		string commitId = payload.CommitId;
		int line = payload.Line;
		string body = payload.Body;

		_ = Task.Run(async () =>
		{
			using CancellationTokenSource timeout =
				CancellationTokenSource.CreateLinkedTokenSource(accountSession.CancellationToken);
			timeout.CancelAfter(PullRequestDocumentTimeout);
			try
			{
				if (ResolveGitHubReviewRepo(root) is not { } repo)
				{
					PublishForAccountSession(accountSession, () => EmitReviewCommentPublished(
						message.Id, localId, 0, false,
						"This document isn't in a GitHub repository, so its comment can't be posted."));
					return;
				}

				Task<long>? operation = null;
				if (!StartForAccountSession(accountSession, () =>
					operation = _auth.WithAccessTokenAsync(
						(token, ct) => _review.CreateReviewCommentAsync(
							token, repo.Owner, repo.Name, number, commitId, relativePath, line, side, body, ct),
						timeout.Token)))
				{
					return;
				}

				long githubId = await operation!;
				_logger.LogInformation(
					"Posted a review comment on pull request #{Number} at {Path}:{Line}", number, relativePath, line);
				PublishForAccountSession(accountSession, () =>
					EmitReviewCommentPublished(message.Id, localId, githubId, true, null));
			}
			catch (OperationCanceledException) when (accountSession.CancellationToken.IsCancellationRequested)
			{
				// Sign-out retired the mutation; its result must not cross account boundaries.
			}
			catch (Exception ex)
			{
				// The post failed (an API rejection — often the line fell outside the diff after the head
				// moved — a request timeout, or a repo read fault). Report it plainly; the thread stays local.
				_logger.LogWarning(ex, "Could not post a review comment on pull request #{Number}", number);
				PublishForAccountSession(accountSession, () => EmitReviewCommentPublished(
					message.Id, localId, 0, false,
					"GitHub couldn't post that comment. It may be on a line that isn't part of this review."));
			}
		});
	}

	private void EmitReviewCommentSync(
		string? id,
		string documentKey,
		int number,
		string headCommitId,
		string path,
		IReadOnlyList<int> commentableLines,
		IReadOnlyList<ReviewCommentAnchor> comments,
		string? error) =>
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.ReviewCommentSync,
			new ReviewCommentSyncPayload(
				documentKey,
				number,
				headCommitId,
				path,
				commentableLines,
				[.. comments.Select(item => new ReviewCommentAnchorPayload(
					item.Id, item.Line, item.Side, item.CommitId, item.InReplyToId, item.Author, item.Body, item.When))],
				error),
			id: id));

	private void EmitReviewCommentPublished(string? id, string localId, long githubId, bool succeeded, string? error) =>
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.ReviewCommentPublished,
			new ReviewCommentPublishedPayload(localId, githubId, succeeded, error),
			id: id));
}
