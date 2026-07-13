using Microsoft.Extensions.Logging;
using SpecDesk.AppInfo;
using SpecDesk.Contracts;
using SpecDesk.Core;
using SpecDesk.Git;
using SpecDesk.GitHub;
using SpecDesk.Markdown;
// LibGit2Sharp is referenced only for its exception type; do not bring the whole namespace in (it
// defines a LogLevel that collides with Microsoft.Extensions.Logging.LogLevel).
using LibGit2SharpException = LibGit2Sharp.LibGit2SharpException;

namespace SpecDesk.Host;

// The document/editing-session slice of HostController: opening/saving, entering and discarding a
// draft, disk autosave, saving versions, image paste, compare, and the load/line-ending helpers.
// The shared fields, locks, constructor, and the IPC router live in HostController.cs.
public sealed partial class HostController
{
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

		long version = message.Version ?? 0;
		// Still the ordering gate for _text itself, not only for a render: an out-of-order/duplicate
		// frame must not overwrite _text with stale content (see HostControllerLineEndingTests'
		// "Discard_ThenAFreshEdit..." for why a second edit in the same draft must strictly advance the
		// version). This stays even though the render it also used to gate is no longer triggered here
		// — see the comment below.
		if (!_coordinator.ShouldRender(version))
		{
			return;
		}

		string text = payload.Text;
		// Publish _text under _sync so the autosave timer's _sync snapshot sees it consistently with
		// _currentPath (the two must stay a matched pair, or autosave could write across documents).
		// The session's Generation tags this text with the checkout it was written against — see
		// DraftSession.Generation — so a later disk-autosave snapshot of this text carries a generation
		// that a stale re-check can actually compare against. Only Generation moves here (a plain edit
		// never changes the lifecycle state), so `with` preserves the rest of the session verbatim.
		lock (_sync)
		{
			_text = text;
			_session = _session with { Generation = Interlocked.Read(ref _draftGeneration) };
		}

		// #preview (the native Markdig render sink RenderAndSend below feeds) is permanently hidden
		// (styles.css: `#preview { display: none; }`) and has no consumer today — Split now pairs the
		// source editor with the editable WYSIWYG, not this pane. Running a full Markdig render plus an
		// HTML IPC round-trip on every debounced edit here was therefore pure overhead, paid on the hot
		// typing path for a panel nobody ever sees. RenderAndSend is kept (internal) as the ready entry
		// point for a future on-demand consumer — diff (PoC-6) or comments (PoC-8) — to call directly;
		// it is simply no longer invoked automatically from here.

		// In an editing state, each edit (re)arms the idle disk autosave (write only, never a commit)
		// and flips the status to "Unsaved changes".
		MarkDirtyAndScheduleDiskAutosave();
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
		// no path falls back to the native open dialog (the toolbar "Open…").
		DocOpenPayload? payload = SafeGetPayload<DocOpenPayload>(message);
		string? path = !string.IsNullOrWhiteSpace(payload?.Path)
			? payload.Path
			: _dialogs.PickOpenFile();
		if (path is not null)
		{
			LoadFile(path);
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
		lock (_sync)
		{
			_workspaceRoot = root;
		}

		_logger.LogInformation("Opened workspace folder {Root}", root);
		// A4: an opened folder is now the most-recent workspace item (emits workspace.state).
		RecordRecent(root, isFolder: true);
		EmitTree(root);
	}

	// Serve the Markdown file tree for the navigator. An explicit path scopes it; otherwise the current
	// workspace folder, else the open document's folder — so requesting the tree without a prior "open
	// folder" still shows something useful (the folder the open document lives in).
	private void OnTreeRequest(IpcMessage message)
	{
		TreeRequestPayload? payload = SafeGetPayload<TreeRequestPayload>(message);
		string? root;
		if (!string.IsNullOrWhiteSpace(payload?.Path))
		{
			root = payload.Path;
		}
		else
		{
			lock (_sync)
			{
				root = _workspaceRoot ?? (_currentPath is not null ? Path.GetDirectoryName(_currentPath) : null);
			}
		}

		if (string.IsNullOrEmpty(root))
		{
			// Nothing to show yet (no folder opened, no document loaded) — an empty tree, not an error.
			Emit(IpcSerializer.SerializeEvent(MessageKinds.Tree, new TreePayload(string.Empty, [])));
			return;
		}

		EmitTree(root);
	}

	private void EmitTree(string root)
	{
		TreePayload tree;
		try
		{
			tree = FileTreeBuilder.Build(root);
		}
		catch (Exception ex) when (
			ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			// ArgumentException/NotSupportedException come from Path.GetFullPath on a malformed root (an
			// embedded null, invalid syntax) — which tree.request's explicit-path branch does not pre-check
			// with Directory.Exists. Turn any of these into a plain error rather than a silent dead request.
			_logger.LogError(ex, "Could not read the folder tree at {Root}", root);
			SendError("That folder could not be read.");
			return;
		}

		Emit(IpcSerializer.SerializeEvent(MessageKinds.Tree, tree));
	}

	// "Save" writes the working copy to disk — it never commits. Committing a version is the explicit
	// "Save a version" action (OnSaveVersion). For a versioned draft this is the same disk write the
	// idle autosave performs; for a plain (unversioned) file it is the ordinary file save.
	private void OnSave()
	{
		string? path = _currentPath ?? _dialogs.PickSaveFile(null);
		if (path is null)
		{
			return;
		}

		// Snapshot the text, its line-ending style, and publish the path under _sync (a matched set,
		// like LoadFile), so the autosave timer can't observe a torn (path, text) state.
		string text;
		string lineEnding;
		lock (_sync)
		{
			text = _text;
			lineEnding = _lineEnding;
			_currentPath = path;
		}

		try
		{
			// Serialize with the autosave timer's write (which holds _repoGate) so the two can't both
			// write this file at once.
			lock (_repoGate)
			{
				File.WriteAllText(path, ApplyLineEnding(text, lineEnding));
			}

			_logger.LogInformation("Saved {Path} to disk ({Length} chars)", path, text.Length);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_logger.LogError(ex, "Could not save {Path}", path);
			Emit(IpcSerializer.SerializeEvent(
				MessageKinds.Error,
				new ErrorPayload("Could not save the file.")));
		}
	}

	// "Edit": fork a working branch and enter Draft. The author names the draft (branch) in a prompt
	// on the webview side; an empty name falls back to the generated one. Until this runs the editor
	// is read-only — editing is only possible once a branch exists.
	private void OnEdit(IpcMessage message)
	{
		if (_repoRoot is null || _currentPath is null)
		{
			SendError("Open a document before editing.");
			return;
		}

		string next = Lifecycle.tryStep(_session.State, "edit");
		if (next.Length == 0)
		{
			_logger.LogDebug("Edit ignored from state {State}", _session.State);
			return;
		}

		if (!IsRepoVersioned(_repoRoot))
		{
			SendError("This folder isn't set up for versioning yet.");
			return;
		}

		EditPayload? payload = SafeGetPayload<EditPayload>(message);

		try
		{
			string? toml = WorkflowSeeds.TryReadRepoToml(_repoRoot);
			string docSlug = WorkflowSeeds.DocSlug(_currentPath);
			// Prefer the author's chosen draft name (sanitized to a valid ref); else the generated one.
			string sanitized = WorkflowSeeds.SanitizeBranchName(payload?.BranchName);
			string branchName = sanitized.Length > 0
				? sanitized
				: WorkflowConfig.branchNameForHost(toml, docSlug, DateTimeOffset.Now);
			string baseBranch = WorkflowConfig.defaultBaseForHost(toml);
			EditSession session;
			lock (_repoGate)
			{
				session = _versioning.BeginEdit(_repoRoot, branchName, baseBranch);
				// Bump while STILL holding _repoGate, immediately after the checkout succeeds — not
				// later, under _sync alone (see the _draftGeneration field comment). A disk-autosave
				// callback from a just-discarded draft on this same document could still be queued for
				// this very gate; bumping here guarantees that by the time it is able to enter _repoGate
				// itself, the generation has already changed, regardless of when its (path, text)
				// snapshot was taken.
				Interlocked.Increment(ref _draftGeneration);
			}

			lock (_sync)
			{
				// A fresh draft has saved nothing and shared nothing yet. Generation is deliberately NOT
				// touched here: BeginEdit bumped _draftGeneration (above, under _repoGate) but did not rewrite
				// _text, so the session's Generation stays behind until the first edit updates _text (see
				// DraftSession.Generation) — the editor still shows the just-published content to edit from.
				_session = _session with
				{
					State = next,
					Branch = session.Branch,
					BaseBranch = session.BaseBranch,
					Dirty = false,
					VersionsSaved = 0,
					VersionsShared = 0,
				};
			}

			_logger.LogInformation(
				"Editing {Doc} on branch {Branch} (base {Base})", docSlug, session.Branch, session.BaseBranch);
			SendLifecycleStatus();
		}
		catch (DirtyWorkingTreeException ex)
		{
			// Another document's draft was autosaved to disk but never saved as a version, and a forced
			// checkout here would have silently wiped it — BeginEdit refused instead. Plain-language,
			// no git vocabulary (branch names stay in the log, not the message the author sees).
			_logger.LogError(ex, "Could not start editing: {DirtyBranch} has unsaved autosaved changes", ex.DirtyBranch);
			SendError("Another document has unsaved changes. Open it and save or discard that draft, then try again.");
		}
		catch (Exception ex) when (ex is LibGit2SharpException or InvalidOperationException)
		{
			_logger.LogError(ex, "Could not start editing");
			SendError("Could not start editing this document.");
		}
	}

	// "Discard": abandon the draft — drop to the base branch, delete the working branch, and reload
	// the document from disk so the editor reflects the published version again.
	private void OnDiscard()
	{
		// Gate the lifecycle transition AND the publish-in-flight check atomically under _sync, matching
		// OnSendForReview/OnUpdateReview's discipline. Computing tryStep from an unlocked read of the session
		// state, then separately re-entering _sync only to check _publishInFlight (as this used to do), leaves
		// a window: a review publish's background task settles the state (TryAdvanceReview) and clears
		// _publishInFlight (ClearPublishInFlight) in two separate, later lock(_sync) acquisitions of its
		// own — if this method's unlocked tryStep read the PRE-push state (still permitting "discard")
		// before that background task started, but this method's lock acquisition happens only after the
		// ENTIRE publish round-trip (including ClearPublishInFlight) has already finished, the stale
		// `next` survives unrechecked and the only-_publishInFlight check passes (false again by then) —
		// Discard then deletes the local branch a just-opened PR now depends on. Re-deriving tryStep here,
		// inside the same critical section as the flag check, closes that: a stale read is impossible
		// because both the state and the flag are read together, under the one lock a concurrent
		// TryAdvanceReview/ClearPublishInFlight also uses.
		string? repoRoot;
		string? path;
		string? branch;
		string? baseBranch;
		lock (_sync)
		{
			DraftSession session = _session;
			string next = Lifecycle.tryStep(session.State, "discard");
			if (next.Length == 0)
			{
				return;
			}

			if (_publishInFlight)
			{
				// A Send for review is publishing this draft right now. Discarding would delete the local
				// branch, which — if the push has already opened the PR — orphans it on GitHub. Ignore the
				// discard; the send settles to In review in a moment, where Discard is no longer offered.
				_logger.LogDebug("Discard ignored: a review publish is in flight");
				return;
			}

			repoRoot = _repoRoot;
			path = _currentPath;
			branch = session.Branch;
			baseBranch = session.BaseBranch;
		}

		if (repoRoot is null || path is null || branch is null || baseBranch is null)
		{
			return;
		}

		try
		{
			CancelAutosave();

			// Deliberately NOT calling LoadFile here: it re-reads the file and resets _text/_session in a
			// LATER, separate lock(_sync) block, after ResolveRepoRoot. Reading the reverted content
			// while still holding _repoGate (right here) means the read that _text below is set from is
			// never racing a second, independent disk read.
			string revertedText;
			lock (_repoGate)
			{
				_versioning.Discard(repoRoot, branch, baseBranch);
				// Bump before the read: even if the read below throws, the revert already happened, so
				// a queued autosave must not be allowed to treat its stale snapshot as still current.
				Interlocked.Increment(ref _draftGeneration);
				revertedText = File.ReadAllText(path);
			}

			lock (_sync)
			{
				_text = revertedText;
				// Re-detected from the reverted file's raw content, same as LoadFile.
				_lineEnding = DetectLineEnding(revertedText);
				// A discarded draft is a fresh Published session that has saved and shared nothing. Dirty is
				// false here (CancelAutosave, above, already cleared it); the fresh record makes that explicit.
				// Generation is tagged with the (already bumped, above) checkout _text is now current for —
				// see DraftSession.Generation for why RunDiskAutosave's snapshot must capture this companion
				// rather than _draftGeneration directly.
				_session = new DraftSession(
					Lifecycle.stateName(Lifecycle.State.Published),
					Branch: null,
					BaseBranch: null,
					Dirty: false,
					VersionsSaved: 0,
					VersionsShared: 0,
					Generation: Interlocked.Read(ref _draftGeneration));
			}

			_logger.LogInformation("Discarded draft on {Branch}", branch);
			Emit(IpcSerializer.SerializeEvent(
				MessageKinds.DocLoaded,
				new DocLoadedPayload(path, revertedText, DocRelativeDir())));
			SendWorkspaceContext();
			SendLifecycleStatus();
		}
		catch (LibGit2SharpException ex)
		{
			_logger.LogError(ex, "Could not discard draft");
			SendError("Could not discard your draft.");
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// The revert itself succeeded (this only runs after _versioning.Discard returned); only the
			// immediate re-read of the reverted file failed (vanished, locked, unreadable).
			_logger.LogError(ex, "Discarded draft on {Branch}, but could not reload {Path}", branch, path);
			SendError("Discarded your draft, but could not reload the file.");
		}
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
		string text;
		string path;
		string lineEnding;
		long generation;
		lock (_sync)
		{
			DraftSession session = _session;
			if (_repoRoot is null || _currentPath is null || !IsEditingState(session.State))
			{
				return;
			}

			_autosaveTimer?.Dispose();
			_autosaveTimer = null;
			text = _text;
			path = _currentPath;
			lineEnding = _lineEnding;
			// Capture the session's Generation (the checkout this text was written against), NOT a live read
			// of _draftGeneration here — see DraftSession.Generation for why the two can briefly disagree,
			// and why only the former is safe to compare against later.
			generation = session.Generation;
		}

		// Outer net: this runs on the Timer thread, which has no last-resort catch of its own (unlike the
		// message pump's OnMessage), so ANY exception escaping — an unexpected IO subtype, or a fault from
		// the SendError transport in the inner catch — would go unobserved and terminate the process.
		try
		{
			try
			{
				// Serialize the disk write with repo mutations: a "Save a version" commit stages the
				// working tree, and writing the file mid-stage would race it.
				lock (_repoGate)
				{
					// Re-check the draft's identity immediately before writing: this callback may have
					// been queued waiting for _repoGate while Discard (or a new Edit) ran and released it
					// first, in which case the snapshot above is for a draft that no longer exists — write
					// it now and it resurrects discarded content on the (now different) checked-out
					// branch. Lock-free by design: _repoGate must never be held while also taking _sync.
					// Comparing against the LIVE _draftGeneration (not the session's captured Generation,
					// which is what the snapshot above holds) is deliberate: it is the authoritative value a
					// checkout's own _repoGate section bumps, so it is guaranteed current here regardless of
					// whether that checkout's later _sync-guarded _text update has happened yet.
					if (Interlocked.Read(ref _draftGeneration) != generation)
					{
						_logger.LogDebug(
							"Disk autosave for {Path} skipped: the draft changed while this write was queued", path);
						return;
					}

					File.WriteAllText(path, ApplyLineEnding(text, lineEnding));
				}

				_logger.LogDebug("Disk-autosaved {Path} ({Length} chars)", path, text.Length);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				_logger.LogError(ex, "Disk autosave failed for {Path}", path);
				SendError("Could not save your changes.");
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Unexpected fault in the disk-autosave timer callback");
		}
	}

	// "Save a version": flush the working copy to disk and commit it (document + any pasted assets)
	// with the author's version note. The only place a commit is created. A no-op (nothing changed
	// since the last version) is reported plainly rather than as an error.
	private void OnSaveVersion(IpcMessage message)
	{
		SaveVersionPayload? payload = SafeGetPayload<SaveVersionPayload>(message);

		string next = Lifecycle.tryStep(_session.State, "saveVersion");
		if (next.Length == 0)
		{
			_logger.LogDebug("Save a version ignored from state {State}", _session.State);
			return;
		}

		string text;
		string path;
		string repoRoot;
		string lineEnding;
		lock (_sync)
		{
			if (_repoRoot is null || _currentPath is null)
			{
				return;
			}

			// This commit supersedes any pending disk autosave.
			_autosaveTimer?.Dispose();
			_autosaveTimer = null;
			text = _text;
			path = _currentPath;
			repoRoot = _repoRoot;
			lineEnding = _lineEnding;
		}

		if (!IsRepoVersioned(repoRoot))
		{
			SendError("This folder isn't set up for versioning yet.");
			return;
		}

		string note = !string.IsNullOrWhiteSpace(payload?.Note)
			? payload!.Note
			: WorkflowSeeds.SuggestedVersionNote(repoRoot, path);

		try
		{
			CommitResult result;
			lock (_repoGate)
			{
				File.WriteAllText(path, ApplyLineEnding(text, lineEnding));
				result = _versioning.SaveVersion(repoRoot, note);
			}

			lock (_sync)
			{
				DraftSession session = _session;
				// Save a version is a self-transition in every editing state (Draft→Draft, InReview→
				// InReview, …) — it never changes the lifecycle state, so we deliberately do NOT write State
				// here. `next` was computed from a possibly-stale read (this commit ran under _repoGate, not
				// _sync); a Send / Update review completing meanwhile may have advanced State (e.g.
				// Draft→InReview), and writing the stale `next` would clobber that. Either way the working
				// copy now matches the last saved version (a no-op commit means it already did), so clear
				// Dirty; a committed version is one more a later Send / Update review can share.
				_session = session with
				{
					Dirty = false,
					VersionsSaved = result.Committed ? session.VersionsSaved + 1 : session.VersionsSaved,
				};
			}

			if (result.Committed)
			{
				_logger.LogInformation("Saved a version of {Path} as {Sha}: {Note}", path, result.Sha, note);
				SendTransientStatus("Version saved");
			}
			else
			{
				_logger.LogInformation("Save a version: nothing changed for {Path}", path);
				SendTransientStatus("No changes since the last version");
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LibGit2SharpException)
		{
			_logger.LogError(ex, "Could not save a version of {Path}", path);
			SendError("Could not save this version.");
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
	private void LoadFile(string path, bool recordRecent = true)
	{
		string text;
		try
		{
			text = File.ReadAllText(path);
		}
		catch (Exception ex) when (
			ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			// ArgumentException/NotSupportedException cover a malformed path (an embedded null, invalid
			// syntax): `doc.open {path}` now carries a webview-supplied path (a tree click / Start "open a
			// file"), not only an OS-dialog result, so a bad string reaches here — surface the same plain
			// "Could not open the file." rather than letting it escape to the last-resort handler unshown.
			_logger.LogError(ex, "Could not open {Path}", path);
			Emit(IpcSerializer.SerializeEvent(
				MessageKinds.Error,
				new ErrorPayload("Could not open the file.")));
			return;
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
		(string state, string? branch, string? baseBranch) = _lifecycleResolvedOnce
			? (Lifecycle.stateName(Lifecycle.State.Published), null, null)
			: ResolveInitialLifecycle(repoRoot);
		_lifecycleResolvedOnce = true;
		lock (_sync)
		{
			// Publish the document fields as one matched set under the lock, so a still-running autosave
			// timer can never snapshot a torn (new path, old text) pair and write across documents.
			_text = text;
			_currentPath = path;
			_repoRoot = repoRoot;
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
		if (payload is null || _currentPath is null || _repoRoot is null)
		{
			ReplyInserted(id, string.Empty);
			return;
		}

		// The editor is navigable (caret/selection) while read-only, so a paste can fire before the
		// author has started a draft. Ignore it: with no working branch the image has nowhere to live,
		// and inserting would mutate a read-only/published document and write a stray file into the repo.
		DraftSession session = _session;
		if (!IsEditingState(session.State))
		{
			_logger.LogDebug("Image paste ignored: not editing (state {State})", session.State);
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

		string repoRoot = _repoRoot;
		string docPath = _currentPath;
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
				// Serialize the image file write with every other repo mutation (begin-edit, autosave,
				// save-version, discard) under _repoGate: libgit2 is not safe for concurrent writes, and
				// a paste landing mid-stage/checkout would corrupt the staged tree or the new asset.
				string? markdown;
				lock (_repoGate)
				{
					markdown = _inserter(repoRoot, docPath, bytes, originalName, mime);
				}

				if (markdown is null)
				{
					_logger.LogWarning("Image insert failed (name={Name}, mime={Mime})", originalName, mime);
					Emit(IpcSerializer.SerializeEvent(
						MessageKinds.Error,
						new ErrorPayload("Could not insert the image.")));
					ReplyInserted(id, string.Empty);
				}
				else
				{
					_logger.LogInformation("Image inserted: {Markdown}", markdown);
					ReplyInserted(id, markdown);
				}
			}
			catch (Exception ex)
			{
				// Never leave the awaiting webview hanging on an unobserved task fault: log, and reply
				// empty so the paste resolves with no insertion.
				_logger.LogError(ex, "Image insert task faulted (name={Name}, mime={Mime})", originalName, mime);
				ReplyInserted(id, string.Empty);
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
		lock (_sync)
		{
			path = _currentPath;
			repoRoot = _repoRoot;
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

	/// <summary>Find the longest persisted versioned root that contains the document. Recent folder roots
	/// cover user-selected clones, while registered repositories map to the current managed-clone location;
	/// the repository tree model can add its explicit clone paths to this candidate list when multi-clone
	/// descriptors land.</summary>
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
