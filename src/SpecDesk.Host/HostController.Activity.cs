using Microsoft.Extensions.Logging;
using SpecDesk.Contracts;
using SpecDesk.GitHub;

namespace SpecDesk.Host;

public sealed partial class HostController
{
	private static readonly TimeSpan DocumentActivityTimeout = TimeSpan.FromSeconds(20);

	private void OnDocumentActivityRequest(IpcMessage message)
	{
		string? path;
		string? repoRoot;
		string? branch;
		lock (_sync)
		{
			path = _currentPath;
			repoRoot = _repoRoot;
			branch = _session.Branch;
		}
		string? id = message.Id;
		CancellationToken lifetimeToken = _lifetimeCts.Token;
		_ = Task.Run(async () =>
		{
			using CancellationTokenSource activityTimeout =
				CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
			activityTimeout.CancelAfter(DocumentActivityTimeout);
			CancellationToken token = activityTimeout.Token;
			List<DocumentVersionPayload> versions = [];
			List<DocumentChangePayload> history = [];
			List<DocumentCommentPayload> comments = [];
			string historyState = "loaded";
			string? historyMessage = null;
			string commentsState = "loaded";
			string? commentsMessage = null;
			string? relative = path is not null && repoRoot is not null
				? Path.GetRelativePath(repoRoot, path).Replace('\\', '/')
				: null;
			GitHubRepo? githubRepo = null;
			bool? repositoryVersioned = null;
			try
			{
				token.ThrowIfCancellationRequested();
				if (relative is not null && repoRoot is not null)
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
			if (relative is not null && repoRoot is not null && _publishing is not null
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
			else if (!auth.IsSignedIn())
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
					IReadOnlyList<ReviewComment> remote = await commentsAuth.WithAccessTokenAsync(
						async (accessToken, ct) =>
						{
							ReviewStatus? status = await commentsReview.GetReviewStatusAsync(
								accessToken, commentsRepo.Owner, commentsRepo.Name, commentsBranch, ct);
							return status is null
								? []
								: await commentsReview.ListReviewCommentsAsync(
									accessToken, commentsRepo.Owner, commentsRepo.Name, status.Number, ct);
						},
						token);
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

			lifetimeToken.ThrowIfCancellationRequested();
			DocumentActivityPayload payload = new(
				path is null ? null : Path.GetFileName(path),
				versions, historyState, historyMessage,
				comments, commentsState, commentsMessage, history);
			Emit(IpcSerializer.SerializeEvent(MessageKinds.DocumentActivity, payload, id: id));
		}, lifetimeToken);
	}
}
