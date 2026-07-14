using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using LibGit2Sharp;

namespace SpecDesk.Git;

/// <summary>
/// The <see cref="IRepositoryCloner"/> implementation backed by LibGit2Sharp. Each call opens no long-lived
/// handle — <see cref="Repository.Clone(string, string, CloneOptions)"/> owns its own — so the type is
/// stateless and thread-safe. The caller supplies the exact destination folder (the host namespaces it by
/// owner and name), so this type never derives a folder name and same-named repos from different owners
/// can't collide.
/// </summary>
public sealed class LibGit2RepositoryCloner : IRepositoryCloner, ILocalRepositoryManager
{
	private const string SafetyCopyPrefix = "specdesk:safety-copy:v1:";

	public LocalRepositoryInfo Fetch(
		string repositoryPath,
		string expectedRepositoryUrl,
		string knownDefaultBranch,
		string? accessToken,
		CancellationToken ct) =>
		Fetch(repositoryPath, expectedRepositoryUrl, knownDefaultBranch, accessToken, beforeNetwork: null, beforeCredentials: null, ct);

	internal static LocalRepositoryInfo Fetch(
		string repositoryPath,
		string expectedRepositoryUrl,
		string knownDefaultBranch,
		string? accessToken,
		Action? beforeNetwork,
		Action? beforeCredentials,
		CancellationToken ct)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedRepositoryUrl);
		ct.ThrowIfCancellationRequested();
		using Repository repository = new(repositoryPath);
		EnsureExactWorkingTree(repository, repositoryPath);
		ValidatedRemote origin = CaptureOrigin(repository, expectedRepositoryUrl, forPush: false);
		FetchCore(repository, origin, accessToken, beforeNetwork, beforeCredentials, ct);
		return Inspect(repository, knownDefaultBranch);
	}

	public LocalRepositoryInfo PullFastForward(
		string repositoryPath,
		string expectedRepositoryUrl,
		string knownDefaultBranch,
		string expectedBranch,
		string? accessToken,
		CancellationToken ct,
		Action? beforeMutation = null,
		Action? onMutationStarting = null) =>
		PullFastForward(
			repositoryPath,
			expectedRepositoryUrl,
			knownDefaultBranch,
			expectedBranch,
			accessToken,
			beforeMutation,
			onMutationStarting,
			beforeNetwork: null,
			beforeCredentials: null,
			ct);

	internal static LocalRepositoryInfo PullFastForward(
		string repositoryPath,
		string expectedRepositoryUrl,
		string knownDefaultBranch,
		string expectedBranch,
		string? accessToken,
		Action? beforeMutation,
		Action? onMutationStarting,
		Action? beforeNetwork,
		Action? beforeCredentials,
		CancellationToken ct)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedRepositoryUrl);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedBranch);
		ct.ThrowIfCancellationRequested();
		using Repository repository = new(repositoryPath);
		EnsureExactWorkingTree(repository, repositoryPath);
		ValidatedRemote origin = CaptureOrigin(repository, expectedRepositoryUrl, forPush: false);
		Branch local = RequireCurrentBranch(repository, expectedBranch);
		beforeMutation?.Invoke();
		EnsurePullIsSafe(repository);
		FetchCore(repository, origin, accessToken, beforeNetwork, beforeCredentials, ct);
		local = RequireCurrentBranch(repository, expectedBranch);
		EnsurePullIsSafe(repository);
		Branch? tracked = ResolveTrackedBranch(repository, local);
		if (tracked is null)
		{
			return Inspect(repository, knownDefaultBranch);
		}
		HistoryDivergence divergence = repository.ObjectDatabase.CalculateHistoryDivergence(local.Tip, tracked.Tip);
		if (divergence.AheadBy is null || divergence.BehindBy is null)
		{
			throw new InvalidOperationException("The local and GitHub working lines cannot be compared safely.");
		}
		if (divergence.AheadBy > 0 && divergence.BehindBy > 0)
		{
			throw new InvalidOperationException(
				"This working line has different local and GitHub versions. Review them before getting updates.");
		}
		if (divergence.BehindBy == 0)
		{
			return Inspect(repository, knownDefaultBranch);
		}

		EnsureIgnoredFilesAreNotOverwritten(repository, local.Tip.Tree, tracked.Tip.Tree);
		ct.ThrowIfCancellationRequested();
		Signature signature = new("SpecDesk", "specdesk@local", DateTimeOffset.Now);
		onMutationStarting?.Invoke();
		MergeResult result = repository.Merge(
			tracked,
			signature,
			new MergeOptions
			{
				FastForwardStrategy = FastForwardStrategy.FastForwardOnly,
				FailOnConflict = true,
			});
		if (result.Status != MergeStatus.FastForward)
		{
			throw new InvalidOperationException("GitHub updates could not be applied as a safe fast-forward.");
		}
		return Inspect(repository, knownDefaultBranch);
	}

	public LocalRepositoryInfo PushBranchSafely(
		string repositoryPath,
		string expectedRepositoryUrl,
		string knownDefaultBranch,
		string expectedBranch,
		string accessToken,
		CancellationToken ct) =>
		PushBranchSafely(
			repositoryPath,
			expectedRepositoryUrl,
			knownDefaultBranch,
			expectedBranch,
			accessToken,
			beforeNetwork: null,
			beforeCredentials: null,
			ct);

	internal static LocalRepositoryInfo PushBranchSafely(
		string repositoryPath,
		string expectedRepositoryUrl,
		string knownDefaultBranch,
		string expectedBranch,
		string accessToken,
		Action? beforeNetwork,
		Action? beforeCredentials,
		CancellationToken ct)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedRepositoryUrl);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedBranch);
		ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
		ct.ThrowIfCancellationRequested();
		using Repository repository = new(repositoryPath);
		EnsureExactWorkingTree(repository, repositoryPath);
		ValidatedRemote origin = CaptureOrigin(repository, expectedRepositoryUrl, forPush: true);
		Branch local = RequireCurrentBranch(repository, expectedBranch);
		if (repository.Index.Conflicts.Any())
		{
			throw new InvalidOperationException("Resolve the overlapping local changes before sharing this working line.");
		}
		FetchCore(repository, origin, accessToken, beforeNetwork, beforeCredentials, ct);
		local = RequireCurrentBranch(repository, expectedBranch);
		if (repository.Index.Conflicts.Any())
		{
			throw new InvalidOperationException("Resolve the overlapping local changes before sharing this working line.");
		}
		Branch? tracked = ResolveTrackedBranch(repository, local);
		if (tracked is not null)
		{
			HistoryDivergence divergence = repository.ObjectDatabase.CalculateHistoryDivergence(local.Tip, tracked.Tip);
			if (divergence.AheadBy is null || divergence.BehindBy is null)
			{
				throw new InvalidOperationException("The local and GitHub working lines cannot be compared safely.");
			}
			if (divergence.BehindBy > 0)
			{
				throw new InvalidOperationException("GitHub has newer versions. Get updates before sharing this working line.");
			}
		}

		string? rejectedReference = null;
		string? rejectionMessage = null;
		PushOptions options = new()
		{
			CredentialsProvider = (endpoint, _, _) =>
			{
				beforeCredentials?.Invoke();
				ct.ThrowIfCancellationRequested();
				return LibGit2DocumentVersioning.ResolveCredentials(endpoint, accessToken);
			},
			OnPushTransferProgress = (_, _, _) => !ct.IsCancellationRequested,
			OnPushStatusError = error =>
			{
				rejectedReference = error.Reference;
				rejectionMessage = error.Message;
			},
		};
		try
		{
			PushCapturedRemote(repository, origin, local.CanonicalName, options);
			LibGit2DocumentVersioning.ThrowIfRejected(rejectedReference, rejectionMessage);
		}
		catch (UserCancelledException) when (ct.IsCancellationRequested)
		{
			throw new OperationCanceledException(ct);
		}

		string remoteReference = $"refs/remotes/{origin.Name}/{expectedBranch}";
		if (repository.Refs[remoteReference] is Reference existing)
		{
			repository.Refs.UpdateTarget(existing, local.Tip.Id);
		}
		else
		{
			repository.Refs.Add(remoteReference, local.Tip.Id);
		}
		repository.Branches.Update(local, updater => updater.TrackedBranch = remoteReference);
		return Inspect(repository, knownDefaultBranch);
	}

	private static void FetchCore(
		Repository repository,
		ValidatedRemote origin,
		string? accessToken,
		Action? beforeNetwork,
		Action? beforeCredentials,
		CancellationToken ct)
	{
		FetchOptions options = new()
		{
			Prune = true,
			OnTransferProgress = _ => !ct.IsCancellationRequested,
		};
		if (!string.IsNullOrEmpty(accessToken))
		{
			options.CredentialsProvider = (endpoint, _, _) =>
			{
				beforeCredentials?.Invoke();
				ct.ThrowIfCancellationRequested();
				return LibGit2DocumentVersioning.ResolveCredentials(endpoint, accessToken);
			};
		}

		try
		{
			beforeNetwork?.Invoke();
			ct.ThrowIfCancellationRequested();
			FetchCapturedRemote(repository, origin, options);
			ct.ThrowIfCancellationRequested();
		}
		catch (UserCancelledException) when (ct.IsCancellationRequested)
		{
			throw new OperationCanceledException(ct);
		}
	}

	private sealed record ValidatedRemote(
		Remote Remote,
		string Name,
		string FetchUrl,
		string EffectivePushUrl,
		IReadOnlyList<string> FetchRefSpecs);

	private static ValidatedRemote CaptureOrigin(
		Repository repository,
		string expectedRepositoryUrl,
		bool forPush)
	{
		Remote origin = RequireOrigin(repository);
		EnsureRemoteMatchesExpectedRepository(origin, expectedRepositoryUrl);
		if (forPush)
		{
			EnsurePushUrlMatchesExpectedRepository(origin, expectedRepositoryUrl);
		}
		return new ValidatedRemote(
			origin,
			origin.Name,
			origin.Url,
			origin.PushUrl ?? origin.Url,
			origin.FetchRefSpecs.Select(spec => spec.Specification).ToArray());
	}


	private static void FetchCapturedRemote(
		Repository repository,
		ValidatedRemote origin,
		FetchOptions options)
	{
		if (!Path.IsPathFullyQualified(origin.FetchUrl))
		{
			Commands.Fetch(repository, origin.FetchUrl, origin.FetchRefSpecs, options, "SpecDesk refresh");
			return;
		}

		string operationRemoteName = $"specdesk-operation-{Guid.NewGuid():N}";
		repository.Network.Remotes.Add(operationRemoteName, origin.FetchUrl);
		try
		{
			Commands.Fetch(repository, operationRemoteName, origin.FetchRefSpecs, options, "SpecDesk refresh");
		}
		finally
		{
			repository.Network.Remotes.Remove(operationRemoteName);
		}
	}

	private static void PushCapturedRemote(
		Repository repository,
		ValidatedRemote origin,
		string canonicalBranchName,
		PushOptions options)
	{
		string operationRemoteName = $"specdesk-operation-{Guid.NewGuid():N}";
		Remote operationRemote = repository.Network.Remotes.Add(operationRemoteName, origin.EffectivePushUrl);
		try
		{
			repository.Network.Push(operationRemote, canonicalBranchName, options);
		}
		finally
		{
			repository.Network.Remotes.Remove(operationRemoteName);
		}
	}

	private static Remote RequireOrigin(Repository repository) =>
		repository.Network.Remotes["origin"]
		?? throw new InvalidOperationException("This local copy has no GitHub source.");

	private static Branch RequireCurrentBranch(Repository repository, string expectedBranch)
	{
		if (repository.Info.IsHeadDetached
			|| !string.Equals(repository.Head.FriendlyName, expectedBranch, StringComparison.Ordinal))
		{
			throw new InvalidOperationException("The current working line changed. Refresh the repository list and try again.");
		}
		return repository.Head;
	}

	private static void EnsurePullIsSafe(Repository repository)
	{
		if (repository.Index.Conflicts.Any())
		{
			throw new InvalidOperationException("Resolve the overlapping local changes before getting updates.");
		}
		if (HasWorkingChanges(repository))
		{
			throw new InvalidOperationException("Finish or protect the unfinished local edits before getting updates.");
		}
	}

	private static void EnsureIgnoredFilesAreNotOverwritten(
		Repository repository,
		Tree localTree,
		Tree remoteTree)
	{
		foreach (StatusEntry entry in repository.RetrieveStatus(new StatusOptions
		{
			IncludeIgnored = true,
			IncludeUntracked = false,
			RecurseIgnoredDirs = true,
		}).Where(entry => (entry.State & FileStatus.Ignored) != 0))
		{
            if (!TreeContainsPath(localTree, entry.FilePath)
                && TargetTreeCollidesWithLocalPath(repository.Info.WorkingDirectory, remoteTree, entry.FilePath))
			{
				throw new InvalidOperationException(
					"A protected local file is in the way of GitHub updates. Move it before getting updates.");
			}
		}
	}

    private static bool TreeContainsPath(Tree tree, string path)
    {
        string[] parts = path.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        Tree current = tree;
        for (int index = 0; index < parts.Length; index++)
        {
            TreeEntry? entry = current.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, parts[index], StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return false;
            }
            if (index == parts.Length - 1)
            {
                return true;
            }
            if (entry.Target is not Tree child)
            {
                return false;
            }
            current = child;
        }
        return false;
    }

    internal static bool TargetTreeCollidesWithLocalPath(string worktreeRoot, Tree targetTree, string path)
    {
        string[] parts = path.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        Tree current = targetTree;
        for (int index = 0; index < parts.Length; index++)
        {
            TreeEntry? target = current.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, parts[index], StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                return false;
            }
            if (index == parts.Length - 1)
            {
                string localPath = Path.Combine(worktreeRoot, Path.Combine(parts));
                return target.Target is not Tree || !Directory.Exists(localPath);
            }
            if (target.Target is not Tree child)
            {
                return true;
            }
            current = child;
        }
        return false;
    }

	private static Branch? ResolveTrackedBranch(Repository repository, Branch local)
	{
		Branch? tracked = local.TrackedBranch;
		if (tracked is not null
			&& tracked.CanonicalName.StartsWith("refs/remotes/origin/", StringComparison.Ordinal))
		{
			return tracked;
		}
		Branch? matchingOrigin = repository.Branches[$"origin/{local.FriendlyName}"];
		if (matchingOrigin is not null)
		{
			repository.Branches.Update(local, updater => updater.TrackedBranch = matchingOrigin.CanonicalName);
		}
		return matchingOrigin;
	}

    public bool IsCloned(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        // A valid clone is a directory that IS a git working tree — a bare existence check would treat
        // partial/faulted debris as "cloned" and open it as a broken (empty) workspace.
        return Directory.Exists(destinationPath) && Repository.IsValid(destinationPath);
    }

	public bool IsCloneOf(string destinationPath, string url)
	{
		ArgumentException.ThrowIfNullOrEmpty(destinationPath);
		ArgumentException.ThrowIfNullOrEmpty(url);
		try
		{
			if (!Directory.Exists(destinationPath) || !Repository.IsValid(destinationPath))
			{
				return false;
			}
			using Repository repository = new(destinationPath);
			EnsureExactWorkingTree(repository, destinationPath);
			return MatchesRequestedRepository(repository, url);
		}
		catch (Exception ex) when (
			ex is ArgumentException
				or LibGit2SharpException
				or InvalidOperationException
				or IOException
				or NotSupportedException
				or UnauthorizedAccessException)
		{
			return false;
		}
	}

	public bool IsCloneOfAtBranch(string destinationPath, string url, string? expectedCurrentBranch)
	{
		ArgumentException.ThrowIfNullOrEmpty(destinationPath);
		ArgumentException.ThrowIfNullOrEmpty(url);
		try
		{
			if (!Directory.Exists(destinationPath))
			{
				return false;
			}
			using Repository repository = new(destinationPath);
			EnsureExactWorkingTree(repository, destinationPath);
			if (!MatchesRequestedRepository(repository, url))
			{
				return false;
			}
			return expectedCurrentBranch is null
				? repository.Info.IsHeadDetached
				: !repository.Info.IsHeadDetached
					&& string.Equals(
						repository.Head.FriendlyName,
						expectedCurrentBranch,
						StringComparison.Ordinal);
		}
		catch (Exception ex) when (
			ex is ArgumentException
				or LibGit2SharpException
				or InvalidOperationException
				or IOException
				or NotSupportedException
				or UnauthorizedAccessException)
		{
			return false;
		}
	}

    public string CloneOrReuse(string url, string destinationPath, string? accessToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        ct.ThrowIfCancellationRequested();

		if (Directory.Exists(destinationPath))
		{
			// Reuse only the repository the caller requested. A valid but unrelated working tree can appear
			// after the host's availability probe; returning it would register and open the wrong repository.
			if (Repository.IsValid(destinationPath))
			{
				using Repository existing = new(destinationPath);
				if (MatchesRequestedRepository(existing, url))
				{
					return destinationPath;
				}
				throw new RepositoryDestinationConflictException(destinationPath);
			}

			// The host can target a user-selected folder. Never guess that an existing non-repository directory
			// is stale clone debris: another process may have created it after the picker chose the destination.
			throw new RepositoryDestinationConflictException(destinationPath);
		}
		if (File.Exists(destinationPath))
		{
			throw new RepositoryDestinationConflictException(destinationPath);
		}

		string parent = Path.GetDirectoryName(Path.GetFullPath(destinationPath))
			?? throw new IOException("The repository destination has no parent folder.");
		string stagingPath = Path.Combine(
			parent,
			$".{Path.GetFileName(destinationPath)}.specdesk-clone-{Guid.NewGuid():N}");

        CloneOptions options = new();
        if (!string.IsNullOrEmpty(accessToken))
        {
            // Authenticate a private repo. The token is released ONLY to an HTTPS github.com host — see
            // LibGit2DocumentVersioning.ResolveCredentials for why anything else (a re-pointed URL, an SSH or
            // local-file remote) must refuse rather than fall back to the Windows user's credentials. Reused
            // here so the same github-host-only guard that protects the push protects the clone.
			options.FetchOptions.CredentialsProvider = (endpoint, _, _) =>
			{
				ct.ThrowIfCancellationRequested();
				return LibGit2DocumentVersioning.ResolveCredentials(endpoint, accessToken);
			};
        }

        // Abort a stalled transfer when the caller cancels (the host bounds the clone with a CTS, cancelled on
        // window teardown). The connect/handshake phase isn't surfaced through this callback, so a stall there
        // is bounded only by the OS socket timeout — the same LibGit2Sharp limitation the push path documents.
        options.FetchOptions.OnTransferProgress = _ => !ct.IsCancellationRequested;

		try
		{
			// Repository.Clone returns the path to the created .git directory, not the working tree; return
			// destinationPath (the workdir we passed) — that is the folder opened as the workspace.
			Repository.Clone(url, stagingPath, options);
			ct.ThrowIfCancellationRequested();
			// The move is same-parent and therefore fail-closed: if anything claimed the requested destination
			// while the clone was running, Directory.Move throws and the unrelated path remains untouched.
			try
			{
				Directory.Move(stagingPath, destinationPath);
			}
			catch (IOException) when (Directory.Exists(destinationPath) || File.Exists(destinationPath))
			{
				throw new RepositoryDestinationConflictException(destinationPath);
			}
            return destinationPath;
        }
        catch (UserCancelledException) when (ct.IsCancellationRequested)
        {
            // The transfer-progress callback above aborted the clone because the caller cancelled (window
            // teardown). Remove the partial working tree and surface it as a plain OperationCanceledException,
            // so the host treats it like any other cancellation rather than a clone error.
			TryDeleteOwnedDirectory(stagingPath);
			throw new OperationCanceledException(ct);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			// Cancellation may arrive after the final transfer callback but before the atomic publish move.
			TryDeleteOwnedDirectory(stagingPath);
			throw;
		}
        catch (Exception ex) when (ex is LibGit2SharpException or InvalidOperationException or IOException)
        {
            // A genuine clone failure leaves a partial directory; remove it so the next attempt — and the
            // host's IsCloned check — doesn't mistake the debris for a usable clone. LibGit2SharpException is
            // the ordinary failure (a missing/private repo, a network fault); InvalidOperationException is the
            // credentials guard refusing a non-github endpoint (ResolveCredentials) surfaced back through
            // Repository.Clone. Rethrow either for the host to report plainly.
			TryDeleteOwnedDirectory(stagingPath);
            throw;
		}
    }

	private static bool MatchesRequestedRepository(Repository repository, string requestedUrl)
	{
		string? existingUrl = repository.Network.Remotes["origin"]?.Url;
		if (string.IsNullOrWhiteSpace(existingUrl))
		{
			return false;
		}
		return RepositoryUrlsMatch(requestedUrl, existingUrl);
	}

	internal static bool RepositoryUrlsMatch(string requestedUrl, string existingUrl)
	{
		bool requestedIsGitHub = TryGitHubIdentity(requestedUrl, out string? requestedIdentity);
		bool existingIsGitHub = TryGitHubIdentity(existingUrl, out string? existingIdentity);
		bool requestedClaimsGitHub = requestedIsGitHub || HasGitHubAuthority(requestedUrl);
		bool existingClaimsGitHub = existingIsGitHub || HasGitHubAuthority(existingUrl);
		if (requestedClaimsGitHub || existingClaimsGitHub)
		{
			return requestedIsGitHub
				&& existingIsGitHub
				&& string.Equals(requestedIdentity, existingIdentity, StringComparison.OrdinalIgnoreCase);
		}
		if (Path.IsPathFullyQualified(requestedUrl) && Path.IsPathFullyQualified(existingUrl))
		{
			return string.Equals(
				Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedUrl)),
				Path.TrimEndingDirectorySeparator(Path.GetFullPath(existingUrl)),
				StringComparison.OrdinalIgnoreCase);
		}
		return string.Equals(
			NormalizeRepositoryUrl(requestedUrl),
			NormalizeRepositoryUrl(existingUrl),
			StringComparison.OrdinalIgnoreCase);
	}

	internal static void EnsurePushUrlMatchesExpectedRepository(Remote remote, string expectedRepositoryUrl)
	{
		if (!string.IsNullOrWhiteSpace(remote.PushUrl)
			&& !RepositoryUrlsMatch(expectedRepositoryUrl, remote.PushUrl))
		{
			throw new RepositoryIdentityMismatchException(
				"This local copy is configured to share changes with a different repository.");
		}
	}

	internal static void EnsureRemoteMatchesExpectedRepository(Remote remote, string expectedRepositoryUrl)
	{
		if (string.IsNullOrWhiteSpace(remote.Url)
			|| !RepositoryUrlsMatch(expectedRepositoryUrl, remote.Url))
		{
			throw new RepositoryIdentityMismatchException();
		}
	}

	private static bool HasGitHubAuthority(string url) =>
		(Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
			&& string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
		|| url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase);

	private static bool TryGitHubIdentity(string url, out string? identity)
	{
		identity = null;
		string path;
		if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
			&& IsApprovedGitHubUri(uri))
		{
			path = uri.AbsolutePath;
		}
		else
		{
			const string scpPrefix = "git@github.com:";
			if (!url.StartsWith(scpPrefix, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			path = url[scpPrefix.Length..];
		}
		string[] parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length != 2)
		{
			return false;
		}
		string name = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
			? parts[1][..^4]
			: parts[1];
		if (parts[0].Length == 0 || name.Length == 0)
		{
			return false;
		}
		identity = $"{parts[0]}/{name}";
		return true;
	}

	private static bool IsApprovedGitHubUri(Uri uri)
	{
		bool https = uri.Scheme == Uri.UriSchemeHttps
			&& string.IsNullOrEmpty(uri.UserInfo);
		bool ssh = uri.Scheme == "ssh"
			&& string.Equals(uri.UserInfo, "git", StringComparison.Ordinal);
		bool approvedPort = uri.IsDefaultPort
			|| (https && uri.Port == 443)
			|| (ssh && uri.Port == 22);
		return (https || ssh)
			&& approvedPort
			&& string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
			&& string.IsNullOrEmpty(uri.Query)
			&& string.IsNullOrEmpty(uri.Fragment);
	}

	private static string NormalizeRepositoryUrl(string url)
	{
		string normalized = url.Trim().TrimEnd('/');
		return normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
			? normalized[..^4]
			: normalized;
	}

	public LocalRepositoryInfo Inspect(string repositoryPath, string knownDefaultBranch)
	{
		using Repository repository = new(repositoryPath);
		EnsureExactWorkingTree(repository, repositoryPath);
		return Inspect(repository, knownDefaultBranch);
	}

	private static LocalRepositoryInfo Inspect(Repository repository, string knownDefaultBranch)
	{
		string defaultBranch = knownDefaultBranch;
		if (string.IsNullOrWhiteSpace(defaultBranch))
		{
			Branch? remoteHead = repository.Branches["origin/HEAD"];
			defaultBranch = remoteHead?.TrackedBranch?.FriendlyName ?? string.Empty;
			if (!string.IsNullOrWhiteSpace(defaultBranch)
				&& defaultBranch.StartsWith("origin/", StringComparison.Ordinal))
			{
				defaultBranch = defaultBranch["origin/".Length..];
			}

			if (string.IsNullOrWhiteSpace(defaultBranch))
			{
				// A normal clone checks out the remote default branch, so HEAD is the final reliable fallback.
				defaultBranch = repository.Head.FriendlyName;
			}
		}
		string? currentBranch = repository.Info.IsHeadDetached ? null : repository.Head.FriendlyName;
		RepositoryStatus workingStatus = repository.RetrieveStatus(new StatusOptions
		{
			IncludeUntracked = true,
			RecurseUntrackedDirs = true,
		});
		bool hasUncommitted = workingStatus.IsDirty;
		bool hasConflicts = repository.Index.Conflicts.Any();
		string remotePrefix = "origin/";
		string[] branchNames = repository.Branches
			.Select(branch => branch.IsRemote && branch.FriendlyName.StartsWith(remotePrefix, StringComparison.Ordinal)
				? branch.FriendlyName[remotePrefix.Length..]
				: branch.FriendlyName)
			.Where(branch => branch.Length > 0
				&& !string.Equals(branch, "HEAD", StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		LocalBranchInfo[] branches = branchNames
			.Select(name => BuildBranchInfo(
				repository,
				name,
				defaultBranch,
				currentBranch,
				hasUncommitted,
				hasConflicts))
			.ToArray();
		LocalRepositoryStatus currentStatus = branches
			.FirstOrDefault(branch => string.Equals(branch.Name, currentBranch, StringComparison.OrdinalIgnoreCase))
			?.Status
			?? new LocalRepositoryStatus(
				0,
				0,
				hasUncommitted,
				currentBranch is null ? 0 : CountBranchStashes(repository, currentBranch),
				hasConflicts);
		LocalRepositoryStatus cloneStatus = currentStatus with { StashCount = repository.Stashes.Count() };
		return new LocalRepositoryInfo(defaultBranch, currentBranch, branches, cloneStatus);
	}

	private static LocalBranchInfo BuildBranchInfo(
		Repository repository,
		string name,
		string defaultBranch,
		string? currentBranch,
		bool hasUncommitted,
		bool hasConflicts)
	{
		Branch? local = repository.Branches[name];
		if (local?.IsRemote == true)
		{
			local = null;
		}
		(int ahead, int behind) = TrackingStatus(local);
		bool current = string.Equals(name, currentBranch, StringComparison.OrdinalIgnoreCase);
		return new LocalBranchInfo(
			name,
			new LocalRepositoryStatus(
				ahead,
				behind,
				current && hasUncommitted,
				CountBranchStashes(repository, name),
				current && hasConflicts),
			local is not null && !string.Equals(name, defaultBranch, StringComparison.OrdinalIgnoreCase));
	}

	private static (int Ahead, int Behind) TrackingStatus(Branch? local)
	{
		if (local is null)
		{
			return (0, 0);
		}
		if (local.TrackedBranch is null)
		{
			return (1, 0);
		}
		return (
			local.TrackingDetails.AheadBy ?? 1,
			local.TrackingDetails.BehindBy ?? 1);
	}

	public BranchSwitchResult SwitchBranchSafely(
		string repositoryPath,
		string expectedRepositoryUrl,
		string expectedCurrentBranch,
		string branch,
		Action? beforeMutation = null,
		Action? onMutationStarting = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedRepositoryUrl);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedCurrentBranch);
		ArgumentException.ThrowIfNullOrWhiteSpace(branch);
		using Repository repository = new(repositoryPath);
		EnsureExactWorkingTree(repository, repositoryPath);
		EnsureRepositoryIdentity(repository, expectedRepositoryUrl);
		Branch current = RequireCurrentBranch(repository, expectedCurrentBranch);
		beforeMutation?.Invoke();
		string currentBranch = current.FriendlyName;
		if (string.Equals(currentBranch, branch, StringComparison.Ordinal))
		{
			if (HasWorkingChanges(repository))
			{
				return new BranchSwitchResult(currentBranch, false, false, false);
			}
			return RestoreSafetyCopy(repository, currentBranch, false, onMutationStarting);
		}
		Branch target = ResolveLocalBranch(repository, branch);
		EnsureIgnoredFilesAreNotOverwritten(repository, repository.Head.Tip.Tree, target.Tip.Tree);

		bool createdSafetyCopy = false;
		if (HasWorkingChanges(repository))
		{
			Signature signature = new("SpecDesk", "specdesk@local", DateTimeOffset.Now);
			repository.Stashes.Add(
				signature,
				SafetyCopyName(currentBranch),
				StashModifiers.IncludeUntracked);
			createdSafetyCopy = true;
		}

		onMutationStarting?.Invoke();
		Commands.Checkout(repository, target);
		return RestoreSafetyCopy(repository, repository.Head.FriendlyName, createdSafetyCopy);
	}

	private static BranchSwitchResult RestoreSafetyCopy(
		Repository repository,
		string branch,
		bool createdSafetyCopy,
		Action? onMutationStarting = null)
	{
		int safetyCopyIndex = FindSafetyCopy(repository, branch);
		if (safetyCopyIndex < 0)
		{
			return new BranchSwitchResult(repository.Head.FriendlyName, createdSafetyCopy, false, false);
		}

		onMutationStarting?.Invoke();
		StashApplyStatus applied = repository.Stashes.Apply(safetyCopyIndex);
		if (applied == StashApplyStatus.Applied)
		{
			repository.Stashes.Remove(safetyCopyIndex);
			return new BranchSwitchResult(repository.Head.FriendlyName, createdSafetyCopy, true, false);
		}
		return new BranchSwitchResult(
			repository.Head.FriendlyName,
			createdSafetyCopy,
			false,
			true);
	}

	private static bool HasWorkingChanges(Repository repository)
	{
		return repository.RetrieveStatus(new StatusOptions
		{
			IncludeUntracked = true,
			RecurseUntrackedDirs = true,
		}).IsDirty;
	}

	private static Branch ResolveLocalBranch(Repository repository, string branch)
	{
		Branch? local = repository.Branches[branch];
		if (local is not null && !local.IsRemote)
		{
			return local;
		}

		Branch? remote = repository.Branches[$"origin/{branch}"];
		if (remote is null)
		{
			throw new InvalidOperationException("That working line is no longer available.");
		}
		local = repository.CreateBranch(branch, remote.Tip);
		repository.Branches.Update(local, updater => updater.TrackedBranch = remote.CanonicalName);
		return local;
	}

	private static string SafetyCopyName(string branch)
	{
		return $"{SafetyCopyBranchPrefix(branch)}{Guid.NewGuid():N}";
	}

	private static int FindSafetyCopy(Repository repository, string branch)
	{
		string prefix = SafetyCopyBranchPrefix(branch);
		int index = 0;
		foreach (Stash stash in repository.Stashes)
		{
			// LibGit2 prefixes custom stash text with "On <branch>: "; the versioned marker remains
			// embedded verbatim and uniquely identifies only SpecDesk-owned safety copies.
			if (stash.Message.Contains(prefix, StringComparison.Ordinal))
			{
				return index;
			}
			index++;
		}
		return -1;
	}

	private static string SafetyCopyBranchPrefix(string branch)
	{
		string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(branch))
			.TrimEnd('=')
			.Replace('+', '-')
			.Replace('/', '_');
		return $"{SafetyCopyPrefix}{encoded}:";
	}

	public RepositoryDeletionRisks InspectDeletionRisks(
		string repositoryPath,
		string expectedRepositoryUrl,
		string? expectedCurrentBranch,
		string? branch = null,
		Action? beforeInspect = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedRepositoryUrl);
		using Repository repository = new(repositoryPath);
		EnsureExactWorkingTree(repository, repositoryPath);
		EnsureRepositoryIdentity(repository, expectedRepositoryUrl);
		EnsureExpectedCurrentBranch(repository, expectedCurrentBranch);
		beforeInspect?.Invoke();
		IEnumerable<Branch> localBranches = repository.Branches.Where(candidate => !candidate.IsRemote);
		if (!string.IsNullOrWhiteSpace(branch))
		{
			localBranches = localBranches.Where(candidate =>
				string.Equals(candidate.FriendlyName, branch, StringComparison.Ordinal));
		}
		Branch[] selected = localBranches.ToArray();
		if (!string.IsNullOrWhiteSpace(branch) && selected.Length == 0)
		{
			throw new InvalidOperationException("That working line is no longer available.");
		}

		bool currentSelected = string.IsNullOrWhiteSpace(branch)
			|| string.Equals(repository.Head.FriendlyName, branch, StringComparison.Ordinal);
		bool hasUncommitted = currentSelected
			&& (branch is null ? HasCloneLocalChanges(repository) : HasWorkingChanges(repository));
		bool hasUnpushed = selected.Any(candidate => HasUnpushedCommits(repository, candidate));
		int stashCount = string.IsNullOrWhiteSpace(branch)
			? repository.Stashes.Count()
			: CountBranchStashes(repository, branch);
		IReadOnlyList<LinkedWorktreeDeletionRisk> linkedWorktrees = branch is null
			? InspectLinkedWorktrees(repository)
			: [];
		return new RepositoryDeletionRisks(
			hasUncommitted,
			hasUnpushed,
			stashCount,
			DeletionToken(repository, branch),
			linkedWorktrees);
	}

	public BranchDeletionResult DeleteBranch(
		string repositoryPath,
		string expectedRepositoryUrl,
		string branch,
		string defaultBranch,
		string confirmationToken,
		Action? onCurrentBranchChangeStarting = null) =>
		DeleteBranchCore(
			repositoryPath, expectedRepositoryUrl, branch, defaultBranch, confirmationToken, beforeRemove: null,
			onCurrentBranchChangeStarting);

	internal static BranchDeletionResult DeleteBranchCore(
		string repositoryPath,
		string expectedRepositoryUrl,
		string branch,
		string defaultBranch,
		string confirmationToken,
		Action? beforeRemove,
		Action? onCurrentBranchChangeStarting = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedRepositoryUrl);
		ArgumentException.ThrowIfNullOrWhiteSpace(branch);
		ArgumentException.ThrowIfNullOrWhiteSpace(defaultBranch);
		using Repository repository = new(repositoryPath);
		EnsureExactWorkingTree(repository, repositoryPath);
		EnsureRepositoryIdentity(repository, expectedRepositoryUrl);
		if (!CryptographicOperations.FixedTimeEquals(
			Convert.FromHexString(DeletionToken(repository, branch)),
			Convert.FromHexString(confirmationToken)))
		{
			throw new RepositoryStateChangedException();
		}
		if (string.Equals(branch, defaultBranch, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("The main working line cannot be removed.");
		}
		Branch? target = repository.Branches[branch];
		if (target is null || target.IsRemote)
		{
			throw new InvalidOperationException("That working line is no longer available.");
		}
		EnsureBranchNotCheckedOutInLinkedWorktree(repository, branch);
		using GitReferenceDeleteOperation deletion = GitReferenceDeleteOperation.Prepare(
			repository.Info.Path, target.CanonicalName, target.Tip.Id.Sha);
		bool switchedCurrentBranch = string.Equals(
			repository.Head.FriendlyName, branch, StringComparison.Ordinal);
		bool createdSafetyCopy = false;
		if (switchedCurrentBranch)
		{
			Branch defaultTarget = ResolveLocalBranch(repository, defaultBranch);
			EnsureIgnoredFilesAreNotOverwritten(repository, repository.Head.Tip.Tree, defaultTarget.Tip.Tree);
			if (HasWorkingChanges(repository))
			{
				Signature signature = new("SpecDesk", "specdesk@local", DateTimeOffset.Now);
				repository.Stashes.Add(
					signature,
					SafetyCopyName(branch),
					StashModifiers.IncludeUntracked);
				createdSafetyCopy = true;
			}
			onCurrentBranchChangeStarting?.Invoke();
			Commands.Checkout(repository, defaultTarget);
		}
		try
		{
			beforeRemove?.Invoke();
			deletion.Delete();
		}
		catch (RepositoryStateChangedException) when (switchedCurrentBranch)
		{
			TryRestoreBranchAfterFailedDelete(repository, branch, createdSafetyCopy);
			throw;
		}
		bool cleanupComplete;
		try
		{
			RemoveBranchStashes(repository, branch);
			cleanupComplete = true;
		}
		catch (Exception ex) when (ex is LibGit2SharpException or InvalidOperationException)
		{
			// The working line is already gone. Keep the operation successful so persisted state cannot
			// resurrect it; the retained snapshots remain recoverable and are surfaced by repository status.
			cleanupComplete = false;
		}
		return new BranchDeletionResult(
			cleanupComplete,
			switchedCurrentBranch,
			Inspect(repository, defaultBranch));
	}

	private static void TryRestoreBranchAfterFailedDelete(
		Repository repository,
		string branch,
		bool createdSafetyCopy)
	{
		try
		{
			Branch restored = ResolveLocalBranch(repository, branch);
			EnsureIgnoredFilesAreNotOverwritten(repository, repository.Head.Tip.Tree, restored.Tip.Tree);
			Commands.Checkout(repository, restored);
			if (createdSafetyCopy)
			{
				RestoreSafetyCopy(repository, branch, createdSafetyCopy: true);
			}
		}
		catch (Exception ex) when (ex is LibGit2SharpException or InvalidOperationException)
		{
			// The saved safety copy remains recoverable if an external change also prevents rollback.
		}
	}

	private static void EnsureBranchNotCheckedOutInLinkedWorktree(Repository repository, string branch)
	{
		foreach (Worktree worktree in repository.Worktrees)
		{
			using Repository linkedRepository = worktree.WorktreeRepository;
			if (!linkedRepository.Info.IsHeadDetached
				&& string.Equals(linkedRepository.Head.FriendlyName, branch, StringComparison.Ordinal))
			{
				throw new InvalidOperationException(
					"That working line is open in another local worktree and cannot be removed.");
			}
		}
	}

	public bool DeleteClone(
		string repositoryPath,
		string expectedRepositoryUrl,
		string confirmationToken,
		Action? onMutationStarting = null) =>
		DeleteCloneCore(
			repositoryPath,
			expectedRepositoryUrl,
			confirmationToken,
			beforeMove: null,
			onMutationStarting);

	internal static bool DeleteCloneCore(
		string repositoryPath,
		string expectedRepositoryUrl,
		string confirmationToken,
		Action? beforeMove,
		Action? onMutationStarting = null,
		Action<string, string>? afterMove = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedRepositoryUrl);
		string fullPath = Path.GetFullPath(repositoryPath);
		string root = Path.GetPathRoot(fullPath)
			?? throw new InvalidOperationException("The local copy path has no filesystem root.");
		if (string.Equals(
			Path.TrimEndingDirectorySeparator(fullPath),
			Path.TrimEndingDirectorySeparator(root),
			StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("A filesystem root cannot be removed as a local copy.");
		}
		if (!Directory.Exists(fullPath))
		{
			throw new DirectoryNotFoundException("The local copy no longer exists.");
		}
		string tombstone = fullPath + ".specdesk-delete-" + Guid.NewGuid().ToString("N");
		using (Repository repository = new(fullPath))
		{
			EnsureExactWorkingTree(repository, fullPath);
			EnsureRepositoryIdentity(repository, expectedRepositoryUrl);
			EnsureNoLinkedWorktrees(repository);
			if (!CryptographicOperations.FixedTimeEquals(
				Convert.FromHexString(DeletionToken(repository, branch: null)),
				Convert.FromHexString(confirmationToken)))
			{
				throw new RepositoryStateChangedException();
			}
			beforeMove?.Invoke();
			EnsureNoLinkedWorktrees(repository);
			onMutationStarting?.Invoke();
			Directory.Move(fullPath, tombstone);
		}
		try
		{
			afterMove?.Invoke(tombstone, fullPath);
			using Repository movedRepository = new(tombstone);
			EnsureExactWorkingTree(movedRepository, tombstone);
			EnsureRepositoryIdentity(movedRepository, expectedRepositoryUrl);
			if (!CryptographicOperations.FixedTimeEquals(
				Convert.FromHexString(DeletionToken(movedRepository, branch: null, logicalPath: fullPath)),
				Convert.FromHexString(confirmationToken)))
			{
				throw new RepositoryStateChangedException();
			}
		}
		catch (Exception ex) when (
			ex is LibGit2SharpException
				or InvalidOperationException
				or IOException
				or UnauthorizedAccessException
				or ArgumentException)
		{
			if (!TryRestoreQuarantinedTree(tombstone, fullPath))
			{
				throw new RepositoryQuarantinedCloneException(tombstone, ex);
			}
			throw new RepositoryStateChangedException();
		}
		try
		{
			DeleteOwnedTree(tombstone);
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// The verified tree was already atomically detached from its registered path. Treat the logical
			// removal as complete and report the quarantined cleanup separately to the caller.
			return false;
		}
	}

	internal static void EnsureExactWorkingTree(Repository repository, string requestedPath)
	{
		string requested = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedPath));
		string actual = Path.TrimEndingDirectorySeparator(
			Path.GetFullPath(repository.Info.WorkingDirectory));
		if (!string.Equals(requested, actual, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("The registered local-copy path is not the repository root.");
		}
	}

	private static void EnsureRepositoryIdentity(Repository repository, string expectedRepositoryUrl)
	{
		if (!MatchesRequestedRepository(repository, expectedRepositoryUrl))
		{
			throw new RepositoryIdentityMismatchException();
		}
	}

	private static void EnsureExpectedCurrentBranch(Repository repository, string? expectedCurrentBranch)
	{
		bool expectedDetached = expectedCurrentBranch is null;
		if (repository.Info.IsHeadDetached != expectedDetached
			|| (!expectedDetached && !string.Equals(
				repository.Head.FriendlyName, expectedCurrentBranch, StringComparison.Ordinal)))
		{
			throw new InvalidOperationException(
				"The current working line changed. Refresh the repository list and try again.");
		}
	}

	private static string DeletionToken(
		Repository repository,
		string? branch,
		string? logicalPath = null)
	{
		StringBuilder state = new();
		string tokenPath = Path.TrimEndingDirectorySeparator(
			Path.GetFullPath(logicalPath ?? repository.Info.WorkingDirectory));
		state.Append(tokenPath).Append('\n')
			.Append(branch ?? "*").Append('\n')
			.Append(repository.Head.FriendlyName).Append('\n')
			.Append(repository.Head.Tip.Id.Sha).Append('\n');
		foreach (StatusEntry entry in repository.RetrieveStatus(new StatusOptions
		{
			IncludeIgnored = branch is null,
			IncludeUntracked = true,
			RecurseIgnoredDirs = branch is null,
			RecurseUntrackedDirs = true,
		}).OrderBy(entry => entry.FilePath, StringComparer.Ordinal))
		{
			state.Append(entry.FilePath).Append(':').Append((int)entry.State).Append(':')
				.Append(repository.Index[entry.FilePath]?.Id.Sha ?? "-").Append(':')
				.Append(WorkingFileHash(repository.Info.WorkingDirectory, entry.FilePath)).Append('\n');
		}
		foreach (Branch candidate in repository.Branches
			.OrderBy(candidate => candidate.FriendlyName, StringComparer.Ordinal))
		{
			state.Append(candidate.FriendlyName).Append(':').Append(candidate.Tip.Id.Sha).Append(':')
				.Append(candidate.TrackedBranch?.Tip.Id.Sha ?? "-").Append(':')
				.Append(candidate.TrackingDetails.AheadBy?.ToString(CultureInfo.InvariantCulture) ?? "-").Append(':')
				.Append(candidate.TrackingDetails.BehindBy?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('\n');
		}
		foreach (Stash stash in repository.Stashes)
		{
			state.Append(stash.WorkTree.Id.Sha).Append(':').Append(stash.Message).Append('\n');
		}
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state.ToString())));
	}

	private static bool TryRestoreQuarantinedTree(string tombstone, string originalPath)
	{
		if (!Directory.Exists(tombstone)
			|| Directory.Exists(originalPath)
			|| File.Exists(originalPath))
		{
			return false;
		}
		try
		{
			Directory.Move(tombstone, originalPath);
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// The unverified tree remains quarantined under its unique tombstone name and is never deleted.
			return false;
		}
	}

	private static bool HasCloneLocalChanges(Repository repository)
	{
		return repository.RetrieveStatus(new StatusOptions
		{
			IncludeIgnored = true,
			IncludeUntracked = true,
			RecurseIgnoredDirs = true,
			RecurseUntrackedDirs = true,
		}).Any();
	}

	private static List<LinkedWorktreeDeletionRisk> InspectLinkedWorktrees(Repository repository)
	{
		List<LinkedWorktreeDeletionRisk> risks = [];
		foreach (Worktree worktree in repository.Worktrees.OrderBy(candidate => candidate.Name, StringComparer.Ordinal))
		{
			try
			{
				using Repository linkedRepository = worktree.WorktreeRepository;
				string path = Path.TrimEndingDirectorySeparator(
					Path.GetFullPath(linkedRepository.Info.WorkingDirectory));
				string? currentBranch = linkedRepository.Info.IsHeadDetached
					? null
					: linkedRepository.Head.FriendlyName;
				RepositoryStatus status = linkedRepository.RetrieveStatus(new StatusOptions
				{
					IncludeUntracked = true,
					RecurseUntrackedDirs = true,
				});
				Branch? branch = currentBranch is null ? null : linkedRepository.Branches[currentBranch];
				risks.Add(new LinkedWorktreeDeletionRisk(
					worktree.Name,
					path,
					currentBranch,
					status.IsDirty,
					branch is not null && HasUnpushedCommits(linkedRepository, branch),
					currentBranch is null ? 0 : CountBranchStashes(linkedRepository, currentBranch),
					linkedRepository.Index.Conflicts.Any()));
			}
			catch (Exception ex) when (
				ex is ArgumentException
					or LibGit2SharpException
					or InvalidOperationException
					or IOException
					or NotSupportedException
					or UnauthorizedAccessException)
			{
				risks.Add(new LinkedWorktreeDeletionRisk(
					worktree.Name,
					worktree.Name,
					null,
					false,
					false,
					0,
					false,
					InspectionFailed: true));
			}
		}
		return risks;
	}

	private static void EnsureNoLinkedWorktrees(Repository repository)
	{
		List<LinkedWorktreeDeletionRisk> linkedWorktrees = InspectLinkedWorktrees(repository);
		if (linkedWorktrees.Count > 0)
		{
			throw new RepositoryHasLinkedWorktreesException(linkedWorktrees);
		}
	}

	private static string WorkingFileHash(string repositoryRoot, string relativePath)
	{
		string path = Path.GetFullPath(Path.Combine(repositoryRoot, relativePath));
		if (!File.Exists(path))
		{
			return "-";
		}
		using FileStream stream = new(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete);
		return Convert.ToHexString(SHA256.HashData(stream));
	}

	private static bool HasUnpushedCommits(Repository repository, Branch branch)
	{
		if (branch.TrackedBranch is null)
		{
			return true;
		}
		return branch.TrackingDetails.AheadBy is null or > 0;
	}

	private static int CountBranchStashes(Repository repository, string branch)
	{
		string libGitPrefix = $"On {branch}:";
		string specDeskPrefix = SafetyCopyBranchPrefix(branch);
		return repository.Stashes.Count(stash =>
			stash.Message.StartsWith(libGitPrefix, StringComparison.Ordinal)
			|| stash.Message.Contains(specDeskPrefix, StringComparison.Ordinal));
	}

	private static void RemoveBranchStashes(Repository repository, string branch)
	{
		string libGitPrefix = $"On {branch}:";
		string specDeskPrefix = SafetyCopyBranchPrefix(branch);
		int[] indexes = repository.Stashes
			.Select((stash, index) => new { stash, index })
			.Where(item => item.stash.Message.StartsWith(libGitPrefix, StringComparison.Ordinal)
				|| item.stash.Message.Contains(specDeskPrefix, StringComparison.Ordinal))
			.Select(item => item.index)
			.OrderDescending()
			.ToArray();
		foreach (int index in indexes)
		{
			repository.Stashes.Remove(index);
		}
	}

    // Best-effort recursive delete that first clears the read-only attribute git sets on pack files (mirrors
    // the Git tests' own teardown). Never throws: leaving debris behind is preferable to faulting the clone.
	private static void TryDeleteOwnedDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
			DeleteOwnedTree(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best-effort — a locked/partly-removed tree is left in place rather than faulting here.
        }
    }

	private static void DeleteOwnedTree(string path)
	{
		FileAttributes rootAttributes = File.GetAttributes(path);
		if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
		{
			Directory.Delete(path, recursive: false);
			return;
		}

		foreach (string entry in Directory.EnumerateFileSystemEntries(path))
		{
			FileAttributes attributes = File.GetAttributes(entry);
			if ((attributes & FileAttributes.Directory) != 0)
			{
				if ((attributes & FileAttributes.ReparsePoint) != 0)
				{
					Directory.Delete(entry, recursive: false);
				}
				else
				{
					DeleteOwnedTree(entry);
				}
				continue;
			}

			File.SetAttributes(entry, FileAttributes.Normal);
			File.Delete(entry);
		}
		Directory.Delete(path, recursive: false);
	}
}
