using Microsoft.Extensions.Logging;
using SpecDesk.AppInfo;
using SpecDesk.Contracts;
using SpecDesk.Git;
using SpecDesk.GitHub;
// LibGit2Sharp is referenced only for its exception type; do not bring the whole namespace in (it defines a
// LogLevel that collides with Microsoft.Extensions.Logging.LogLevel).
using LibGit2SharpException = LibGit2Sharp.LibGit2SharpException;

namespace SpecDesk.Host;

// The workspace-state slice of HostController (A4): the persisted recents / favorites / registered GitHub
// repositories, served and mutated over IPC (workspace.request → workspace.state; workspace.favorite /
// repo.register / repo.unregister mutate then re-emit workspace.state). Recents are recorded as a side effect
// of opening a file (LoadFile) or a folder (OnOpenFolder). All handlers are inert when _workspace is null,
// the same graceful-degradation pattern as the chat/auth slices. A4 only STORES a registered repo — no GitHub
// cloning yet. The shared fields, locks, constructor, and the IPC router live in HostController.cs.
public sealed partial class HostController
{
	private enum PendingRepoActionKind
	{
		Register,
		Open,
		Clone,
		CloneToFolder,
		Browse,
		File,
	}

	private sealed record PendingRepoAction(
		PendingRepoActionKind Kind,
		string Owner,
		string Name,
		long NavigationGeneration = 0,
		string? Branch = null,
		string? Path = null,
		string? WirePath = null,
		string? LocalDestination = null);
	private sealed record PendingRepoActions(
		PendingRepoAction[] Registrations,
		PendingRepoAction? Open);

	// Repository access is an authenticated GitHub operation. Requests made while signed out are retained
	// until the device-flow completes, so clicking Add/Open opens GitHub in the normal browser and then
	// continues the exact action instead of making the author repeat it.
	private readonly Dictionary<string, PendingRepoAction> _pendingRepoRegistrations =
		new(StringComparer.OrdinalIgnoreCase);
	private PendingRepoAction? _pendingRepoOpen;
	private sealed record RepoMetadataLookup(long Generation, CancellationTokenSource Cts);
	private readonly Dictionary<string, RepoMetadataLookup> _repoMetadataLookups =
		new(StringComparer.OrdinalIgnoreCase);
	private long _repoMetadataGeneration;

	// Emit the current workspace state to the webview. A null store (workspace persistence unconfigured) makes
	// this a no-op, so the mutating handlers below can call it unconditionally, like the chat guards.
	private void EmitWorkspaceState()
	{
		if (_workspace is null)
		{
			return;
		}

		Emit(IpcSerializer.SerializeEvent(MessageKinds.WorkspaceState, _workspace.State()));
	}

	// Serve the current workspace state (the Start screen asks for it on load).
	private void OnWorkspaceRequest() => EmitWorkspaceState();

	// Toggle a file/folder as a favorite. The item is reconstructed from the path so the store keeps a display
	// label and the file/folder distinction even though the webview only sends the path + the new flag.
	private void OnWorkspaceFavorite(IpcMessage message)
	{
		WorkspaceFavoritePayload? payload = SafeGetPayload<WorkspaceFavoritePayload>(message);
		if (payload is null || string.IsNullOrWhiteSpace(payload.Path))
		{
			return;
		}

		WorkspaceItem? item = FavoriteItem(payload);
		if (item is null)
		{
			return;
		}
		_workspace?.SetFavorite(item, payload.Favorite);
		EmitWorkspaceState();
	}

	private WorkspaceItem? FavoriteItem(WorkspaceFavoritePayload payload)
	{
		if (string.Equals(payload.Kind, "repository", StringComparison.OrdinalIgnoreCase))
		{
			string id = payload.RepositoryId ?? payload.Path;
			RegisteredRepo? repo = _workspace?.FindRepo(id);
			return repo is null && payload.Favorite
				? null
				: new WorkspaceItem(id, repo?.Name ?? id, true, "repository", id);
		}
		if (string.Equals(payload.Kind, "remote", StringComparison.OrdinalIgnoreCase))
		{
			string owner;
			string name;
			string branch;
			string path;
			if (!TryParseRemotePath(payload.Path, out owner, out name, out branch, out path))
			{
				if (string.IsNullOrWhiteSpace(payload.RepositoryId)
					|| string.IsNullOrWhiteSpace(payload.Branch)
					|| !TryParseGitHubRepo(payload.RepositoryId, out owner, out name))
				{
					return null;
				}
				branch = payload.Branch;
				path = payload.Path;
				string[] segments = path.Split('/');
				if (path.Length > 4096 || segments.Length > 64
					|| segments.Any(segment => segment is "" or "." or ".."))
				{
					return null;
				}
			}
			string id = $"{owner}/{name}";
			if (payload.Favorite && _workspace?.FindRepo(id) is null)
			{
				return null;
			}
			return new WorkspaceItem(
				path, path.Split('/')[^1], payload.IsFolder == true, "remote", id, branch);
		}

		string full;
		try
		{
			full = Path.GetFullPath(payload.Path);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
		{
			return null;
		}
		bool isFolder = Directory.Exists(full);
		if (payload.Favorite && !isFolder && !File.Exists(full))
		{
			return null;
		}
		if (!payload.Favorite && !isFolder)
		{
			isFolder = payload.IsFolder == true;
		}
		return new WorkspaceItem(full, LabelFor(full), isFolder);
	}

	// Register a GitHub repository from a URL/spec. Parsing/normalization happens here (the store only holds a
	// validated entry); a string that doesn't name a repo is reported plainly and nothing is stored. A4 stores
	// the entry only — no clone.
	private void OnRegisterRepo(IpcMessage message)
	{
		RegisterRepoPayload? payload = SafeGetPayload<RegisterRepoPayload>(message);
		if (payload is null)
		{
			return;
		}

		if (!TryParseGitHubRepo(payload.Url, out string owner, out string name))
		{
			SendError("That doesn't look like a GitHub repository.");
			return;
		}

		if (!EnsureGitHubAccess(new PendingRepoAction(PendingRepoActionKind.Register, owner, name)))
		{
			return;
		}

		RegisterRepoCore(owner, name);
	}

	// Remove a registered repository by its stable id (owner/name).
	private void OnUnregisterRepo(IpcMessage message)
	{
		UnregisterRepoPayload? payload = SafeGetPayload<UnregisterRepoPayload>(message);
		if (payload is null || string.IsNullOrWhiteSpace(payload.Id))
		{
			return;
		}

		lock (_clonePublishSync)
		{
			lock (_sync)
			{
				if (_disposed)
				{
					return;
				}
				if (_repoMetadataLookups.Remove(payload.Id, out RepoMetadataLookup? lookup))
				{
					lookup.Cts.Cancel();
				}
				if (string.Equals(_cloneRepoId, payload.Id, StringComparison.OrdinalIgnoreCase))
				{
					_cloneGeneration++;
					_cloneCts?.Cancel();
					_cloneCts = null;
					_cloneRepoId = null;
				}
				if (_pendingRepoOpen?.Kind is PendingRepoActionKind.Browse or PendingRepoActionKind.File
					&& string.Equals(
						$"{_pendingRepoOpen.Owner}/{_pendingRepoOpen.Name}", payload.Id,
						StringComparison.OrdinalIgnoreCase))
				{
					_pendingRepoOpen = null;
				}
				_workspace?.UnregisterRepo(payload.Id);
			}
			InvalidateRemoteRepository(payload.Id);
			EmitWorkspaceState();
		}
	}

	// Cancels an in-flight repository clone (window teardown). Guarded by _sync; a non-null value also
	// single-flights the clone, so only one runs at a time — a repo open can take many seconds. Cancelled and
	// nulled on Dispose (which lets the task's own finally dispose it), mirroring _chatCts / _signInCts.
	private CancellationTokenSource? _cloneCts;
	private readonly object _clonePublishSync = new();
	private string? _cloneRepoId;
	private long _cloneGeneration;

	// A6 "Open a GitHub repository": clone it into the managed repos folder (AppPaths.Repos) and open that
	// folder as the workspace. A repo already cloned is opened straight away (no network, no background task);
	// an un-cloned one clones on a background task — it can take many seconds — single-flighted with a CTS
	// cancelled on teardown, mirroring the chat slice. Inert when the cloner isn't wired (graceful, like the
	// other optional dependencies). The GitHub access token, when signed in, is taken transiently
	// (WithAccessTokenAsync) for the clone and is never stored or logged here.
	private void OnOpenRepo(IpcMessage message)
	{
		RepoOpenPayload? payload = SafeGetPayload<RepoOpenPayload>(message);
		if (payload is null)
		{
			return;
		}

		if (!TryParseGitHubRepo(payload.Url, out string owner, out string name))
		{
			SendError("That doesn't look like a GitHub repository.");
			return;
		}

		if (_cloner is null)
		{
			// No cloner configured — nothing to open (graceful degradation, like a null _workspace / _auth).
			return;
		}

		if (!string.IsNullOrWhiteSpace(payload.ClonePath))
		{
			TryOpenRegisteredClone($"{owner}/{name}", payload.ClonePath);
			return;
		}

		if (!EnsureGitHubAccess(new PendingRepoAction(PendingRepoActionKind.Open, owner, name)))
		{
			return;
		}

		OpenRepoCore(owner, name, forceNewClone: false);
	}

	private void OnCloneRepo(IpcMessage message)
	{
		RepoClonePayload? payload = SafeGetPayload<RepoClonePayload>(message);
		RegisteredRepo? repo = payload is null ? null : _workspace?.FindRepo(payload.Id);
		if (repo is null || !TryParseGitHubRepo(repo.Url, out string owner, out string name))
		{
			SendError("That repository is no longer registered.");
			return;
		}

		if (_cloner is null
			|| !EnsureGitHubAccess(new PendingRepoAction(PendingRepoActionKind.Clone, owner, name)))
		{
			return;
		}

		OpenRepoCore(owner, name, forceNewClone: true);
	}

	private void OnCloneRepoManaged(IpcMessage message)
	{
		RepoCloneManagedPayload? payload = SafeGetPayload<RepoCloneManagedPayload>(message);
		if (payload is null || !TryParseGitHubRepo(payload.Url, out string owner, out string name))
		{
			SendError("That doesn't look like a GitHub repository.");
			return;
		}
		if (_cloner is null
			|| !EnsureGitHubAccess(new PendingRepoAction(PendingRepoActionKind.Clone, owner, name)))
		{
			return;
		}

		OpenRepoCore(owner, name, forceNewClone: true);
	}

	private void OnCloneRepoToFolder(IpcMessage message)
	{
		RepoCloneToFolderPayload? payload = SafeGetPayload<RepoCloneToFolderPayload>(message);
		if (payload is null || !TryParseGitHubRepo(payload.Url, out string owner, out string name))
		{
			SendError("That doesn't look like a GitHub repository.");
			return;
		}
		if (_cloner is null)
		{
			return;
		}

		string? parentFolder = _dialogs.PickOpenFolder();
		if (string.IsNullOrWhiteSpace(parentFolder))
		{
			return;
		}
		string destination = NextFolderClonePath(Path.GetFullPath(parentFolder), name);
		if (!EnsureGitHubAccess(new PendingRepoAction(
			PendingRepoActionKind.CloneToFolder,
			owner,
			name,
			LocalDestination: destination)))
		{
			return;
		}

		OpenRepoCore(owner, name, forceNewClone: true, requestedDestination: destination);
	}

	private static string NextFolderClonePath(string parentFolder, string repositoryName)
	{
		for (int copy = 1; copy <= 10_000; copy++)
		{
			string suffix = copy == 1 ? string.Empty : $"-{copy}";
			string candidate = Path.Combine(parentFolder, repositoryName + suffix);
			if (!Directory.Exists(candidate) && !File.Exists(candidate))
			{
				return candidate;
			}
		}
		throw new IOException("No available destination folder could be allocated for the repository.");
	}

	private void OpenRepoCore(
		string owner, string name, bool forceNewClone, string? requestedDestination = null)
	{
		IRepositoryCloner? cloner = _cloner;
		if (cloner is null)
		{
			return;
		}
		string cloneUrl = $"https://github.com/{owner}/{name}.git";
		string repoId = $"{owner}/{name}";
		string localPath;
		bool requireStillRegistered;
		CancellationTokenSource? cts = null;
		long cloneGeneration = 0;
		lock (_clonePublishSync)
		{
			lock (_sync)
			{
				if (_disposed)
				{
					return;
				}
			}
		}

		// Filesystem and LibGit2 probes can block. Check the teardown state before them, then re-enter the
		// publication gate afterwards before any open/claim side effect. Dispose can therefore finish while a
		// probe is blocked, and the completed probe is discarded without starting work.
		RegisteredRepo? descriptor = _workspace?.FindRepo(repoId);
		requireStillRegistered = descriptor is not null;
		string? existingClone = descriptor?.Clones?
			.FirstOrDefault(clone => IsUsableClone(clone.Path))?.Path;
		string legacyManagedPath = Path.Combine(AppPaths.Repos, $"{owner}_{name}");
		if (!forceNewClone && existingClone is null && cloner.IsCloned(legacyManagedPath))
		{
			existingClone = legacyManagedPath;
		}
		localPath = requestedDestination ?? (!forceNewClone && existingClone is not null
			? existingClone
			: NextClonePath(owner, name, descriptor?.Clones ?? []));
		bool isCloned = cloner.IsCloned(localPath);
		bool requestedDestinationOccupied = requestedDestination is not null
			&& (Directory.Exists(localPath) || File.Exists(localPath));

		lock (_clonePublishSync)
		{
			lock (_sync)
			{
				RegisteredRepo? currentDescriptor = _workspace?.FindRepo(repoId);
				if (_disposed || (requireStillRegistered && !ReferenceEquals(currentDescriptor, descriptor)))
				{
					return;
				}
				if (requestedDestinationOccupied)
				{
					SendError("That destination became unavailable. Choose another folder and try again.");
					return;
				}
				if (!isCloned)
				{
					if (_cloneCts is not null)
					{
						_logger.LogDebug("Ignoring a repo open while a clone is already in flight");
						SendError("Another repository copy is still being prepared. Try again when it finishes.");
						return;
					}

					cts = new CancellationTokenSource();
					_cloneCts = cts;
					_cloneRepoId = repoId;
					cloneGeneration = ++_cloneGeneration;
				}
			}

			if (isCloned)
			{
				// Already cloned (a valid working tree, not just any leftover folder) — open it straight away
				// (synchronous, no network). Register it too so a repo opened from an existing clone still lands in
				// the picker.
				RegisterOpenedRepo(owner, name, localPath);
				OpenWorkspaceFolder(localPath);
				return;
			}
		}

		if (cts is null)
		{
			return;
		}
		CancellationToken token = cts.Token;
		IGitHubAuth? auth = _auth;
		_ = Task.Run(async () =>
		{
			try
			{
				if (requestedDestination is not null
					&& (Directory.Exists(localPath) || File.Exists(localPath)))
				{
					throw new IOException("The requested clone destination became unavailable.");
				}
				// A signed-in session clones with the access token so a private repo authenticates (the token
				// is confined to WithAccessTokenAsync's scope); otherwise clone anonymously (public repos). The
				// clone itself is synchronous, so it runs on its own Task.Run inside the token scope.
				string localClone;
				if (auth is not null && auth.IsSignedIn())
				{
					localClone = await auth.WithAccessTokenAsync(
						(accessToken, innerCt) =>
							Task.Run(() => cloner.CloneOrReuse(cloneUrl, localPath, accessToken, innerCt), innerCt),
						token);
				}
				else
				{
					localClone = await Task.Run(() => cloner.CloneOrReuse(cloneUrl, localPath, null, token), token);
				}

				// Register + surface the freshly cloned repo, then open it as the workspace. Emit is
				// thread-safe; OpenWorkspaceFolder's _workspaceRoot mutation takes _sync itself.
				lock (_clonePublishSync)
				{
					bool current;
					lock (_sync)
					{
						current = !_disposed
							&& ReferenceEquals(_cloneCts, cts)
							&& _cloneGeneration == cloneGeneration
							&& (!requireStillRegistered || _workspace?.FindRepo(repoId) is not null);
					}
					if (!current)
					{
						return;
					}
					RegisterOpenedRepo(owner, name, localClone);
					OpenWorkspaceFolder(localClone);
					_logger.LogInformation("Opened repository {Repo} at {Path}", repoId, localClone);
				}
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				// The window is tearing down (or the open was cancelled) — stay quiet; the webview is gone.
			}
			catch (Exception ex) when (
				ex is LibGit2SharpException or InvalidOperationException or IOException or UnauthorizedAccessException)
			{
				// The clone failed (a missing/private repo, an auth refusal, a network or disk fault) — one
				// plain line, never a token or a stack trace. InvalidOperationException also covers
				// ResolveCredentials refusing a non-GitHub host, and WithAccessTokenAsync when signed out.
				_logger.LogError(ex, "Could not open the repository {Repo}", repoId);
				lock (_clonePublishSync)
				{
					bool current;
					lock (_sync)
					{
						current = !_disposed
							&& ReferenceEquals(_cloneCts, cts)
							&& _cloneGeneration == cloneGeneration
							&& (!requireStillRegistered || _workspace?.FindRepo(repoId) is not null);
						if (current)
						{
							_cloneCts = null;
							_cloneRepoId = null;
						}
					}
					if (current)
					{
						SendError("Could not open that repository. Check the name and your connection, then try again.");
					}
				}
			}
			finally
			{
				lock (_sync)
				{
					if (ReferenceEquals(_cloneCts, cts))
					{
						_cloneCts = null;
						_cloneRepoId = null;
					}
				}

				cts.Dispose();
			}
		});
	}

	private bool EnsureGitHubAccess(PendingRepoAction action)
	{
		lock (_signInPublishSync)
		{
			lock (_sync)
			{
				if (_disposed)
				{
					return false;
				}
			}
			if (_auth is null)
			{
				_logger.LogWarning(
					"GitHub access is not configured; set {ClientIdVariable} for development builds",
					GitHubAuthOptions.ClientIdEnvironmentVariable);
				SendError("GitHub access isn't available in this build. Ask your administrator for help.");
				return false;
			}
		}

		bool startSignIn;
		lock (_sync)
		{
			if (_disposed)
			{
				return false;
			}
			// Check the persisted auth state under the same lock used by ResumePendingRepoActions. Without
			// this atomic check+enqueue, authorization could finish between them, drain an empty queue, and
			// leave the newly enqueued action stranded behind a flow that is already complete.
			if (_auth.IsSignedIn())
			{
				return true;
			}

			if (action.Kind == PendingRepoActionKind.Register)
			{
				// Repeated clicks for the same repository collapse to one durable registration request.
				_pendingRepoRegistrations[$"{action.Owner}/{action.Name}"] = action;
			}
			else
			{
				// Opening a repository changes the single central workspace. A later Open supersedes an
				// earlier one while authorization is pending, matching ordinary navigation semantics and
				// avoiding a burst of clones after the author returns from GitHub.
				_pendingRepoOpen = action;
			}

			startSignIn = _signInCts is null;
		}

		if (startSignIn)
		{
			OnGitHubSignIn();
		}

		return false;
	}

	private PendingRepoActions TakePendingRepoActions()
	{
		PendingRepoActions actions = new([.. _pendingRepoRegistrations.Values], _pendingRepoOpen);
		_pendingRepoRegistrations.Clear();
		_pendingRepoOpen = null;
		return actions;
	}

	private void ResumePendingRepoActions(PendingRepoActions actions)
	{
		foreach (PendingRepoAction action in actions.Registrations)
		{
			RegisterRepoCore(action.Owner, action.Name);
		}

		if (actions.Open is not null)
		{
			if (actions.Open.Kind == PendingRepoActionKind.Browse)
			{
				BrowseRepoCore(
					actions.Open.Owner, actions.Open.Name, actions.Open.NavigationGeneration, actions.Open.Branch);
			}
			else if (actions.Open.Kind == PendingRepoActionKind.File
				&& actions.Open.Branch is not null
				&& actions.Open.Path is not null
				&& actions.Open.WirePath is not null)
			{
				LoadRemoteFileCore(
					actions.Open.Owner,
					actions.Open.Name,
					actions.Open.Branch,
					actions.Open.Path,
					actions.Open.WirePath,
					actions.Open.NavigationGeneration);
			}
			else
			{
				OpenRepoCore(
					actions.Open.Owner,
					actions.Open.Name,
					forceNewClone: actions.Open.Kind is PendingRepoActionKind.Clone or PendingRepoActionKind.CloneToFolder,
					requestedDestination: actions.Open.LocalDestination);
			}
		}
	}

	private void ClearPendingRepoActions()
	{
		lock (_sync)
		{
			_pendingRepoRegistrations.Clear();
			_pendingRepoOpen = null;
		}
	}

	private void RegisterRepoCore(string owner, string name)
	{
		lock (_clonePublishSync)
		{
			RegisterRepoCorePublished(owner, name);
		}
	}

	private void RegisterRepoCorePublished(string owner, string name)
	{
		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}
		}
		string id = $"{owner}/{name}";
		string url = $"https://github.com/{owner}/{name}";
		RegisteredRepo? saved = _workspace?.FindRepo(id);
		if (saved is not null && !string.IsNullOrWhiteSpace(saved.DefaultBranch))
		{
			// The descriptor already owns the authoritative default. Do not turn every repeated Add into
			// another network lookup or silently change the stored baseline underneath existing copies.
			EmitWorkspaceState();
			return;
		}
		if (_repositoryCatalog is null || _auth is null)
		{
			_workspace?.UpdateRepo(new RegisteredRepo(id, id, url, string.Empty, []));
			EmitWorkspaceState();
			return;
		}

		CancellationTokenSource cts = new();
		RepoMetadataLookup lookup;
		lock (_sync)
		{
			if (_repoMetadataLookups.Remove(id, out RepoMetadataLookup? previous))
			{
				previous.Cts.Cancel();
			}
			lookup = new RepoMetadataLookup(Interlocked.Increment(ref _repoMetadataGeneration), cts);
			_repoMetadataLookups[id] = lookup;
		}
		CancellationToken cancellationToken = cts.Token;
		_ = Task.Run(async () =>
		{
			try
			{
				GitHubRepositoryMetadata metadata = await _auth.WithAccessTokenAsync(
					(token, ct) => _repositoryCatalog.GetMetadataAsync(owner, name, token, ct),
					cancellationToken);
				lock (_clonePublishSync)
				{
					bool publish;
					lock (_sync)
					{
						publish = !_disposed
							&& _repoMetadataLookups.TryGetValue(id, out RepoMetadataLookup? current)
							&& ReferenceEquals(current, lookup);
						if (publish)
						{
							_workspace?.SetRepoDefaultBranch(
								new RegisteredRepo(id, id, url, string.Empty, []), metadata.DefaultBranch);
							_repoMetadataLookups.Remove(id);
						}
					}
					if (publish)
					{
						EmitWorkspaceState();
					}
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				// Superseded registration, explicit removal, or controller teardown.
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Could not register GitHub repository {Repo}", id);
				lock (_clonePublishSync)
				{
					bool current;
					lock (_sync)
					{
						current = !_disposed
							&& _repoMetadataLookups.TryGetValue(id, out RepoMetadataLookup? active)
							&& ReferenceEquals(active, lookup);
						if (current)
						{
							_repoMetadataLookups.Remove(id);
						}
					}
					if (current)
					{
						SendError("Could not add that repository. Check its name and your access, then try again.");
					}
				}
			}
			finally
			{
				lock (_sync)
				{
					if (_repoMetadataLookups.TryGetValue(id, out RepoMetadataLookup? active)
						&& ReferenceEquals(active, lookup))
					{
						_repoMetadataLookups.Remove(id);
					}
				}
				cts.Dispose();
			}
		});
	}

	// Register the just-opened repo (de-duplicated by id in the store) and re-emit workspace.state, so opening
	// a GitHub repo also keeps it in the Repositories picker. Same normalized shape as OnRegisterRepo.
	private void RegisterOpenedRepo(string owner, string name, string clonePath)
	{
		string id = $"{owner}/{name}";
		RegisteredRepo descriptor = _workspace?.FindRepo(id)
			?? new RegisteredRepo(id, id, $"https://github.com/{owner}/{name}", string.Empty, []);
		LocalRepositoryInfo info;
		try
		{
			info = _repositoryInspector?.Inspect(clonePath, descriptor.DefaultBranch)
				?? new LocalRepositoryInfo(descriptor.DefaultBranch, []);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LibGit2SharpException)
		{
			// The clone itself succeeded. Keep it usable even if its optional branch inventory could not be
			// read this time; opening it is more important than the nested branch labels.
			_logger.LogWarning(ex, "Could not inspect branches for local repository {Repo}", id);
			info = new LocalRepositoryInfo(descriptor.DefaultBranch, []);
		}
		RegisteredClone clone = new(
			Path.GetFileName(clonePath), Path.GetFullPath(clonePath), info.Branches);
		_workspace?.UpsertRepoClone(descriptor, clone, info.DefaultBranch);
		EmitWorkspaceState();
	}

	private bool TryOpenRegisteredClone(string repoId, string path)
	{
		lock (_clonePublishSync)
		{
			RegisteredRepo? repo;
			lock (_sync)
			{
				if (_disposed)
				{
					return false;
				}
				repo = _workspace?.FindRepo(repoId);
			}
			RegisteredClone? clone = repo?.Clones?.FirstOrDefault(candidate => SameFullPath(candidate.Path, path));
			if (clone is null || !IsUsableClone(clone.Path))
			{
				SendError("That local copy is no longer available.");
				return false;
			}

			RegisterOpenedRepo(
				repoId[..repoId.IndexOf('/', StringComparison.Ordinal)],
				repoId[(repoId.IndexOf('/', StringComparison.Ordinal) + 1)..], clone.Path);
			OpenWorkspaceFolder(clone.Path);
			return true;
		}
	}

	private static string NextClonePath(
		string owner, string name, IReadOnlyList<RegisteredClone> registeredClones)
	{
		string stem = $"{owner}_{name}";
		HashSet<string> known = registeredClones
			.Select(clone => TryFullPath(clone.Path))
			.Where(path => path is not null)
			.Select(path => path!)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		for (int suffix = 1; ; suffix++)
		{
			string folder = suffix == 1 ? stem : $"{stem}-{suffix}";
			string candidate = Path.GetFullPath(Path.Combine(AppPaths.Repos, folder));
			if (!known.Contains(candidate) && !Directory.Exists(candidate))
			{
				return candidate;
			}
		}
	}

	private static bool SameFullPath(string left, string right)
	{
		string? leftFull = TryFullPath(left);
		string? rightFull = TryFullPath(right);
		return leftFull is not null && rightFull is not null
			&& string.Equals(leftFull, rightFull, StringComparison.OrdinalIgnoreCase);
	}

	private static string? TryFullPath(string path)
	{
		try
		{
			return Path.GetFullPath(path);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return null;
		}
	}

	private bool IsUsableClone(string path)
	{
		if (_cloner is null || TryFullPath(path) is null)
		{
			return false;
		}
		try
		{
			return _cloner.IsCloned(path);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
		{
			_logger.LogWarning(ex, "Ignoring an unavailable registered repository copy");
			return false;
		}
	}

	// Record a freshly opened file/folder as the most recent, then push the refreshed state. Called from
	// LoadFile (a file) and OnOpenFolder (a folder). Inert when the store is unconfigured.
	private void RecordRecent(string path, bool isFolder)
	{
		if (_workspace is null)
		{
			return;
		}

		_workspace.AddRecent(new WorkspaceItem(path, LabelFor(path), isFolder));
		EmitWorkspaceState();
	}

	// The display label for a path: its last segment (trimming any trailing separator), falling back to the
	// whole path when there is no segment — e.g. a drive root like "C:\", where GetFileName yields "".
	private static string LabelFor(string path)
	{
		string label = Path.GetFileName(path.TrimEnd('/', '\\'));
		return label.Length > 0 ? label : path;
	}

	// Parse a GitHub repo reference into owner/name. Accepts the three forms the register prompt allows:
	// https://github.com/owner/name(.git), a bare owner/name, and git@github.com:owner/name(.git). Pure and
	// static so it is unit-testable apart from the controller. Rejects anything that isn't exactly an
	// owner/name pair of valid (alphanumeric / - / _ / .) segments.
	internal static bool TryParseGitHubRepo(string input, out string owner, out string name)
	{
		owner = string.Empty;
		name = string.Empty;
		if (string.IsNullOrWhiteSpace(input))
		{
			return false;
		}

		string spec = input.Trim();

		// Reduce each accepted form to a bare "owner/name" path. A scheme'd URL MUST be a github.com URL — a
		// non-github host (gitlab.com/…, example.com/…) is rejected outright rather than mis-read as the owner.
		const string scp = "git@github.com:";
		if (spec.StartsWith(scp, StringComparison.OrdinalIgnoreCase))
		{
			spec = spec[scp.Length..];
		}
		else if (spec.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
			|| spec.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
		{
			spec = StripPrefix(spec, "https://");
			spec = StripPrefix(spec, "http://");
			spec = StripPrefix(spec, "www.");
			if (!spec.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			spec = spec["github.com/".Length..];
		}
		else
		{
			// No scheme: a bare "github.com/owner/name" or "owner/name". Peel an optional github.com host.
			spec = StripPrefix(spec, "www.");
			spec = StripPrefix(spec, "github.com/");
		}

		spec = spec.Trim('/');
		if (spec.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
		{
			spec = spec[..^4];
		}

		string[] segments = spec.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		// The owner must obey GitHub's account-name rules (no dots), which also rejects a host-looking first
		// segment (e.g. "gitlab.com") from the scheme-less path; the repo name allows the wider GitHub set.
		if (segments.Length != 2 || !IsValidOwner(segments[0]) || !IsValidRepoName(segments[1]))
		{
			return false;
		}

		owner = segments[0];
		name = segments[1];
		return true;
	}

	private static string StripPrefix(string value, string prefix) =>
		value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;

	// A GitHub account (owner) name: letters/digits/hyphens only (no dots or underscores — GitHub forbids them),
	// and not starting or ending with a hyphen. Rejecting dots also rejects a host masquerading as the owner.
	private static bool IsValidOwner(string segment)
	{
		if (segment.Length == 0 || segment[0] == '-' || segment[^1] == '-')
		{
			return false;
		}

		foreach (char c in segment)
		{
			if (!char.IsAsciiLetterOrDigit(c) && c != '-')
			{
				return false;
			}
		}

		return true;
	}

	// A GitHub repository name: non-empty, not a "." / ".." path token, and only the characters GitHub permits
	// (letters, digits, hyphen, underscore, dot).
	private static bool IsValidRepoName(string segment)
	{
		if (segment.Length == 0 || segment == "." || segment == "..")
		{
			return false;
		}

		foreach (char c in segment)
		{
			if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
			{
				return false;
			}
		}

		return true;
	}
}
