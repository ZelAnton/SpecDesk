using System.Net;
using Microsoft.Extensions.Logging;
using SpecDesk.AppInfo;
using SpecDesk.Contracts;
using SpecDesk.Core;
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
		long RemoteGeneration = 0,
		string? Branch = null,
		string? Path = null,
		string? WirePath = null,
		string? LocalDestination = null,
		string? RequestUrl = null,
		long RequestId = 0,
		WorkspaceStore.RepositoryRegistrationSnapshot? RegistrationIntent = null);
	private sealed record PendingRepoActions(
		PendingRepoAction[] Registrations,
		PendingRepoAction? Open);

	// Repository access is an authenticated GitHub operation. Requests made while signed out are retained
	// until the device-flow completes, so clicking Add/Open opens GitHub in the normal browser and then
	// continues the exact action instead of making the author repeat it.
	private readonly Dictionary<string, PendingRepoAction> _pendingRepoRegistrations =
		new(StringComparer.OrdinalIgnoreCase);
	private PendingRepoAction? _pendingRepoOpen;
	private sealed record RepoMetadataLookup(
		long Generation,
		CancellationTokenSource Cts,
		WorkspaceStore.RepositoryRegistrationSnapshot Registration,
		bool RollbackRegistrationOnAccountInvalidation);
	private readonly Dictionary<string, RepoMetadataLookup> _repoMetadataLookups =
		new(StringComparer.OrdinalIgnoreCase);
	private long _repoMetadataGeneration;
	private long _lastEnqueuedWorkspaceRevision = -1;

	// Emit the current workspace state to the webview. A null store (workspace persistence unconfigured) makes
	// this a no-op, so the mutating handlers below can call it unconditionally, like the chat guards.
	private void EmitWorkspaceState(bool force = false)
	{
		if (_workspace is null)
		{
			return;
		}

		WorkspaceStore.WorkspaceStateSnapshot snapshot = _workspace.StateWithRevision();
		if (force)
		{
			WorkspaceRequestStateCapturedForTest?.Invoke();
		}
		EmitWorkspaceState(snapshot, force);
	}

	private void EmitWorkspaceState(WorkspaceStore.WorkspaceStateSnapshot snapshot, bool force = false)
	{
		lock (_sync)
		{
			if (snapshot.Revision < _lastEnqueuedWorkspaceRevision
				|| (!force && snapshot.Revision == _lastEnqueuedWorkspaceRevision))
			{
				return;
			}
			_lastEnqueuedWorkspaceRevision = Math.Max(
				_lastEnqueuedWorkspaceRevision, snapshot.Revision);
			Emit(IpcSerializer.SerializeEvent(MessageKinds.WorkspaceState, snapshot.State));
		}
	}

	private void CompleteRepositoryOperation(long requestId) =>
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.RepoOperationCompleted,
			new RepoOperationCompletedPayload(requestId)));

	// Serve the current workspace state (the Start screen asks for it on load).
	private void OnWorkspaceRequest() => EmitWorkspaceState(force: true);

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

		if (payload.Kind is "clone" or "branch")
		{
			if (string.IsNullOrWhiteSpace(payload.RepositoryId)
				|| (payload.Kind == "clone" && payload.Branch is not null)
				|| (payload.Kind == "branch" && string.IsNullOrWhiteSpace(payload.Branch)))
			{
				return null;
			}
			string clonePath;
			try
			{
				clonePath = Path.GetFullPath(payload.Path);
			}
			catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
			{
				return null;
			}
			RegisteredRepo? registeredRepo = _workspace?.FindRepo(payload.RepositoryId);
			RegisteredClone? registeredClone = registeredRepo?.Clones.FirstOrDefault(
				clone => SameFullPath(clone.Path, clonePath));
			if (payload.Favorite
				&& (registeredClone is null
					|| (payload.Kind == "branch"
						&& !registeredClone.Branches.Any(branch => string.Equals(
							branch.Name, payload.Branch, StringComparison.Ordinal)))))
			{
				return null;
			}
			string cloneLabel = registeredClone?.Id ?? Path.GetFileName(clonePath);
			string label = payload.Kind == "branch"
				? $"{cloneLabel} · {payload.Branch}"
				: cloneLabel;
			return new WorkspaceItem(
				clonePath, label, true, payload.Kind, payload.RepositoryId, payload.Branch);
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

		WorkspaceStore.RepositoryRegistrationSnapshot? registrationIntent =
			_workspace?.CaptureRepoRegistration($"{owner}/{name}");
		if (!EnsureGitHubAccess(new PendingRepoAction(
			PendingRepoActionKind.Register, owner, name, RegistrationIntent: registrationIntent)))
		{
			return;
		}

		RegisterRepoCore(owner, name, registrationIntent);
	}

	// Remove a registered repository by its stable id (owner/name).
	private void OnUnregisterRepo(IpcMessage message)
	{
		UnregisterRepoPayload? payload = SafeGetPayload<UnregisterRepoPayload>(message);
		if (payload is null || string.IsNullOrWhiteSpace(payload.Id))
		{
			return;
		}

		bool actionInProgress;
		CancellationTokenSource? metadataCts = null;
		CancellationTokenSource? cloneCts = null;
		CancellationTokenSource? allRepositoriesCts = null;
		WorkspaceStore.WorkspaceStateSnapshot? unregisteredState = null;
		lock (_clonePublishSync)
		{
			lock (_sync)
			{
				if (_disposed)
				{
					return;
				}
				actionInProgress = _localRepositoryActionCts is not null
					&& string.Equals(
						_localRepositoryActionRepoId, payload.Id, StringComparison.OrdinalIgnoreCase);
				if (!actionInProgress)
				{
					_pendingRepoRegistrations.Remove(payload.Id);
					if (_repoMetadataLookups.Remove(payload.Id, out RepoMetadataLookup? lookup))
					{
						metadataCts = lookup.Cts;
					}
					if (string.Equals(_cloneRepoId, payload.Id, StringComparison.OrdinalIgnoreCase))
					{
						_cloneGeneration++;
						cloneCts = _cloneCts;
					}
					if (_localRepositoryActionRepoId == AllRepositoriesActionId)
					{
						_localRepositoryActionGeneration++;
						allRepositoriesCts = _localRepositoryActionCts;
					}
					if (_pendingRepoOpen is not null
						&& string.Equals(
							$"{_pendingRepoOpen.Owner}/{_pendingRepoOpen.Name}", payload.Id,
							StringComparison.OrdinalIgnoreCase))
					{
						_pendingRepoOpen = null;
					}
				}
			}
			if (!actionInProgress)
			{
				RepositoryUnregisterCommitEnteredForTest?.Set();
				RepositoryUnregisterCommitReleaseForTest?.Wait();
				unregisteredState = _workspace?.UnregisterRepoWithSnapshot(payload.Id);
				if (unregisteredState is not null)
				{
					EmitWorkspaceState(unregisteredState);
				}
			}
		}
		if (actionInProgress)
		{
			SendError("That repository is still finishing a local action. Try removing it again in a moment.");
			return;
		}
		metadataCts?.Cancel();
		cloneCts?.Cancel();
		allRepositoriesCts?.Cancel();
		InvalidateRemoteRepository(payload.Id);
	}
	// Tracks an in-flight repository clone. Guarded by _sync; a non-null value single-flights cloning and acts
	// as the close mutation lease. Teardown, sign-out, and unregister cancel and invalidate publication, but the
	// task retains ownership until its terminal finally clears and disposes the source.
	private CancellationTokenSource? _cloneCts;
	private readonly object _clonePublishSync = new();
	internal ManualResetEventSlim? RepositoryUnregisterCommitEnteredForTest { get; set; }
	internal ManualResetEventSlim? RepositoryUnregisterCommitReleaseForTest { get; set; }
	private string? _cloneRepoId;
	private long _cloneGeneration;
	private CancellationTokenSource? _localRepositoryActionCts;
	private long _localRepositoryActionGeneration;
	private long _localRepositoryActionNavigationReservation;
	private string? _localRepositoryActionRepoId;
	private const string AllRepositoriesActionId = "*";

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

		long navigationGeneration = ReserveWorkspaceNavigationIntent();
		if (navigationGeneration == 0)
		{
			return;
		}
		WorkspaceStore.RepositoryRegistrationSnapshot? registrationIntent =
			_workspace?.CaptureRepoRegistration($"{owner}/{name}");

		if (!string.IsNullOrWhiteSpace(payload.ClonePath))
		{
			TryOpenRegisteredClone($"{owner}/{name}", payload.ClonePath, navigationGeneration);
			return;
		}

		if (!EnsureGitHubAccess(new PendingRepoAction(
			PendingRepoActionKind.Open,
			owner,
			name,
			NavigationGeneration: navigationGeneration,
			RegistrationIntent: registrationIntent)))
		{
			return;
		}

		OpenRepoCore(
			owner,
			name,
			forceNewClone: false,
			navigationGeneration: navigationGeneration,
			registrationIntent: registrationIntent);
	}

	private long ReserveWorkspaceNavigationIntent()
	{
		lock (_sync)
		{
			return _disposed ? 0 : ++_workspaceNavigationIntentSequence;
		}
	}

	private bool AcceptWorkspaceNavigationIntentLocked(long navigationGeneration)
	{
		if (navigationGeneration <= 0)
		{
			return false;
		}
		if (navigationGeneration > _workspaceNavigationIntentGeneration)
		{
			_workspaceNavigationIntentGeneration = navigationGeneration;
		}
		return navigationGeneration == _workspaceNavigationIntentGeneration;
	}

	private void OnCloneRepo(IpcMessage message)
	{
		RepoClonePayload? payload = SafeGetPayload<RepoClonePayload>(message);
		WorkspaceStore.RepositoryRegistrationSnapshot? registrationIntent = payload is null
			? null
			: _workspace?.CaptureRepoRegistration(payload.Id);
		RegisteredRepo? repo = registrationIntent?.Repository;
		if (repo is null || !TryParseGitHubRepo(repo.Url, out string owner, out string name))
		{
			SendError("That repository is no longer registered.");
			return;
		}

		long navigationGeneration = ReserveWorkspaceNavigationIntent();
		if (navigationGeneration == 0
			|| _cloner is null
			|| !EnsureGitHubAccess(new PendingRepoAction(
				PendingRepoActionKind.Clone,
				owner,
				name,
				NavigationGeneration: navigationGeneration,
				RegistrationIntent: registrationIntent)))
		{
			return;
		}

		OpenRepoCore(
			owner,
			name,
			forceNewClone: true,
			navigationGeneration: navigationGeneration,
			registrationIntent: registrationIntent);
	}

	private void OnCloneRepoManaged(IpcMessage message)
	{
		RepoCloneManagedPayload? payload = SafeGetPayload<RepoCloneManagedPayload>(message);
		if (payload is null || !TryParseGitHubRepo(payload.Url, out string owner, out string name))
		{
			SendError("That doesn't look like a GitHub repository.");
			return;
		}
		string repoId = $"{owner}/{name}";
		WorkspaceStore.RepositoryRegistrationSnapshot? registrationIntent =
			_workspace?.CaptureRepoRegistration(repoId);
		IReadOnlyList<RegisteredClone> clones = registrationIntent?.Repository?.Clones ?? [];
		string localName = string.IsNullOrWhiteSpace(payload.LocalName)
			? Path.GetFileName(NextClonePath(owner, name, clones))
			: payload.LocalName.Trim();
		if (!TryManagedClonePath(localName, out string destination))
		{
			SendError("Choose a short folder name without slashes or reserved characters.");
			return;
		}
		if (!string.IsNullOrWhiteSpace(payload.DestinationPath)
			&& !SameFullPath(payload.DestinationPath, destination))
		{
			SendError("The managed destination changed. Review the new path and try again.");
			return;
		}
		RegisteredClone? existing = clones.FirstOrDefault(clone => SameFullPath(clone.Path, destination));
		if (existing is not null && IsUsableCloneOf(existing.Path, $"https://github.com/{repoId}"))
		{
			Emit(IpcSerializer.SerializeEvent(
				MessageKinds.RepoCloneConflict,
				new RepoCloneConflictPayload(
					payload.Url,
					localName,
					existing.Path,
					"A local copy with that name already exists. Open it instead?")));
			return;
		}
		if (Directory.Exists(destination) || File.Exists(destination))
		{
			SendError("A file or folder with that local copy name already exists. Choose another name.");
			return;
		}
		long navigationGeneration = ReserveWorkspaceNavigationIntent();
		if (navigationGeneration == 0
			|| _cloner is null
			|| !EnsureGitHubAccess(new PendingRepoAction(
				PendingRepoActionKind.Clone,
				owner,
				name,
				NavigationGeneration: navigationGeneration,
				LocalDestination: destination,
				RequestUrl: payload.Url,
				RegistrationIntent: registrationIntent)))
		{
			return;
		}

		OpenRepoCore(
			owner,
			name,
			forceNewClone: true,
			requestedDestination: destination,
			requestUrl: payload.Url,
			navigationGeneration: navigationGeneration,
			registrationIntent: registrationIntent);
	}

	private void OnCloneDestinationRequest(IpcMessage message)
	{
		RepoCloneDestinationRequestPayload? payload = SafeGetPayload<RepoCloneDestinationRequestPayload>(message);
		if (payload is null)
		{
			return;
		}
		string? destination = null;
		string? existingClonePath = null;
		string localName = payload.LocalName?.Trim() ?? string.Empty;
		if (TryParseGitHubRepo(payload.Url, out string owner, out string name))
		{
			string repoId = $"{owner}/{name}";
			IReadOnlyList<RegisteredClone> clones = _workspace?.FindRepo(repoId)?.Clones ?? [];
			if (TryManagedClonePath(localName, out string candidate))
			{
				destination = candidate;
				RegisteredClone? registered = clones.FirstOrDefault(clone => SameFullPath(clone.Path, candidate));
				if (registered is not null
					&& IsUsableCloneOf(registered.Path, $"https://github.com/{repoId}"))
				{
					existingClonePath = registered.Path;
				}
			}
		}
		bool occupied = destination is not null
			&& (Directory.Exists(destination) || File.Exists(destination));
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.RepoCloneDestination,
			new RepoCloneDestinationPayload(
				payload.Url,
				payload.RequestId,
				destination,
				localName,
				existingClonePath is not null || occupied,
				existingClonePath)));
	}

	private void OnRepositoryDescriptionRequest(IpcMessage message)
	{
		RepoDescriptionRequestPayload? payload = SafeGetPayload<RepoDescriptionRequestPayload>(message);
		if (payload is null)
		{
			return;
		}

		CancellationTokenSource cts;
		long generation;
		CancellationTokenSource? previousCts;
		lock (_repositoryDescriptionPublishSync)
		{
			lock (_sync)
			{
				if (_disposed)
				{
					return;
				}
				previousCts = _repositoryDescriptionCts;
				cts = new CancellationTokenSource();
				_repositoryDescriptionCts = cts;
				generation = ++_repositoryDescriptionGeneration;
			}
		}
		previousCts?.Cancel();

		if (!TryParseGitHubRepo(payload.Url, out string owner, out string name))
		{
			PublishRepositoryDescriptionIfCurrent(
				cts, generation, payload, RepoDescriptionStates.NotFound, description: null);
			lock (_sync)
			{
				if (ReferenceEquals(_repositoryDescriptionCts, cts))
				{
					_repositoryDescriptionCts = null;
				}
			}
			cts.Dispose();
			return;
		}

		CancellationToken cancellationToken = cts.Token;
		_ = Task.Run(async () =>
		{
			try
			{
				if (_repositoryCatalog is null)
				{
					PublishRepositoryDescriptionIfCurrent(
						cts, generation, payload, RepoDescriptionStates.Error, description: null);
					return;
				}
				GitHubRepositoryMetadata metadata = _auth?.IsSignedIn() == true
					? await _auth.WithAccessTokenAsync(
						(token, ct) => _repositoryCatalog.GetMetadataAsync(owner, name, token, ct),
						cancellationToken)
					: await _repositoryCatalog.GetPublicMetadataAsync(owner, name, cancellationToken);
				PublishRepositoryDescriptionIfCurrent(
					cts,
					generation,
					payload,
					metadata.IsPrivate ? RepoDescriptionStates.Private : RepoDescriptionStates.Found,
					metadata.Description);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				// A newer repository entry owns the description line now.
			}
			catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
			{
				PublishRepositoryDescriptionIfCurrent(
					cts, generation, payload, RepoDescriptionStates.NotFound, description: null);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Could not load the repository description");
				PublishRepositoryDescriptionIfCurrent(
					cts, generation, payload, RepoDescriptionStates.Error, description: null);
			}
			finally
			{
				lock (_sync)
				{
					if (ReferenceEquals(_repositoryDescriptionCts, cts))
					{
						_repositoryDescriptionCts = null;
					}
				}
				cts.Dispose();
			}
		});
	}

	private void PublishRepositoryDescriptionIfCurrent(
		CancellationTokenSource cts,
		long generation,
		RepoDescriptionRequestPayload request,
		string state,
		string? description)
	{
		lock (_repositoryDescriptionPublishSync)
		{
			lock (_sync)
			{
				if (_disposed
					|| generation != _repositoryDescriptionGeneration
					|| !ReferenceEquals(_repositoryDescriptionCts, cts))
				{
					return;
				}
			}
			Emit(IpcSerializer.SerializeEvent(
				MessageKinds.RepoDescription,
				new RepoDescriptionPayload(request.Url, request.RequestId, state, description)));
		}
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
		string localName = string.IsNullOrWhiteSpace(payload.LocalName) ? name : payload.LocalName.Trim();
		if (!IsValidCloneFolderName(localName))
		{
			SendError("Choose a short folder name without slashes or reserved characters.");
			return;
		}
		string destination = Path.GetFullPath(Path.Combine(Path.GetFullPath(parentFolder), localName));
		string repoId = $"{owner}/{name}";
		WorkspaceStore.RepositoryRegistrationSnapshot? registrationIntent =
			_workspace?.CaptureRepoRegistration(repoId);
		RegisteredClone? existing = registrationIntent?.Repository?.Clones
			.FirstOrDefault(clone => SameFullPath(clone.Path, destination));
		if (existing is not null && IsUsableCloneOf(existing.Path, $"https://github.com/{repoId}"))
		{
			Emit(IpcSerializer.SerializeEvent(
				MessageKinds.RepoCloneConflict,
				new RepoCloneConflictPayload(
					payload.Url,
					localName,
					existing.Path,
					"A local copy with that name already exists. Open it instead?")));
			return;
		}
		if (Directory.Exists(destination) || File.Exists(destination))
		{
			SendError("A file or folder with that local copy name already exists. Choose another name or folder.");
			return;
		}
		long navigationGeneration = ReserveWorkspaceNavigationIntent();
		if (navigationGeneration == 0
			|| !EnsureGitHubAccess(new PendingRepoAction(
				PendingRepoActionKind.CloneToFolder,
				owner,
				name,
				NavigationGeneration: navigationGeneration,
				LocalDestination: destination,
				RequestUrl: payload.Url,
				RegistrationIntent: registrationIntent)))
		{
			return;
		}

		OpenRepoCore(
			owner,
			name,
			forceNewClone: true,
			requestedDestination: destination,
			requestUrl: payload.Url,
			navigationGeneration: navigationGeneration,
			registrationIntent: registrationIntent);
	}

	private static bool TryManagedClonePath(string localName, out string destination)
	{
		destination = string.Empty;
		if (!IsValidCloneFolderName(localName))
		{
			return false;
		}
		destination = Path.GetFullPath(Path.Combine(AppPaths.Repos, localName));
		return SameFullPath(Path.GetDirectoryName(destination)!, AppPaths.Repos);
	}

	private static bool IsValidCloneFolderName(string localName) =>
		localName.Length is > 0 and <= 120
		&& localName is not ("." or "..")
		&& !localName.EndsWith(' ')
		&& !localName.EndsWith('.')
		&& !IsWindowsDeviceName(localName)
		&& localName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
		&& !localName.Contains(Path.DirectorySeparatorChar)
		&& !localName.Contains(Path.AltDirectorySeparatorChar);

	private static bool IsWindowsDeviceName(string localName)
	{
		string stem = localName.Split('.')[0];
		if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
			|| stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
			|| stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
			|| stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		return stem.Length == 4
			&& stem[^1] is >= '1' and <= '9'
			&& (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
				|| stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
	}

	private void OpenRepoCore(
		string owner,
		string name,
		bool forceNewClone,
		string? requestedDestination = null,
		string? requestUrl = null,
		long navigationGeneration = 0,
		WorkspaceStore.RepositoryRegistrationSnapshot? registrationIntent = null)
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
				if (_disposed || _closePreparationClaimed)
				{
					return;
				}
			}
		}

		// Filesystem and LibGit2 probes can block. Check the teardown state before them, then re-enter the
		// publication gate afterwards before any open/claim side effect. Dispose can therefore finish while a
		// probe is blocked, and the completed probe is discarded without starting work.
		registrationIntent ??= _workspace?.CaptureRepoRegistration(repoId);
		RegisteredRepo? descriptor = registrationIntent?.Repository;
		requireStillRegistered = descriptor is not null;
		string? existingClone = null;
		bool existingCloneIdentityMismatch = false;
		if (!forceNewClone)
		{
			foreach (RegisteredClone clone in descriptor?.Clones ?? [])
			{
				if (!IsUsableClone(clone.Path))
				{
					continue;
				}
				if (IsUsableCloneOf(clone.Path, cloneUrl))
				{
					existingClone = clone.Path;
					existingCloneIdentityMismatch = false;
					break;
				}
				existingCloneIdentityMismatch = true;
			}
		}
		string legacyManagedPath = Path.Combine(AppPaths.Repos, $"{owner}_{name}");
		if (!forceNewClone && existingClone is null && cloner.IsCloned(legacyManagedPath))
		{
			if (IsUsableCloneOf(legacyManagedPath, cloneUrl))
			{
				existingClone = legacyManagedPath;
				existingCloneIdentityMismatch = false;
			}
			else
			{
				existingCloneIdentityMismatch = true;
			}
		}
		localPath = requestedDestination ?? (!forceNewClone && existingClone is not null
			? existingClone
			: NextClonePath(owner, name, descriptor?.Clones ?? []));
		bool isCloned = !forceNewClone && cloner.IsCloned(localPath);
		bool requestedDestinationOccupied = requestedDestination is not null
			&& (Directory.Exists(localPath) || File.Exists(localPath));
		RegisteredClone? occupiedRegistration = requestedDestination is null
			? null
			: descriptor?.Clones.FirstOrDefault(candidate => SameFullPath(candidate.Path, requestedDestination));
		bool occupiedRegistrationUsable = occupiedRegistration is not null
			&& IsUsableCloneOf(occupiedRegistration.Path, cloneUrl);
		bool existingLocalPathUsable = !isCloned || IsUsableCloneOf(localPath, cloneUrl);
		bool abort = false;
		bool openExisting = false;
		bool cloneBusy = false;
		bool localActionBusy = false;
		string? preflightError = null;
		RepoCloneConflictPayload? conflict = null;

		lock (_clonePublishSync)
		{
			lock (_sync)
			{
				abort = _disposed || _closePreparationClaimed
					|| (registrationIntent is not null
						&& !_workspace!.IsRepoRegistrationCurrent(registrationIntent, requireStillRegistered));
				if (!abort && existingCloneIdentityMismatch)
				{
					preflightError = "That local copy belongs to a different repository. Choose another copy or clone this repository again.";
				}
				else if (!abort && requestedDestinationOccupied)
				{
					if (occupiedRegistrationUsable)
					{
						conflict = new RepoCloneConflictPayload(
							requestUrl ?? repoId,
							Path.GetFileName(requestedDestination!),
							occupiedRegistration!.Path,
							"A local copy with that name already exists. Open it instead?");
					}
					else
					{
						preflightError = "A file or folder with that local copy name already exists. Choose another name.";
					}
				}
				else if (!abort && !isCloned)
				{
					if (_localRepositoryActionCts is not null)
					{
						localActionBusy = true;
					}
					else if (_cloneCts is not null)
					{
						cloneBusy = true;
					}
					else
					{
						cts = new CancellationTokenSource();
						_cloneCts = cts;
						_cloneRepoId = repoId;
						cloneGeneration = ++_cloneGeneration;
						AcceptWorkspaceNavigationIntentLocked(navigationGeneration);
					}
				}
				else if (!abort && !existingLocalPathUsable)
				{
					preflightError = "That local copy belongs to a different repository. Choose another copy or clone this repository again.";
				}
				else if (!abort)
				{
					if (_localRepositoryActionCts is not null)
					{
						localActionBusy = true;
					}
					else
					{
						openExisting = true;
						AcceptWorkspaceNavigationIntentLocked(navigationGeneration);
					}
				}
			}
		}
		if (abort)
		{
			return;
		}
		if (conflict is not null)
		{
			Emit(IpcSerializer.SerializeEvent(MessageKinds.RepoCloneConflict, conflict));
			return;
		}
		if (preflightError is not null)
		{
			SendError(preflightError);
			return;
		}
		if (localActionBusy)
		{
			SendError("Repository work is still finishing. Wait a moment, then open that repository again.");
			return;
		}
		if (cloneBusy)
		{
			_logger.LogDebug("Ignoring a repo open while a clone is already in flight");
			SendError("Another repository copy is still being prepared. Try again when it finishes.");
			return;
		}
		if (openExisting)
		{
			PreparedOpenedRepository prepared = PrepareOpenedRepository(owner, name, localPath, descriptor);
			if (TryCommitOpenedRepository(
				prepared,
				registrationIntent,
				requireStillRegistered,
				navigationGeneration,
				out WorkspaceRootPublication? publication)
				&& publication is { } committedPublication)
			{
				PublishWorkspaceFolder(committedPublication);
			}
			return;
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
					throw new RepositoryDestinationConflictException(localPath);
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

				bool usableClone = IsUsableCloneOf(localClone, cloneUrl);
				bool current = IsOpenRepositoryIntentCurrent(
					registrationIntent, requireStillRegistered, cts, cloneGeneration);
				if (!current)
				{
					return;
				}
				if (!usableClone)
				{
					SendError("SpecDesk could not verify the new local copy. It was left untouched and was not opened.");
					return;
				}
				PreparedOpenedRepository prepared = PrepareOpenedRepository(owner, name, localClone, descriptor);
				if (!TryCommitOpenedRepository(
					prepared,
					registrationIntent,
					requireStillRegistered,
					navigationGeneration,
					out WorkspaceRootPublication? publication,
					cts,
					cloneGeneration))
				{
					return;
				}
				if (publication is { } committedPublication)
				{
					PublishWorkspaceFolder(committedPublication);
					_logger.LogInformation("Opened repository {Repo} at {Path}", repoId, localClone);
				}
				else
				{
					_logger.LogInformation(
						"Registered repository {Repo} at {Path} without replacing newer navigation",
						repoId,
						localClone);
				}
			}
			catch (RepositoryDestinationConflictException ex) when (requestedDestination is not null)
			{
				_logger.LogInformation(
					"Repository clone destination {Path} was claimed before publication",
					ex.DestinationPath);
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
					if (current)
					{
						SendError("A file or folder with that local copy name appeared while cloning. Choose another name.");
					}
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

					}
					if (current)
					{
						SendError("Could not open that repository. Check the name and your connection, then try again.");
					}
				}
			}
			finally
			{
				FinishCloneAfterOutbound(cts);
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
		long displacedDocumentOpenRequestId = 0;
		lock (_remotePublishSync)
		{
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

				if (action.Kind != PendingRepoActionKind.Register)
				{
					AcceptWorkspaceNavigationIntentLocked(action.NavigationGeneration);
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
					if (_pendingRepoOpen is { Kind: PendingRepoActionKind.File } pendingFile
						&& (action.Kind != PendingRepoActionKind.File
							|| action.RequestId != pendingFile.RequestId))
					{
						displacedDocumentOpenRequestId = pendingFile.RequestId;
						_remoteFileRequestId = 0;
						_remoteFileGeneration++;
						_remoteFileRepoId = null;
					}
					_pendingRepoOpen = action;
				}

				startSignIn = _signInCts is null;
			}
		}

		CompleteDocumentOpen(displacedDocumentOpenRequestId, succeeded: false);
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
			RegisterRepoCore(action.Owner, action.Name, action.RegistrationIntent);
		}

		if (actions.Open is not null)
		{
			if (actions.Open.Kind == PendingRepoActionKind.Browse)
			{
				BrowseRepoCore(
					actions.Open.Owner,
					actions.Open.Name,
					actions.Open.RemoteGeneration,
					actions.Open.NavigationGeneration,
					actions.Open.Branch);
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
					actions.Open.RemoteGeneration,
					actions.Open.NavigationGeneration,
					actions.Open.RequestId);
			}
			else
			{
				OpenRepoCore(
					actions.Open.Owner,
					actions.Open.Name,
					forceNewClone: actions.Open.Kind is PendingRepoActionKind.Clone or PendingRepoActionKind.CloneToFolder,
					requestedDestination: actions.Open.LocalDestination,
					requestUrl: actions.Open.RequestUrl,
					navigationGeneration: actions.Open.NavigationGeneration,
					registrationIntent: actions.Open.RegistrationIntent);
			}
		}
	}

	private void CompletePendingDocumentOpen(PendingRepoActions? actions)
	{
		if (actions?.Open is { Kind: PendingRepoActionKind.File } open)
		{
			CompleteDocumentOpen(open.RequestId, succeeded: false);
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

	private void RegisterRepoCore(
		string owner,
		string name,
		WorkspaceStore.RepositoryRegistrationSnapshot? registrationIntent)
	{
		RegisterRepoCorePublished(owner, name, registrationIntent);
	}

	private void RegisterRepoCorePublished(
		string owner,
		string name,
		WorkspaceStore.RepositoryRegistrationSnapshot? registrationIntent)
	{
		string id = $"{owner}/{name}";
		string url = $"https://github.com/{owner}/{name}";
		bool metadataLookupAvailable = _repositoryCatalog is not null && _auth is not null;
		bool introducedByLookup = registrationIntent?.Repository is null && metadataLookupAvailable;
		WorkspaceStore.WorkspaceStateSnapshot registeredState;
		WorkspaceStore.RepositoryRegistrationSnapshot metadataIntent;
		lock (_clonePublishSync)
		{
			lock (_sync)
			{
				if (_disposed)
				{
					return;
				}
			}
			if (_workspace is null
				|| registrationIntent is null
				|| !_workspace.TryRegisterRepo(
					registrationIntent,
					new RegisteredRepo(id, id, url, string.Empty, []),
					out registeredState))
			{
				return;
			}
			metadataIntent = _workspace.CaptureRepoRegistration(id);
		}
		EmitWorkspaceState(registeredState);
		RepoRegistrationPublishedForTest?.Set();
		RepoRegistrationResumeForTest?.Wait();
		RegisteredRepo? saved = metadataIntent.Repository;
		if (saved is not null && !string.IsNullOrWhiteSpace(saved.DefaultBranch))
		{
			// The descriptor already owns the authoritative default. Do not turn every repeated Add into
			// another network lookup or silently change the stored baseline underneath existing copies.
			EmitWorkspaceState();
			return;
		}
		if (!metadataLookupAvailable)
		{
			return;
		}
		if (!TryCaptureAccountSession(out AccountSession accountSession))
		{
			if (introducedByLookup
				&& _workspace.TryRollbackNewRepoRegistration(
					metadataIntent, out WorkspaceStore.WorkspaceStateSnapshot rollbackState))
			{
				EmitWorkspaceState(rollbackState);
			}
			return;
		}

		CancellationTokenSource cts =
			CancellationTokenSource.CreateLinkedTokenSource(accountSession.CancellationToken);
		RepoMetadataLookup? lookup = null;
		CancellationTokenSource? previousCts = null;
		WorkspaceStore.WorkspaceStateSnapshot? invalidatedState = null;
		lock (_signInPublishSync)
		{
			lock (_clonePublishSync)
			{
				lock (_sync)
				{
					if (!IsAccountSessionCurrentLocked(accountSession)
						|| !_workspace.IsRepoRegistrationPresenceCurrent(metadataIntent))
					{
						if (introducedByLookup)
						{
							_workspace.TryRollbackNewRepoRegistration(
								metadataIntent, out invalidatedState);
						}
					}
					else
					{
						if (_repoMetadataLookups.Remove(id, out RepoMetadataLookup? previous))
						{
							previousCts = previous.Cts;
						}
						lookup = new RepoMetadataLookup(
							Interlocked.Increment(ref _repoMetadataGeneration),
							cts,
							metadataIntent,
							introducedByLookup);
						_repoMetadataLookups[id] = lookup;
					}
				}
			}
		}
		if (lookup is null)
		{
			cts.Dispose();
			if (invalidatedState is not null)
			{
				EmitWorkspaceState(invalidatedState);
			}
			return;
		}
		RepoMetadataLookup activeLookup = lookup;
		previousCts?.Cancel();
		CancellationToken cancellationToken = cts.Token;
		_ = Task.Run(async () =>
		{
			try
			{
				Task<GitHubRepositoryMetadata>? operation = null;
				if (!StartForAccountSession(accountSession, () =>
					operation = _auth!.WithAccessTokenAsync(
						(token, ct) => _repositoryCatalog!.GetMetadataAsync(owner, name, token, ct),
						cancellationToken)))
				{
					return;
				}
				GitHubRepositoryMetadata metadata = await operation!;
				ArgumentException.ThrowIfNullOrWhiteSpace(metadata.DefaultBranch);
				bool publish = false;
				WorkspaceStore.WorkspaceStateSnapshot? metadataState = null;
				PublishForAccountSession(accountSession, () =>
				{
					lock (_clonePublishSync)
					{
						lock (_sync)
						{
							publish = !_disposed
								&& _repoMetadataLookups.TryGetValue(id, out RepoMetadataLookup? current)
								&& ReferenceEquals(current, activeLookup);
							if (publish)
							{
								publish = _workspace?.TrySetRepoDefaultBranch(
									metadataIntent, metadata.DefaultBranch, out metadataState) == true;
								_repoMetadataLookups.Remove(id);
							}
						}
					}
				});
				if (publish)
				{
					EmitWorkspaceState(metadataState!);
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				// Superseded registration, explicit removal, or controller teardown.
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Could not register GitHub repository {Repo}", id);
				bool current = false;
				PublishForAccountSession(accountSession, () =>
				{
					lock (_clonePublishSync)
					{
						lock (_sync)
						{
							current = !_disposed
								&& _repoMetadataLookups.TryGetValue(id, out RepoMetadataLookup? active)
								&& ReferenceEquals(active, activeLookup);
							if (current)
							{
								_repoMetadataLookups.Remove(id);
							}
						}
					}
				});
				if (current)
				{
					SendError("Could not add that repository. Check its name and your access, then try again.");
				}
			}
			finally
			{
				lock (_sync)
				{
					if (_repoMetadataLookups.TryGetValue(id, out RepoMetadataLookup? active)
						&& ReferenceEquals(active, activeLookup))
					{
						_repoMetadataLookups.Remove(id);
					}
				}
				cts.Dispose();
			}
		});
	}

	private sealed record PreparedOpenedRepository(
		RegisteredRepo Seed,
		RegisteredClone LocalClone,
		string InferredDefaultBranch,
		string Root);

	// Repository inspection can touch disk and LibGit2Sharp, so it is deliberately outside the short
	// publication lease. The returned value is immutable and can be committed atomically afterwards.
	private PreparedOpenedRepository PrepareOpenedRepository(
		string owner,
		string name,
		string clonePath,
		RegisteredRepo? descriptor)
	{
		string id = $"{owner}/{name}";
		descriptor ??= new RegisteredRepo(id, id, $"https://github.com/{owner}/{name}", string.Empty, []);
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
			Path.GetFileName(clonePath),
			Path.GetFullPath(clonePath),
			info.CurrentBranch,
			info.Branches.Select(branch => new RegisteredBranch(
				branch.Name,
				ToRepositoryStatusPayload(branch.Status),
				branch.CanDelete)).ToArray(),
			ToRepositoryStatusPayload(info.Status));
		return new PreparedOpenedRepository(descriptor, clone, info.DefaultBranch, Path.GetFullPath(clonePath));
	}

	private bool IsOpenRepositoryIntentCurrent(
		WorkspaceStore.RepositoryRegistrationSnapshot? registrationIntent,
		bool requireStillRegistered,
		CancellationTokenSource cloneCts,
		long cloneGeneration)
	{
		lock (_clonePublishSync)
		{
			lock (_sync)
			{
				if (_disposed
					|| _closePreparationClaimed
					|| cloneCts.IsCancellationRequested
					|| !ReferenceEquals(_cloneCts, cloneCts)
					|| _cloneGeneration != cloneGeneration)
				{
					return false;
				}
			}
			return registrationIntent is null
				|| _workspace!.IsRepoRegistrationCurrent(registrationIntent, requireStillRegistered);
		}
	}

	private bool TryCommitOpenedRepository(
		PreparedOpenedRepository prepared,
		WorkspaceStore.RepositoryRegistrationSnapshot? registrationIntent,
		bool requireStillRegistered,
		long navigationGeneration,
		out WorkspaceRootPublication? publication,
		CancellationTokenSource? cloneCts = null,
		long cloneGeneration = 0,
		CancellationTokenSource? localActionCts = null,
		long localActionGeneration = 0)
	{
		publication = null;
		lock (_clonePublishSync)
		{
			lock (_sync)
			{
				if (_disposed || _closePreparationClaimed
					|| (localActionCts is null && _localRepositoryActionCts is not null)
					|| (cloneCts is not null
						&& (cloneCts.IsCancellationRequested
							|| !ReferenceEquals(_cloneCts, cloneCts)
							|| _cloneGeneration != cloneGeneration))
					|| (localActionCts is not null
						&& (localActionCts.IsCancellationRequested
							|| !ReferenceEquals(_localRepositoryActionCts, localActionCts)
							|| _localRepositoryActionGeneration != localActionGeneration)))
				{
					return false;
				}
			}

			WorkspaceStore.WorkspaceStateSnapshot? state = null;
			if (registrationIntent is not null)
			{
				if (_workspace is null
					|| !_workspace.TryCommitRepoClone(
						registrationIntent,
						requireStillRegistered,
						prepared.Seed,
						prepared.LocalClone,
						prepared.InferredDefaultBranch,
						out WorkspaceStore.WorkspaceStateSnapshot committedState))
				{
					return false;
				}
				state = committedState;
			}

			lock (_sync)
			{
				if (navigationGeneration == _workspaceNavigationIntentGeneration)
				{
					_workspaceRoot = prepared.Root;
					publication = new WorkspaceRootPublication(
						prepared.Root, ++_workspaceRootGeneration, navigationGeneration);
				}
			}
			if (state is not null)
			{
				EmitWorkspaceState(state);
			}
			return true;
		}
	}

	private static RepositoryStatusPayload ToRepositoryStatusPayload(LocalRepositoryStatus status) =>
		new(
			status.Ahead,
			status.Behind,
			status.HasUncommitted,
			status.StashCount,
			status.HasConflicts);

	private bool TryOpenRegisteredClone(string repoId, string path, long navigationGeneration)
	{
		WorkspaceStore.RepositoryRegistrationSnapshot? registrationIntent;
		RegisteredRepo? repo;
		RegisteredClone? clone;
		bool actionInProgress;
		lock (_clonePublishSync)
		{
			lock (_sync)
			{
				actionInProgress = _localRepositoryActionCts is not null;
				registrationIntent = _disposed ? null : _workspace?.CaptureRepoRegistration(repoId);
				repo = registrationIntent?.Repository;
				clone = repo?.Clones?.FirstOrDefault(candidate => SameFullPath(candidate.Path, path));
			}
		}
		if (actionInProgress)
		{
			SendError("Repository work is still finishing. Wait a moment, then open that local copy again.");
			return false;
		}
		if (clone is null || !IsUsableClone(clone.Path))
		{
			SendError("That local copy is no longer available.");
			return false;
		}
		if (!IsUsableCloneOf(clone.Path, repo!.Url))
		{
			SendError("That local copy belongs to a different repository. Choose another copy or clone this repository again.");
			return false;
		}

		int slash = repoId.IndexOf('/', StringComparison.Ordinal);
		if (slash <= 0 || slash == repoId.Length - 1)
		{
			return false;
		}
		PreparedOpenedRepository prepared = PrepareOpenedRepository(
			repoId[..slash], repoId[(slash + 1)..], clone.Path, repo);
		bool committed = false;
		lock (_clonePublishSync)
		{
			lock (_sync)
			{
				actionInProgress = _localRepositoryActionCts is not null;
			}
			if (!actionInProgress)
			{
				lock (_sync)
				{
					AcceptWorkspaceNavigationIntentLocked(navigationGeneration);
				}
				committed = TryCommitOpenedRepository(
					prepared,
					registrationIntent,
					requireStillRegistered: true,
					navigationGeneration,
					out WorkspaceRootPublication? publication);
				if (publication is { } committedPublication)
				{
					PublishWorkspaceFolder(committedPublication);
				}
			}
		}
		if (actionInProgress)
		{
			SendError("Repository work is still finishing. Wait a moment, then open that local copy again.");
		}
		return committed;
	}
	private void OnSwitchRepoBranch(IpcMessage message)
	{
		RepoSwitchBranchPayload? payload = SafeGetPayload<RepoSwitchBranchPayload>(message);
		if (payload is null
			|| string.IsNullOrWhiteSpace(payload.Id)
			|| string.IsNullOrWhiteSpace(payload.ClonePath)
			|| string.IsNullOrWhiteSpace(payload.Branch))
		{
			return;
		}
		RegisteredRepo? repo;
		lock (_sync)
		{
			repo = _disposed ? null : _workspace?.FindRepo(payload.Id);
		}
		RegisteredClone? clone = repo?.Clones.FirstOrDefault(candidate =>
			SameFullPath(candidate.Path, payload.ClonePath));
		if (clone is null || !IsUsableClone(clone.Path))
		{
			SendError("That local copy is no longer available.");
			CompleteRepositoryOperation(payload.RequestId);
			return;
		}
		if (_repositoryInspector is not ILocalRepositoryManager manager)
		{
			SendError("Switching working lines isn't available in this build.");
			CompleteRepositoryOperation(payload.RequestId);
			return;
		}
		if (!TryBeginNavigatingLocalRepositoryAction(
			payload.Id,
			out CancellationTokenSource cts,
			out long generation,
			out long navigationGeneration))
		{
			SendError("That repository is no longer registered, or another local action is still finishing.");
			CompleteRepositoryOperation(payload.RequestId);
			return;
		}

		DocumentRepositoryTransition? documentTransition = null;
		DocumentRepositoryRetirement? deferredRetirement = null;
		string? deferredRetirementError = null;
		bool repositoryMutationStarted = false;
		_ = Task.Run(() =>
		{
			try
			{
				documentTransition = BeginDocumentRepositoryTransition(clone.Path);
				BranchSwitchResult result;
				lock (_repoGate)
				{
					result = manager.SwitchBranchSafely(
						clone.Path,
						repo!.Url,
						clone.CurrentBranch
							?? throw new InvalidOperationException("Refresh the repository list before switching working lines."),
						payload.Branch,
						beforeMutation: () => PersistDocumentRepositoryTransition(documentTransition),
						onMutationStarting: () =>
						{
							if (documentTransition is not null)
							{
								Interlocked.Increment(ref _draftGeneration);
							}
							repositoryMutationStarted = true;
						});
				}
				bool publish;
				lock (_clonePublishSync)
				{
					lock (_sync)
					{
						RegisteredClone? currentClone = _workspace?.FindRepo(payload.Id)?.Clones
							.FirstOrDefault(candidate => SameFullPath(candidate.Path, clone.Path));
						publish = !_disposed
							&& !cts.IsCancellationRequested
							&& ReferenceEquals(_localRepositoryActionCts, cts)
							&& generation == _localRepositoryActionGeneration
							&& currentClone is not null;
					}
				}
				if (!publish)
				{
					deferredRetirement ??= RetireDocumentRepositoryTransitionAfterMutation(
						ref documentTransition, clone.Path);
					return;
				}
				if (!IsUsableCloneOfAtBranch(clone.Path, repo!.Url, result.CurrentBranch))
				{
					deferredRetirement ??= RetireDocumentRepositoryTransitionAfterMutation(
						ref documentTransition, clone.Path);
					deferredRetirementError = "That local copy or its current working line changed. The open document was closed to protect it; refresh the repository list before continuing.";
					return;
				}
				string? resumedBranch = string.Equals(
					payload.Branch, repo!.DefaultBranch, StringComparison.OrdinalIgnoreCase)
					? null
					: payload.Branch;
				CompleteDocumentRepositoryTransition(documentTransition, resumedBranch, repo!.DefaultBranch);
				documentTransition = null;
				if (result.HasConflicts)
				{
					SendError("The saved local work overlaps with this working line. Review the marked files before continuing.");
				}
				int slash = payload.Id.IndexOf('/', StringComparison.Ordinal);
				string owner = slash > 0 ? payload.Id[..slash] : string.Empty;
				string repoName = owner.Length == 0 ? string.Empty : payload.Id[(slash + 1)..];
				if (owner.Length == 0 || repoName.Length == 0)
				{
					return;
				}
				WorkspaceStore.RepositoryRegistrationSnapshot? registrationIntent =
					_workspace?.CaptureRepoRegistration(payload.Id);
				PreparedOpenedRepository prepared = PrepareOpenedRepository(
					owner, repoName, clone.Path, registrationIntent?.Repository);
				if (TryCommitOpenedRepository(
					prepared,
					registrationIntent,
					requireStillRegistered: true,
					navigationGeneration,
					out WorkspaceRootPublication? publication,
					localActionCts: cts,
					localActionGeneration: generation)
					&& publication is { } committedPublication)
				{
					PublishWorkspaceFolder(committedPublication);
				}
			}
			catch (RepositoryIdentityMismatchException ex)
			{
				_logger.LogWarning(ex, "Refused to switch a local copy whose GitHub source changed");
				bool notify;
				lock (_sync)
				{
					notify = !_disposed && ReferenceEquals(_localRepositoryActionCts, cts);
				}
				if (notify)
				{
					SendError("That local copy belongs to a different repository. No local work was changed.");
				}
			}
			catch (Exception ex) when (
				ex is LibGit2SharpException
					or InvalidOperationException
					or IOException
					or UnauthorizedAccessException
					or ArgumentException)
			{
				_logger.LogWarning(ex, "Could not switch the local repository working line");
				bool notify;
				lock (_sync)
				{
					notify = !_disposed && ReferenceEquals(_localRepositoryActionCts, cts);
				}
				if (notify)
				{
					if (repositoryMutationStarted)
					{
						deferredRetirementError = "The working line changed, but its saved work could not be restored. The open document was closed to protect the checked-out files.";
					}
					else
					{
						SendError("Could not switch to that working line. Your local work was kept safe.");
					}
				}
			}
			finally
			{
				if (repositoryMutationStarted)
				{
					deferredRetirement ??= RetireDocumentRepositoryTransitionAfterMutation(ref documentTransition, clone.Path);
				}
				else
				{
					CancelDocumentRepositoryTransition(documentTransition);
				}
				PublishDocumentRepositoryRetirement(deferredRetirement);
				if (deferredRetirementError is not null)
				{
					SendError(deferredRetirementError);
				}
				CompleteRepositoryOperation(payload.RequestId);
				FinishLocalRepositoryActionAfterOutbound(cts);
			}
		});
	}

	private sealed record RepositoryRefreshTarget(
		string Id,
		string Url,
		string DefaultBranch,
		RegisteredClone LocalCopy);

	private sealed record RepositoryRefreshResult(int Refreshed, int Failed);

	private void OnRefreshAllRepositories(IpcMessage message)
	{
		RepoRefreshAllPayload? payload = SafeGetPayload<RepoRefreshAllPayload>(message);
		if (payload is null || payload.RequestId <= 0)
		{
			return;
		}
		if (_repositoryInspector is not ILocalRepositoryManager manager || _workspace is null)
		{
			SendError("Refreshing local copies isn't available in this build.");
			CompleteRepositoryOperation(payload.RequestId);
			return;
		}
		RepositoryRefreshTarget[]? targets;
		lock (_sync)
		{
			if (_disposed)
			{
				targets = null;
			}
			else
			{
				targets = _workspace.State().Repositories
					.SelectMany(repo => repo.Clones.Select(clone =>
						new RepositoryRefreshTarget(repo.Id, repo.Url, repo.DefaultBranch, clone)))
					.ToArray();
			}
		}
		if (targets is null)
		{
			CompleteRepositoryOperation(payload.RequestId);
			return;
		}
		if (!TryBeginLocalRepositoryAction(AllRepositoriesActionId, out CancellationTokenSource cts, out long generation))
		{
			SendError("Another local repository action is still finishing. Try again in a moment.");
			CompleteRepositoryOperation(payload.RequestId);
			return;
		}
		AccountSession? accountSession = TryCaptureAccountSession(out AccountSession capturedSession)
			? capturedSession
			: null;

		_ = Task.Run(async () =>
		{
			using CancellationTokenSource? refreshCts = accountSession is AccountSession session
				? CancellationTokenSource.CreateLinkedTokenSource(cts.Token, session.CancellationToken)
				: null;
			CancellationToken refreshToken = refreshCts?.Token ?? cts.Token;
			try
			{
				RepositoryRefreshResult result;
				if (accountSession is not null)
				{
					result = await (_auth ?? throw new InvalidOperationException("GitHub authorization is unavailable."))
						.WithAccessTokenAsync(
						(token, tokenCt) => Task.FromResult(
							RefreshRepositories(manager, targets, token, cts, generation, tokenCt)),
						refreshToken).ConfigureAwait(false);
				}
				else
				{
					result = RefreshRepositories(manager, targets, null, cts, generation, cts.Token);
				}

				if (CanPublishLocalRepositoryAction(cts, generation, AllRepositoriesActionId))
				{
					EmitWorkspaceState();
					if (result.Failed > 0)
					{
						SendError(result.Failed == 1
							? $"Refreshed {result.Refreshed} of {targets.Length} local copies; one could not be refreshed."
							: $"Refreshed {result.Refreshed} of {targets.Length} local copies; {result.Failed} could not be refreshed.");
					}
				}
			}
			catch (OperationCanceledException) when (cts.IsCancellationRequested)
			{
				// Window teardown or unregistering a repository cancels the batch; no stale result is published.
			}
			catch (OperationCanceledException) when (accountSession?.CancellationToken.IsCancellationRequested == true)
			{
				// Signing out invalidates the captured token before another local copy can use it.
			}
			catch (InvalidOperationException ex)
			{
				// The account can be disconnected after IsSignedIn but before the transient token scope starts.
				_logger.LogWarning(ex, "Could not start the local repository refresh batch");
				if (CanPublishLocalRepositoryAction(cts, generation, AllRepositoriesActionId))
				{
					EmitWorkspaceState();
					SendError("Could not refresh local copies. Connect to GitHub and try again.");
				}
			}
			finally
			{
				CompleteRepositoryOperation(payload.RequestId);
				FinishLocalRepositoryActionAfterOutbound(cts);
			}
		});
	}

	private RepositoryRefreshResult RefreshRepositories(
		ILocalRepositoryManager manager,
		IReadOnlyList<RepositoryRefreshTarget> targets,
		string? accessToken,
		CancellationTokenSource actionCts,
		long actionGeneration,
		CancellationToken ct)
	{
		int refreshed = 0;
		int failed = 0;
		foreach (RepositoryRefreshTarget target in targets)
		{
			ct.ThrowIfCancellationRequested();
			try
			{
				LocalRepositoryInfo info;
				lock (_repoGate)
				{
					info = manager.Fetch(
						target.LocalCopy.Path,
						target.Url,
						target.DefaultBranch,
						accessToken,
						ct);
				}
				bool publish = CanPublishLocalRepositoryAction(
					actionCts, actionGeneration, AllRepositoriesActionId);
				bool usable = IsUsableCloneOfAtBranch(
					target.LocalCopy.Path, target.Url, info.CurrentBranch);
				RegisteredClone updatedClone = new(
					target.LocalCopy.Id,
					target.LocalCopy.Path,
					info.CurrentBranch,
					info.Branches.Select(branch => new RegisteredBranch(
						branch.Name,
						ToRepositoryStatusPayload(branch.Status),
						branch.CanDelete)).ToArray(),
					ToRepositoryStatusPayload(info.Status));
				bool updated = publish
					&& usable
					&& _workspace?.TryUpdateRepoClone(
						target.Id,
						target.Url,
						target.LocalCopy.Path,
						target.LocalCopy.Id,
						updatedClone,
						info.DefaultBranch) == true;
				if (!updated)
				{
					failed++;
					_logger.LogWarning(
						"Skipped refreshed state for local repository copy {Path} because its registration or GitHub source changed",
						target.LocalCopy.Path);
					continue;
				}
				refreshed++;
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex) when (
				ex is LibGit2SharpException
					or InvalidOperationException
					or IOException
					or UnauthorizedAccessException
					or ArgumentException)
			{
				failed++;
				_logger.LogWarning(ex, "Could not refresh local repository copy {Path}", target.LocalCopy.Path);
			}
		}
		return new RepositoryRefreshResult(refreshed, failed);
	}

	private void OnPullRepository(IpcMessage message) =>
		OnRepositoryTransfer(message, push: false);

	private void OnPushRepository(IpcMessage message) =>
		OnRepositoryTransfer(message, push: true);

	private void OnRepositoryTransfer(IpcMessage message, bool push)
	{
		RepoBranchActionPayload? payload = SafeGetPayload<RepoBranchActionPayload>(message);
		if (payload is null
			|| string.IsNullOrWhiteSpace(payload.Id)
			|| string.IsNullOrWhiteSpace(payload.ClonePath)
			|| string.IsNullOrWhiteSpace(payload.Branch))
		{
			return;
		}
		if (!TryGetRegisteredClone(payload.Id, payload.ClonePath, out RegisteredRepo repo, out RegisteredClone clone)
			|| _repositoryInspector is not ILocalRepositoryManager manager)
		{
			SendError("That local copy is no longer available.");
			if (!push)
			{
				CompleteRepositoryOperation(payload.RequestId);
			}
			return;
		}
		AccountSession? accountSession = TryCaptureAccountSession(out AccountSession capturedSession)
			? capturedSession
			: null;
		if (push && accountSession is null)
		{
			EmitWorkspaceState();
			SendError("Connect to GitHub before sharing saved versions.");
			return;
		}
		if (!TryBeginLocalRepositoryAction(payload.Id, out CancellationTokenSource cts, out long generation))
		{
			SendError("Another local repository action is still finishing. Try again in a moment.");
			if (!push)
			{
				CompleteRepositoryOperation(payload.RequestId);
			}
			return;
		}

		_ = Task.Run(async () =>
		{
			using CancellationTokenSource? transferCts = accountSession is AccountSession session
				? CancellationTokenSource.CreateLinkedTokenSource(cts.Token, session.CancellationToken)
				: null;
			CancellationToken transferToken = transferCts?.Token ?? cts.Token;
			DocumentRepositoryTransition? documentTransition = null;
			DocumentRepositoryRetirement? deferredRetirement = null;
			string? deferredRetirementError = null;
			bool repositoryMutationStarted = false;
			void ReportRepositoryTransferFailure(
				RepoBranchActionPayload failurePayload,
				CancellationTokenSource failureCts,
				long failureGeneration,
				string error)
			{
				if (!push && repositoryMutationStarted)
				{
					deferredRetirementError ??= error;
					return;
				}
				PublishRepositoryTransferFailure(
					failurePayload, failureCts, failureGeneration, error);
			}
			try
			{
				if (!push)
				{
					documentTransition = BeginDocumentRepositoryTransition(clone.Path);
				}
				LocalRepositoryInfo info;
				if (accountSession is not null)
				{
					info = await (_auth ?? throw new InvalidOperationException("GitHub authorization is unavailable."))
						.WithAccessTokenAsync(
						(token, tokenCt) => Task.FromResult(
							RunRepositoryTransfer(
								manager,
								clone.Path,
								repo.Url,
								repo.DefaultBranch,
								payload.Branch,
								token,
								push,
								documentTransition,
								onMutationStarting: () => repositoryMutationStarted = true,
								ct: tokenCt)),
						transferToken).ConfigureAwait(false);
				}
				else
				{
					info = RunRepositoryTransfer(
						manager,
						clone.Path,
						repo.Url,
						repo.DefaultBranch,
						payload.Branch,
						accessToken: null,
						push: false,
						documentTransition: documentTransition,
						onMutationStarting: () => repositoryMutationStarted = true,
						ct: cts.Token);
				}
				bool publish;
				lock (_clonePublishSync)
				{
					publish = CanPublishLocalRepositoryAction(cts, generation, payload.Id);
				}
				if (!publish)
				{
					deferredRetirement ??= RetireDocumentRepositoryTransitionAfterMutation(
						ref documentTransition, clone.Path);
					return;
				}
				if (!IsUsableCloneOfAtBranch(clone.Path, repo.Url, info.CurrentBranch))
				{
					deferredRetirement ??= RetireDocumentRepositoryTransitionAfterMutation(
						ref documentTransition, clone.Path);
					deferredRetirementError = "That local copy or its current working line changed. The open document was closed to protect it; refresh the repository list before continuing.";
					return;
				}
				if (!push)
				{
					CompleteDocumentRepositoryTransition(documentTransition);
					documentTransition = null;
				}
				PublishRepositoryTransferResult(payload, info, cts, generation, error: null);
			}
			catch (OperationCanceledException) when (cts.IsCancellationRequested)
			{
				// Window teardown or unregistering this repository cancels the action without a stale event.
			}
			catch (OperationCanceledException) when (accountSession?.CancellationToken.IsCancellationRequested == true)
			{
				ReportRepositoryTransferFailure(
					payload,
					cts,
					generation,
					repositoryMutationStarted && !push
						? "Updates were applied, but the account changed before the result could be inspected. The open document was closed to protect the updated files."
						: "The GitHub account changed before the operation completed. No local work was overwritten.");
			}
			catch (OperationCanceledException ex)
			{
				_logger.LogWarning(ex, "Repository transfer was cancelled by the transport");
				ReportRepositoryTransferFailure(
					payload,
					cts,
					generation,
					repositoryMutationStarted && !push
						? "Updates were applied, but the result could not be inspected. The open document was closed to protect the updated files."
						: "The operation was cancelled before it completed. No local work was overwritten.");
			}
			catch (RepositoryIdentityMismatchException ex)
			{
				_logger.LogWarning(ex, "Refused a repository transfer after the local copy source changed");
				PublishRepositoryTransferResult(
					payload,
					info: null,
					cts,
					generation,
					"That local copy belongs to a different repository. No local work was changed.");
			}
			catch (InvalidOperationException ex)
			{
				_logger.LogWarning(ex, "Could not {Operation} repository working line", push ? "share" : "update");
				ReportRepositoryTransferFailure(
					payload,
					cts,
					generation,
					repositoryMutationStarted && !push
						? "Updates were applied, but their result could not be inspected. The open document was closed to protect the updated files."
						: ex.Message);
			}
			catch (Exception ex) when (
				ex is LibGit2SharpException
					or IOException
					or UnauthorizedAccessException
					or ArgumentException)
			{
				_logger.LogWarning(ex, "Could not {Operation} repository working line", push ? "share" : "update");
				ReportRepositoryTransferFailure(
					payload,
					cts,
					generation,
					push
						? "Could not share this working line. No local files were changed."
						: repositoryMutationStarted
							? "Updates were applied, but their result could not be inspected. The open document was closed to protect the updated files."
							: "Could not get updates. No unfinished local work was overwritten.");
			}
			finally
			{
				if (!push && repositoryMutationStarted)
				{
					deferredRetirement ??= RetireDocumentRepositoryTransitionAfterMutation(ref documentTransition, clone.Path);
				}
				else
				{
					CancelDocumentRepositoryTransition(documentTransition);
				}
				if (!push)
				{
					PublishDocumentRepositoryRetirement(deferredRetirement);
					if (deferredRetirementError is not null)
					{
						SendError(deferredRetirementError);
					}
					CompleteRepositoryOperation(payload.RequestId);
				}
				FinishLocalRepositoryActionAfterOutbound(cts);
			}
		});
	}

	private LocalRepositoryInfo RunRepositoryTransfer(
		ILocalRepositoryManager manager,
		string clonePath,
		string expectedRepositoryUrl,
		string defaultBranch,
		string expectedBranch,
		string? accessToken,
		bool push,
		DocumentRepositoryTransition? documentTransition,
		Action? onMutationStarting,
		CancellationToken ct)
	{
		lock (_repoGate)
		{
			if (push)
			{
				return manager.PushBranchSafely(
					clonePath,
					expectedRepositoryUrl,
					defaultBranch,
					expectedBranch,
					accessToken ?? throw new InvalidOperationException("Connect to GitHub before sharing saved versions."),
					ct);
			}
			else
			{
				LocalRepositoryInfo info = manager.PullFastForward(
					clonePath,
					expectedRepositoryUrl,
					defaultBranch,
					expectedBranch,
					accessToken,
					ct,
					beforeMutation: () => PersistDocumentRepositoryTransition(documentTransition),
					onMutationStarting: () =>
					{
						if (documentTransition is not null)
						{
							Interlocked.Increment(ref _draftGeneration);
						}
						onMutationStarting?.Invoke();
					});
				return info;
			}
		}
	}

	private void PublishRepositoryTransferFailure(
		RepoBranchActionPayload payload,
		CancellationTokenSource cts,
		long generation,
		string error)
	{
		PublishRepositoryTransferResult(payload, info: null, cts, generation, error);
	}

	private void PublishRepositoryTransferResult(
		RepoBranchActionPayload payload,
		LocalRepositoryInfo? info,
		CancellationTokenSource cts,
		long generation,
		string? error)
	{
		bool publish = CanPublishLocalRepositoryAction(cts, generation, payload.Id);
		if (publish && info is not null)
		{
			RegisteredRepo? currentRepo = _workspace?.FindRepo(payload.Id);
			RegisteredClone? currentClone = currentRepo?.Clones.FirstOrDefault(candidate =>
				SameFullPath(candidate.Path, payload.ClonePath));
			publish = currentRepo is not null
				&& currentClone is not null
				&& _workspace?.TryUpdateRepoClone(
					payload.Id,
					currentRepo.Url,
					currentClone.Path,
					currentClone.Id,
					new RegisteredClone(
						currentClone.Id,
						currentClone.Path,
						info.CurrentBranch,
						info.Branches.Select(branch => new RegisteredBranch(
							branch.Name,
							ToRepositoryStatusPayload(branch.Status),
							branch.CanDelete)).ToArray(),
						ToRepositoryStatusPayload(info.Status)),
					info.DefaultBranch) == true;
		}
		if (!publish)
		{
			return;
		}
		EmitWorkspaceState();
		if (error is not null)
		{
			SendError(error);
		}
	}
	private void OnDeleteRepoClone(IpcMessage message)
	{
		RepoDeleteClonePayload? payload = SafeGetPayload<RepoDeleteClonePayload>(message);
		if (payload is null
			|| string.IsNullOrWhiteSpace(payload.Id)
			|| string.IsNullOrWhiteSpace(payload.ClonePath))
		{
			return;
		}
		if (!TryFindRegisteredClone(payload.Id, payload.ClonePath, out RegisteredRepo repo, out RegisteredClone clone))
		{
			SendError("That local copy is no longer available.");
			CompleteRepositoryOperation(payload.RequestId);
			return;
		}
		if (!IsUsableCloneOf(clone.Path, repo.Url))
		{
			if (!TryBeginLocalRepositoryAction(payload.Id, out CancellationTokenSource staleCts, out long staleGeneration))
			{
				SendError("Another local repository action is still finishing. Try again in a moment.");
				CompleteRepositoryOperation(payload.RequestId);
				return;
			}
			try
			{
				bool occupied = Directory.Exists(clone.Path) || File.Exists(clone.Path);
				_workspace?.RemoveRepoClone(payload.Id, clone.Path);
				if (CanPublishLocalRepositoryAction(staleCts, staleGeneration, payload.Id))
				{
					EmitWorkspaceState();
					if (occupied)
					{
						SendError("SpecDesk forgot this unavailable local copy. Existing files were left untouched; choose another name before cloning.");
					}
				}
			}
			finally
			{
				CompleteRepositoryOperation(payload.RequestId);
				FinishLocalRepositoryActionAfterOutbound(staleCts);
			}
			return;
		}
		if (_repositoryInspector is not ILocalRepositoryManager manager)
		{
			SendError("Removing local copies isn't available in this build.");
			CompleteRepositoryOperation(payload.RequestId);
			return;
		}
		if (!TryBeginLocalRepositoryAction(payload.Id, out CancellationTokenSource cts, out long generation))
		{
			SendError("Another local repository action is still finishing. Try again in a moment.");
			CompleteRepositoryOperation(payload.RequestId);
			return;
		}

		_ = Task.Run(() =>
		{
			DocumentRepositoryTransition? documentTransition = null;
			DocumentRepositoryRetirement? deferredRetirement = null;
			string? deferredRetirementError = null;
			bool repositoryMutationStarted = false;
			try
			{
				documentTransition = BeginDocumentRepositoryTransition(clone.Path);
				RepositoryDeletionRisks risks;
				lock (_repoGate)
				{
					risks = manager.InspectDeletionRisks(
						clone.Path,
						repo.Url,
						clone.CurrentBranch,
						beforeInspect: () => PersistDocumentRepositoryTransition(documentTransition));
				}
				if (TryRejectCloneDeletionWithLinkedWorktrees(
					risks.LinkedWorktrees, cts, generation, payload.Id))
				{
					CancelDocumentRepositoryTransition(documentTransition);
					documentTransition = null;
					return;
				}
				if (NeedsConfirmation(payload.ConfirmationToken, risks))
				{
					CancelDocumentRepositoryTransition(documentTransition);
					documentTransition = null;
					PublishDeleteConfirmationIfCurrent(
						cts, generation, "deleteClone", payload.Id, clone.Path, null, risks);
					return;
				}
				bool cleanupComplete;
				lock (_repoGate)
				{
					cleanupComplete = manager.DeleteClone(
						clone.Path,
						repo.Url,
						risks.ConfirmationToken,
						onMutationStarting: () =>
						{
							if (documentTransition is not null)
							{
								Interlocked.Increment(ref _draftGeneration);
							}
							repositoryMutationStarted = true;
						});
				}
				RegisteredClone? currentClone;
				bool publishWorkspaceState;
				lock (_clonePublishSync)
				{
					publishWorkspaceState = CanPublishLocalRepositoryAction(cts, generation, payload.Id);
					RegisteredRepo? currentRepo = publishWorkspaceState
						? _workspace?.FindRepo(payload.Id)
						: null;
					currentClone = currentRepo?.Clones.FirstOrDefault(candidate =>
						SameFullPath(candidate.Path, clone.Path));
				}
				if (!publishWorkspaceState)
				{
					deferredRetirement ??= RetireDocumentRepositoryTransitionAfterMutation(
						ref documentTransition, clone.Path);
					return;
				}
				if (currentClone is null)
				{
					deferredRetirement ??= RetireDocumentRepositoryTransitionAfterMutation(
						ref documentTransition, clone.Path);
					deferredRetirementError = "The local copy was removed, but its registration changed while the action was finishing. Refresh the repository list before continuing.";
					return;
				}
				bool registrationRemoved = _workspace!.TryRemoveRepoClone(
					payload.Id, repo.Url, currentClone.Path, currentClone.Id);
				deferredRetirement ??= RetireDocumentRepositoryTransitionAfterMutation(
					ref documentTransition, currentClone.Path);
				if (!registrationRemoved)
				{
					deferredRetirementError = "The local copy was removed, but its registration changed while the action was finishing. Refresh the repository list before continuing.";
					return;
				}
				WorkspaceRootClearPublication? workspaceClear = CloseDeletedWorkspace(currentClone.Path);
				if (!cleanupComplete)
				{
					deferredRetirementError = "The local copy was removed, but some quarantined files could not be cleaned up.";
				}
				EmitWorkspaceState();
				if (workspaceClear is { } clearPublication)
				{
					PublishClearedWorkspace(clearPublication);
				}
			}
			catch (RepositoryQuarantinedCloneException ex)
			{
				_logger.LogError(
					ex,
					"Local repository copy was quarantined at {QuarantinePath} after its registered path changed",
					ex.QuarantinePath);
				deferredRetirement ??= RetireDocumentRepositoryTransitionAfterMutation(ref documentTransition, clone.Path);
				RegisteredClone? currentClone;
				bool publishWorkspaceState;
				lock (_clonePublishSync)
				{
					currentClone = _workspace?.FindRepo(payload.Id)?.Clones.FirstOrDefault(candidate =>
						SameFullPath(candidate.Path, clone.Path));
					publishWorkspaceState = currentClone is not null
						&& CanPublishLocalRepositoryAction(cts, generation, payload.Id);
				}
				bool registrationRemoved = publishWorkspaceState
					&& currentClone is not null
					&& _workspace!.TryRemoveRepoClone(
						payload.Id, repo.Url, currentClone.Path, currentClone.Id);
				WorkspaceRootClearPublication? workspaceClear = registrationRemoved
					? CloseDeletedWorkspace(clone.Path)
					: null;
				publishWorkspaceState = registrationRemoved
					&& CanPublishLocalRepositoryAction(cts, generation, payload.Id);
				if (publishWorkspaceState)
				{
					deferredRetirementError = $"The local copy could not be restored to its original folder. Your files were kept at {ex.QuarantinePath}. Its unavailable registration was removed.";
				}
				else if (CanPublishLocalRepositoryAction(cts, generation, payload.Id))
				{
					deferredRetirementError = $"The local copy could not be restored to its original folder. Your files were kept at {ex.QuarantinePath}. Its registration changed while the action was finishing.";
				}
				if (publishWorkspaceState)
				{
					EmitWorkspaceState();
				}
				if (workspaceClear is { } clearPublication)
				{
					PublishClearedWorkspace(clearPublication);
				}
			}
			catch (RepositoryHasLinkedWorktreesException ex)
			{
				CancelDocumentRepositoryTransition(documentTransition);
				_logger.LogWarning(ex, "Refused to delete a local copy that owns linked working copies");
				TryRejectCloneDeletionWithLinkedWorktrees(
					ex.LinkedWorktrees, cts, generation, payload.Id);
			}
			catch (RepositoryIdentityMismatchException ex)
			{
				CancelDocumentRepositoryTransition(documentTransition);
				_logger.LogWarning(ex, "Refused to delete a local copy whose GitHub source changed");
				if (CanPublishLocalRepositoryAction(cts, generation, payload.Id))
				{
					_workspace?.RemoveRepoClone(payload.Id, clone.Path);
					EmitWorkspaceState();
					SendError("SpecDesk forgot this unavailable local copy. Existing files were left untouched.");
				}
			}
			catch (RepositoryStateChangedException)
			{
				if (repositoryMutationStarted)
				{
					deferredRetirement ??= RetireDocumentRepositoryTransitionAfterMutation(ref documentTransition, clone.Path);
					if (CanPublishLocalRepositoryAction(cts, generation, payload.Id))
					{
						SendError("The local copy changed while it was being removed. The open document was closed to protect the local files; refresh the repository list before continuing.");
					}
				}
				else
				{
					CancelDocumentRepositoryTransition(documentTransition);
					documentTransition = null;
					RepromptDeleteCloneIfCurrent(
						manager, payload, repo.Url, clone.CurrentBranch, cts, generation);
				}
			}
			catch (Exception ex) when (
				ex is LibGit2SharpException
					or InvalidOperationException
					or IOException
					or UnauthorizedAccessException
					or ArgumentException
					or FormatException)
			{
				_logger.LogWarning(ex, "Could not delete local repository copy");
				if (repositoryMutationStarted)
				{
					deferredRetirement ??= RetireDocumentRepositoryTransitionAfterMutation(ref documentTransition, clone.Path);
				}
				else
				{
					CancelDocumentRepositoryTransition(documentTransition);
					documentTransition = null;
				}
				if (CanPublishLocalRepositoryAction(cts, generation, payload.Id))
				{
					if (repositoryMutationStarted)
					{
						deferredRetirementError = "The local copy changed while it was being removed. The open document was closed to protect the local files; refresh the repository list before continuing.";
					}
					else
					{
						SendError("Could not remove that local copy. Nothing was removed from your list.");
					}
				}
			}
			finally
			{
				if (repositoryMutationStarted)
				{
					deferredRetirement ??= RetireDocumentRepositoryTransitionAfterMutation(ref documentTransition, clone.Path);
				}
				else
				{
					CancelDocumentRepositoryTransition(documentTransition);
				}
				PublishDocumentRepositoryRetirement(deferredRetirement);
				if (deferredRetirementError is not null)
				{
					SendError(deferredRetirementError);
				}
				CompleteRepositoryOperation(payload.RequestId);
				FinishLocalRepositoryActionAfterOutbound(cts);
			}
		});
	}

	private void OnDeleteRepoBranch(IpcMessage message)
	{
		RepoDeleteBranchPayload? payload = SafeGetPayload<RepoDeleteBranchPayload>(message);
		if (payload is null
			|| string.IsNullOrWhiteSpace(payload.Id)
			|| string.IsNullOrWhiteSpace(payload.ClonePath)
			|| string.IsNullOrWhiteSpace(payload.Branch))
		{
			return;
		}
		if (!TryGetRegisteredClone(payload.Id, payload.ClonePath, out RegisteredRepo repo, out RegisteredClone clone)
			|| _repositoryInspector is not ILocalRepositoryManager manager)
		{
			SendError("That working line is no longer available.");
			CompleteRepositoryOperation(payload.RequestId);
			return;
		}
		if (!TryBeginLocalRepositoryAction(payload.Id, out CancellationTokenSource cts, out long generation))
		{
			SendError("Another local repository action is still finishing. Try again in a moment.");
			CompleteRepositoryOperation(payload.RequestId);
			return;
		}

		_ = Task.Run(() =>
		{
			DocumentRepositoryTransition? documentTransition = null;
			DocumentRepositoryRetirement? deferredRetirement = null;
			string? deferredRetirementError = null;
			bool repositoryMutationStarted = false;
			try
			{
				documentTransition = BeginDocumentRepositoryTransition(clone.Path);
				RepositoryDeletionRisks risks;
				BranchDeletionResult? deletion = null;
				bool needsConfirmation;
				lock (_repoGate)
				{
					risks = manager.InspectDeletionRisks(
						clone.Path,
						repo.Url,
						clone.CurrentBranch,
						payload.Branch,
						() => PersistDocumentRepositoryTransition(documentTransition));
					needsConfirmation = NeedsConfirmation(payload.ConfirmationToken, risks);
					if (!needsConfirmation)
					{
						deletion = manager.DeleteBranch(
							clone.Path,
							repo.Url,
							payload.Branch,
							repo.DefaultBranch,
							risks.ConfirmationToken,
							onCurrentBranchChangeStarting: () =>
							{
								if (documentTransition is not null)
								{
									Interlocked.Increment(ref _draftGeneration);
								}
								repositoryMutationStarted = true;
							});
					}
				}
				if (needsConfirmation)
				{
					CancelDocumentRepositoryTransition(documentTransition);
					documentTransition = null;
					PublishDeleteConfirmationIfCurrent(
						cts, generation, "deleteBranch", payload.Id, clone.Path, payload.Branch, risks);
					return;
				}
				RegisteredRepo? currentRepo = _workspace?.FindRepo(payload.Id);
				RegisteredClone? currentClone = currentRepo?.Clones.FirstOrDefault(candidate =>
					SameFullPath(candidate.Path, clone.Path));
				bool usable = currentRepo is not null
					&& currentClone is not null
					&& IsUsableCloneOfAtBranch(
						currentClone.Path, currentRepo.Url, deletion!.Repository.CurrentBranch);
				if (!CanPublishLocalRepositoryAction(cts, generation, payload.Id) || !usable)
				{
					deferredRetirement ??= RetireDocumentRepositoryTransitionAfterMutation(
						ref documentTransition, clone.Path);
					deferredRetirementError = "The local-copy folder or current working line changed while the working line was being removed. The open document was closed to protect it; refresh the repository list before continuing.";
					return;
				}

				LocalRepositoryInfo updatedInfo = deletion!.Repository;
				RegisteredClone updatedClone = new(
					currentClone!.Id,
					currentClone.Path,
					updatedInfo.CurrentBranch,
					updatedInfo.Branches.Select(branch => new RegisteredBranch(
						branch.Name,
						ToRepositoryStatusPayload(branch.Status),
						branch.CanDelete)).ToArray(),
					ToRepositoryStatusPayload(updatedInfo.Status));
				bool updated = _workspace?.TryUpdateRepoClone(
					payload.Id,
					currentRepo!.Url,
					currentClone.Path,
					currentClone.Id,
					updatedClone,
					updatedInfo.DefaultBranch) == true;
				if (!updated)
				{
					deferredRetirement ??= RetireDocumentRepositoryTransitionAfterMutation(
						ref documentTransition, clone.Path);
					deferredRetirementError = "The local-copy registration changed while the working line was being removed. The open document was closed to protect it; refresh the repository list before continuing.";
					return;
				}
				_workspace!.SetFavorite(
					new WorkspaceItem(
						currentClone.Path,
						payload.Branch,
						IsFolder: true,
						Kind: "branch",
						RepositoryId: payload.Id,
						Branch: payload.Branch),
					favorite: false);
				if (deletion.SwitchedCurrentBranch)
				{
					deferredRetirement ??= RetireDocumentRepositoryTransitionAfterMutation(
						ref documentTransition, clone.Path);
					deferredRetirement ??= new DocumentRepositoryRetirement(
						CloseDeletedWorkspace(clone.Path), ContentGeneration: null);
				}
				else
				{
					CancelDocumentRepositoryTransition(documentTransition);
				}
				documentTransition = null;
				EmitWorkspaceState();
				if (!deletion.CleanupComplete)
				{
					SendError("The working line was removed, but one or more protected snapshots remain.");
				}
			}
			catch (RepositoryIdentityMismatchException ex)
			{
				_logger.LogWarning(ex, "Refused to delete a working line whose GitHub source changed");
				if (CanPublishLocalRepositoryAction(cts, generation, payload.Id))
				{
					SendError("That local copy belongs to a different repository. No local work was changed.");
				}
			}
			catch (RepositoryStateChangedException)
			{
				RepromptDeleteBranchIfCurrent(
					manager, payload, repo.Url, clone.CurrentBranch, cts, generation);
			}
			catch (Exception ex) when (
				ex is LibGit2SharpException
					or InvalidOperationException
					or IOException
					or UnauthorizedAccessException
					or ArgumentException
					or FormatException)
			{
				_logger.LogWarning(ex, "Could not delete repository working line");
				if (CanPublishLocalRepositoryAction(cts, generation, payload.Id))
				{
					if (repositoryMutationStarted)
					{
						deferredRetirementError = "The working line changed, but its removal could not be finished. The open document was closed to protect the checked-out files.";
					}
					else
					{
						SendError("Could not remove that working line. No saved work was discarded.");
					}
				}
			}
			finally
			{
				if (repositoryMutationStarted)
				{
					deferredRetirement ??= RetireDocumentRepositoryTransitionAfterMutation(ref documentTransition, clone.Path);
				}
				else
				{
					CancelDocumentRepositoryTransition(documentTransition);
				}
				PublishDocumentRepositoryRetirement(deferredRetirement);
				if (deferredRetirementError is not null)
				{
					SendError(deferredRetirementError);
				}
				CompleteRepositoryOperation(payload.RequestId);
				FinishLocalRepositoryActionAfterOutbound(cts);
			}
		});
	}

	private bool TryGetRegisteredClone(
		string id,
		string clonePath,
		out RegisteredRepo repo,
		out RegisteredClone clone)
	{
		if (!TryFindRegisteredClone(id, clonePath, out repo, out clone)
			|| !IsUsableCloneOf(clone.Path, repo.Url))
		{
			repo = null!;
			clone = null!;
			return false;
		}
		return true;
	}

	private bool TryFindRegisteredClone(
		string id,
		string clonePath,
		out RegisteredRepo repo,
		out RegisteredClone clone)
	{
		RegisteredRepo? foundRepo;
		lock (_sync)
		{
			foundRepo = _disposed ? null : _workspace?.FindRepo(id);
		}
		RegisteredClone? foundClone = foundRepo?.Clones
			.FirstOrDefault(candidate => SameFullPath(candidate.Path, clonePath));
		if (foundRepo is null || foundClone is null)
		{
			repo = null!;
			clone = null!;
			return false;
		}
		repo = foundRepo;
		clone = foundClone;
		return true;
	}

	private bool TryBeginNavigatingLocalRepositoryAction(
		string repoId,
		out CancellationTokenSource cts,
		out long generation,
		out long navigationGeneration)
	{
		lock (_clonePublishSync)
		{
			lock (_sync)
			{
				if (!TryBeginLocalRepositoryAction(repoId, out cts, out generation))
				{
					navigationGeneration = 0;
					return false;
				}
				navigationGeneration = _localRepositoryActionNavigationReservation;
				AcceptWorkspaceNavigationIntentLocked(navigationGeneration);
				return true;
			}
		}
	}

	private bool TryBeginLocalRepositoryAction(
		string repoId,
		out CancellationTokenSource cts,
		out long generation)
	{
		lock (_clonePublishSync)
		{
			lock (_sync)
			{
				bool registrationMissing = repoId != AllRepositoriesActionId
					&& _workspace?.FindRepo(repoId) is null;
				if (_disposed || _closePreparationClaimed || _documentOpenTransition
					|| _documentRepositoryTransition
					|| _cloneCts is not null || _localRepositoryActionCts is not null || registrationMissing)
				{
					cts = null!;
					generation = 0;
					return false;
				}
				cts = new CancellationTokenSource();
				_localRepositoryActionCts = cts;
				_localRepositoryActionRepoId = repoId;
				generation = ++_localRepositoryActionGeneration;
				_localRepositoryActionNavigationReservation = ++_workspaceNavigationIntentSequence;
				return true;
			}
		}
	}

	private bool TryRejectCloneDeletionWithLinkedWorktrees(
		IReadOnlyList<LinkedWorktreeDeletionRisk>? linkedWorktrees,
		CancellationTokenSource cts,
		long generation,
		string repoId)
	{
		if (linkedWorktrees is not { Count: > 0 })
		{
			return false;
		}
		if (!CanPublishLocalRepositoryAction(cts, generation, repoId))
		{
			return true;
		}

		string copies = string.Join("; ", linkedWorktrees.Select(linked =>
		{
			List<string> details = [];
			if (linked.InspectionFailed)
			{
				details.Add("its local-work state could not be checked");
			}
			else
			{
				if (linked.HasUncommitted)
				{
					details.Add("unfinished edits");
				}
				if (linked.HasUnpushed)
				{
					details.Add("unshared saved versions");
				}
				if (linked.StashCount > 0)
				{
					details.Add(linked.StashCount == 1
						? "one protected work snapshot"
						: $"{linked.StashCount} protected work snapshots");
				}
				if (linked.HasConflicts)
				{
					details.Add("overlapping changes");
				}
			}
			string state = details.Count == 0 ? "no unfinished work found" : string.Join(", ", details);
			return $"{linked.Path} ({state})";
		}));
		SendError(
			$"This local copy is also used by linked working copies: {copies}. "
			+ "Close and remove those linked working copies first, then try again. Nothing was deleted.");
		return true;
	}

	private static bool NeedsConfirmation(string? suppliedToken, RepositoryDeletionRisks risks)
	{
		bool hasRisks = risks.HasUncommitted
			|| risks.HasUnpushed
			|| risks.StashCount > 0
			|| risks.LinkedWorktrees?.Count > 0;
		return suppliedToken is null
			? hasRisks
			: !string.Equals(suppliedToken, risks.ConfirmationToken, StringComparison.Ordinal);
	}

	private bool CanPublishLocalRepositoryAction(
		CancellationTokenSource cts,
		long generation,
		string repoId)
	{
		lock (_sync)
		{
			return !_disposed
				&& !cts.IsCancellationRequested
				&& ReferenceEquals(_localRepositoryActionCts, cts)
				&& generation == _localRepositoryActionGeneration
				&& (repoId == AllRepositoriesActionId || _workspace?.FindRepo(repoId) is not null);
		}
	}

	private void FinishLocalRepositoryAction(CancellationTokenSource cts)
	{
		lock (_sync)
		{
			if (ReferenceEquals(_localRepositoryActionCts, cts))
			{
				_localRepositoryActionCts = null;
				_localRepositoryActionNavigationReservation = 0;
				_localRepositoryActionRepoId = null;
			}
		}
		cts.Dispose();
	}

	private void FinishLocalRepositoryActionAfterOutbound(CancellationTokenSource cts) =>
		CompleteOutboundBatch(() => FinishLocalRepositoryAction(cts));

	private void FinishCloneAfterOutbound(CancellationTokenSource cts) =>
		CompleteOutboundBatch(() =>
		{
			lock (_clonePublishSync)
			{
				lock (_sync)
				{
					if (ReferenceEquals(_cloneCts, cts))
					{
						_cloneCts = null;
						_cloneRepoId = null;
					}
				}
			}
			cts.Dispose();
		});


	private void PublishDeleteConfirmationIfCurrent(
		CancellationTokenSource cts,
		long generation,
		string operation,
		string id,
		string clonePath,
		string? branch,
		RepositoryDeletionRisks risks)
	{
		if (!CanPublishLocalRepositoryAction(cts, generation, id))
		{
			return;
		}
		if (branch is null
			&& TryRejectCloneDeletionWithLinkedWorktrees(risks.LinkedWorktrees, cts, generation, id))
		{
			return;
		}
		List<string> warnings = [];
		if (risks.HasUncommitted)
		{
			warnings.Add("There are unfinished local edits.");
		}
		if (risks.HasUnpushed)
		{
			warnings.Add("There are saved versions that have not been shared.");
		}
		if (risks.StashCount > 0)
		{
			warnings.Add(risks.StashCount == 1
				? "There is one protected local work snapshot."
				: $"There are {risks.StashCount} protected local work snapshots.");
		}
		if (warnings.Count == 0)
		{
			warnings.Add("The local repository changed since you confirmed.");
		}
		string message = branch is null
			? "Delete this local copy from this computer?"
			: "Delete this local working line?";
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.RepoConfirmation,
			new RepoConfirmationPayload(
				operation,
				id,
				clonePath,
				branch,
				message,
				warnings,
				risks.ConfirmationToken)));
	}

	private void RepromptDeleteCloneIfCurrent(
		ILocalRepositoryManager manager,
		RepoDeleteClonePayload payload,
		string expectedRepositoryUrl,
		string? expectedCurrentBranch,
		CancellationTokenSource cts,
		long generation)
	{
		if (!CanPublishLocalRepositoryAction(cts, generation, payload.Id))
		{
			return;
		}
		try
		{
			RepositoryDeletionRisks current = manager.InspectDeletionRisks(
				payload.ClonePath, expectedRepositoryUrl, expectedCurrentBranch);
			PublishDeleteConfirmationIfCurrent(
				cts, generation, "deleteClone", payload.Id, payload.ClonePath, null, current);
		}
		catch (Exception ex) when (
			ex is LibGit2SharpException
				or InvalidOperationException
				or IOException
				or UnauthorizedAccessException
				or ArgumentException)
		{
			_logger.LogWarning(ex, "Could not refresh local-copy deletion confirmation");
			if (CanPublishLocalRepositoryAction(cts, generation, payload.Id))
			{
				SendError("The local copy changed and could not be checked again. Nothing was deleted.");
			}
		}
	}

	private void RepromptDeleteBranchIfCurrent(
		ILocalRepositoryManager manager,
		RepoDeleteBranchPayload payload,
		string expectedRepositoryUrl,
		string? expectedCurrentBranch,
		CancellationTokenSource cts,
		long generation)
	{
		if (!CanPublishLocalRepositoryAction(cts, generation, payload.Id))
		{
			return;
		}
		try
		{
			RepositoryDeletionRisks current = manager.InspectDeletionRisks(
				payload.ClonePath, expectedRepositoryUrl, expectedCurrentBranch, payload.Branch);
			PublishDeleteConfirmationIfCurrent(
				cts, generation, "deleteBranch", payload.Id, payload.ClonePath, payload.Branch, current);
		}
		catch (Exception ex) when (
			ex is LibGit2SharpException
				or InvalidOperationException
				or IOException
				or UnauthorizedAccessException
				or ArgumentException)
		{
			_logger.LogWarning(ex, "Could not refresh working-line deletion confirmation");
			if (CanPublishLocalRepositoryAction(cts, generation, payload.Id))
			{
				SendError("The working line changed and could not be checked again. Nothing was deleted.");
			}
		}
	}

	private sealed record DocumentRepositoryTransition(string Path, string Text, string LineEnding);

	private DocumentRepositoryTransition? BeginDocumentRepositoryTransition(string clonePath)
	{
		string cloneRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(clonePath));
		lock (_sync)
		{
			if (_currentPath is null)
			{
				return null;
			}
			string documentPath = Path.GetFullPath(_currentPath);
			if (!SameFullPath(documentPath, cloneRoot)
				&& !AppAssetResolver.IsInside(cloneRoot, documentPath))
			{
				return null;
			}
			if (_closePreparationClaimed
				|| _documentMutationLeaseClaimed
				|| _documentDiscardTransition
				|| _documentOpenTransition
				|| _documentRepositoryTransition)
			{
				throw new InvalidOperationException(
					"The open document is still being saved or changed. Wait a moment, then try again.");
			}
			_documentRepositoryTransition = true;
			_autosaveTimer?.Dispose();
			_autosaveTimer = null;
			return new DocumentRepositoryTransition(documentPath, _text, _lineEnding);
		}
	}

	private static void PersistDocumentRepositoryTransition(DocumentRepositoryTransition? transition)
	{
		if (transition is not null)
		{
			File.WriteAllText(transition.Path, ApplyLineEnding(transition.Text, transition.LineEnding));
		}
	}

	private void CompleteDocumentRepositoryTransition(
		DocumentRepositoryTransition? transition,
		string? resumedBranch = null,
		string? resumedBaseBranch = null)
	{
		if (transition is null)
		{
			return;
		}
		try
		{
			if (!File.Exists(transition.Path)
				|| !LoadFile(
					transition.Path,
					recordRecent: false,
					resumedBranch,
					resumedBaseBranch))
			{
				ClearActiveDocument();
			}
		}
		finally
		{
			lock (_sync)
			{
				_documentRepositoryTransition = false;
			}
		}
	}

	private void CancelDocumentRepositoryTransition(DocumentRepositoryTransition? transition)
	{
		if (transition is null)
		{
			return;
		}
		bool reschedule;
		lock (_sync)
		{
			_documentRepositoryTransition = false;
			reschedule = _session.Dirty;
		}
		if (reschedule)
		{
			MarkDirtyAndScheduleDiskAutosave();
		}
	}

	private readonly record struct DocumentRepositoryRetirement(
		WorkspaceRootClearPublication? WorkspaceClear,
		long? ContentGeneration,
		long NavigationGeneration = 0);

	private DocumentRepositoryRetirement? RetireDocumentRepositoryTransitionAfterMutation(
		ref DocumentRepositoryTransition? transition,
		string clonePath)
	{
		if (transition is null)
		{
			return null;
		}

		// A timer callback may already have snapshotted the old path while waiting for the repository gate.
		// Retire that snapshot as well as the timer still owned by the active document.
		long contentGeneration;
		long navigationGeneration;
		lock (_sync)
		{
			Interlocked.Increment(ref _draftGeneration);
			ClearActiveDocumentStateLocked();
			contentGeneration = Interlocked.Read(ref _contentGeneration);
			navigationGeneration = _localRepositoryActionNavigationReservation;
			if (navigationGeneration == 0)
			{
				navigationGeneration = ++_workspaceNavigationIntentSequence;
			}
			AcceptWorkspaceNavigationIntentLocked(navigationGeneration);
			_documentRepositoryTransition = false;
		}
		DocumentRetirementStateClearedForTest?.Invoke();
		transition = null;
		return new DocumentRepositoryRetirement(
			CloseDeletedWorkspace(clonePath), contentGeneration, navigationGeneration);
	}

	private void PublishDocumentRepositoryRetirement(DocumentRepositoryRetirement? retirement)
	{
		if (retirement is null)
		{
			return;
		}
		DocumentRetirementPublishingForTest?.Invoke();
		if (retirement.Value.ContentGeneration is { } contentGeneration)
		{
			PublishRetiredActiveDocumentClear(
				contentGeneration, retirement.Value.NavigationGeneration);
		}
		if (retirement.Value.WorkspaceClear is { } clearPublication)
		{
			PublishClearedWorkspace(clearPublication);
		}
	}

	private void PublishRetiredActiveDocumentClear(
		long contentGeneration, long navigationGeneration)
	{
		lock (_sync)
		{
			if (_disposed
				|| navigationGeneration != _workspaceNavigationIntentGeneration
				|| _currentPath is not null
				|| _remoteDocument is not null
				|| Interlocked.Read(ref _contentGeneration) != contentGeneration)
			{
				return;
			}
			PublishActiveDocumentCleared();
		}
	}

	private void PublishClearedWorkspace(WorkspaceRootClearPublication publication)
	{
		WorkspaceRootClearPublishingForTest?.Invoke();
		lock (_workspaceRootPublicationSync)
		{
			lock (_sync)
			{
				if (_disposed
					|| publication.Generation != _workspaceRootGeneration
					|| publication.NavigationGeneration != _workspaceNavigationIntentGeneration
					|| _workspaceRoot is not null)
				{
					return;
				}
				Emit(IpcSerializer.SerializeEvent(
					MessageKinds.Tree,
					new TreePayload(string.Empty, [])));
			}
		}
	}

	private void ClearActiveDocument()
	{
		lock (_sync)
		{
			ClearActiveDocumentStateLocked();
		}
		PublishActiveDocumentCleared();
	}

	private void ClearActiveDocumentStateLocked()
	{
		_autosaveTimer?.Dispose();
		_autosaveTimer = null;
		_currentPath = null;
		_repoRoot = null;
		_remoteDocument = null;
		_text = string.Empty;
		_lineEnding = "\n";
		Interlocked.Increment(ref _contentGeneration);
		_session = new DraftSession(
			Lifecycle.stateName(Lifecycle.State.Published), null, null, false, 0, 0,
			Interlocked.Read(ref _draftGeneration));
	}

	private void PublishActiveDocumentCleared()
	{
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.DocLoaded,
			new DocLoadedPayload(string.Empty, string.Empty, string.Empty, ReadOnly: true)));
		SendWorkspaceContext();
	}

	private WorkspaceRootClearPublication? CloseDeletedWorkspace(string clonePath)
	{
		WorkspaceRootClearPublication? publication = null;
		lock (_sync)
		{
			string cloneRoot = Path.GetFullPath(clonePath);
			bool closed = _workspaceRoot is not null
				&& (SameFullPath(_workspaceRoot, cloneRoot)
					|| AppAssetResolver.IsInside(cloneRoot, Path.GetFullPath(_workspaceRoot)));
			if (closed)
			{
				long navigationGeneration = _localRepositoryActionNavigationReservation;
				if (navigationGeneration == 0)
				{
					navigationGeneration = ++_workspaceNavigationIntentSequence;
				}
				AcceptWorkspaceNavigationIntentLocked(navigationGeneration);
				_workspaceRoot = null;
				_workspaceRootGeneration++;
				publication = new WorkspaceRootClearPublication(
					_workspaceRootGeneration, navigationGeneration);
			}
			bool documentDeleted = _repoRoot is not null
				&& (SameFullPath(_repoRoot, cloneRoot)
					|| AppAssetResolver.IsInside(cloneRoot, Path.GetFullPath(_repoRoot)));
			if (documentDeleted)
			{
				_autosaveTimer?.Dispose();
				_autosaveTimer = null;
				_currentPath = null;
				_repoRoot = null;
				_remoteDocument = null;
			}
		}
		return publication;
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
			if (!known.Contains(candidate) && !Directory.Exists(candidate) && !File.Exists(candidate))
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
		catch (Exception ex) when (
			ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
		{
			_logger.LogWarning(ex, "Ignoring an unavailable registered repository copy");
			return false;
		}
	}

	private bool IsUsableCloneOf(string path, string url)
	{
		if (!IsUsableClone(path) || _cloner is null)
		{
			return false;
		}
		try
		{
			return _cloner.IsCloneOf(path, url);
		}
		catch (Exception ex) when (
			ex is ArgumentException
				or NotSupportedException
				or InvalidOperationException
				or IOException
				or UnauthorizedAccessException)
		{
			_logger.LogWarning(ex, "Ignoring a registered copy that no longer matches its GitHub repository");
			return false;
		}
	}

	private bool IsUsableCloneOfAtBranch(string path, string url, string? expectedCurrentBranch)
	{
		if (!IsUsableClone(path) || _cloner is null)
		{
			return false;
		}
		try
		{
			return _cloner.IsCloneOfAtBranch(path, url, expectedCurrentBranch);
		}
		catch (Exception ex) when (
			ex is ArgumentException
				or NotSupportedException
				or InvalidOperationException
				or IOException
				or UnauthorizedAccessException)
		{
			_logger.LogWarning(
				ex,
				"Ignoring a registered copy whose GitHub repository or current working line changed");
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
