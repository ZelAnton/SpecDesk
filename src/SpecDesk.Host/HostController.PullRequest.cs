using Microsoft.Extensions.Logging;
using SpecDesk.Contracts;
using SpecDesk.GitHub;

namespace SpecDesk.Host;

public sealed partial class HostController
{
	private static readonly TimeSpan PullRequestDocumentTimeout = TimeSpan.FromSeconds(20);

	private void OnPrDetails(IpcMessage message)
	{
		PrDetailsRequestPayload? payload = SafeGetPayload<PrDetailsRequestPayload>(message);
		if (!TryValidatePullRequest(payload?.Repo, payload?.Number ?? 0, out string owner, out string repo,
			out string? error))
		{
			Emit(IpcSerializer.SerializeEvent(MessageKinds.PrDetails, EmptyPrDetails(payload, error), id: message.Id));
			return;
		}
		RunPrDetails(message.Id, owner, repo, payload!.Number);
	}

	private void OnPrReviewers(IpcMessage message)
	{
		PrReviewersRequestPayload? payload = SafeGetPayload<PrReviewersRequestPayload>(message);
		if (!TryValidatePullRequest(payload?.Repo, payload?.Number ?? 0, out string owner, out string repo,
			out string? error) || payload is null
			|| !TryNormalizeReviewers(payload.Reviewers, out IReadOnlyList<string> reviewers, out error))
		{
			EmitMutation(message.Id, false, error ?? "Enter at least one reviewer.");
			return;
		}
		RunPrMutation(
			message.Id,
			(token, ct) => _review!.RequestReviewersAsync(token, owner, repo, payload.Number, reviewers, ct),
			"request reviewers");
	}

	private void OnPrCommentCreate(IpcMessage message)
	{
		PrCommentCreatePayload? payload = SafeGetPayload<PrCommentCreatePayload>(message);
		if (!TryValidateComment(payload?.Repo, payload?.Number ?? 0, payload?.Body, out string owner,
			out string repo, out string? error) || payload is null)
		{
			EmitMutation(message.Id, false, error);
			return;
		}
		RunPrMutation(
			message.Id,
			async (token, ct) =>
			{
				await _review!.CreatePullRequestCommentAsync(token, owner, repo, payload.Number, payload.Body, ct);
				return 0;
			},
			"create comment");
	}

	private void OnPrCommentReply(IpcMessage message)
	{
		PrCommentReplyPayload? payload = SafeGetPayload<PrCommentReplyPayload>(message);
		if (!TryValidateComment(payload?.Repo, payload?.Number ?? 0, payload?.Body, out string owner,
			out string repo, out string? error) || payload is null)
		{
			EmitMutation(message.Id, false, error);
			return;
		}
		string author = NormalizeMention(payload.Author);
		string body = author.Length == 0 ? payload.Body : $"@{author} {payload.Body}";
		RunPrMutation(
			message.Id,
			async (token, ct) =>
			{
				if (payload.Kind == "review" && payload.CommentId > 0)
				{
					await _review!.ReplyToReviewCommentAsync(
						token, owner, repo, payload.Number, payload.CommentId, payload.Body, ct);
				}
				else
				{
					await _review!.CreatePullRequestCommentAsync(token, owner, repo, payload.Number, body, ct);
				}
				return 0;
			},
			"reply to comment");
	}

	private void OnPrCommentUpdate(IpcMessage message)
	{
		PrCommentUpdatePayload? payload = SafeGetPayload<PrCommentUpdatePayload>(message);
		if (!TryValidateComment(payload?.Repo, payload?.Number ?? 0, payload?.Body, out string owner,
			out string repo, out string? error) || payload is null || payload.CommentId <= 0)
		{
			EmitMutation(message.Id, false, error ?? "This comment can't be changed.");
			return;
		}
		RunPrMutation(
			message.Id,
			async (token, ct) =>
			{
				if (payload.Kind == "review")
				{
					await _review!.UpdateReviewCommentAsync(token, owner, repo, payload.CommentId, payload.Body, ct);
				}
				else
				{
					await _review!.UpdatePullRequestCommentAsync(
						token, owner, repo, payload.CommentId, payload.Body, ct);
				}
				return 0;
			},
			"update comment");
	}

	private void RunPrDetails(string? id, string owner, string repo, int number)
	{
		if (_auth is null || _review is null || !TryCaptureAccountSession(out AccountSession accountSession))
		{
			Emit(IpcSerializer.SerializeEvent(
				MessageKinds.PrDetails,
				EmptyPrDetails(new PrDetailsRequestPayload($"{owner}/{repo}", number),
					"Connect a GitHub account to open this review."),
				id: id));
			return;
		}

		_ = Task.Run(async () =>
		{
			using CancellationTokenSource timeout =
				CancellationTokenSource.CreateLinkedTokenSource(accountSession.CancellationToken);
			timeout.CancelAfter(PullRequestDocumentTimeout);
			try
			{
				Task<PullRequestDetails>? operation = null;
				if (!StartForAccountSession(accountSession, () =>
					operation = _auth.WithAccessTokenAsync(
						(token, ct) => _review.GetPullRequestDetailsAsync(token, owner, repo, number, ct),
						timeout.Token)))
				{
					return;
				}
				PullRequestDetails details = await operation!;
				PublishForAccountSession(accountSession, () => Emit(IpcSerializer.SerializeEvent(
					MessageKinds.PrDetails, ToPayload(details), id: id)));
			}
			catch (OperationCanceledException) when (accountSession.CancellationToken.IsCancellationRequested)
			{
				// Signing out retires the account session; its private review data must not reach the next session.
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Could not load pull request {Repo}#{Number}", $"{owner}/{repo}", number);
				PublishForAccountSession(accountSession, () => Emit(IpcSerializer.SerializeEvent(
					MessageKinds.PrDetails,
					EmptyPrDetails(new PrDetailsRequestPayload($"{owner}/{repo}", number),
						"Couldn't load this review. Check your connection and try again."),
					id: id)));
			}
		});
	}

	private void RunPrMutation(
		string? id, Func<string, CancellationToken, Task<int>> mutate, string operation)
	{
		if (_auth is null || _review is null || !TryCaptureAccountSession(out AccountSession accountSession))
		{
			EmitMutation(id, false, "Connect a GitHub account first.");
			return;
		}
		_ = Task.Run(async () =>
		{
			using CancellationTokenSource timeout =
				CancellationTokenSource.CreateLinkedTokenSource(accountSession.CancellationToken);
			timeout.CancelAfter(PullRequestDocumentTimeout);
			try
			{
				Task<int>? operationTask = null;
				if (!StartForAccountSession(accountSession, () =>
					operationTask = _auth.WithAccessTokenAsync(mutate, timeout.Token)))
				{
					return;
				}
				await operationTask!;
				PublishForAccountSession(accountSession, () => EmitMutation(id, true, null));
			}
			catch (OperationCanceledException) when (accountSession.CancellationToken.IsCancellationRequested)
			{
				// Signing out retires the mutation and prevents its result from crossing account boundaries.
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Could not {Operation} on GitHub", operation);
				PublishForAccountSession(accountSession, () =>
					EmitMutation(id, false, "GitHub couldn't save that change. Try again."));
			}
		});
	}

	private void EmitMutation(string? id, bool succeeded, string? error) =>
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.PrMutationCompleted, new PrMutationCompletedPayload(succeeded, error), id: id));

	private static bool TryValidatePullRequest(
		string? value, int number, out string owner, out string repo, out string? error)
	{
		if (number <= 0 || value is null || !TryParseGitHubRepo(value, out owner, out repo))
		{
			owner = string.Empty;
			repo = string.Empty;
			error = "This review reference isn't valid.";
			return false;
		}
		error = null;
		return true;
	}

	private static bool TryValidateComment(
		string? value, int number, string? body, out string owner, out string repo, out string? error)
	{
		if (!TryValidatePullRequest(value, number, out owner, out repo, out error))
		{
			return false;
		}
		if (string.IsNullOrWhiteSpace(body) || body.Length > 65_536)
		{
			error = "Write a comment between 1 and 65,536 characters.";
			return false;
		}
		return true;
	}

	private static string NormalizeMention(string value)
	{
		string candidate = value.Trim().TrimStart('@');
		return candidate.Length is > 0 and <= 39
			&& candidate.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
			? candidate
			: string.Empty;
	}

	private static bool TryNormalizeReviewers(
		IReadOnlyList<string> values, out IReadOnlyList<string> reviewers, out string? error)
	{
		if (values.Count is 0 or > 15)
		{
			reviewers = [];
			error = values.Count == 0
				? "Enter at least one reviewer."
				: "Request at most 15 reviewers at a time.";
			return false;
		}

		HashSet<string> normalized = new(StringComparer.OrdinalIgnoreCase);
		foreach (string value in values)
		{
			string candidate = value.Trim().TrimStart('@');
			string[] segments = candidate.Split('/');
			bool valid = segments.Length is 1 or 2
				&& segments.All(segment => segment.Length is > 0 and <= 100
					&& segment.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
				&& segments[0].Length <= 39;
			if (!valid)
			{
				reviewers = [];
				error = "Use GitHub names such as octocat or org/team.";
				return false;
			}
			normalized.Add(candidate);
		}
		reviewers = [.. normalized];
		error = null;
		return true;
	}

	private static PrDetailsPayload ToPayload(PullRequestDetails details) => new(
		details.Number, details.Repo, details.Title, details.Body, details.Url, details.State, details.IsDraft,
		details.Author, details.AuthorAvatarUrl, details.BaseBranch, details.HeadBranch,
		[.. details.Reviewers.Select(item => new PrParticipantPayload(item.Login, item.AvatarUrl, item.Kind))],
		[.. details.Comments.Select(item => new PrCommentPayload(
			item.Id, item.Kind, item.Path, item.Author, item.AvatarUrl, item.Body,
			item.CreatedAt, item.UpdatedAt, item.ViewerDidAuthor))],
		[.. details.Commits.Select(item => new PrCommitPayload(
			item.Oid, item.ShortOid, item.Title, item.When, item.CheckState))],
		details.CommentsIncomplete,
		details.CommitsIncomplete,
		null);

	private static PrDetailsPayload EmptyPrDetails(PrDetailsRequestPayload? request, string? error) => new(
		request?.Number ?? 0, request?.Repo ?? string.Empty, string.Empty, string.Empty, string.Empty,
		"unknown", false, string.Empty, string.Empty, string.Empty, string.Empty, [], [], [], false, false, error);
}
