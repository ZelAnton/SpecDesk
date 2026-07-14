using Microsoft.Extensions.Logging;
using SpecDesk.AppInfo;
using SpecDesk.Contracts;
using SpecDesk.Core;
using SpecDesk.Git;
using SpecDesk.GitHub;
using SpecDesk.Markdown;
using System.Text;
// LibGit2Sharp is referenced only for its exception type; do not bring the whole namespace in (it
// defines a LogLevel that collides with Microsoft.Extensions.Logging.LogLevel).
using LibGit2SharpException = LibGit2Sharp.LibGit2SharpException;

namespace SpecDesk.Host;

// The document/editing-session slice of HostController: opening/saving, entering and discarding a
// draft, disk autosave, saving versions, image paste, compare, and the load/line-ending helpers.
// The shared fields, locks, constructor, and the IPC router live in HostController.cs.
public sealed partial class HostController
{
	private const int MaxPreviewBytes = 4 * 1024 * 1024;
	private void OnReady()
	{
		// Only the first "ready" may auto-load the initial document — see _initialDocLoadAttempted.
		if (!_initialDocLoadAttempted)
		{
			_initialDocLoadAttempted = true;
			if (_initialDocPath is not null && File.Exists(_initialDocPath))
			{
				// The auto-opened welcome doc is not a user choice — don't put it in "recent".
				LoadFile(_initialDocPath, recordRecent: false);
			}
		}

		// Tell the webview the current GitHub connection so it can render (or hide) the account affordance.
		SendCurrentAccount();
	}

	private void OnEditorChanged(IpcMessage message)
	{
		EditorChangedPayload? payload = SafeGetPayload<EditorChangedPayload>(message);
		if (payload is null)
		{
			return;
		}

		bool announce = false;
		lock (_sync)
		{
			if (_disposed
				|| _remoteDocument is not null
				|| _currentPath is null
				|| _closePreparationClaimed
				|| _documentMutationLeaseClaimed
				|| _documentOpenTransition
				|| _documentRepositoryTransition
				|| _documentDiscardTransition)
			{
				return;
			}

			long version = message.Version ?? 0;
			if (!_coordinator.ShouldRender(version))
			{
				return;
			}

			long draftGeneration = Interlocked.Read(ref _draftGeneration);
			DraftSession session = _session;
			bool editing = _repoRoot is not null && IsEditingState(session.State);
			_text = payload.Text;
			Interlocked.Increment(ref _contentGeneration);
			_session = session with
			{
				Generation = draftGeneration,
				Dirty = editing || session.Dirty,
			};
			if (editing)
			{
				announce = !session.Dirty;
				_autosaveTimer?.Dispose();
				_autosaveTimer = new Timer(
					_ => RunDiskAutosave(), null, _autosaveIdle, Timeout.InfiniteTimeSpan);
			}
		}

		if (announce)
		{
			SendTransientStatus("Unsaved changes");
		}
	}
	/// <summary>
	/// Render <paramref name="text"/> to HTML and emit it as <see cref="MessageKinds.PreviewHtml"/>,
	/// unless a newer edit has since superseded <paramref name="version"/>
	/// (<see cref="PreviewCoordinator.ShouldEmit"/>). No longer called automatically from
	/// <see cref="OnEditorChanged"/> (see its comment) — kept `internal`, like
	/// <see cref="RunDiskAutosave"/>, so a future on-demand consumer can call it directly and a test can
	/// exercise it deterministically without resurrecting the automatic hot-path call.
	/// </summary>
	internal void RenderAndSend(string text, long version, string docDir)
	{
		Renderer.RenderResult result;
		try
		{
			result = _render(docDir, text);
		}
		catch (Exception ex)
		{
			// A parser fault must never crash whatever called this (a future on-demand caller, or a
			// test); the author sees a plain-language notice instead of a stale or broken preview —
			// but only if this render is still the newest, so a superseded failure stays silent.
			_logger.LogError(ex, "Markdown render failed (version {Version}, {Length} chars)", version, text.Length);
			if (_coordinator.ShouldEmit(version))
			{
				Emit(IpcSerializer.SerializeEvent(
					MessageKinds.Error,
					new ErrorPayload("Could not render the preview.")));
			}

			return;
		}

		if (!_coordinator.ShouldEmit(version))
		{
			return;
		}

		LineSpan[] lineMap = new LineSpan[result.LineMap.Length];
		for (int i = 0; i < lineMap.Length; i++)
		{
			lineMap[i] = new LineSpan(result.LineMap[i].LineStart, result.LineMap[i].LineEnd);
		}

		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.PreviewHtml,
			new PreviewPayload(result.Html, lineMap),
			version));
	}

	private void OnOpen(IpcMessage message)
	{
		// An explicit path (the Start screen's "open a file", or a click in the folder tree) opens directly;
		// no path falls back to the native open dialog (the toolbar "Open…"). The request id belongs to the
		// webview's edit lock: every terminal path completes it exactly once, while a remote load carries it
		// through its asynchronous/authenticated continuation.
		DocOpenPayload? payload = SafeGetPayload<DocOpenPayload>(message);
		long requestId = payload?.RequestId ?? 0;
		string? path = !string.IsNullOrWhiteSpace(payload?.Path)
			? payload.Path
			: _dialogs.PickOpenFile();
		if (path is null)
		{
			EmitDocumentOpenCompletion(requestId, succeeded: false);
			return;
		}

		bool remotePath = TryParseRemotePath(
			path, out string owner, out string name, out string branch, out string remoteDocumentPath);
		if (remotePath && (_repositoryCatalog is null || _auth is null))
		{
			SendError("Browsing repositories online isn't available in this build.");
			EmitDocumentOpenCompletion(requestId, succeeded: false);
			return;
		}
		if (remotePath && _workspace?.FindRepo($"{owner}/{name}") is null)
		{
			SendError("That repository is no longer registered.");
			EmitDocumentOpenCompletion(requestId, succeeded: false);
			return;
		}

		if (!TryBeginDocumentOpen(
			requestId,
			out bool repositoryActionInProgress,
			out DocumentMutationSnapshot? pendingDraft,
			out long navigationGeneration))
		{
			SendError(repositoryActionInProgress
				? "Repository work is still finishing. Wait a moment, then open the file again."
				: "The document is changing right now. Wait a moment, then open the file again.");
			EmitDocumentOpenCompletion(requestId, succeeded: false);
			return;
		}

		if (pendingDraft is not null
			&& !TryPersistCurrentDocumentBeforeNavigation(pendingDraft, requestId))
		{
			CompleteDocumentOpen(requestId, succeeded: false);
			return;
		}

		if (remotePath)
		{
			lock (_sync)
			{
				AcceptWorkspaceNavigationIntentLocked(navigationGeneration);
			}
			LoadRemoteFile(
				owner, name, branch, remoteDocumentPath, path, requestId, navigationGeneration);
			return;
		}

		InvalidateRemoteNavigation(browse: true, file: true);
		CompleteDocumentOpen(requestId, LoadFile(path, navigationGeneration: navigationGeneration));
	}

	// The only nested acquisition for document navigation. Repository publication uses the same
	// clone -> remote -> session order. The locks protect only the transition claim and snapshot;
	// disk/network work runs after they are released under the claimed transition lease.
	private bool TryBeginDocumentOpen(
		long requestId,
		out bool repositoryActionInProgress,
		out DocumentMutationSnapshot? pendingDraft,
		out long navigationGeneration)
	{
		lock (_clonePublishSync)
		{
			lock (_remotePublishSync)
			{
				lock (_sync)
				{
					repositoryActionInProgress = _localRepositoryActionCts is not null;
					if (_disposed
						|| _closePreparationClaimed
						|| _documentMutationLeaseClaimed
						|| _documentRepositoryTransition
						|| _documentDiscardTransition
						|| _documentOpenTransition
						|| repositoryActionInProgress)
					{
						pendingDraft = null;
						navigationGeneration = 0;
						return false;
					}

					_documentOpenTransition = true;
					navigationGeneration = ++_workspaceNavigationIntentSequence;
					_documentOpenTransitionRequestId = requestId;
					DraftSession session = _session;
					pendingDraft = _remoteDocument is null && _currentPath is not null && session.Dirty
						? new DocumentMutationSnapshot(
							_currentPath,
							_repoRoot,
							_text,
							_lineEnding,
							Interlocked.Read(ref _draftGeneration),
							Interlocked.Read(ref _contentGeneration),
							session.Branch,
							session.BaseBranch)
						: null;
					if (pendingDraft is not null)
					{
						_autosaveTimer?.Dispose();
						_autosaveTimer = null;
					}
					return true;
				}
			}
		}
	}
	private void CompleteDocumentOpen(long requestId, bool succeeded)
	{
		bool completed = false;
		lock (_sync)
		{
			if (_documentOpenTransition && _documentOpenTransitionRequestId == requestId)
			{
				completed = true;
				if (!succeeded
					&& _repoRoot is not null
					&& _currentPath is not null
					&& _session.Dirty
					&& IsEditingState(_session.State))
				{
					_autosaveTimer?.Dispose();
					_autosaveTimer = new Timer(
						_ => RunDiskAutosave(), null, _autosaveIdle, Timeout.InfiniteTimeSpan);
				}
				_documentOpenTransition = false;
				_documentOpenTransitionRequestId = 0;
			}
		}
		if (completed)
		{
			EmitDocumentOpenCompletion(requestId, succeeded);
		}
	}

	private void EmitDocumentOpenCompletion(long requestId, bool succeeded)
	{
		if (requestId <= 0)
		{
			return;
		}
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.DocOpenCompleted,
			new DocOpenCompletedPayload(requestId, succeeded)));
	}

	// The open transition owns the old document identity while this exact snapshot is written. No
	// publication monitor is held during I/O; editor, repository, discard, autosave, and close paths all
	// reject the transition lease until the new identity is published or the open is cancelled.
	private bool TryPersistCurrentDocumentBeforeNavigation(
		DocumentMutationSnapshot snapshot,
		long requestId)
	{
		try
		{
			bool stale;
			lock (_repoGate)
			{
				stale = !IsDocumentOpenSnapshotCurrentUnderRepositoryGate(snapshot, requestId);
				if (!stale)
				{
					File.WriteAllText(snapshot.Path, ApplyLineEnding(snapshot.Text, snapshot.LineEnding));
				}
			}

			if (stale || !IsDocumentOpenSnapshotCurrent(snapshot, requestId))
			{
				_logger.LogWarning(
					"Opening another document was cancelled because the current draft changed while saving {Path}",
					snapshot.Path);
				SendError("The current document changed while it was being saved. Please try opening the other file again.");
				return false;
			}

			_logger.LogDebug("Saved pending changes to {Path} before opening another document", snapshot.Path);
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_logger.LogError(ex, "Could not save pending changes to {Path} before opening another document", snapshot.Path);
			SendError("Could not save your changes, so the other document was not opened.");
			return false;
		}
	}

	// Called only while _repoGate is held; taking _sync here would invert the repository -> session order.
	private bool IsDocumentOpenSnapshotCurrentUnderRepositoryGate(
		DocumentMutationSnapshot snapshot,
		long requestId)
	{
		DraftSession session = Volatile.Read(ref _session);
		return Volatile.Read(ref _documentOpenTransition)
			&& Interlocked.Read(ref _documentOpenTransitionRequestId) == requestId
			&& !Volatile.Read(ref _disposed)
			&& !Volatile.Read(ref _documentMutationLeaseClaimed)
			&& !Volatile.Read(ref _documentRepositoryTransition)
			&& !Volatile.Read(ref _documentDiscardTransition)
			&& Volatile.Read(ref _remoteDocument) is null
			&& string.Equals(Volatile.Read(ref _currentPath), snapshot.Path, StringComparison.Ordinal)
			&& string.Equals(Volatile.Read(ref _repoRoot), snapshot.RepoRoot, StringComparison.Ordinal)
			&& Interlocked.Read(ref _draftGeneration) == snapshot.DraftGeneration
			&& Interlocked.Read(ref _contentGeneration) == snapshot.ContentGeneration
			&& string.Equals(session.Branch, snapshot.Branch, StringComparison.Ordinal)
			&& string.Equals(session.BaseBranch, snapshot.BaseBranch, StringComparison.Ordinal);
	}

	private bool IsDocumentOpenSnapshotCurrent(DocumentMutationSnapshot snapshot, long requestId)
	{
		lock (_sync)
		{
			return _documentOpenTransition
				&& _documentOpenTransitionRequestId == requestId
				&& !_disposed
				&& !_documentMutationLeaseClaimed
				&& !_documentRepositoryTransition
				&& !_documentDiscardTransition
				&& _remoteDocument is null
				&& string.Equals(_currentPath, snapshot.Path, StringComparison.Ordinal)
				&& string.Equals(_repoRoot, snapshot.RepoRoot, StringComparison.Ordinal)
				&& Interlocked.Read(ref _draftGeneration) == snapshot.DraftGeneration
				&& Interlocked.Read(ref _contentGeneration) == snapshot.ContentGeneration
				&& string.Equals(_session.Branch, snapshot.Branch, StringComparison.Ordinal)
				&& string.Equals(_session.BaseBranch, snapshot.BaseBranch, StringComparison.Ordinal);
		}
	}
	// Open a folder as the left-rail file navigator's root: an explicit path (a registered repo, or a folder
	// the Start screen offered) or the native folder-picker. Sets the workspace root and emits its tree. Does
	// NOT change the open document — the author can browse one folder while editing a document elsewhere.
	private void OnOpenFolder(IpcMessage message)
	{
		FolderOpenPayload? payload = SafeGetPayload<FolderOpenPayload>(message);
		string? path = !string.IsNullOrWhiteSpace(payload?.Path)
			? payload.Path
			: _dialogs.PickOpenFolder();
		if (path is null)
		{
			return;
		}

		if (!Directory.Exists(path))
		{
			SendError("That folder could not be opened.");
			return;
		}

		OpenWorkspaceFolder(Path.GetFullPath(path));
	}

	// Make <paramref name="root"/> the left-rail file navigator's root: publish it under _sync, record it as a
	// recent (emits workspace.state), and emit its tree. Shared by OnOpenFolder (a picked / registered folder)
	// and OnOpenRepo (a freshly cloned repo) so both surface a workspace identically. Does NOT change the open
	// document — the author can browse one folder while editing a document elsewhere.
	private void OpenWorkspaceFolder(string root)
	{
		WorkspaceRootPublication publication;
		lock (_sync)
		{
			long navigationGeneration = ++_workspaceNavigationIntentSequence;
			AcceptWorkspaceNavigationIntentLocked(navigationGeneration);
			_workspaceRoot = root;
			publication = new WorkspaceRootPublication(
				root, ++_workspaceRootGeneration, navigationGeneration);
		}
		PublishWorkspaceFolder(publication);
	}

	// Publish the already-committed workspace root. Repository opens set the root in the same publication
	// lease as their registration CAS, then perform the potentially slow tree read after releasing it. The
	// final token check and both outbound events are serialized with later root publications, so an older
	// asynchronous open can never overwrite a newer navigator selection.
	private void PublishWorkspaceFolder(WorkspaceRootPublication publication)
	{
		WorkspaceRootPublishingForTest?.Invoke(publication.Root);
		WorkspaceContextPayload? folderContext = TryBuildWorkspaceFolderContext(publication.Root);
		lock (_workspaceRootPublicationSync)
		{
			WorkspaceRootRemoteInvalidationStartingForTest?.Invoke(publication.Root);
			if (!TryInvalidateRemoteNavigationForWorkspacePublication(publication))
			{
				return;
			}
			TreePayload tree;
			try
			{
				tree = FileTreeBuilder.Build(publication.Root);
			}
			catch (Exception ex) when (
				ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
			{
				lock (_sync)
				{
					if (!IsWorkspaceRootPublicationCurrentLocked(publication))
					{
						return;
					}
				}
				_logger.LogError(ex, "Could not read the folder tree at {Root}", publication.Root);
				SendError("That folder could not be read.");
				return;
			}

			lock (_sync)
			{
				if (!IsWorkspaceRootPublicationCurrentLocked(publication))
				{
					return;
				}
				RecordRecent(publication.Root, isFolder: true);
				Emit(IpcSerializer.SerializeEvent(MessageKinds.Tree, tree));
				if (folderContext is not null)
				{
					SendWorkspaceContext(folderContext);
				}
			}
			_logger.LogInformation("Opened workspace folder {Root}", publication.Root);
		}
	}

	private bool IsWorkspaceRootPublicationCurrentLocked(WorkspaceRootPublication publication) =>
		!_disposed
		&& publication.Generation == _workspaceRootGeneration
		&& publication.NavigationGeneration == _workspaceNavigationIntentGeneration
		&& _workspaceRoot is not null
		&& SameFullPath(_workspaceRoot, publication.Root);

	private WorkspaceContextPayload? TryBuildWorkspaceFolderContext(string repoRoot)
	{
		try
		{
			if (!IsRepoVersioned(repoRoot))
			{
				return null;
			}
			string? branch;
			string branchState;
			string? defaultBranch;
			string repository = Path.GetFileName(Path.TrimEndingDirectorySeparator(repoRoot));
			string? configured = WorkflowConfig.defaultBaseForHost(WorkflowSeeds.TryReadRepoToml(repoRoot));
			lock (_repoGate)
			{
				CurrentBranchInfo current = _versioning.DescribeCurrentBranch(repoRoot);
				branch = current.Name;
				branchState = current.IsDetached ? "detached" : current.Name is null ? "unavailable" : "named";
				defaultBranch = _versioning.DefaultBranch(repoRoot, configured);
				GitHubRepo? remote = _publishing is null
					? null
					: GitHubRemote.TryParse(_publishing.RemoteUrl(repoRoot));
				if (remote is not null)
				{
					repository = $"{remote.Owner}/{remote.Name}";
				}
			}
			return new WorkspaceContextPayload(
				repository, repoRoot, branch, branchState, defaultBranch, string.Empty);
		}
		catch (Exception ex) when (ex is LibGit2SharpException or InvalidOperationException)
		{
			_logger.LogWarning(ex, "Could not read workspace repository context for {Root}", repoRoot);
			return null;
		}
	}

	// Serve one Folder-panel level. An explicit path is accepted only below the current workspace (or the
	// open document's folder when no workspace exists); otherwise the webview could enumerate arbitrary disk
	// locations. A request without a path uses that same authoritative root.
	private void OnTreeRequest(IpcMessage message)
	{
		TreeRequestPayload? payload = SafeGetPayload<TreeRequestPayload>(message);
		if (!string.IsNullOrWhiteSpace(payload?.Path)
			&& TryRequestRemoteLevel(payload.Path, payload.RequestId))
		{
			return;
		}
		InvalidateRemoteNavigation(browse: true, file: false);
		string? root;
		WorkspaceTreeRequestPublication? publication = null;
		if (!string.IsNullOrWhiteSpace(payload?.Path))
		{
			string? authorizedRoot;
			lock (_sync)
			{
				authorizedRoot = _workspaceRoot
					?? (_currentPath is not null ? Path.GetDirectoryName(_currentPath) : null);
			}
			try
			{
				root = Path.GetFullPath(payload.Path);
				string? authorizedFull = authorizedRoot is null ? null : Path.GetFullPath(authorizedRoot);
				if (authorizedFull is null
					|| (!SameFullPath(authorizedFull, root) && !AppAssetResolver.IsInside(
						Path.TrimEndingDirectorySeparator(authorizedFull), root))
					|| AppAssetResolver.HasReparseTraversal(authorizedFull, root))
				{
					Emit(IpcSerializer.SerializeEvent(
						MessageKinds.Tree, new TreePayload(payload.Path, [], payload.RequestId)));
					return;
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
				or ArgumentException or NotSupportedException)
			{
				_logger.LogWarning(ex, "Rejected an invalid Folder tree path");
				SendError("That folder could not be read.");
				return;
			}
		}
		else
		{
			lock (_sync)
			{
				root = _workspaceRoot ?? (_currentPath is not null ? Path.GetDirectoryName(_currentPath) : null);
				publication = new WorkspaceTreeRequestPublication(
					_workspaceRoot, _currentPath, _workspaceRootGeneration);
			}
			WorkspaceTreeRequestCapturedForTest?.Invoke(root ?? string.Empty);
		}

		if (string.IsNullOrEmpty(root))
		{
			// Nothing to show yet (no folder opened, no document loaded) — an empty tree, not an error.
			TreePayload emptyTree = new(string.Empty, [], payload?.RequestId ?? 0);
			if (publication is { } emptyPublication)
			{
				EmitWorkspaceTreeRequest(emptyTree, emptyPublication);
			}
			else
			{
				Emit(IpcSerializer.SerializeEvent(MessageKinds.Tree, emptyTree));
			}
			return;
		}

		EmitTree(root, payload?.RequestId ?? 0, publication);
	}

	private void EmitTree(
		string root,
		long requestId = 0,
		WorkspaceTreeRequestPublication? publication = null)
	{
		TreePayload tree;
		try
		{
			tree = FileTreeBuilder.Build(root, requestId);
		}
		catch (Exception ex) when (
			ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			// ArgumentException/NotSupportedException come from Path.GetFullPath on a malformed root (an
			// embedded null, invalid syntax) — which tree.request's explicit-path branch does not pre-check
			// with Directory.Exists. Turn any of these into a plain error rather than a silent dead request.
			if (publication is { } failedPublication)
			{
				lock (_workspaceRootPublicationSync)
				{
					lock (_sync)
					{
						if (!IsWorkspaceTreeRequestCurrentLocked(failedPublication))
						{
							return;
						}
					}
					_logger.LogError(ex, "Could not read the folder tree at {Root}", root);
					SendError("That folder could not be read.");
				}
				return;
			}
			_logger.LogError(ex, "Could not read the folder tree at {Root}", root);
			SendError("That folder could not be read.");
			return;
		}

		if (publication is { } currentPublication)
		{
			EmitWorkspaceTreeRequest(tree, currentPublication);
			return;
		}

		Emit(IpcSerializer.SerializeEvent(MessageKinds.Tree, tree));
	}

	private void EmitWorkspaceTreeRequest(
		TreePayload tree,
		WorkspaceTreeRequestPublication publication)
	{
		lock (_workspaceRootPublicationSync)
		{
			lock (_sync)
			{
				if (!IsWorkspaceTreeRequestCurrentLocked(publication))
				{
					return;
				}
				Emit(IpcSerializer.SerializeEvent(MessageKinds.Tree, tree));
			}
		}
	}

	private bool IsWorkspaceTreeRequestCurrentLocked(WorkspaceTreeRequestPublication publication) =>
		!_disposed
		&& publication.Generation == _workspaceRootGeneration
		&& ((publication.Root is null && _workspaceRoot is null)
			|| (publication.Root is not null
				&& _workspaceRoot is not null
				&& SameFullPath(publication.Root, _workspaceRoot)))
		&& (publication.Root is not null
			|| string.Equals(publication.DocumentPath, _currentPath, StringComparison.OrdinalIgnoreCase));

	/// <summary>Claim the close boundary only when no local repository mutation is active, then synchronously
	/// persist the current local draft before the native window is allowed to close. A failed or stale write
	/// releases the claim and fails closed, so the author can retry after the operation or storage fault ends.</summary>
	internal bool TryPersistPendingLocalDraftForClose()
	{
		bool repositoryActionInProgress;
		bool documentMutationInProgress;
		lock (_clonePublishSync)
		{
			lock (_sync)
			{
				if (_disposed)
				{
					return false;
				}
				repositoryActionInProgress = _cloneCts is not null
					|| _localRepositoryActionCts is not null;
				documentMutationInProgress = _documentMutationLeaseClaimed
					|| _documentOpenTransition
					|| _documentRepositoryTransition
					|| _documentDiscardTransition;
				if (!repositoryActionInProgress && !documentMutationInProgress)
				{
					_closePreparationClaimed = true;
				}
			}
		}

		if (repositoryActionInProgress)
		{
			SendError("SpecDesk is still finishing repository work. Wait a moment, then close again.");
			return false;
		}
		if (documentMutationInProgress)
		{
			SendError("SpecDesk is still finishing the current document. Wait a moment, then close again.");
			return false;
		}

		bool succeeded = false;
		try
		{
			succeeded = TryPersistPendingLocalDraftForCloseCore();
			return succeeded;
		}
		finally
		{
			if (!succeeded)
			{
				ReleaseClosePreparationClaim();
			}
		}
	}

	private void ReleaseClosePreparationClaim()
	{
		lock (_clonePublishSync)
		{
			lock (_sync)
			{
				if (!_disposed)
				{
					_closePreparationClaimed = false;
				}
			}
		}
	}

	private bool TryPersistPendingLocalDraftForCloseCore()
	{
		string? path;
		lock (_sync)
		{
			DraftSession session = _session;
			if (_remoteDocument is not null || _currentPath is null || !session.Dirty)
			{
				return true;
			}
			path = _currentPath;
		}
		if (!TryClaimDocumentMutation(
			path,
			requireRepository: false,
			requireEditing: false,
			assignPathWhenMissing: false,
			out DocumentMutationSnapshot snapshot,
			allowClosePreparation: true))
		{
			SendError("The document is changing right now. Please try closing again.");
			return false;
		}
		lock (_sync)
		{
			_autosaveTimer?.Dispose();
			_autosaveTimer = null;
		}

		try
		{
			bool stale;
			lock (_repoGate)
			{
				stale = !IsDocumentMutationCurrentUnderRepositoryGate(snapshot);
				if (!stale)
				{
					File.WriteAllText(snapshot.Path, ApplyLineEnding(snapshot.Text, snapshot.LineEnding));
				}
			}

			if (stale || !IsDocumentMutationCurrent(snapshot))
			{
				_logger.LogWarning(
					"Window close was cancelled because the draft identity changed while saving {Path}",
					snapshot.Path);
				MarkDirtyAndScheduleDiskAutosave();
				SendError("The document changed while it was being saved. Please try closing again.");
				return false;
			}

			_logger.LogDebug("Saved pending changes to {Path} before closing", snapshot.Path);
			return true;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Could not save pending changes to {Path} before closing", snapshot.Path);
			MarkDirtyAndScheduleDiskAutosave();
			SendError("Could not save your changes, so SpecDesk stayed open.");
			return false;
		}
		finally
		{
			ReleaseDocumentMutationLease();
		}
	}

	private sealed record DocumentMutationSnapshot(
		string Path,
		string? RepoRoot,
		string Text,
		string LineEnding,
		long DraftGeneration,
		long ContentGeneration,
		string? Branch,
		string? BaseBranch);

	private bool TryClaimDocumentMutation(
		string path,
		bool requireRepository,
		bool requireEditing,
		bool assignPathWhenMissing,
		out DocumentMutationSnapshot snapshot,
		bool allowClosePreparation = false)
	{
		lock (_sync)
		{
			if (_disposed
				|| (!allowClosePreparation && _closePreparationClaimed)
				|| _remoteDocument is not null
				|| _documentMutationLeaseClaimed
				|| _documentRepositoryTransition
				|| _documentDiscardTransition
				|| _documentOpenTransition)
			{
				snapshot = null!;
				return false;
			}
			if (assignPathWhenMissing && _currentPath is null)
			{
				_currentPath = path;
			}
			DraftSession session = _session;
			if (_currentPath is null
				|| !string.Equals(_currentPath, path, StringComparison.Ordinal)
				|| (requireRepository && _repoRoot is null)
				|| (requireEditing && !IsEditingState(session.State)))
			{
				snapshot = null!;
				return false;
			}

			snapshot = new DocumentMutationSnapshot(
				_currentPath,
				_repoRoot,
				_text,
				_lineEnding,
				Interlocked.Read(ref _draftGeneration),
				Interlocked.Read(ref _contentGeneration),
				session.Branch,
				session.BaseBranch);
			_documentMutationLeaseClaimed = true;
			return true;
		}
	}

	// Called only while _repoGate is held. It deliberately performs lock-free/volatile reads: taking _sync
	// here would invert the repository→session lock order. The lease was published under _sync before the
	// caller entered _repoGate, and every identity transition must reject that lease before mutating.
	private bool IsDocumentMutationCurrentUnderRepositoryGate(DocumentMutationSnapshot snapshot)
	{
		DraftSession session = Volatile.Read(ref _session);
		return Volatile.Read(ref _documentMutationLeaseClaimed)
			&& !Volatile.Read(ref _disposed)
			&& !Volatile.Read(ref _documentRepositoryTransition)
			&& !Volatile.Read(ref _documentDiscardTransition)
			&& !Volatile.Read(ref _documentOpenTransition)
			&& Volatile.Read(ref _remoteDocument) is null
			&& string.Equals(Volatile.Read(ref _currentPath), snapshot.Path, StringComparison.Ordinal)
			&& string.Equals(Volatile.Read(ref _repoRoot), snapshot.RepoRoot, StringComparison.Ordinal)
			&& Interlocked.Read(ref _draftGeneration) == snapshot.DraftGeneration
			&& Interlocked.Read(ref _contentGeneration) == snapshot.ContentGeneration
			&& string.Equals(session.Branch, snapshot.Branch, StringComparison.Ordinal)
			&& string.Equals(session.BaseBranch, snapshot.BaseBranch, StringComparison.Ordinal);
	}

	private bool IsDocumentMutationCurrent(DocumentMutationSnapshot snapshot)
	{
		lock (_sync)
		{
			return _documentMutationLeaseClaimed
				&& !_disposed
				&& !_documentRepositoryTransition
				&& !_documentDiscardTransition
				&& !_documentOpenTransition
				&& _remoteDocument is null
				&& string.Equals(_currentPath, snapshot.Path, StringComparison.Ordinal)
				&& string.Equals(_repoRoot, snapshot.RepoRoot, StringComparison.Ordinal)
				&& Interlocked.Read(ref _draftGeneration) == snapshot.DraftGeneration
				&& Interlocked.Read(ref _contentGeneration) == snapshot.ContentGeneration
				&& string.Equals(_session.Branch, snapshot.Branch, StringComparison.Ordinal)
				&& string.Equals(_session.BaseBranch, snapshot.BaseBranch, StringComparison.Ordinal);
		}
	}

	private void ReleaseDocumentMutationLease()
	{
		lock (_sync)
		{
			_documentMutationLeaseClaimed = false;
		}
	}

	// "Save" writes the working copy to disk — it never commits. Committing a version is the explicit
	// "Save a version" action (OnSaveVersion). For a versioned draft this is the same disk write the
	// idle autosave performs; for a plain (unversioned) file it is the ordinary file save.
	private void OnSave()
	{
		string? path;
		lock (_sync)
		{
			path = _currentPath;
		}
		path ??= _dialogs.PickSaveFile(null);
		if (path is null)
		{
			return;
		}

		if (!TryClaimDocumentMutation(
			path,
			requireRepository: false,
			requireEditing: false,
			assignPathWhenMissing: true,
			out DocumentMutationSnapshot snapshot))
		{
			SendError("The document is changing right now. Wait a moment, then save again.");
			return;
		}

		try
		{
			bool stale = false;
			lock (_repoGate)
			{
				if (!IsDocumentMutationCurrentUnderRepositoryGate(snapshot))
				{
					stale = true;
				}
				else
				{
					File.WriteAllText(path, ApplyLineEnding(snapshot.Text, snapshot.LineEnding));
				}
			}
			if (stale)
			{
				SendError("The document changed before it could be saved. Try again.");
				return;
			}

			if (!IsDocumentMutationCurrent(snapshot))
			{
				SendError("The document changed while it was being saved. Check the open document and try again.");
				return;
			}
			_logger.LogInformation("Saved {Path} to disk ({Length} chars)", path, snapshot.Text.Length);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_logger.LogError(ex, "Could not save {Path}", path);
			SendError("Could not save the file.");
		}
		finally
		{
			ReleaseDocumentMutationLease();
		}
	}
	// "Edit": fork a working branch and enter Draft. The author names the draft (branch) in a prompt
	// on the webview side; an empty name falls back to the generated one. Until this runs the editor
	// is read-only — editing is only possible once a branch exists.
	private sealed record DocumentEditTransition(
		string Path,
		string RepoRoot,
		DraftSession Session,
		long DraftGeneration,
		long ContentGeneration);

	private void OnEdit(IpcMessage message)
	{
		if (!TryBeginDocumentEdit(
			out DocumentEditTransition? transition,
			out string next,
			out string? rejection))
		{
			if (rejection is not null)
			{
				SendError(rejection);
			}
			return;
		}

		DocumentEditTransition activeTransition = transition!;
		EditPayload? payload = SafeGetPayload<EditPayload>(message);
		bool mutationStarted = false;
		long mutationGeneration = 0;
		try
		{
			string? toml = WorkflowSeeds.TryReadRepoToml(activeTransition.RepoRoot);
			string docSlug = WorkflowSeeds.DocSlug(activeTransition.Path);
			// Prefer the author's chosen draft name (sanitized to a valid ref); else the generated one.
			string sanitized = WorkflowSeeds.SanitizeBranchName(payload?.BranchName);
			string branchName = sanitized.Length > 0
				? sanitized
				: WorkflowConfig.branchNameForHost(toml, docSlug, DateTimeOffset.Now);
			string baseBranch = WorkflowConfig.defaultBaseForHost(toml);
			EditSession? editSession = null;
			string? checkedOutText = null;
			bool versioned;
			lock (_repoGate)
			{
				if (!IsDocumentEditTransitionCurrentUnderRepositoryGate(activeTransition))
				{
					throw new InvalidOperationException("The open document changed before editing could start.");
				}
				versioned = _versioning.IsVersioned(activeTransition.RepoRoot);
				if (versioned)
				{
					editSession = _versioning.BeginEdit(
						activeTransition.RepoRoot,
						branchName,
						baseBranch,
						onMutationStarting: () =>
						{
							mutationGeneration = Interlocked.Increment(ref _draftGeneration);
							mutationStarted = true;
						});
					if (!mutationStarted)
					{
						throw new InvalidOperationException("Editing did not report its working-line change.");
					}
					checkedOutText = ReadBoundedUtf8File(activeTransition.Path);
				}
			}

			if (!versioned)
			{
				CancelDocumentEditTransition(ref transition);
				SendError("This folder isn't set up for versioning yet.");
				return;
			}

			bool published;
			lock (_sync)
			{
				published = IsDocumentEditTransitionCurrentAfterMutation(activeTransition, mutationGeneration);
				if (published)
				{
					_text = checkedOutText!;
					Interlocked.Increment(ref _contentGeneration);
					_lineEnding = DetectLineEnding(checkedOutText!);
					_session = new DraftSession(
						next,
						editSession!.Branch,
						editSession.BaseBranch,
						Dirty: false,
						VersionsSaved: 0,
						VersionsShared: 0,
						Generation: mutationGeneration);
				}
			}
			if (!published)
			{
				RetireDocumentEditTransitionAfterMutation(ref transition);
				SendError("The working line changed, but the document could not be opened safely. The document was closed to protect the local files.");
				return;
			}
			_logger.LogInformation(
				"Editing {Doc} on branch {Branch} (base {Base})", docSlug, editSession!.Branch, editSession.BaseBranch);
			Emit(IpcSerializer.SerializeEvent(
				MessageKinds.DocLoaded,
				new DocLoadedPayload(activeTransition.Path, checkedOutText!, DocRelativeDir())));
			SendLifecycleStatus();
			lock (_sync)
			{
				_documentRepositoryTransition = false;
			}
			transition = null;
		}
		catch (DirtyWorkingTreeException ex)
		{
			if (mutationStarted)
			{
				RetireDocumentEditTransitionAfterMutation(ref transition);
			}
			else
			{
				CancelDocumentEditTransition(ref transition);
			}
			// Another document's draft was autosaved to disk but never saved as a version, and a forced
			// checkout here would have silently wiped it — BeginEdit refused instead. Plain-language,
			// no git vocabulary (branch names stay in the log, not the message the author sees).
			_logger.LogError(ex, "Could not start editing: {DirtyBranch} has unsaved autosaved changes", ex.DirtyBranch);
			SendError("Another document has unsaved changes. Open it and save or discard that draft, then try again.");
		}
		catch (ProtectedLocalFileException ex)
		{
			CancelDocumentEditTransition(ref transition);
			_logger.LogWarning(ex, "Could not start editing because local path {Path} would be overwritten", ex.FilePath);
			SendError("A protected local file is in the way. Move or rename it, then start editing again.");
		}
		catch (Exception ex) when (
			ex is LibGit2SharpException
				or InvalidOperationException
				or IOException
				or UnauthorizedAccessException
				or ArgumentException
				or FormatException)
		{
			if (mutationStarted)
			{
				RetireDocumentEditTransitionAfterMutation(ref transition);
			}
			else
			{
				CancelDocumentEditTransition(ref transition);
			}
			_logger.LogError(ex, "Could not start editing");
			SendError(mutationStarted
				? "The working line changed, but editing could not be finished. The document was closed to protect the local files."
				: "Could not start editing this document.");
		}
		finally
		{
			if (mutationStarted)
			{
				RetireDocumentEditTransitionAfterMutation(ref transition);
			}
			else
			{
				CancelDocumentEditTransition(ref transition);
			}
		}
	}

	private bool TryBeginDocumentEdit(
		out DocumentEditTransition? transition,
		out string next,
		out string? rejection)
	{
		lock (_clonePublishSync)
		{
			lock (_remotePublishSync)
			{
				lock (_sync)
				{
					DraftSession session = _session;
					next = Lifecycle.tryStep(session.State, "edit");
					if (_repoRoot is null || _currentPath is null || _remoteDocument is not null)
					{
						transition = null;
						rejection = "Open a document before editing.";
						return false;
					}
					if (next.Length == 0)
					{
						_logger.LogDebug("Edit ignored from state {State}", session.State);
						transition = null;
						rejection = null;
						return false;
					}
					bool repositoryActionInProgress = _localRepositoryActionCts is not null;
					if (_disposed
						|| _closePreparationClaimed
						|| _documentMutationLeaseClaimed
						|| _documentOpenTransition
						|| _documentRepositoryTransition
						|| _documentDiscardTransition
						|| _publishInFlight
						|| repositoryActionInProgress)
					{
						transition = null;
						rejection = repositoryActionInProgress
							? "Repository work is still finishing. Wait a moment, then start editing again."
							: "The document is changing right now. Wait a moment, then start editing again.";
						return false;
					}

					_documentRepositoryTransition = true;
					_autosaveTimer?.Dispose();
					_autosaveTimer = null;
					transition = new DocumentEditTransition(
						_currentPath,
						_repoRoot,
						session,
						Interlocked.Read(ref _draftGeneration),
						Interlocked.Read(ref _contentGeneration));
					rejection = null;
					return true;
				}
			}
		}
	}

	private bool IsDocumentEditTransitionCurrentUnderRepositoryGate(DocumentEditTransition transition)
	{
		return Volatile.Read(ref _documentRepositoryTransition)
			&& !Volatile.Read(ref _disposed)
			&& !Volatile.Read(ref _documentMutationLeaseClaimed)
			&& !Volatile.Read(ref _documentOpenTransition)
			&& !Volatile.Read(ref _documentDiscardTransition)
			&& Volatile.Read(ref _localRepositoryActionCts) is null
			&& Volatile.Read(ref _remoteDocument) is null
			&& string.Equals(Volatile.Read(ref _currentPath), transition.Path, StringComparison.Ordinal)
			&& string.Equals(Volatile.Read(ref _repoRoot), transition.RepoRoot, StringComparison.Ordinal)
			&& ReferenceEquals(Volatile.Read(ref _session), transition.Session)
			&& Interlocked.Read(ref _draftGeneration) == transition.DraftGeneration
			&& Interlocked.Read(ref _contentGeneration) == transition.ContentGeneration;
	}

	private bool IsDocumentEditTransitionCurrentAfterMutation(
		DocumentEditTransition transition,
		long mutationGeneration)
	{
		return _documentRepositoryTransition
			&& !_disposed
			&& !_documentMutationLeaseClaimed
			&& !_documentOpenTransition
			&& !_documentDiscardTransition
			&& _localRepositoryActionCts is null
			&& _remoteDocument is null
			&& string.Equals(_currentPath, transition.Path, StringComparison.Ordinal)
			&& string.Equals(_repoRoot, transition.RepoRoot, StringComparison.Ordinal)
			&& ReferenceEquals(_session, transition.Session)
			&& Interlocked.Read(ref _draftGeneration) == mutationGeneration
			&& Interlocked.Read(ref _contentGeneration) == transition.ContentGeneration;
	}

	private void CancelDocumentEditTransition(ref DocumentEditTransition? transition)
	{
		if (transition is null)
		{
			return;
		}
		lock (_sync)
		{
			_documentRepositoryTransition = false;
		}
		transition = null;
	}

	private void RetireDocumentEditTransitionAfterMutation(ref DocumentEditTransition? transition)
	{
		if (transition is null)
		{
			return;
		}
		lock (_sync)
		{
			Interlocked.Increment(ref _draftGeneration);
			ClearActiveDocumentStateLocked();
			_documentRepositoryTransition = false;
		}
		DocumentRetirementStateClearedForTest?.Invoke();
		transition = null;
		PublishActiveDocumentCleared();
	}

	// "Discard": abandon the draft — drop to the base branch, delete the working branch, and reload
	// the document from disk so the editor reflects the published version again.
	private void OnDiscard(IpcMessage message)
	{
		DocDiscardPayload? payload = SafeGetPayload<DocDiscardPayload>(message);
		long requestId = payload?.RequestId ?? 0;
		bool succeeded = false;
		try
		{
			succeeded = TryDiscardCurrentDraft();
		}
		finally
		{
			CompleteDocumentDiscard(requestId, succeeded);
		}
	}

	private sealed record DocumentDiscardTransition(
		string RepoRoot,
		string Path,
		string Branch,
		string BaseBranch,
		string Text,
		string LineEnding,
		DraftSession Session,
		long DraftGeneration,
		long ContentGeneration);

	private bool TryDiscardCurrentDraft()
	{
		// Gate the lifecycle transition AND the publish-in-flight check atomically under _sync, matching
		// OnSendForReview/OnUpdateReview's discipline. Once claimed, reject every editor.changed until the
		// checkout either publishes its reloaded identity or restores the old editable session.
		DocumentDiscardTransition transition;
		long discardGeneration;
		lock (_sync)
		{
			DraftSession session = _session;
			string next = Lifecycle.tryStep(session.State, "discard");
			if (next.Length == 0 || _documentDiscardTransition)
			{
				return false;
			}
			if (_closePreparationClaimed
				|| _documentMutationLeaseClaimed
				|| _documentRepositoryTransition
				|| _documentOpenTransition)
			{
				_logger.LogDebug("Discard ignored: the document is still being saved or changed");
				SendError("The document is changing right now. Wait a moment, then discard the draft again.");
				return false;
			}

			if (_publishInFlight)
			{
				_logger.LogDebug("Discard ignored: a review publish is in flight");
				return false;
			}

			string? repoRoot = _repoRoot;
			string? path = _currentPath;
			string? branch = session.Branch;
			string? baseBranch = session.BaseBranch;
			if (repoRoot is null || path is null || branch is null || baseBranch is null)
			{
				return false;
			}

			_documentDiscardTransition = true;
			// Pause the timer, but keep Dirty=true until checkout AND reload have both succeeded. Clearing it
			// here made a failed discard look safely persisted to the close handshake.
			_autosaveTimer?.Dispose();
			_autosaveTimer = null;
			transition = new DocumentDiscardTransition(
				repoRoot,
				path,
				branch,
				baseBranch,
				_text,
				_lineEnding,
				session,
				Interlocked.Read(ref _draftGeneration),
				Interlocked.Read(ref _contentGeneration));
			discardGeneration = transition.DraftGeneration;
		}

		try
		{
			string revertedText;
			lock (_repoGate)
			{
				// Keep the working branch ref until the published file has been reloaded. If that read fails,
				// RestoreDiscardFailure can check the same draft back out before autosave resumes; deleting the
				// branch first would leave no safe destination for the retained in-memory text.
				_versioning.BeginDiscard(transition.RepoRoot, transition.Branch, transition.BaseBranch);
				discardGeneration = Interlocked.Increment(ref _draftGeneration);
				revertedText = File.ReadAllText(transition.Path);
				_versioning.CompleteDiscard(
					transition.RepoRoot, transition.Branch, transition.BaseBranch);
			}

			lock (_sync)
			{
				_text = revertedText;
				Interlocked.Increment(ref _contentGeneration);
				_lineEnding = DetectLineEnding(revertedText);
				_session = new DraftSession(
					Lifecycle.stateName(Lifecycle.State.Published),
					Branch: null,
					BaseBranch: null,
					Dirty: false,
					VersionsSaved: 0,
					VersionsShared: 0,
					Generation: Interlocked.Read(ref _draftGeneration));
				_documentDiscardTransition = false;
			}

			_logger.LogInformation("Discarded draft on {Branch}", transition.Branch);
			Emit(IpcSerializer.SerializeEvent(
				MessageKinds.DocLoaded,
				new DocLoadedPayload(transition.Path, revertedText, DocRelativeDir())));
			SendWorkspaceContext();
			SendLifecycleStatus();
			return true;
		}
		catch (LibGit2SharpException ex)
		{
			_logger.LogError(ex, "Could not discard draft");
			bool restored = RestoreDiscardFailure(transition, discardGeneration);
			SendDiscardFailure(restored);
			return false;
		}
		catch (ProtectedLocalFileException ex)
		{
			_logger.LogWarning(ex, "Could not discard because local path {Path} would be overwritten", ex.FilePath);
			bool restored = RestoreDiscardFailure(transition, discardGeneration);
			SendError(restored
				? "A protected local file is in the way. Move or rename it, then discard the draft again."
				: "Could not restore your draft safely, so the document was closed.");
			return false;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_logger.LogError(ex, "Could not discard or reload draft {Path}", transition.Path);
			bool restored = RestoreDiscardFailure(transition, discardGeneration);
			SendDiscardFailure(restored);
			return false;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Unexpected failure discarding draft");
			bool restored = RestoreDiscardFailure(transition, discardGeneration);
			SendDiscardFailure(restored);
			return false;
		}
	}

	private bool RestoreDiscardFailure(
		DocumentDiscardTransition transition,
		long restoreGeneration)
	{
		bool identityRestored = false;
		bool restoreMutationStarted = false;
		try
		{
			lock (_repoGate)
			{
				if (!IsDocumentDiscardTransitionCurrentUnderRepositoryGate(
					transition, restoreGeneration))
				{
					throw new InvalidOperationException(
						"The document changed before its draft working line could be restored.");
				}
				string? currentBranch = _versioning.CurrentBranch(transition.RepoRoot);
				if (!string.Equals(currentBranch, transition.Branch, StringComparison.Ordinal))
				{
					_versioning.BeginEdit(
						transition.RepoRoot,
						transition.Branch,
						transition.BaseBranch,
						onMutationStarting: () =>
						{
							restoreGeneration = Interlocked.Increment(ref _draftGeneration);
							restoreMutationStarted = true;
						});
					if (!restoreMutationStarted)
					{
						throw new InvalidOperationException(
							"Restoring the draft did not report its working-line change.");
					}
				}
				identityRestored = string.Equals(
					_versioning.CurrentBranch(transition.RepoRoot),
					transition.Branch,
					StringComparison.Ordinal);
				if (!identityRestored)
				{
					throw new InvalidOperationException("The draft working line was not restored.");
				}
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(
				ex,
				"Could not restore draft branch {Branch} after discard failed (mutation started: {MutationStarted})",
				transition.Branch,
				restoreMutationStarted);
		}

		if (!identityRestored)
		{
			RetireFailedDiscardRestoration();
			return false;
		}

		bool published;
		bool reschedule = false;
		lock (_sync)
		{
			published = IsDocumentDiscardTransitionCurrent(transition, restoreGeneration);
			if (published)
			{
				_session = _session with { Generation = Interlocked.Read(ref _draftGeneration) };
				_documentDiscardTransition = false;
				reschedule = _session.Dirty;
			}
		}
		if (!published)
		{
			RetireFailedDiscardRestoration();
			return false;
		}
		if (reschedule)
		{
			MarkDirtyAndScheduleDiskAutosave();
		}
		return true;
	}

	private bool IsDocumentDiscardTransitionCurrentUnderRepositoryGate(
		DocumentDiscardTransition transition,
		long generation)
	{
		return Volatile.Read(ref _documentDiscardTransition)
			&& !Volatile.Read(ref _disposed)
			&& !Volatile.Read(ref _documentMutationLeaseClaimed)
			&& !Volatile.Read(ref _documentOpenTransition)
			&& !Volatile.Read(ref _documentRepositoryTransition)
			&& Volatile.Read(ref _remoteDocument) is null
			&& string.Equals(Volatile.Read(ref _currentPath), transition.Path, StringComparison.Ordinal)
			&& string.Equals(Volatile.Read(ref _repoRoot), transition.RepoRoot, StringComparison.Ordinal)
			&& ReferenceEquals(Volatile.Read(ref _session), transition.Session)
			&& string.Equals(Volatile.Read(ref _text), transition.Text, StringComparison.Ordinal)
			&& string.Equals(Volatile.Read(ref _lineEnding), transition.LineEnding, StringComparison.Ordinal)
			&& Interlocked.Read(ref _draftGeneration) == generation
			&& Interlocked.Read(ref _contentGeneration) == transition.ContentGeneration;
	}

	private bool IsDocumentDiscardTransitionCurrent(
		DocumentDiscardTransition transition,
		long generation)
	{
		return _documentDiscardTransition
			&& !_disposed
			&& !_documentMutationLeaseClaimed
			&& !_documentOpenTransition
			&& !_documentRepositoryTransition
			&& _remoteDocument is null
			&& string.Equals(_currentPath, transition.Path, StringComparison.Ordinal)
			&& string.Equals(_repoRoot, transition.RepoRoot, StringComparison.Ordinal)
			&& ReferenceEquals(_session, transition.Session)
			&& string.Equals(_text, transition.Text, StringComparison.Ordinal)
			&& string.Equals(_lineEnding, transition.LineEnding, StringComparison.Ordinal)
			&& Interlocked.Read(ref _draftGeneration) == generation
			&& Interlocked.Read(ref _contentGeneration) == transition.ContentGeneration;
	}

	private void RetireFailedDiscardRestoration()
	{
		lock (_sync)
		{
			Interlocked.Increment(ref _draftGeneration);
			ClearActiveDocumentStateLocked();
			_documentDiscardTransition = false;
		}
		DocumentRetirementStateClearedForTest?.Invoke();
		PublishActiveDocumentCleared();
	}

	private void SendDiscardFailure(bool restored)
	{
		SendError(restored
			? "Could not discard your draft. Your unsaved changes are still protected."
			: "The working line changed, but the draft could not be reopened safely. The document was closed to protect the local files.");
	}

	private void CompleteDocumentDiscard(long requestId, bool succeeded)
	{
		if (requestId <= 0)
		{
			return;
		}
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.DocDiscardCompleted,
			new DocDiscardCompletedPayload(requestId, succeeded)));
	}
	// Whether the given lifecycle state is one in which the document is being edited on a working
	// branch (so disk autosave should run and "Save a version" is allowed). Takes the state explicitly so
	// callers pass the value from a session snapshot they already read, rather than re-reading _session.
	private static bool IsEditingState(string state) => Lifecycle.tryStep(state, "saveVersion").Length > 0;

	// Whether the repo is set up for versioning — a libgit2 read, so it is serialized under _repoGate like
	// every other repository access (a background push / image insert may be driving libgit2 concurrently).
	private bool IsRepoVersioned(string repoRoot)
	{
		lock (_repoGate)
		{
			return _versioning.IsVersioned(repoRoot);
		}
	}

	// (Re)arm the idle disk-autosave timer after an edit, and flip the status to "Unsaved changes"
	// on the first dirty change. "Dirty" means the working copy differs from the last saved version;
	// it stays dirty across disk autosaves and only clears when the author saves a version.
	private void MarkDirtyAndScheduleDiskAutosave()
	{
		bool announce = false;
		lock (_sync)
		{
			DraftSession session = _session;
			if (_repoRoot is null || _currentPath is null || !IsEditingState(session.State))
			{
				return;
			}

			if (!session.Dirty)
			{
				_session = session with { Dirty = true };
				announce = true;
			}

			_autosaveTimer?.Dispose();
			_autosaveTimer = new Timer(_ => RunDiskAutosave(), null, _autosaveIdle, Timeout.InfiniteTimeSpan);
		}

		if (announce)
		{
			SendTransientStatus("Unsaved changes");
		}
	}

	// Write the in-memory text to disk so a quiet moment never loses the author's typing. This is
	// purely a disk save — it does NOT commit (committing is the explicit "Save a version") and does
	// NOT clear the dirty flag. Runs on the timer thread, so it must not throw. Internal (rather than
	// private) so a test can invoke it directly, decoupled from the real Timer, to deterministically
	// reproduce the race with a concurrent Discard this method's generation re-check guards against.
	internal void RunDiskAutosave()
	{
		string? path;
		lock (_sync)
		{
			DraftSession session = _session;
			if (_repoRoot is null || _currentPath is null || !IsEditingState(session.State))
			{
				return;
			}
			path = _currentPath;
		}
		if (!TryClaimDocumentMutation(
			path,
			requireRepository: true,
			requireEditing: true,
			assignPathWhenMissing: false,
			out DocumentMutationSnapshot snapshot))
		{
			MarkDirtyAndScheduleDiskAutosave();
			return;
		}
		lock (_sync)
		{
			_autosaveTimer?.Dispose();
			_autosaveTimer = null;
		}

		bool rearmAutosave = false;
		try
		{
			try
			{
				bool stale;
				lock (_repoGate)
				{
					stale = !IsDocumentMutationCurrentUnderRepositoryGate(snapshot);
					if (!stale)
					{
						File.WriteAllText(
							snapshot.Path,
							ApplyLineEnding(snapshot.Text, snapshot.LineEnding));
					}
				}
				if (stale || !IsDocumentMutationCurrent(snapshot))
				{
					rearmAutosave = true;
					_logger.LogDebug(
						"Disk autosave for {Path} skipped: the document content changed while this write was queued",
						snapshot.Path);
					return;
				}

				_logger.LogDebug(
					"Disk-autosaved {Path} ({Length} chars)", snapshot.Path, snapshot.Text.Length);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				rearmAutosave = true;
				_logger.LogError(ex, "Disk autosave failed for {Path}", snapshot.Path);
				SendError("Could not save your changes.");
			}
		}
		catch (Exception ex)
		{
			rearmAutosave = true;
			_logger.LogError(ex, "Unexpected fault in the disk-autosave timer callback");
		}
		finally
		{
			ReleaseDocumentMutationLease();
			if (rearmAutosave)
			{
				MarkDirtyAndScheduleDiskAutosave();
			}
		}
	}
	private void OnSaveVersion(IpcMessage message)
	{
		SaveVersionPayload? payload = SafeGetPayload<SaveVersionPayload>(message);
		string? path;
		lock (_sync)
		{
			path = _currentPath;
		}
		if (path is null
			|| !TryClaimDocumentMutation(
				path,
				requireRepository: true,
				requireEditing: true,
				assignPathWhenMissing: false,
				out DocumentMutationSnapshot snapshot))
		{
			SendError("The document is changing right now. Wait a moment, then save the version again.");
			return;
		}

		lock (_sync)
		{
			_autosaveTimer?.Dispose();
			_autosaveTimer = null;
		}
		string note = !string.IsNullOrWhiteSpace(payload?.Note)
			? payload!.Note
			: WorkflowSeeds.SuggestedVersionNote(snapshot.RepoRoot!, snapshot.Path);
		bool rearmAutosave = false;

		try
		{
			CommitResult? result = null;
			bool stale = false;
			bool versioned = true;
			lock (_repoGate)
			{
				if (!IsDocumentMutationCurrentUnderRepositoryGate(snapshot))
				{
					stale = true;
				}
				else if (!_versioning.IsVersioned(snapshot.RepoRoot!))
				{
					versioned = false;
				}
				else
				{
					File.WriteAllText(
						snapshot.Path,
						ApplyLineEnding(snapshot.Text, snapshot.LineEnding));
					result = _versioning.SaveVersion(snapshot.RepoRoot!, note);
				}
			}

			if (stale || !IsDocumentMutationCurrent(snapshot))
			{
				rearmAutosave = true;
				SendError("The document changed before this version could be saved. Check the open document and try again.");
				return;
			}
			if (!versioned)
			{
				rearmAutosave = true;
				SendError("This folder isn't set up for versioning yet.");
				return;
			}

			CommitResult saved = result!;
			bool published;
			lock (_sync)
			{
				published = _documentMutationLeaseClaimed
					&& !_disposed
					&& !_documentRepositoryTransition
					&& !_documentDiscardTransition
					&& !_documentOpenTransition
					&& _remoteDocument is null
					&& string.Equals(_currentPath, snapshot.Path, StringComparison.Ordinal)
					&& string.Equals(_repoRoot, snapshot.RepoRoot, StringComparison.Ordinal)
					&& Interlocked.Read(ref _draftGeneration) == snapshot.DraftGeneration
					&& Interlocked.Read(ref _contentGeneration) == snapshot.ContentGeneration
					&& string.Equals(_session.Branch, snapshot.Branch, StringComparison.Ordinal)
					&& string.Equals(_session.BaseBranch, snapshot.BaseBranch, StringComparison.Ordinal);
				if (published)
				{
					DraftSession session = _session;
					_session = session with
					{
						Dirty = false,
						VersionsSaved = saved.Committed ? session.VersionsSaved + 1 : session.VersionsSaved,
					};
				}
			}
			if (!published)
			{
				rearmAutosave = true;
				SendError("The document changed while this version was being saved. Check the open document before continuing.");
				return;
			}

			if (saved.Committed)
			{
				_logger.LogInformation("Saved a version of {Path} as {Sha}: {Note}", snapshot.Path, saved.Sha, note);
				SendTransientStatus("Version saved");
			}
			else
			{
				_logger.LogInformation("Save a version: nothing changed for {Path}", snapshot.Path);
				SendTransientStatus("No changes since the last version");
			}
		}
		catch (Exception ex) when (
			ex is IOException
				or UnauthorizedAccessException
				or LibGit2SharpException
				or InvalidOperationException)
		{
			rearmAutosave = true;
			_logger.LogError(ex, "Could not save a version of {Path}", snapshot.Path);
			SendError("Could not save this version.");
		}
		finally
		{
			ReleaseDocumentMutationLease();
			if (rearmAutosave)
			{
				MarkDirtyAndScheduleDiskAutosave();
			}
		}
	}
	// Reply to the webview's request for a version note to prefill the "Save a version" prompt. The
	// reply is correlated by the request id (the webview awaits it).
	private void OnSuggestVersionNote(IpcMessage message)
	{
		string? id = message.Id;
		string? repoRoot = _repoRoot;
		string? path = _currentPath;
		string note = repoRoot is not null && path is not null ? WorkflowSeeds.SuggestedVersionNote(repoRoot, path) : string.Empty;
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.VersionNoteSuggested,
			new VersionNoteSuggestedPayload(note),
			id: id));
	}

	// Reply to the webview's request for a draft (branch) name to prefill the Edit prompt. Correlated
	// by the request id (the webview awaits it).
	private void OnSuggestBranchName(IpcMessage message)
	{
		string? id = message.Id;
		string? repoRoot = _repoRoot;
		string? path = _currentPath;
		string name = repoRoot is not null && path is not null
			? WorkflowSeeds.SuggestedBranchName(repoRoot, path)
			: string.Empty;
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.BranchNameSuggested,
			new BranchNameSuggestedPayload(name),
			id: id));
	}

	private void CancelAutosave()
	{
		lock (_sync)
		{
			_autosaveTimer?.Dispose();
			_autosaveTimer = null;
			_session = _session with { Dirty = false };
			// Deliberately does NOT bump _draftGeneration: this runs before the repoGate-guarded repo
			// mutation (Discard) or before BeginEdit's checkout, well before _text is reset to match.
			// Bumping here would let a snapshot taken in the resulting gap capture the ALREADY-bumped
			// generation together with the STILL-stale _text — a torn pair that would then pass its own
			// later re-check. See OnDiscard/OnEdit: the generation bumps exactly where the repo mutation
			// and the _text reset are closest together, not here.
		}
	}

	// recordRecent: whether a successful load adds this file to the workspace "recent" list. True for a user
	// open (the toolbar/Start "Open…", a tree click); false for the initial auto-opened welcome doc, which the
	// author never chose and which would otherwise sit permanently at the top of Recent.
	private bool LoadFile(
		string path,
		bool recordRecent = true,
		string? resumedBranch = null,
		string? resumedBaseBranch = null,
		long navigationGeneration = 0)
	{
		string text;
		try
		{
			text = ReadBoundedUtf8File(path);
		}
		catch (Exception ex) when (
			ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
				or InvalidDataException)
		{
			// ArgumentException/NotSupportedException cover a malformed path (an embedded null, invalid
			// syntax): `doc.open {path}` now carries a webview-supplied path (a tree click / Start "open a
			// file"), not only an OS-dialog result, so a bad string reaches here — surface the same plain
			// "Could not open the file." rather than letting it escape to the last-resort handler unshown.
			_logger.LogError(ex, "Could not open {Path}", path);
			Emit(IpcSerializer.SerializeEvent(
				MessageKinds.Error,
				new ErrorPayload("Could not open the file.")));
			return false;
		}

		lock (_sync)
		{
			if (_disposed
				|| (navigationGeneration > 0
					&& !AcceptWorkspaceNavigationIntentLocked(navigationGeneration)))
			{
				return false;
			}
		}

		// Reset any in-progress draft this HOST OBJECT remembers so an autosave can never commit the
		// newly opened file onto some other, previously-open document's branch.
		CancelAutosave();
		string repoRoot = ResolveDocumentRepoRoot(path);
		// M-16: the FIRST document loaded in this process cannot trust its own (virgin) in-memory
		// _session — a previous session may have been force-quit / crashed / restarted mid-draft, leaving
		// a working (draft) branch checked out on disk with no in-memory record of it. Always stamping
		// Published here would lie to the author ("Published" while HEAD sits on a draft branch) and, if
		// they then clicked "Edit", would drive BeginEdit's forced checkout against a state it thinks is
		// fresh — silently resetting the very autosaved content this restores. Resolve that FIRST load's
		// lifecycle from the repo's actual checked-out branch; every later "Open" this session already
		// has an authoritative in-memory state of its own (see _lifecycleResolvedOnce).
		(string state, string? branch, string? baseBranch) = !string.IsNullOrWhiteSpace(resumedBranch)
			? (Lifecycle.stateName(Lifecycle.State.Draft), resumedBranch, resumedBaseBranch)
			: _lifecycleResolvedOnce
				? (Lifecycle.stateName(Lifecycle.State.Published), null, null)
				: ResolveInitialLifecycle(repoRoot);
		lock (_sync)
		{
			if (_disposed
				|| (navigationGeneration > 0
					&& navigationGeneration != _workspaceNavigationIntentGeneration))
			{
				return false;
			}
			// A successful load publishes a new document identity even when it reopens the same path. A review
			// push belongs to the identity that started it, so it must no longer block Edit on this replacement.
			// Retiring only its claim (rather than weakening TryBeginDocumentEdit's general publish gate) keeps
			// same-document actions single-flight. The claim id prevents the old task's terminal cleanup from
			// clearing a newer publish, while _draftGeneration prevents its result from advancing a new draft.
			RetirePublishInFlightLocked();
			_lifecycleResolvedOnce = true;
			// Publish the document fields as one matched set under the lock, so a still-running autosave
			// timer can never snapshot a torn (new path, old text) pair and write across documents.
			_text = text;
			Interlocked.Increment(ref _contentGeneration);
			_currentPath = path;
			_repoRoot = repoRoot;
			_remoteDocument = null;
			// Detected from the RAW content just read, before anything normalizes it — see the
			// _lineEnding field comment.
			_lineEnding = DetectLineEnding(text);
			// A newly loaded document is a fresh session: no LOCAL record of saved / shared versions from a
			// prior session (even when we just resumed a draft) — the counters only drive "Update review"'s
			// no-op guard and a fresh session has nothing of its own to compare against yet — and Dirty
			// false (CancelAutosave, above, already cleared it). See DraftSession.Generation: kept consistent
			// with every other _text assignment site, even though a plain document load never bumps
			// _draftGeneration itself.
			_session = new DraftSession(
				state,
				Branch: branch,
				BaseBranch: baseBranch,
				Dirty: false,
				VersionsSaved: 0,
				VersionsShared: 0,
				Generation: Interlocked.Read(ref _draftGeneration));
		}

		_logger.LogInformation("Loaded {Path} ({Length} chars); repo root {Root}", path, text.Length, repoRoot);
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.DocLoaded,
			new DocLoadedPayload(path, text, DocRelativeDir())));
		SendWorkspaceContext();

		// A4: a user-opened file just loaded successfully is now the most-recent workspace item (emits
		// workspace.state). The initial welcome doc is skipped (recordRecent false) — see the parameter.
		if (recordRecent)
		{
			RecordRecent(path, isFolder: false);
		}

		// M-16: the webview's own "doc loaded" handler always assumes a freshly loaded document is
		// Published (there being no prior in-flight lifecycle to speak of, before this fix) and renders
		// that chrome unconditionally. When this load actually resumed a draft left checked out from a
		// previous session, that assumption is wrong — follow up with the real status so the resumed
		// Draft (and its branch) actually reaches the UI, in the same order the author's own "Edit" click
		// already does (DocLoaded is never emitted by Edit, only by a fresh load). Every ordinary load
		// (Published — by far the common case) emits nothing extra here, matching prior behavior exactly.
		if (state != Lifecycle.stateName(Lifecycle.State.Published))
		{
			SendLifecycleStatus();
		}
		return true;
	}

	private static string ReadBoundedUtf8File(string path)
	{
		using FileStream stream = new(
			path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
			bufferSize: 64 * 1024, FileOptions.SequentialScan);
		byte[] buffer = new byte[MaxPreviewBytes + 1];
		int length = 0;
		while (length < buffer.Length)
		{
			int read = stream.Read(buffer, length, buffer.Length - length);
			if (read == 0)
			{
				break;
			}
			length += read;
		}
		if (length > MaxPreviewBytes || buffer.AsSpan(0, length).IndexOf((byte)0) >= 0)
		{
			throw new InvalidDataException("File is too large or is not text.");
		}

		int offset = length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
		try
		{
			return new UTF8Encoding(false, true).GetString(buffer, offset, length - offset);
		}
		catch (DecoderFallbackException ex)
		{
			throw new InvalidDataException("File is not valid UTF-8 text.", ex);
		}
	}

	// M-16: the lifecycle state / branch fields live only in this object's memory, so a restart (or
	// opening a document this process never saw before) has nothing to remember from a previous
	// session. Reconstruct the starting point from the repository's ACTUAL checked-out branch rather
	// than always assuming Published: if HEAD is sitting on some branch other than the configured
	// published base, a previous session began editing and never returned to Published (crash, force
	// quit, or a restart mid-draft) — resume as Draft on that branch so the status bar reports reality
	// and a later "Edit" click is correctly refused (Lifecycle.tryStep(Draft, "edit") is invalid) rather
	// than re-running BeginEdit's forced checkout against a document it thinks has no draft yet. This
	// only distinguishes Published vs. Draft — recovering directly into a review state (InReview /
	// ChangesRequested / Approved) would need a GitHub read, which is exactly what a subsequent
	// "refresh review status" (poll / focus) already performs once the state and branch here make the
	// document eligible for it.
	private (string State, string? Branch, string? BaseBranch) ResolveInitialLifecycle(string repoRoot)
	{
		string published = Lifecycle.stateName(Lifecycle.State.Published);
		if (!IsRepoVersioned(repoRoot))
		{
			// Not (yet) a git working tree — nothing to resume from; matches the pre-existing behavior
			// for a plain, unversioned file.
			return (published, null, null);
		}

		string? currentBranch;
		try
		{
			lock (_repoGate)
			{
				currentBranch = _versioning.CurrentBranch(repoRoot);
			}
		}
		catch (Exception ex) when (ex is LibGit2SharpException or InvalidOperationException)
		{
			// A corrupt/unreadable repo read must never block opening the document — fall back to the
			// old, safe default rather than leaving the document unopenable.
			_logger.LogWarning(ex, "Could not read the checked-out branch for {Root}; assuming Published", repoRoot);
			return (published, null, null);
		}

		string? toml = WorkflowSeeds.TryReadRepoToml(repoRoot);
		string baseBranch = WorkflowConfig.defaultBaseForHost(toml);
		// R-01: `currentBranch is null` is the documented detached-HEAD signal (see IDocumentVersioning.
		// CurrentBranch), but also reject libgit2's own "(no branch)" placeholder explicitly in case some
		// future IDocumentVersioning implementation forwards it verbatim instead of translating it to
		// null — either way there is no real branch name here to resume a draft onto or later store as
		// the session's Branch (which OnSaveVersion/OnDiscard would otherwise act on as if it were a real ref).
		if (currentBranch is null or "(no branch)" || string.Equals(currentBranch, baseBranch, StringComparison.Ordinal))
		{
			// Detached HEAD (no friendly branch to resume onto), or HEAD is already on the published
			// base — either way there is no draft left checked out to recover.
			return (published, null, null);
		}

		_logger.LogInformation(
			"Resuming a draft left checked out from a previous session: {Branch} (base {Base})",
			currentBranch, baseBranch);
		return (Lifecycle.stateName(Lifecycle.State.Draft), currentBranch, baseBranch);
	}

	private void OnImagePaste(IpcMessage message)
	{
		ImagePastePayload? payload = SafeGetPayload<ImagePastePayload>(message);
		string? id = message.Id;
		if (payload is null)
		{
			ReplyInserted(id, string.Empty);
			return;
		}

		byte[] bytes;
		try
		{
			bytes = Convert.FromBase64String(payload.Base64);
		}
		catch (FormatException)
		{
			ReplyInserted(id, string.Empty);
			return;
		}

		string? path;
		lock (_sync)
		{
			path = _currentPath;
		}
		if (path is null
			|| !TryClaimDocumentMutation(
				path,
				requireRepository: true,
				requireEditing: true,
				assignPathWhenMissing: false,
				out DocumentMutationSnapshot snapshot))
		{
			_logger.LogDebug("Image paste ignored because the document identity is changing or is read-only");
			ReplyInserted(id, string.Empty);
			return;
		}

		string? originalName = payload.OriginalName;
		string? mime = payload.Mime;
		_logger.LogInformation(
			"Image paste: {Bytes} bytes, name={Name}, mime={Mime}",
			bytes.Length,
			originalName,
			mime);
		_ = Task.Run(() =>
		{
			try
			{
				string? markdown = null;
				bool stale;
				lock (_repoGate)
				{
					stale = !IsDocumentMutationCurrentUnderRepositoryGate(snapshot);
					if (!stale)
					{
						markdown = _inserter(
							snapshot.RepoRoot!,
							snapshot.Path,
							bytes,
							originalName,
							mime);
					}
				}

				if (stale || !IsDocumentMutationCurrent(snapshot))
				{
					_logger.LogWarning("Image insert result suppressed because the document changed");
					ReplyInserted(id, string.Empty);
					return;
				}
				if (markdown is null)
				{
					_logger.LogWarning("Image insert failed (name={Name}, mime={Mime})", originalName, mime);
					Emit(IpcSerializer.SerializeEvent(
						MessageKinds.Error,
						new ErrorPayload("Could not insert the image.")));
					ReplyInserted(id, string.Empty);
					return;
				}

				_logger.LogInformation("Image inserted: {Markdown}", markdown);
				ReplyInserted(id, markdown);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Image insert task faulted (name={Name}, mime={Mime})", originalName, mime);
				ReplyInserted(id, string.Empty);
			}
			finally
			{
				ReleaseDocumentMutationLease();
			}
		});
	}
	// Compare the working copy (head) against the requested base and send the structural diff for the
	// editors to overlay (PoC-6). The webview overlay owns which base to ask for (DiffRequestPayload.Base);
	// only "lastVersion" (the file's last committed version, local only — no GitHub) is wired so far.
	// "published"/"pr" are PoC-7's vs-main / vs-PR-head compares, not implemented yet. An empty diff (no
	// committed version, or nothing changed) clears any existing overlay. The editor-content version is
	// echoed back so the webview can drop a result it has already edited past.
	private void OnCompare(IpcMessage message)
	{
		// A missing/malformed payload defaults to the pre-existing local compare rather than erroring, so
		// an older webview build (no payload at all) keeps working unchanged.
		DiffRequestPayload? requested = SafeGetPayload<DiffRequestPayload>(message);
		string requestedBase = requested?.Base ?? DiffBaseKinds.LastVersion;
		if (requestedBase != DiffBaseKinds.LastVersion)
		{
			SendError("Comparing against that base isn't supported yet.");
			return;
		}

		string text;
		string? path;
		string? repoRoot;
		lock (_sync)
		{
			text = _text;
			path = _currentPath;
			repoRoot = _repoRoot;
		}

		if (path is null || repoRoot is null)
		{
			SendError("Open a document before comparing.");
			return;
		}

		if (!IsRepoVersioned(repoRoot))
		{
			SendError("This folder isn't set up for versioning yet.");
			return;
		}

		string relativePath = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');

		string? baseText;
		lock (_repoGate)
		{
			baseText = _versioning.ReadHeadContent(repoRoot, relativePath);
		}

		// The base/head → diff.result projection (incl. the empty diff for a null base, which clears any
		// overlay) lives in DiffProjection so the wire shape is unit-testable apart from the controller.
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.DiffResult, DiffProjection.Build(baseText, text), message.Version));
	}

	private void ReplyInserted(string? id, string markdown) =>
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.ImageInserted,
			new ImageInsertedPayload(markdown),
			id: id));

	/// <summary>The open document's directory relative to the repo root (forward slashes, "" at root).</summary>
	private string DocRelativeDir()
	{
		if (_currentPath is null || _repoRoot is null)
		{
			return string.Empty;
		}

		string docDir = Path.GetDirectoryName(Path.GetFullPath(_currentPath)) ?? _repoRoot;
		string relative = Path.GetRelativePath(_repoRoot, docDir);
		return relative == "." ? string.Empty : relative.Replace('\\', '/');
	}

	/// <summary>Emit the open document's repository context from the versioning root. The independently
	/// browsed file-tree root is deliberately never consulted.</summary>
	private void SendWorkspaceContext()
	{
		string? path;
		string? repoRoot;
		RemoteDocumentContext? remoteDocument;
		lock (_sync)
		{
			path = _currentPath;
			repoRoot = _repoRoot;
			remoteDocument = _remoteDocument;
		}

		if (remoteDocument is not null)
		{
			string id = $"{remoteDocument.Owner}/{remoteDocument.Name}";
			string? remoteDefaultBranch = _workspace?.FindRepo(id)?.DefaultBranch;
			SendWorkspaceContext(
				new WorkspaceContextPayload(
					id,
					null,
					remoteDocument.Branch,
					"named",
					string.IsNullOrWhiteSpace(remoteDefaultBranch) ? null : remoteDefaultBranch,
					remoteDocument.Path));
			return;
		}

		if (path is null || repoRoot is null || !IsRepoVersioned(repoRoot))
		{
			SendWorkspaceContext(
				new WorkspaceContextPayload(
					null, null, null, "unavailable", null, Path.GetFileName(path ?? string.Empty)));
			return;
		}

		string? branch = null;
		string branchState = "unavailable";
		string? defaultBranch = null;
		string repository = Path.GetFileName(Path.TrimEndingDirectorySeparator(repoRoot));
		try
		{
			string? configured = WorkflowConfig.defaultBaseForHost(WorkflowSeeds.TryReadRepoToml(repoRoot));
			lock (_repoGate)
			{
				CurrentBranchInfo current = _versioning.DescribeCurrentBranch(repoRoot);
				branch = current.Name;
				branchState = current.IsDetached ? "detached" : current.Name is null ? "unavailable" : "named";
				defaultBranch = _versioning.DefaultBranch(repoRoot, configured);
				GitHubRepo? remote = _publishing is null
					? null
					: GitHubRemote.TryParse(_publishing.RemoteUrl(repoRoot));
				if (remote is not null)
				{
					repository = $"{remote.Owner}/{remote.Name}";
				}
			}
		}
		catch (Exception ex) when (ex is LibGit2SharpException or InvalidOperationException)
		{
			_logger.LogWarning(ex, "Could not read repository context for {Root}", repoRoot);
		}

		string relative = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
		if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
		{
			relative = Path.GetFileName(path);
		}
		SendWorkspaceContext(
			new WorkspaceContextPayload(
				repository,
				repoRoot,
				branch,
				branchState,
				defaultBranch,
				relative));
	}

	/// <summary>Explicit context publication path shared by local documents and remote-only document
	/// loaders. A remote loader supplies owner/name, branch and repository-relative path with a null local
	/// root; it does not need to populate <c>_currentPath</c> or <c>_repoRoot</c>.</summary>
	private void SendWorkspaceContext(WorkspaceContextPayload context) =>
		Emit(IpcSerializer.SerializeEvent(MessageKinds.WorkspaceContext, context));

	/// <summary>
	/// The dominant line ending in raw (on-disk) file content: "\r\n" if CRLF line breaks outnumber
	/// bare-LF ones, else "\n" — including for a document with no line breaks at all, which has no
	/// style to preserve and gets the plain default. Counts a `\r\n` pair once towards CRLF and every
	/// `\n` NOT immediately preceded by `\r` once towards LF, so a mixed file is judged by whichever
	/// style is actually more common rather than by, say, only its first line.
	/// </summary>
	internal static string DetectLineEnding(string rawText)
	{
		int crlf = 0;
		int lfOnly = 0;
		for (int i = 0; i < rawText.Length; i++)
		{
			if (rawText[i] != '\n')
			{
				continue;
			}

			if (i > 0 && rawText[i - 1] == '\r')
			{
				crlf++;
			}
			else
			{
				lfOnly++;
			}
		}
		return crlf > lfOnly ? "\r\n" : "\n";
	}

	/// <summary>
	/// Re-apply a document's on-disk line-ending style to <paramref name="text"/> before writing it back.
	/// Normalizes to a bare "\n" first rather than assuming the input already is: text that came back from
	/// the webview IS already LF-only (its editor model normalizes every line break on the way in), but
	/// `_text` can also still hold the RAW, possibly-CRLF file content straight from `LoadFile`/`Discard`
	/// when `OnSave`/`OnSaveVersion` runs before the webview has reported a single edit — naively replacing
	/// "\n" with "\r\n" on THAT input would double every "\r" ("\r\n" → "\r\r\n"), corrupting the file
	/// instead of preserving it. Normalizing unconditionally is correct and idempotent either way.
	/// </summary>
	private static string ApplyLineEnding(string text, string lineEnding)
	{
		string normalized = text.Contains('\r') ? text.Replace("\r\n", "\n").Replace("\r", "\n") : text;
		return lineEnding == "\n" ? normalized : normalized.Replace("\n", lineEnding);
	}

	/// <summary>
	/// Resolve the repo root for a document: the nearest ancestor containing a <c>.spectool.toml</c>,
	/// else the document's own directory. We deliberately do NOT treat a <c>.git</c> ancestor as the
	/// root here — that would, during the dev demo, resolve to the SpecDesk source tree and write
	/// images into it. Real repo registration (and git-root discovery) is PoC-4.
	/// </summary>
	private static string ResolveRepoRoot(string docPath)
	{
		DirectoryInfo? directory = new FileInfo(docPath).Directory;
		string fallback = directory?.FullName ?? Path.GetDirectoryName(docPath) ?? ".";

		for (DirectoryInfo? current = directory; current is not null; current = current.Parent)
		{
			if (File.Exists(Path.Combine(current.FullName, ".spectool.toml")))
			{
				return current.FullName;
			}
		}

		return fallback;
	}

	/// <summary>Resolve the versioning root for an opened document. A versioned workspace (including a
	/// managed clone) wins only when the document is actually inside it; an independently browsed folder
	/// can therefore never relabel a document opened elsewhere.</summary>
	private string ResolveDocumentRepoRoot(string docPath)
	{
		string? persistedRoot = ResolvePersistedRepoRoot(docPath);
		if (persistedRoot is not null)
		{
			return persistedRoot;
		}

		string? workspaceRoot;
		lock (_sync)
		{
			workspaceRoot = _workspaceRoot;
		}

		if (workspaceRoot is not null)
		{
			string relative = Path.GetRelativePath(workspaceRoot, docPath);
			bool inside = relative != ".."
				&& !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
				&& !Path.IsPathRooted(relative);
			if (inside && IsRepoVersioned(workspaceRoot))
			{
				return workspaceRoot;
			}
		}

		return ResolveRepoRoot(docPath);
	}

	/// <summary>Find the longest persisted versioned root that contains the document. Recent folder roots,
	/// every explicit registered clone, and the legacy managed-clone location are all candidates.</summary>
	private string? ResolvePersistedRepoRoot(string docPath)
	{
		WorkspaceStatePayload? state = _workspace?.State();
		if (state is null)
		{
			return null;
		}

		List<string> candidates = [.. state.Recent.Where(item => item.IsFolder).Select(item => item.Path)];
		foreach (RegisteredRepo repo in state.Repositories)
		{
			candidates.AddRange(repo.Clones.Select(clone => clone.Path));
			if (TryParseGitHubRepo(repo.Id, out string owner, out string name))
			{
				candidates.Add(Path.Combine(AppPaths.Repos, $"{owner}_{name}"));
			}
		}

		string? best = null;
		foreach (string candidate in candidates)
		{
			try
			{
				string fullPath = Path.GetFullPath(candidate);
				if (PathContains(fullPath, docPath)
					&& IsRepoVersioned(fullPath)
					&& (best is null || fullPath.Length > best.Length))
				{
					best = fullPath;
				}
			}
			catch (Exception ex) when (
				ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
			{
				_logger.LogDebug(ex, "Ignoring invalid persisted repository path {Path}", candidate);
			}
		}
		return best;
	}

	private static bool PathContains(string root, string path)
	{
		string relative = Path.GetRelativePath(root, path);
		return relative != ".."
			&& !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
			&& !Path.IsPathRooted(relative);
	}
}
