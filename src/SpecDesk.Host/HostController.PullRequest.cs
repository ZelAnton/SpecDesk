using Microsoft.Extensions.Logging;
using SpecDesk.Contracts;
using SpecDesk.GitHub;

namespace SpecDesk.Host;

public sealed partial class HostController
{
	// Pull-request detail / reviewer / inline-comment mutation kinds (see the central RegisterMessageHandlers).
	private void RegisterPullRequestHandlers()
	{
		_messageHandlers.Register(MessageKinds.PrDetailsRequest, OnPrDetails);
		_messageHandlers.Register(MessageKinds.PrReviewersRequest, OnPrReviewers);
		_messageHandlers.Register(MessageKinds.PrCommentCreate, OnPrCommentCreate);
		_messageHandlers.Register(MessageKinds.PrCommentReply, OnPrCommentReply);
		_messageHandlers.Register(MessageKinds.PrCommentUpdate, OnPrCommentUpdate);
		// PoC-7 Part C "in-flight PR awareness & comparison": the open PRs touching the current file, and a
		// read-only comparison of a chosen one against the working copy / main. Registered in the PR domain
		// (K-012), never the central HostController.cs switch.
		_messageHandlers.Register(MessageKinds.PrForFile, OnPrForFile);
		_messageHandlers.Register(MessageKinds.PrCompareRequest, OnPrCompare);
	}

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

	// --- PoC-7 Part C: in-flight PR awareness (pr.forFile) and comparison (pr.compare.request) ---

	// "Which open PRs touch the file I'm editing?" A best-effort background read, correlated to the request by
	// id. The webview passes its view of the path, but the host resolves the authoritative repository-relative
	// path from its OWN current-document state (K-010) and echoes it back. Not connected / not a GitHub repo /
	// no versioned document all reply with an empty list and no error (the affordance simply stays hidden),
	// so this passive awareness never nags the author; only a genuine load failure carries a plain reason.
	private void OnPrForFile(IpcMessage message)
	{
		// Parse defensively so a malformed payload still gets a correlated (empty) reply rather than hanging
		// the request; the path is only a stale-view hint (K-010), so its value is deliberately unused.
		_ = SafeGetPayload<PrForFileRequestPayload>(message);

		string? path;
		string? repoRoot;
		lock (_sync)
		{
			path = _currentPath;
			repoRoot = _repoRoot;
		}

		if (path is null || repoRoot is null || !IsRepoVersioned(repoRoot))
		{
			ReplyForFile(message.Id, string.Empty, [], null);
			return;
		}

		string relativePath = RepoRelativePath(repoRoot, path);
		if (_auth is null || _review is null || ResolveGitHubReviewRepo(repoRoot) is not { } repo
			|| !TryCaptureAccountSession(out AccountSession accountSession))
		{
			ReplyForFile(message.Id, relativePath, [], null);
			return;
		}

		RunPrForFile(message.Id, accountSession, repo, relativePath);
	}

	private void RunPrForFile(string? id, AccountSession accountSession, GitHubRepo repo, string relativePath)
	{
		_ = Task.Run(async () =>
		{
			using CancellationTokenSource timeout =
				CancellationTokenSource.CreateLinkedTokenSource(accountSession.CancellationToken);
			timeout.CancelAfter(PrListTimeout);
			PrForFilePayload payload;
			try
			{
				Task<IReadOnlyList<PullRequestForFile>>? operation = null;
				if (!StartForAccountSession(accountSession, () =>
					operation = _auth!.WithAccessTokenAsync(
						(token, ct) => _review!.ListOpenPullRequestsForFileAsync(
							token, repo.Owner, repo.Name, relativePath, ct),
						timeout.Token)))
				{
					return;
				}
				IReadOnlyList<PullRequestForFile> items = await operation!;
				string repoId = $"{repo.Owner}/{repo.Name}";
				payload = new PrForFilePayload(
					relativePath,
					[.. items.Select(item => new PrForFileItemPayload(item.Number, item.Title, item.Url, repoId))],
					null);
			}
			catch (OperationCanceledException) when (accountSession.CancellationToken.IsCancellationRequested)
			{
				// Sign-out retired the account session; its private review data must not reach the next session.
				return;
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Could not list pull requests touching {Path}", relativePath);
				payload = new PrForFilePayload(relativePath, [], "Couldn't check for other reviews of this file.");
			}

			PublishForAccountSession(accountSession, () =>
				ReplyForFile(id, payload.Path, payload.Items, payload.Error));
		});
	}

	private void ReplyForFile(
		string? id, string path, IReadOnlyList<PrForFileItemPayload> items, string? error) =>
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.PrForFile, new PrForFilePayload(path, items, error), id: id));

	// "Compare the chosen PR's version of this file against my working copy / against main." The base is
	// resolved locally (the editor buffer for the working copy, or the local main-branch blob), the PR head
	// content is fetched read-only, and the two are compared through the SAME structural diff as the local
	// "Show changes" overlay (PrCompareHtml → DiffProjection) — no new diff algorithm. owner/repo and the
	// repository-relative path come from the host's own current-document state, never the payload (K-010).
	private void OnPrCompare(IpcMessage message)
	{
		PrCompareRequestPayload? payload = SafeGetPayload<PrCompareRequestPayload>(message);
		string mode = payload?.Mode ?? PrCompareModes.Rendered;
		string baseKind = payload?.Base ?? PrCompareBases.WorkingCopy;
		if (payload is null || payload.PrNumber <= 0
			|| (baseKind != PrCompareBases.WorkingCopy && baseKind != PrCompareBases.Main)
			|| (mode != PrCompareModes.Rendered && mode != PrCompareModes.Raw))
		{
			ReplyCompare(message.Id, string.Empty, mode, baseKind, "This comparison request isn't valid.");
			return;
		}

		string text;
		string? path;
		string? repoRoot;
		string? preferredBase;
		lock (_sync)
		{
			text = _text;
			path = _currentPath;
			repoRoot = _repoRoot;
			preferredBase = _session.BaseBranch;
		}

		if (path is null || repoRoot is null || !IsRepoVersioned(repoRoot))
		{
			ReplyCompare(message.Id, string.Empty, mode, baseKind, "Open a document before comparing.");
			return;
		}

		string relativePath = RepoRelativePath(repoRoot, path);
		string docDir = DocRelativeDir();

		// Resolve the base text locally (no network): the working copy is the live editor buffer (with unsaved
		// edits); main is the file's blob at the local default-branch tip. Both repo reads take _repoGate, the
		// same lock OnCompare uses, so a concurrent autosave/versioning op can't race the handle.
		string? baseText;
		if (baseKind == PrCompareBases.WorkingCopy)
		{
			baseText = text;
		}
		else
		{
			lock (_repoGate)
			{
				string mainBranch = _versioning.DefaultBranch(repoRoot, preferredBase) ?? "main";
				baseText = _versioning.ReadBranchContent(repoRoot, mainBranch, relativePath);
			}
		}

		if (_auth is null || _review is null || ResolveGitHubReviewRepo(repoRoot) is not { } repo
			|| !TryCaptureAccountSession(out AccountSession accountSession))
		{
			ReplyCompare(
				message.Id, string.Empty, mode, baseKind, "Connect a GitHub account to compare with this review.");
			return;
		}

		RunPrCompare(
			message.Id, accountSession, repo, payload.PrNumber, relativePath, baseKind, mode, baseText, docDir);
	}

	private void RunPrCompare(
		string? id, AccountSession accountSession, GitHubRepo repo, int prNumber, string relativePath,
		string baseKind, string mode, string? baseText, string docDir)
	{
		_ = Task.Run(async () =>
		{
			using CancellationTokenSource timeout =
				CancellationTokenSource.CreateLinkedTokenSource(accountSession.CancellationToken);
			timeout.CancelAfter(PullRequestDocumentTimeout);
			PrComparePayload payload;
			try
			{
				Task<string?>? operation = null;
				if (!StartForAccountSession(accountSession, () =>
					operation = _auth!.WithAccessTokenAsync(
						(token, ct) => _review!.ReadFileAtPullRequestHeadAsync(
							token, repo.Owner, repo.Name, prNumber, relativePath, ct),
						timeout.Token)))
				{
					return;
				}
				string? headText = await operation!;
				if (headText is null)
				{
					// The PR head no longer carries this file (it was removed in the proposal) — nothing to
					// compare, so say so plainly rather than showing an empty diff the author can't interpret.
					payload = new PrComparePayload(
						string.Empty, mode, baseKind, "This review no longer changes this file.");
				}
				else
				{
					string html = PrCompareHtml.Build(baseText, headText, mode, docDir, _render);
					payload = new PrComparePayload(html, mode, baseKind, null);
				}
			}
			catch (OperationCanceledException) when (accountSession.CancellationToken.IsCancellationRequested)
			{
				// Sign-out retired the account session; its private review data must not reach the next session.
				return;
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Could not compare with pull request #{Number}", prNumber);
				payload = new PrComparePayload(
					string.Empty, mode, baseKind,
					"Couldn't load that comparison. Check your connection and try again.");
			}

			PublishForAccountSession(accountSession, () =>
				ReplyCompare(id, payload.Html, payload.Mode, payload.Base, payload.Error));
		});
	}

	private void ReplyCompare(string? id, string html, string mode, string baseKind, string? error) =>
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.PrCompareRendered, new PrComparePayload(html, mode, baseKind, error), id: id));
}
