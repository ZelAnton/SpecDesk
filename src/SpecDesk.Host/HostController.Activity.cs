using Microsoft.Extensions.Logging;
using SpecDesk.Contracts;
using SpecDesk.GitHub;

namespace SpecDesk.Host;

public sealed partial class HostController
{
	// Document activity-timeline request kind (see the central RegisterMessageHandlers).
	private void RegisterActivityHandlers()
	{
		_messageHandlers.Register(MessageKinds.DocumentActivityRequest, OnDocumentActivityRequest);
	}

	private static readonly TimeSpan DocumentActivityTimeout = TimeSpan.FromSeconds(20);

	private void OnDocumentActivityRequest(IpcMessage message)
	{
		string? path;
		string? repoRoot;
		string? branch;
		RemoteDocumentContext? remoteDocument;
		lock (_sync)
		{
			path = _currentPath;
			repoRoot = _repoRoot;
			remoteDocument = _remoteDocument;
			branch = remoteDocument?.Branch ?? _session.Branch;
		}
		string? id = message.Id;
		CancellationToken lifetimeToken = _lifetimeCts.Token;
		AccountSession? accountSession = TryCaptureAccountSession(out AccountSession capturedSession)
			? capturedSession
			: null;
		_ = Task.Run(async () =>
		{
			using CancellationTokenSource activityTimeout =
				accountSession is { } linkedSession
					? CancellationTokenSource.CreateLinkedTokenSource(
						lifetimeToken, linkedSession.CancellationToken)
					: CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
			activityTimeout.CancelAfter(DocumentActivityTimeout);
			CancellationToken token = activityTimeout.Token;
			List<DocumentVersionPayload> versions = [];
			List<DocumentChangePayload> history = [];
			List<DocumentCommentPayload> comments = [];
			string historyState = "loaded";
			string? historyMessage = null;
			string commentsState = "loaded";
			string? commentsMessage = null;
			string? relative = remoteDocument?.Path
				?? (path is not null && repoRoot is not null
					? Path.GetRelativePath(repoRoot, path).Replace('\\', '/')
					: null);
			GitHubRepo? githubRepo = null;
			bool? repositoryVersioned = null;
			try
			{
				token.ThrowIfCancellationRequested();
				if (remoteDocument is not null)
				{
					repositoryVersioned = false;
					historyState = "notVersioned";
					historyMessage = "Saved versions are available after you copy this repository locally.";
				}
				else if (relative is not null && repoRoot is not null)
				{
					IReadOnlyList<SpecDesk.Git.DocumentVersion> stored;
					lock (_repoGate)
					{
						token.ThrowIfCancellationRequested();
						repositoryVersioned = _versioning.IsVersioned(repoRoot);
						stored = repositoryVersioned.Value
							? _versioning.GetDocumentVersions(
								repoRoot, relative, cancellationToken: token)
							: [];
					}
					if (!repositoryVersioned.Value)
					{
						historyState = "notVersioned";
						historyMessage = "Saved versions are available for repository documents.";
					}
					versions = stored
						.Select(item => new DocumentVersionPayload(item.Id, item.Note, item.Author, item.When))
						.ToList();
					history = stored
						.Select(item => new DocumentChangePayload(
							item.Id, item.Summary, item.Note, item.Author, item.When))
						.ToList();
				}
			}
			catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
			{
				return;
			}
			catch (OperationCanceledException) when (
				accountSession is { } cancelledSession
				&& cancelledSession.CancellationToken.IsCancellationRequested)
			{
				return;
			}
			catch (OperationCanceledException)
			{
				_logger.LogWarning("Document activity timed out for {Path}", path);
				historyState = "unavailable";
				historyMessage = "Could not load saved history. Try again.";
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Could not read saved document history for {Path}", path);
				historyState = "unavailable";
				historyMessage = "Could not load saved history. Try again.";
			}

			IGitHubAuth? auth = _auth;
			IGitHubReview? review = _review;
			bool remoteResolutionFailed = false;
			if (remoteDocument is not null)
			{
				githubRepo = new GitHubRepo(remoteDocument.Owner, remoteDocument.Name);
			}
			else if (relative is not null && repoRoot is not null && _publishing is not null
				&& repositoryVersioned is not false)
			{
				try
				{
					lock (_repoGate)
					{
						githubRepo = GitHubRemote.TryParse(_publishing.RemoteUrl(repoRoot));
					}
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Could not resolve the GitHub repository for {Path}", path);
					remoteResolutionFailed = true;
				}
			}

			bool commentsSourceExists = relative is not null && branch is not null && githubRepo is not null;
			if (remoteResolutionFailed)
			{
				commentsState = "unavailable";
				commentsMessage = "Could not load comments. Try again.";
			}
			else if (!commentsSourceExists)
			{
				commentsState = "loaded";
				commentsMessage = null;
			}
			else if (auth is null || review is null)
			{
				commentsState = "unavailable";
				commentsMessage = "Comments aren't available right now.";
			}
			else if (accountSession is null)
			{
				commentsState = "notConnected";
				commentsMessage = "Connect to GitHub to load review comments.";
			}
			else if (token.IsCancellationRequested)
			{
				commentsState = "unavailable";
				commentsMessage = "Could not load comments. Try again.";
			}
			else
			{
				IGitHubAuth commentsAuth = auth!;
				IGitHubReview commentsReview = review!;
				GitHubRepo commentsRepo = githubRepo!;
				string commentsBranch = branch!;
				string commentsPath = relative!;
				try
				{
					AccountSession activeAccount = accountSession!.Value;
					Task<IReadOnlyList<ReviewComment>>? operation = null;
					if (!StartForAccountSession(activeAccount, () =>
						operation = commentsAuth.WithAccessTokenAsync(
							async (accessToken, ct) =>
							{
								Task<ReviewStatus?>? statusOperation = null;
								if (!StartForAccountSession(activeAccount, () =>
									statusOperation = commentsReview.GetReviewStatusAsync(
										accessToken, commentsRepo.Owner, commentsRepo.Name, commentsBranch, ct)))
								{
									return [];
								}
								ReviewStatus? status = await statusOperation!;
								if (status is null)
								{
									return [];
								}
								Task<IReadOnlyList<ReviewComment>>? commentsOperation = null;
								if (!StartForAccountSession(activeAccount, () =>
									commentsOperation = commentsReview.ListReviewCommentsAsync(
										accessToken, commentsRepo.Owner, commentsRepo.Name, status.Number, ct)))
								{
									return [];
								}
								return await commentsOperation!;
							},
							token)))
					{
						return;
					}
					IReadOnlyList<ReviewComment> remote = await operation!;
					comments = remote
						.Where(item => string.Equals(item.Path, commentsPath, StringComparison.Ordinal))
						.Take(100)
						.Select(item => new DocumentCommentPayload(
							item.Id, item.Author, item.Body, item.When))
						.ToList();
					commentsState = "loaded";
					commentsMessage = null;
				}
				catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
				{
					return;
				}
				catch (OperationCanceledException) when (
					accountSession is { } cancelledSession
					&& cancelledSession.CancellationToken.IsCancellationRequested)
				{
					return;
				}
				catch (OperationCanceledException)
				{
					_logger.LogWarning("Review comments timed out for {Path}", path);
					commentsState = "unavailable";
					commentsMessage = "Could not load comments. Try again.";
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Could not read review comments for {Path}", path);
					commentsState = "unavailable";
					commentsMessage = "Could not load comments. Try again.";
				}
			}

			if (accountSession is { } activeSession && !IsAccountSessionCurrent(activeSession))
			{
				return;
			}
			lifetimeToken.ThrowIfCancellationRequested();
			DocumentActivityPayload payload = new(
				remoteDocument is not null
					? remoteDocument.Path.Split('/')[^1]
					: path is null ? null : Path.GetFileName(path),
				versions, historyState, historyMessage,
				comments, commentsState, commentsMessage, history);
			if (accountSession is { } currentSession)
			{
				PublishForAccountSession(currentSession, () => Emit(IpcSerializer.SerializeEvent(
					MessageKinds.DocumentActivity, payload, id: id)));
			}
			else
			{
				Emit(IpcSerializer.SerializeEvent(MessageKinds.DocumentActivity, payload, id: id));
			}
		}, lifetimeToken);
	}
}
