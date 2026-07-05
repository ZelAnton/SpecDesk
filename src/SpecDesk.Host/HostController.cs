using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpecDesk.Contracts;
using SpecDesk.Core;
using SpecDesk.Git;
using SpecDesk.GitHub;
using SpecDesk.Markdown;
// LibGit2Sharp is referenced only for its exception type; do not bring the whole namespace in (it
// defines a LogLevel that collides with Microsoft.Extensions.Logging.LogLevel).
using LibGit2SharpException = LibGit2Sharp.LibGit2SharpException;

namespace SpecDesk.Host;

/// <summary>Abstracts the native open/save file pickers so the controller is testable.</summary>
public interface IFileDialogs
{
	/// <summary>Prompt for a file to open; <c>null</c> if the user cancelled.</summary>
	string? PickOpenFile();

	/// <summary>Prompt for a save location; <c>null</c> if the user cancelled.</summary>
	string? PickSaveFile(string? suggestedPath);
}

/// <summary>
/// Runs the image rule engine: process the bytes, write the file into the repo, and return the
/// document-relative Markdown link, or <c>null</c> on failure. Injected so the controller stays
/// free of image processing / config / I/O and remains unit-testable.
/// </summary>
public delegate string? ImageInserter(
	string repoRoot,
	string docPath,
	byte[] bytes,
	string? originalName,
	string? mime);

/// <summary>
/// Owns the PoC-2 editor session: the current document, the latest edit, the preview
/// version-guard, and the Markdown renderer. Dispatches incoming IPC envelopes and emits preview
/// / doc events. Photino-agnostic — the transport (send callback) and dialogs are injected, so
/// the parse/version orchestration is exercisable without a window.
/// </summary>
public sealed class HostController : IDisposable
{
	/// <summary>Default idle gap after the last keystroke before a draft autosaves to disk (no commit).</summary>
	private static readonly TimeSpan DefaultAutosaveIdle = TimeSpan.FromMilliseconds(1500);

	// Upper bound on the whole "Send for review" round-trip (push + open PR), so a stalled transfer can't
	// hold _repoGate / the single-flight claim indefinitely. The PR call also has its own 30s API timeout.
	private static readonly TimeSpan SendForReviewTimeout = TimeSpan.FromSeconds(120);

	// Upper bound on a read-only review-status refresh (a quick repo read + one GraphQL call). Kept short and
	// separate from the publish round-trip: the client bounds the HTTP call at 30s, this bounds the whole task.
	private static readonly TimeSpan ReviewStatusTimeout = TimeSpan.FromSeconds(35);

	// Upper bound on the "My reviews" list read. Deliberately BELOW the webview's ipc.request timeout (30s):
	// the reply is correlated, so the host must answer before the waiter gives up, or a slow-but-successful
	// load surfaces as a failure and the real reply is dropped against an abandoned request id.
	private static readonly TimeSpan PrListTimeout = TimeSpan.FromSeconds(20);

	// Upper bound on an incoming wire frame (UTF-16 chars). The webview is untrusted, so a single
	// malformed/hostile frame must not be able to exhaust memory. Generous: a large spec plus a
	// base64 image paste fit well under this.
	private const int MaxFrameChars = 64 * 1024 * 1024;

	private readonly Func<string, string, Renderer.RenderResult> _render;
	private readonly Action<string> _send;
	private readonly IFileDialogs _dialogs;
	private readonly ImageInserter _inserter;
	private readonly IDocumentVersioning _versioning;
	private readonly IGitHubAuth? _auth;
	private readonly IGitPublishing? _publishing;
	private readonly IGitHubReview? _review;
	private readonly ILogger<HostController> _logger;
	private readonly string? _initialDocPath;
	private readonly TimeSpan _autosaveIdle;
	private readonly PreviewCoordinator _coordinator = new();
	private readonly LogBridge _logBridge;

	// Guards the lifecycle / autosave fields below, which the message thread and the autosave timer
	// callback both touch. _text/_currentPath/_repoRoot are also published and snapshotted under this
	// lock so the timer never sees a torn (path, text) pair when a document switch races a pending save.
	private readonly object _sync = new();

	// Serializes every repository call (begin edit, autosave commit, discard, and the review push) so the
	// message thread and the autosave timer never drive LibGit2Sharp against one repo concurrently — it is
	// not safe for concurrent writes. Never acquired while holding _sync (and vice versa), so the two locks
	// cannot deadlock. Note the review push holds this across its whole network transfer (see PushBranch),
	// so a message-thread handler that also takes _repoGate (Save / Save a version / Discard / Compare)
	// blocks until the push returns — a bounded responsiveness cost on a slow/stalled network, not a
	// deadlock. The push itself runs off the message thread; only a concurrent repo-gated action contends.
	private readonly object _repoGate = new();
	private string _state = Lifecycle.stateName(Lifecycle.State.Published);
	private string? _branch;
	private string? _baseBranch;
	private Timer? _autosaveTimer;
	private bool _dirty;

	// Monotonic "which repo checkout is this" token. Bumped only under _repoGate, immediately after a
	// repo mutation that changes what is checked out (BeginEdit in OnEdit; Discard in OnDiscard) —
	// never under _sync alone, and never in CancelAutosave (called by both, but before either's
	// checkout — bumping there would be observable together with _text/_state that haven't caught up).
	// Read with Interlocked from RunDiskAutosave's re-check, which runs while holding _repoGate itself:
	// _repoGate must never be held while also taking _sync (see the _repoGate comment below), so that
	// re-check cannot take _sync to read _text/_state freshly — it can only compare against this counter.
	//
	// That comparison is only meaningful if the snapshot's captured "generation" reflects the checkout
	// _text was actually written against — NOT whatever this live counter happens to read at snapshot
	// time. Between a checkout's bump (still holding _repoGate) and _text catching up under a LATER,
	// separate _sync block (e.g. OnDiscard re-reading the reverted file — I/O — before resetting _text),
	// this counter has already moved even though _text has not. A snapshot landing in that window would,
	// if it captured this field directly, carry the NEW generation paired with the OLD text — a torn
	// pair whose later re-check trivially matches. See _textGeneration below for the companion field
	// that closes this, and RunDiskAutosave for how the two are used together.
	private long _draftGeneration;

	// _sync-guarded companion to _draftGeneration: set to whatever _draftGeneration currently reads,
	// in the SAME _sync critical section as every assignment to _text (OnEditorChanged, LoadFile,
	// OnDiscard's post-revert reset). RunDiskAutosave's snapshot captures THIS field (under _sync,
	// alongside text/path) as its "generation" — not _draftGeneration directly — so the captured value
	// always reflects the checkout _text was current for, never a checkout whose bump has landed but
	// whose _text update has not. Any checkout that happens after _text was last written (i.e. any
	// _repoGate-scoped bump to _draftGeneration since) leaves _textGeneration strictly behind — so
	// RunDiskAutosave's later re-check (comparing the captured value against the LIVE _draftGeneration
	// inside _repoGate) reports a mismatch regardless of exactly when, during the checkout's repoGate
	// section, the snapshot was taken. This is what actually closes the window a snapshot that read
	// _draftGeneration directly could not: the two fields change together under _sync, so a snapshot can
	// never observe "new checkout, old text" — only "old checkout, old text" (correctly stale) or "new
	// checkout, new text" (also filtered by IsEditingState(), since a fresh checkout via BeginEdit
	// changes _state, and OnDiscard's reset sets it to Published).
	//
	// A plain "open a different document" (LoadFile without a Discard or BeginEdit) does not bump
	// _draftGeneration at all: it never touches the checked-out branch, so a late write for the document
	// it replaces still lands, correctly, on that document's own unchanged branch; LoadFile still updates
	// _textGeneration alongside _text there, consistent with every other assignment site.
	private long _textGeneration;

	// Monotonic count of versions committed on the current draft ("Save a version"), and how many of
	// those have been pushed to an open review. Guarded by _sync; both reset when the draft changes (begin
	// edit / open a document / discard). "Has versions not yet shared" ⟺ _versionsSaved > _versionsShared —
	// this is what makes Update review meaningful: it guards against a no-op push (and the pointless
	// status refresh that would follow) when nothing new was saved since the review was last updated.
	private long _versionsSaved;
	private long _versionsShared;

	// True while a review-publishing round-trip — "Send for review" (push + open PR) or "Update review"
	// (push to the open PR) — is in flight (guarded by _sync). It single-flights both, and across both:
	// the button stays visible until the status settles (after the multi-second push), so without this a
	// double-click would fire a second push (and, for Send, open a second PR). Send and Update are never
	// legal in the same state, but sharing one claim also stops a state change mid-flight racing the two.
	private bool _publishInFlight;

	// True while a review-status refresh (a read-only GitHub query) is in flight (guarded by _sync). It
	// single-flights the refresh so repeated window-focus triggers don't fan out concurrent queries.
	private bool _refreshingStatus;

	// Set when a refresh is asked for while one is already in flight (guarded by _sync). The in-flight read
	// may have started before the change the new request wants to see (e.g. a slow poll vs. a focus after a
	// reviewer acted), so instead of dropping it we run exactly one more refresh when the current one ends.
	private bool _refreshPending;

	// Cancels an in-flight GitHub sign-in (the long-running poll). Guarded by _sync; replaced on a new
	// sign-in, cancelled on the cancel action and on Dispose.
	private CancellationTokenSource? _signInCts;

	private string _text = string.Empty;
	private string? _currentPath;
	private string? _repoRoot;

	public HostController(
		Func<string, string, Renderer.RenderResult> render,
		Action<string> send,
		IFileDialogs dialogs,
		ImageInserter inserter,
		IDocumentVersioning versioning,
		ILogger<HostController> logger,
		string? initialDocPath = null,
		TimeSpan? autosaveIdle = null,
		IGitHubAuth? auth = null,
		IGitPublishing? publishing = null,
		IGitHubReview? review = null)
	{
		ArgumentNullException.ThrowIfNull(render);
		ArgumentNullException.ThrowIfNull(send);
		ArgumentNullException.ThrowIfNull(dialogs);
		ArgumentNullException.ThrowIfNull(inserter);
		ArgumentNullException.ThrowIfNull(versioning);
		ArgumentNullException.ThrowIfNull(logger);
		_render = render;
		_send = send;
		_dialogs = dialogs;
		_inserter = inserter;
		_versioning = versioning;
		_auth = auth;
		_publishing = publishing;
		_review = review;
		_logger = logger;
		_initialDocPath = initialDocPath;
		_autosaveIdle = autosaveIdle ?? DefaultAutosaveIdle;
		_logBridge = new LogBridge(_logger, _dialogs, SendError, Logging.LogDirectory);
	}

	/// <summary>The repo working-tree root of the open document — the <c>app://</c> asset root.</summary>
	public string? RepoRoot => _repoRoot;

	/// <summary>Disposes the pending autosave timer and cancels any in-flight sign-in.</summary>
	public void Dispose()
	{
		lock (_sync)
		{
			_autosaveTimer?.Dispose();
			_autosaveTimer = null;
			// Cancel any in-flight sign-in, but leave disposal to that task's finally — disposing the cts
			// here while its token is still in flight risks ObjectDisposedException from the running task.
			_signInCts?.Cancel();
			_signInCts = null;
		}
	}

	/// <summary>Route one incoming wire envelope. Unknown or malformed frames are ignored. Runs on the
	/// native WebView2 callback thread, so it caps the (untrusted) frame size and is the last-resort
	/// guard so no handler exception reaches — and tears down — the message pump.</summary>
	public void OnMessage(string json)
	{
		if (json.Length > MaxFrameChars)
		{
			_logger.LogWarning("Dropped an oversized IPC frame ({Length} chars)", json.Length);
			return;
		}

		try
		{
			DispatchMessage(json);
		}
		catch (Exception ex)
		{
			// Per-handler catches cover expected failures; this catches the unexpected, because an
			// exception escaping into the native message pump can crash the process.
			_logger.LogError(ex, "Unhandled exception handling an IPC frame");
		}
	}

	private void DispatchMessage(string json)
	{
		IpcMessage? message = IpcSerializer.TryDeserialize(json);
		if (message is null)
		{
			_logger.LogWarning("Dropped a malformed IPC frame ({Length} chars)", json.Length);
			return;
		}

		// The webview log channel is high-volume and logs itself; don't echo its routing.
		if (message.Kind != MessageKinds.Log)
		{
			_logger.LogDebug(
				"IPC {Kind} (id={Id}, version={Version}, payload={Bytes}B)",
				message.Kind,
				message.Id,
				message.Version,
				message.Payload?.GetRawText().Length ?? 0);
		}

		switch (message.Kind)
		{
			case MessageKinds.Ready:
				OnReady();
				break;
			case MessageKinds.EditorChanged:
				OnEditorChanged(message);
				break;
			case MessageKinds.DocOpen:
				OnOpen();
				break;
			case MessageKinds.DocSave:
				OnSave();
				break;
			case MessageKinds.DocEdit:
				OnEdit(message);
				break;
			case MessageKinds.DocSaveVersion:
				OnSaveVersion(message);
				break;
			case MessageKinds.DocSendForReview:
				OnSendForReview(message);
				break;
			case MessageKinds.PrSuggestedRequest:
				OnSuggestPrText(message);
				break;
			case MessageKinds.DocUpdateReview:
				OnUpdateReview();
				break;
			case MessageKinds.ReviewRefresh:
				OnRefreshReviewStatus();
				break;
			case MessageKinds.PrListRequest:
				OnListReviews(message);
				break;
			case MessageKinds.BranchNameRequest:
				OnSuggestBranchName(message);
				break;
			case MessageKinds.VersionNoteRequest:
				OnSuggestVersionNote(message);
				break;
			case MessageKinds.DocDiscard:
				OnDiscard();
				break;
			case MessageKinds.ImagePaste:
				OnImagePaste(message);
				break;
			case MessageKinds.Log:
				OnLog(message);
				break;
			case MessageKinds.LogExport:
				OnExportLog();
				break;
			case MessageKinds.LinkOpen:
				OnOpenExternal(message);
				break;
			case MessageKinds.DiffRequest:
				OnCompare(message);
				break;
			case MessageKinds.GitHubSignIn:
				OnGitHubSignIn();
				break;
			case MessageKinds.GitHubSignInCancel:
				OnGitHubSignInCancel();
				break;
			case MessageKinds.GitHubSignOut:
				OnGitHubSignOut();
				break;
			default:
				_logger.LogDebug("Ignoring unknown IPC kind {Kind}", message.Kind);
				break;
		}
	}

	private void OnReady()
	{
		if (_initialDocPath is not null && File.Exists(_initialDocPath))
		{
			LoadFile(_initialDocPath);
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
		if (!_coordinator.ShouldRender(version))
		{
			return;
		}

		string text = payload.Text;
		// Publish _text under _sync so the autosave timer's _sync snapshot sees it consistently with
		// _currentPath (the two must stay a matched pair, or autosave could write across documents).
		// _textGeneration tags this text with the checkout it was written against — see its field
		// comment — so a later disk-autosave snapshot of this text carries a generation that a stale
		// re-check can actually compare against.
		lock (_sync)
		{
			_text = text;
			_textGeneration = Interlocked.Read(ref _draftGeneration);
		}

		string docDir = DocRelativeDir();
		_ = Task.Run(() =>
		{
			try
			{
				RenderAndSend(text, version, docDir);
			}
			catch (Exception ex)
			{
				// RenderAndSend handles parse faults itself; this guards an unexpected fault on the
				// background _send/transport path so it never becomes an unobserved task exception.
				_logger.LogError(ex, "Background render task faulted (version {Version})", version);
			}
		});

		// In an editing state, each edit (re)arms the idle disk autosave (write only, never a commit)
		// and flips the status to "Unsaved changes".
		MarkDirtyAndScheduleDiskAutosave();
	}

	private void RenderAndSend(string text, long version, string docDir)
	{
		Renderer.RenderResult result;
		try
		{
			result = _render(docDir, text);
		}
		catch (Exception ex)
		{
			// A parser fault must never crash the background task / message pump; the author sees
			// a plain-language notice instead of a stale or broken preview — but only if this
			// render is still the newest, so a superseded failure stays silent.
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

	private void OnOpen()
	{
		string? path = _dialogs.PickOpenFile();
		if (path is not null)
		{
			LoadFile(path);
		}
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

		// Snapshot the text and publish the path under _sync (a matched pair, like LoadFile), so the
		// autosave timer can't observe a torn (path, text) state.
		string text;
		lock (_sync)
		{
			text = _text;
			_currentPath = path;
		}

		try
		{
			// Serialize with the autosave timer's write (which holds _repoGate) so the two can't both
			// write this file at once.
			lock (_repoGate)
			{
				File.WriteAllText(path, text);
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

		string next = Lifecycle.tryStep(_state, "edit");
		if (next.Length == 0)
		{
			_logger.LogDebug("Edit ignored from state {State}", _state);
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
				_state = next;
				_branch = session.Branch;
				_baseBranch = session.BaseBranch;
				_dirty = false;
				// A fresh draft has saved nothing and shared nothing yet.
				_versionsSaved = 0;
				_versionsShared = 0;
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
		string next = Lifecycle.tryStep(_state, "discard");
		if (next.Length == 0)
		{
			return;
		}

		string? branch;
		string? baseBranch;
		lock (_sync)
		{
			if (_publishInFlight)
			{
				// A Send for review is publishing this draft right now. Discarding would delete the local
				// branch, which — if the push has already opened the PR — orphans it on GitHub. Ignore the
				// discard; the send settles to In review in a moment, where Discard is no longer offered.
				_logger.LogDebug("Discard ignored: a review publish is in flight");
				return;
			}

			branch = _branch;
			baseBranch = _baseBranch;
		}

		if (_repoRoot is null || _currentPath is null || branch is null || baseBranch is null)
		{
			return;
		}

		string repoRoot = _repoRoot;
		string path = _currentPath;

		try
		{
			CancelAutosave();

			// Deliberately NOT calling LoadFile here: it re-reads the file and resets _text/_state in a
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
				_state = Lifecycle.stateName(Lifecycle.State.Published);
				_branch = null;
				_baseBranch = null;
				// A discarded draft leaves nothing saved or shared.
				_versionsSaved = 0;
				_versionsShared = 0;
				_text = revertedText;
				// Tag with the (already bumped, above) checkout _text is now current for — see the
				// _textGeneration field comment for why RunDiskAutosave's snapshot must capture this
				// companion rather than _draftGeneration directly.
				_textGeneration = Interlocked.Read(ref _draftGeneration);
			}

			_logger.LogInformation("Discarded draft on {Branch}", branch);
			Emit(IpcSerializer.SerializeEvent(
				MessageKinds.DocLoaded,
				new DocLoadedPayload(path, revertedText, DocRelativeDir())));
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

	// Whether the current lifecycle state is one in which the document is being edited on a working
	// branch (so disk autosave should run and "Save a version" is allowed).
	private bool IsEditingState() => Lifecycle.tryStep(_state, "saveVersion").Length > 0;

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
			if (_repoRoot is null || _currentPath is null || !IsEditingState())
			{
				return;
			}

			if (!_dirty)
			{
				_dirty = true;
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
		long generation;
		lock (_sync)
		{
			if (_repoRoot is null || _currentPath is null || !IsEditingState())
			{
				return;
			}

			_autosaveTimer?.Dispose();
			_autosaveTimer = null;
			text = _text;
			path = _currentPath;
			// Capture _textGeneration (the checkout this text was written against), NOT a live read of
			// _draftGeneration here — see the _textGeneration field comment for why the two can briefly
			// disagree, and why only the former is safe to compare against later.
			generation = _textGeneration;
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
					// Comparing against the LIVE _draftGeneration (not _textGeneration, which is what the
					// snapshot captured) is deliberate: it is the authoritative value a checkout's own
					// _repoGate section bumps, so it is guaranteed current here regardless of whether that
					// checkout's later _sync-guarded _text update has happened yet.
					if (Interlocked.Read(ref _draftGeneration) != generation)
					{
						_logger.LogDebug(
							"Disk autosave for {Path} skipped: the draft changed while this write was queued", path);
						return;
					}

					File.WriteAllText(path, text);
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

		string next = Lifecycle.tryStep(_state, "saveVersion");
		if (next.Length == 0)
		{
			_logger.LogDebug("Save a version ignored from state {State}", _state);
			return;
		}

		string text;
		string path;
		string repoRoot;
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
				File.WriteAllText(path, text);
				result = _versioning.SaveVersion(repoRoot, note);
			}

			// Either way the working copy now matches the last saved version (a no-op commit means it
			// already did), so clear the dirty flag.
			lock (_sync)
			{
				_dirty = false;
				if (result.Committed)
				{
					// Save a version is a self-transition in every editing state (Draft→Draft, InReview→
					// InReview, …) — it never changes the lifecycle state, so we deliberately do NOT write
					// _state here. `next` was computed from a possibly-stale read (this commit ran under
					// _repoGate, not _sync); a Send / Update review completing meanwhile may have advanced
					// _state (e.g. Draft→InReview), and writing the stale `next` would clobber that.
					// One more version now exists that a later Send / Update review can share.
					_versionsSaved++;
				}
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

	// "Send for review": push the draft branch to GitHub and open a pull request, then move the
	// document to In review. Needs the GitHub feature wired, a connected account, and a GitHub remote;
	// the access token is taken transiently (WithAccessTokenAsync) for the push + API call and is never
	// stored or logged here. The network round-trip runs on a background task — it can take seconds.
	private void OnSendForReview(IpcMessage message)
	{
		// The author-confirmed PR title/body (edited from the suggestion in the send prompt). Absent for a
		// bare send — the publish delegate then falls back to the fully generated text, so the round-trip
		// stays robust whether or not the confirm step ran.
		SendForReviewPayload? prText = SafeGetPayload<SendForReviewPayload>(message);

		// Gate the lifecycle transition AND claim the single-flight slot atomically under _sync, so a
		// stale state read or a double-click can't slip a second round-trip through. fromState/_branch are
		// re-checked before the transition is committed (the document may have moved on); seq records how
		// many saved versions this push carries, so a later Update review knows what is already shared.
		string next;
		string? repoRoot;
		string? branch;
		string? baseBranch;
		string? path;
		string fromState;
		long seq;
		lock (_sync)
		{
			next = Lifecycle.tryStep(_state, "sendForReview");
			if (next.Length == 0)
			{
				_logger.LogDebug("Send for review ignored from state {State}", _state);
				return;
			}

			if (_publishInFlight)
			{
				_logger.LogDebug("Send for review ignored: a review publish is already in flight");
				return;
			}

			fromState = _state;
			repoRoot = _repoRoot;
			branch = _branch;
			baseBranch = _baseBranch;
			path = _currentPath;
			seq = _versionsSaved;
			_publishInFlight = true;
		}

		// The two pure null checks below can't throw, so it's safe to release the claim and return here.
		// EVERYTHING that can throw (IsSignedIn, the repo read, the push, the API call) runs inside the
		// background task (RunReviewPublish), whose finally always releases the claim — otherwise a single
		// libgit2/IO fault on the synchronous path would leak the claim and wedge the feature for the session.
		if (_auth is null || _publishing is null || _review is null)
		{
			ClearPublishInFlight();
			SendError("Connect a GitHub account to send a document for review.");
			return;
		}

		if (repoRoot is null || branch is null || baseBranch is null || path is null)
		{
			ClearPublishInFlight();
			return;
		}

		// Non-null copies so the background closure below sees them as non-nullable.
		string root = repoRoot;
		string branchName = branch;
		string baseName = baseBranch;
		string docPath = path;

		RunReviewPublish(
			fromState, branchName, next, seq, "Sent for review",
			"Couldn't send this for review. Check your connection and try again.",
			async ct =>
			{
				// Re-check readiness at send time (the prompt already gated on it, but the state could have
				// moved) — one shared policy for the prompt and the send, and it hands back the title seed.
				(string? blocked, GitHubRepo? repo, string? lastNote) = CheckSendReadiness(root, branchName, baseName);
				if (blocked is not null)
				{
					SendError(blocked);
					return false;
				}

				SendTransientStatus("Sending for review…");
				// Use the author's confirmed text. A blank title degrades to the generated seed — GitHub
				// rejects an empty title. The body degrades ONLY when it is absent (a bare send with no
				// payload, or a payload missing the field) — a description the author cleared is honoured as
				// empty (it's optional). The prompt only opens once readiness passes, so a failed suggestion
				// can't reach here with a spuriously-blank body.
				(string genTitle, string genBody) = ReviewRequestContent(lastNote, docPath);
				string title = string.IsNullOrWhiteSpace(prText?.Title) ? genTitle : prText!.Title;
				string body = prText?.Body is null ? genBody : prText.Body;
				// The explicit @user/@team reviewers to request (a plain .spectool.toml read, no repo access);
				// "codeowners" is filtered out upstream and left to GitHub's own auto-request.
				string[] reviewers = WorkflowConfig.reviewersForHost(WorkflowSeeds.TryReadRepoToml(root));

				PullRequest pr = await _auth.WithAccessTokenAsync(
					async (token, innerCt) =>
					{
						// The push is a repo operation AND a network transfer, so _repoGate is held across the
						// whole push to serialize it with every other libgit2 access (libgit2 isn't
						// concurrency-safe). This can block a concurrent repo-gated message-thread handler for
						// the push's duration — see the _repoGate note. The lock is released before the
						// separate PR API call below, which needs no repo access.
						lock (_repoGate)
						{
							_publishing.PushBranch(root, branchName, token, cancellationToken: innerCt);
						}

						PullRequest opened = await _review.OpenPullRequestAsync(
							token, repo!.Owner, repo.Name, branchName, baseName, title, body, innerCt);
						// Assign reviewers within the same token scope, best-effort (never fails the send).
						await RequestReviewersBestEffort(token, repo, opened, reviewers, innerCt);
						return opened;
					},
					ct);

				_logger.LogInformation(
					"Opened pull request #{Number} ({Url}) for {Branch}", pr.Number, pr.Url, branchName);
				return true;
			});
	}

	// "Update review": push the newly-saved versions of a draft that is already under review to its open
	// pull request. The PR tracks the head branch, so pushing is all it takes — no second PR is opened. The
	// lifecycle settles the state on the push: from Approved back to In review (new versions need
	// re-approval), a self-transition from In review / Changes requested (a change request stands until the
	// reviewer re-reviews). The push does NOT read GitHub right away — just after a push its GraphQL can still
	// return the pre-push head (replication lag) and re-stamp a stale decision; the periodic poll / next
	// window focus pick up the settled decision once replication catches up. Needs the GitHub feature wired, a
	// connected account, and a GitHub remote; the token is taken transiently (WithAccessTokenAsync) for the
	// push and never stored or logged. Shares OnSendForReview's single-flight + off-thread scaffold
	// (RunReviewPublish), minus the PR-open step, plus a "nothing new to share" guard.
	private void OnUpdateReview()
	{
		// Gate the transition AND claim the shared single-flight slot atomically under _sync (see
		// OnSendForReview). seq/shared capture how many saved versions exist vs. have been shared, so the
		// "nothing new" guard below and the post-push bookkeeping agree on one snapshot.
		string next;
		string? repoRoot;
		string? branch;
		string fromState;
		long seq;
		long shared;
		lock (_sync)
		{
			next = Lifecycle.tryStep(_state, "updateReview");
			if (next.Length == 0)
			{
				_logger.LogDebug("Update review ignored from state {State}", _state);
				return;
			}

			if (_publishInFlight)
			{
				_logger.LogDebug("Update review ignored: a review publish is already in flight");
				return;
			}

			fromState = _state;
			repoRoot = _repoRoot;
			branch = _branch;
			seq = _versionsSaved;
			shared = _versionsShared;
			_publishInFlight = true;
		}

		if (_auth is null || _publishing is null)
		{
			ClearPublishInFlight();
			SendError("Connect a GitHub account to update a review.");
			return;
		}

		if (repoRoot is null || branch is null)
		{
			ClearPublishInFlight();
			return;
		}

		if (seq <= shared)
		{
			// Nothing saved since the review was last shared: a push would be a no-op and re-opening review
			// (or dropping an Approved status) would be misleading. Report it plainly, touch nothing. This is
			// a pure field compare, so it's safe on the synchronous path — no background task is spun up.
			ClearPublishInFlight();
			SendTransientStatus("No new versions to update the review with");
			return;
		}

		// Non-null copies so the background closure below sees them as non-nullable.
		string root = repoRoot;
		string branchName = branch;

		RunReviewPublish(
			fromState, branchName, next, seq, "Updated the review",
			"Couldn't update the review. Check your connection and try again.",
			ct =>
			{
				if (!_auth.IsSignedIn())
				{
					SendError("Connect your GitHub account first, then update the review.");
					return Task.FromResult(false);
				}

				if (ResolveGitHubReviewRepo(root) is null)
				{
					SendError("This document isn't in a GitHub repository, so its review can't be updated.");
					return Task.FromResult(false);
				}

				SendTransientStatus("Updating the review…");

				// Push only — the PR already exists and tracks the branch, so there is no network step to
				// await once the repo-gated push returns.
				return _auth.WithAccessTokenAsync(
					(token, innerCt) =>
					{
						lock (_repoGate)
						{
							_publishing.PushBranch(root, branchName, token, cancellationToken: innerCt);
						}

						return Task.FromResult(true);
					},
					ct);
				});
	}

	// "Refresh review status": while a document is under review, read GitHub's current review decision for
	// its open pull request and reflect it — In review / Changes requested / Approved. This is what makes
	// those states reachable (a reviewer acts on GitHub, out of band); the webview triggers it on window
	// focus. Read-only and best-effort: a failure or a since-closed PR leaves the last-known status. Needs
	// the GitHub feature wired, a connected account, and a GitHub remote; the token is taken transiently.
	private void OnRefreshReviewStatus()
	{
		string? repoRoot;
		string? branch;
		string fromState;
		lock (_sync)
		{
			fromState = _state;
			repoRoot = _repoRoot;
			branch = _branch;
			// Nothing to refresh unless under review with the feature wired (there's an open PR to check), or
			// while a send/update is publishing — that flow authoritatively sets the state, so a concurrent
			// read could clobber it (just after a push its GraphQL can lag; the poll / next focus pick it up).
			if (!IsReviewState(fromState) || _publishInFlight || _auth is null
				|| _publishing is null || _review is null || repoRoot is null || branch is null)
			{
				return;
			}

			// A refresh is already running: the in-flight read may predate what this request wants to see, so
			// queue exactly one follow-up rather than dropping it (a focus refresh must not be lost to a poll).
			if (_refreshingStatus)
			{
				_refreshPending = true;
				return;
			}

			_refreshingStatus = true;
		}

		string root = repoRoot;
		string branchName = branch;
		_ = Task.Run(async () =>
		{
			using CancellationTokenSource timeout = new();
			timeout.CancelAfter(ReviewStatusTimeout);
			try
			{
				if (!_auth.IsSignedIn() || ResolveGitHubReviewRepo(root) is not { } repo)
				{
					return;
				}

				ReviewStatus? status = await _auth.WithAccessTokenAsync(
					(token, ct) => _review.GetReviewStatusAsync(token, repo.Owner, repo.Name, branchName, ct),
					timeout.Token);
				if (status is null)
				{
					// The branch never had a pull request (shouldn't happen once under review) — nothing to do.
					return;
				}

				if (status.PrState != PullRequestState.Open)
				{
					// The PR is merged or closed on GitHub. We deliberately do NOT force a lifecycle change
					// from this background read: flipping to Published (read-only) could strand uncommitted
					// edits, and flipping to Draft would swap in the destructive Discard chrome — both without
					// the author asking. Merging / abandoning is a deliberate step (the Publish flow, PoC-10).
					// Leave the last-known status; the refresh is a no-op. (If the review is done the poll keeps
					// reading it merged until the author moves on — a small, self-correcting cost that avoids the
					// stale-freeze / never-recover bugs a host-side "stop polling" latch kept introducing.)
					_logger.LogDebug(
						"Pull request #{Number} for {Branch} is {State} on GitHub — leaving the last-known status",
						status.Number, branchName, status.PrState);
					return;
				}

				// Map GitHub's live decision straight to the target review state (not through Lifecycle.next,
				// which models the author's local actions) — GitHub is the source of truth while the PR is open.
				// NOTE on a narrow race: a poll/focus refresh landing within GitHub's replication lag right after
				// an Update-review push can briefly read the pre-push head and re-stamp a stale Approved onto
				// just-pushed content. It self-heals on the next refresh once GitHub indexes the push (the head
				// no longer matches the approval's commit → In review). Publish (PoC-10) must do its own
				// head-level freshness check before merging rather than trusting this transient status.
				string mapped = DecisionStateName(status.Decision);

				bool changed = false;
				lock (_sync)
				{
					// Apply the decision only if the document is still the same review draft that we queried
					// for, no publish started meanwhile (its committed state wins), and the decision moved.
					if (_branch == branchName && !_publishInFlight && IsReviewState(_state) && _state != mapped)
					{
						_state = mapped;
						changed = true;
					}
				}

				if (changed)
				{
					_logger.LogInformation(
						"Review status for {Branch} (PR #{Number}) is now {State}", branchName, status.Number, mapped);
					SendLifecycleStatus();
				}
			}
			catch (Exception ex)
			{
				// Best-effort: a status refresh failure must never disturb the author — the last-known status
				// stands. (HttpRequestException / a request timeout / a repo read fault.)
				_logger.LogWarning(ex, "Could not refresh the review status for {Branch}", branchName);
			}
			finally
			{
				bool again;
				lock (_sync)
				{
					_refreshingStatus = false;
					again = _refreshPending;
					_refreshPending = false;
				}

				// A refresh was requested mid-flight — run exactly one more so a focus/poll read that arrived
				// during this one isn't lost (it may have wanted a newer decision than this read saw).
				if (again)
				{
					OnRefreshReviewStatus();
				}
			}
		});
	}

	// Reply to the webview's request for the author's open reviews (the browse list). A best-effort network
	// read on a background thread, correlated to the request by id. The token is taken transiently and never
	// stored or logged; a failure returns a plain reason with an empty list rather than leaving the request
	// unanswered. No git vocabulary reaches the author.
	private void OnListReviews(IpcMessage message)
	{
		const string connectFirst = "Connect a GitHub account to see your reviews.";
		string? id = message.Id;
		IGitHubAuth? auth = _auth;
		IGitHubReview? review = _review;
		if (auth is null || review is null)
		{
			Emit(IpcSerializer.SerializeEvent(MessageKinds.PrList, new PrListPayload([], connectFirst), id: id));
			return;
		}

		_ = Task.Run(async () =>
		{
			using CancellationTokenSource timeout = new();
			// Bounded below the webview's ipc.request timeout (30s) so the host always replies before the
			// waiter gives up — otherwise a slow-but-successful load would surface as a failure and the real
			// reply would be dropped against an already-abandoned request id.
			timeout.CancelAfter(PrListTimeout);
			PrListPayload payload;
			try
			{
				if (!auth.IsSignedIn())
				{
					payload = new PrListPayload([], connectFirst);
				}
				else
				{
					IReadOnlyList<ReviewSummary> reviews = await auth.WithAccessTokenAsync(
						(token, ct) => review.ListReviewsAsync(token, ct), timeout.Token);
					payload = new PrListPayload([.. reviews.Select(ToListItem)], null);
				}
			}
			catch (Exception ex)
			{
				// Best-effort browse: a failure (HttpRequestException / a request timeout) is reported plainly,
				// never as a token or a stack trace, so the panel shows a reason instead of hanging.
				_logger.LogWarning(ex, "Could not list the user's reviews");
				payload = new PrListPayload([], "Couldn't load your reviews. Check your connection and try again.");
			}

			Emit(IpcSerializer.SerializeEvent(MessageKinds.PrList, payload, id: id));
		});
	}

	// One review-list row: the wire status name for styling and its author-facing label (Lifecycle.labelOf,
	// the same source as the status bar) so the panel never re-implements the state vocabulary.
	private static PrListItemPayload ToListItem(ReviewSummary summary)
	{
		string state = DecisionStateName(summary.Decision);
		return new PrListItemPayload(
			summary.Number, summary.Title, summary.Url, summary.Repo,
			summary.Role == ReviewRole.Author ? "author" : "reviewer", state, Lifecycle.labelOf(state));
	}

	private static string DecisionStateName(ReviewDecision decision) => decision switch
	{
		ReviewDecision.Approved => Lifecycle.stateName(Lifecycle.State.Approved),
		ReviewDecision.ChangesRequested => Lifecycle.stateName(Lifecycle.State.ChangesRequested),
		_ => Lifecycle.stateName(Lifecycle.State.InReview),
	};

	// Whether a wire state name is one of the under-review states (an open PR exists to query / update).
	// Derived from Lifecycle.stateName so a rename of a review state's wire name can't silently desync.
	private static bool IsReviewState(string state) =>
		state == Lifecycle.stateName(Lifecycle.State.InReview)
		|| state == Lifecycle.stateName(Lifecycle.State.ChangesRequested)
		|| state == Lifecycle.stateName(Lifecycle.State.Approved);

	// The background scaffold both review pushes share: bound the round-trip with the timeout, run the
	// caller's <paramref name="publish"/> (its own signed-in / remote checks, guards, and the token-scoped
	// push [+ PR open]), and — only when it reports it actually pushed AND the document is still the same
	// draft — commit the lifecycle transition and record how far the review is now shared. Always releases
	// the single-flight claim. A <paramref name="publish"/> that returns false has already told the author
	// why it bailed, so the lifecycle is left untouched.
	private void RunReviewPublish(
		string fromState,
		string branchName,
		string next,
		long seq,
		string action,
		string errorMessage,
		Func<CancellationToken, Task<bool>> publish)
	{
		_ = Task.Run(async () =>
		{
			// Bound the whole round-trip so a stalled push/API call can't hold _repoGate (and the single-
			// flight claim) indefinitely. The transfer phase honours this; a connect-phase stall is bounded
			// only by the OS socket timeout (see PushBranch).
			using CancellationTokenSource timeout = new();
			timeout.CancelAfter(SendForReviewTimeout);
			try
			{
				if (!await publish(timeout.Token))
				{
					return;
				}

				if (TryAdvanceReview(fromState, branchName, next, seq))
				{
					_logger.LogInformation("{Action}: {Branch}", action, branchName);
				}
				else
				{
					// The document changed during the push (discard / switch), so the branch was pushed / the
					// PR opened but this document must NOT be stamped — re-sync the chrome to the real state so
					// the transient "…" label never lingers.
					_logger.LogInformation(
						"{Action} completed, but the document moved on — not advancing: {Branch}", action, branchName);
				}

				// Settle on the lifecycle label — the terminal status frame, so the author is never left
				// staring at the transient "Updating…/Sending…" message. For Update review (a self-transition)
				// the "Updating the review…" transient clearing to the settled state is the confirmation, and
				// a no-op instead shows the distinct "No new versions…" line. GitHub's own decision (if it
				// changed) is picked up by the periodic poll / next window focus.
				SendLifecycleStatus();
			}
			catch (Exception ex)
			{
				// Push / token / API / repo faults (HttpRequestException, LibGit2SharpException,
				// InvalidOperationException, a request timeout) all surface as one plain line — never the
				// token or a stack trace. The document stays where it was so the author can retry.
				_logger.LogError(ex, "Review push failed for {Branch}", branchName);
				SendError(errorMessage);
			}
			finally
			{
				ClearPublishInFlight();
			}
		});
	}

	// Commit a review lifecycle transition iff the document is still the same draft that began the push,
	// and record how far the review has now been shared. seq is the saved-version count captured when the
	// push began. A version saved mid-push is deliberately NOT counted as shared even though the push may
	// actually carry it to the PR (git pushes HEAD): the bias is to UNDER-count, never over-count. Under-
	// counting only costs a later Update review a harmless no-op re-push; over-counting would mark a version
	// shared that never left, so the next Update would report "nothing new" and the reviewer would never see
	// it. Exactly tracking "what HEAD held at push time" would need the counter under _repoGate (the push's
	// lock), which the _sync/_repoGate ordering rule forbids reading here — not worth it for a no-op re-push.
	// Returns whether the transition was applied.
	private bool TryAdvanceReview(string fromState, string branchName, string next, long seq)
	{
		lock (_sync)
		{
			if (_state != fromState || _branch != branchName)
			{
				return false;
			}

			_state = next;
			_versionsShared = seq;
			return true;
		}
	}

	// Resolve the GitHub owner/repo the current remote points at (a repo-gated read of the remote URL, then
	// the strict github.com parse), or null when there is no GitHub remote to host a review. Callers have
	// already established _publishing is non-null on the synchronous path.
	private GitHubRepo? ResolveGitHubReviewRepo(string root)
	{
		string? remoteUrl;
		lock (_repoGate)
		{
			remoteUrl = _publishing!.RemoteUrl(root);
		}

		return GitHubRemote.TryParse(remoteUrl);
	}

	// The single readiness policy shared by the pre-send prompt (OnSuggestPrText) and the send itself:
	// whether a review can be sent for this draft right now (signed in, a GitHub remote, at least one saved
	// version), as plain-language checks over local git/store reads (no network). Returns the blocking
	// reason and a null repo when not ready, or (null, the parsed repo) when ready — so the prompt never
	// opens for a send that would be rejected, and both paths speak the same words. Callers guarantee
	// _auth/_publishing are non-null.
	private (string? Blocked, GitHubRepo? Repo, string? LastNote) CheckSendReadiness(
		string root, string branch, string baseBranch)
	{
		if (!_auth!.IsSignedIn())
		{
			return ("Connect your GitHub account first, then send for review.", null, null);
		}

		// Resolve the GitHub remote first so a non-GitHub repo returns its specific message without a wasted
		// (and possibly throwing) has-commits/last-note read. Then the remaining two local-git reads batch
		// under one lock. (Two lock acquisitions on the GitHub path, one on the non-GitHub path; the only
		// concurrent _repoGate contender during a prompt-open is the quick disk autosave.)
		if (ResolveGitHubReviewRepo(root) is not { } repo)
		{
			return ("This document isn't in a GitHub repository, so it can't be sent for review.", null, null);
		}

		bool hasCommits;
		string? lastNote;
		lock (_repoGate)
		{
			hasCommits = _publishing!.HasCommitsToReview(root, branch, baseBranch);
			lastNote = _publishing.LastVersionNote(root, branch);
		}

		if (!hasCommits)
		{
			// The draft is level with its base (no saved version) — GitHub would reject the PR as "no commits
			// between base and head"; ask the author to save a version rather than surfacing that raw.
			return ("Save a version before sending it for review.", null, null);
		}

		return (null, repo, lastNote);
	}

	// Release the shared single-flight claim taken by OnSendForReview / OnUpdateReview (success, failure,
	// or an early gate exit).
	private void ClearPublishInFlight()
	{
		lock (_sync)
		{
			_publishInFlight = false;
		}
	}

	// Compose the pull-request title and body for a review request: the title is the author's last
	// version note (falling back to the document name when there is none), and the body is a short,
	// plain-language line naming the document. PR content is reviewer-facing on GitHub, so it may name
	// the file — but it stays free of internal git vocabulary.
	private static (string Title, string Body) ReviewRequestContent(string? lastNote, string docPath)
	{
		string docName = Path.GetFileName(docPath);
		string title = !string.IsNullOrWhiteSpace(lastNote) ? lastNote! : $"Review: {docName}";
		string body = $"Review requested for {docName} via SpecDesk.";
		return (title, body);
	}

	// Request the configured reviewers on the freshly-opened PR, best-effort. The PR is already open (the
	// author is In review), so a reviewer-request failure — a handle that isn't a collaborator, a team that
	// needs read:org, a network blip — is logged and swallowed, never failing the send; the author can add
	// reviewers on GitHub. Skipped when there are no explicit reviewers, or the PR number is unknown (a 2xx
	// create with an unparseable body). Runs inside the caller's token scope.
	private async Task RequestReviewersBestEffort(
		string token, GitHubRepo repo, PullRequest pr, string[] reviewers, CancellationToken ct)
	{
		if (reviewers.Length == 0 || _review is null)
		{
			return;
		}

		if (pr.Number == 0)
		{
			// The PR opened but its number couldn't be read (a 2xx create with an unparseable body), so the
			// reviewers endpoint can't be targeted. Say so rather than skipping silently — the author may
			// need to add the configured reviewers on GitHub.
			_logger.LogWarning(
				"Opened a pull request with an unknown number; could not request {Count} configured reviewer(s)",
				reviewers.Length);
			return;
		}

		try
		{
			int requested = await _review.RequestReviewersAsync(token, repo.Owner, repo.Name, pr.Number, reviewers, ct);
			if (requested > 0)
			{
				_logger.LogInformation("Requested {Count} reviewer(s) on pull request #{Number}", requested, pr.Number);
			}
			else
			{
				// The configured entries resolved to nothing GitHub could be asked for (all filtered or
				// malformed) — report honestly rather than claiming an assignment that didn't happen.
				_logger.LogWarning(
					"Configured reviewers resolved to none that could be requested on pull request #{Number}",
					pr.Number);
			}
		}
		catch (Exception ex)
		{
			// Best-effort: assigning reviewers must never undo an opened PR. Swallow the fault (an API
			// rejection, a request timeout, an unexpected error) with a diagnostic, and stay In review.
			_logger.LogWarning(ex, "Could not request reviewers on pull request #{Number}", pr.Number);
		}
	}

	// Reply to the webview's request for the suggested PR title/body to prefill the "send for review"
	// confirm prompt. The title seeds from the branch's last version note (the last commit message),
	// falling back to the document name; the body is a short plain-language line. Correlated by the
	// request id (the webview awaits it). Mirrors the send flow's own generation, so the prompt shows
	// exactly what a bare send would use.
	private void OnSuggestPrText(IpcMessage message)
	{
		string? id = message.Id;
		string? repoRoot;
		string? branch;
		string? baseBranch;
		string? path;
		bool sendLegal;
		bool publishInFlight;
		lock (_sync)
		{
			repoRoot = _repoRoot;
			branch = _branch;
			baseBranch = _baseBranch;
			path = _currentPath;
			sendLegal = Lifecycle.tryStep(_state, "sendForReview").Length > 0;
			publishInFlight = _publishInFlight;
		}

		string title = string.Empty;
		string body = string.Empty;
		string? blocked;
		try
		{
			if (_auth is null || _publishing is null || _review is null)
			{
				blocked = "Connect a GitHub account to send a document for review.";
			}
			else if (repoRoot is null || branch is null || baseBranch is null || path is null || !sendLegal)
			{
				// No draft to send (or the document already moved past Draft). The Send button is draft-only,
				// so this is a defensive reply rather than a reachable UI path.
				blocked = "Start a draft before sending it for review.";
			}
			else if (publishInFlight)
			{
				// A send is already publishing this draft — don't open the prompt to compose text that a
				// second in-flight send would just drop.
				blocked = "This document is already being sent for review.";
			}
			else
			{
				(blocked, GitHubRepo? repo, string? lastNote) = CheckSendReadiness(repoRoot, branch, baseBranch);
				if (repo is not null)
				{
					// Ready — seed the prompt with the same text a bare send would generate.
					(title, body) = ReviewRequestContent(lastNote, path);
				}
			}
		}
		catch (Exception ex)
		{
			// Whatever the readiness read faulted with (a libgit2 error, an I/O or permission fault, an
			// unexpected edge), we MUST still reply — an unanswered request hangs the prompt for the full IPC
			// timeout. So this deliberately catches broadly: reply with a blocking message; the author retries.
			_logger.LogError(ex, "Could not prepare the review suggestion");
			blocked = "Couldn't prepare the review. Try again.";
		}

		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.PrSuggested,
			new PrSuggestedPayload(title, body, blocked),
			id: id));
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
			_dirty = false;
			// Deliberately does NOT bump _draftGeneration: this runs before the repoGate-guarded repo
			// mutation (Discard) or before BeginEdit's checkout, well before _text is reset to match.
			// Bumping here would let a snapshot taken in the resulting gap capture the ALREADY-bumped
			// generation together with the STILL-stale _text — a torn pair that would then pass its own
			// later re-check. See OnDiscard/OnEdit: the generation bumps exactly where the repo mutation
			// and the _text reset are closest together, not here.
		}
	}

	private void SendLifecycleStatus()
	{
		string state;
		string? branch;
		lock (_sync)
		{
			state = _state;
			branch = _branch;
		}

		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.Status,
			new StatusPayload(state, Lifecycle.labelOf(state), branch)));
	}

	private void SendTransientStatus(string label)
	{
		string state;
		string? branch;
		lock (_sync)
		{
			state = _state;
			branch = _branch;
		}

		Emit(IpcSerializer.SerializeEvent(MessageKinds.Status, new StatusPayload(state, label, branch)));
	}

	private void SendError(string message) =>
		Emit(IpcSerializer.SerializeEvent(MessageKinds.Error, new ErrorPayload(message)));

	// Single funnel for every outbound frame. The webview transport (_send) can throw if the window is
	// being torn down (SendWebMessage on a disposed window); a send is best-effort, so swallow that here so
	// it can never surface as an unobserved fault on a background task (RunReviewPublish / sign-in / render
	// / image) or need a guard at each catch site. Message-thread sends are already under OnMessage's net;
	// this makes the background paths equally safe and uniform.
	private void Emit(string json)
	{
		try
		{
			_send(json);
		}
		catch (ObjectDisposedException ex)
		{
			// The window is being torn down — expected and quiet.
			_logger.LogDebug(ex, "Dropped an outbound IPC frame (window torn down)");
		}
		catch (Exception ex)
		{
			// Any other transport fault is a real problem worth surfacing above Debug (the pre-Emit code let
			// it reach OnMessage's Error net); still swallowed so a background task can't fault on it.
			_logger.LogWarning(ex, "Dropped an outbound IPC frame (webview transport error)");
		}
	}

	// Connect the author's GitHub account: show the one-time code, then poll for authorization on a
	// background task (it runs for minutes). Cancellable; only one flow at a time.
	private void OnGitHubSignIn()
	{
		if (_auth is null)
		{
			SendCurrentAccount();
			return;
		}

		CancellationTokenSource cts;
		lock (_sync)
		{
			// Cancel the previous flow but do NOT dispose it here: its still-running task captured that
			// token and may be about to build a linked source from it, and disposing under it would throw
			// ObjectDisposedException that escapes as a spurious "Couldn't reach GitHub". Each task disposes
			// its OWN cts in its finally instead — a cancelled-but-alive token just yields clean cancellation.
			_signInCts?.Cancel();
			cts = new CancellationTokenSource();
			_signInCts = cts;
		}

		CancellationToken token = cts.Token;
		_ = Task.Run(async () =>
		{
			try
			{
				DeviceCodePrompt prompt = await _auth.StartSignInAsync(token);
				Emit(IpcSerializer.SerializeEvent(
					MessageKinds.GitHubCode,
					new GitHubCodePayload(prompt.UserCode, prompt.VerificationUri.ToString())));

				SignInResult result = await _auth.AwaitAuthorizationAsync(prompt, token);
				if (token.IsCancellationRequested)
				{
					// The author dismissed the sign-in. AwaitAuthorizationAsync folds our own cancellation
					// into TimedOut (it never throws once polling), so check the token here and fall back to
					// the signed-out affordance rather than showing the "code expired" message.
					SendCurrentAccount();
				}
				else if (result.Outcome == SignInOutcome.Authorized)
				{
					SendAccount(true, result.Login, message: null);
				}
				else
				{
					SendAccount(false, login: null, SignInMessage(result.Outcome));
				}
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				// Cancelled during the up-front device-code request (StartSignInAsync still throws on cancel,
				// unlike the poll) — fall back to the signed-out affordance.
				SendCurrentAccount();
			}
			catch (Exception ex)
			{
				// The up-front device-code request failed (transport / a GitHub error / a timeout).
				_logger.LogError(ex, "GitHub sign-in could not start");
				SendAccount(false, login: null, "Couldn't reach GitHub. Check your connection and try again.");
			}
			finally
			{
				// Dispose this flow's cts now that its token is no longer in use. Only clear the field if it
				// is still the current flow — a newer sign-in may have replaced it (and owns its own cts).
				lock (_sync)
				{
					if (ReferenceEquals(_signInCts, cts))
					{
						_signInCts = null;
					}
				}

				cts.Dispose();
			}
		});
	}

	private void OnGitHubSignInCancel()
	{
		lock (_sync)
		{
			_signInCts?.Cancel();
		}
	}

	private void OnGitHubSignOut()
	{
		_auth?.SignOut();
		SendCurrentAccount();
	}

	/// <summary>Emit the account affordance state from the store (signed in / out, or unavailable).</summary>
	private void SendCurrentAccount()
	{
		if (_auth is null)
		{
			SendAccount(false, login: null, message: null, available: false);
			return;
		}
		SendAccount(_auth.IsSignedIn(), _auth.SignedInLogin(), message: null);
	}

	private void SendAccount(bool signedIn, string? login, string? message, bool available = true) =>
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.GitHubAccount,
			new GitHubAccountPayload(available, signedIn, login, message)));

	/// <summary>The author-facing line for a non-authorized sign-in ending (plain words, no OAuth jargon).</summary>
	private static string SignInMessage(SignInOutcome outcome) => outcome switch
	{
		SignInOutcome.Expired or SignInOutcome.TimedOut => "Your sign-in code expired. Connect again to retry.",
		SignInOutcome.Denied => "Sign-in was declined on GitHub.",
		SignInOutcome.Unreachable => "Couldn't reach GitHub. Check your connection and try again.",
		SignInOutcome.StorageFailed => "Signed in to GitHub, but couldn't save it on this device. Try again.",
		_ => "Couldn't sign in to GitHub. Please try again.",
	};

	private void LoadFile(string path)
	{
		string text;
		try
		{
			text = File.ReadAllText(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_logger.LogError(ex, "Could not open {Path}", path);
			Emit(IpcSerializer.SerializeEvent(
				MessageKinds.Error,
				new ErrorPayload("Could not open the file.")));
			return;
		}

		// A freshly loaded document is always at Published — reset any in-progress draft so an
		// autosave can never commit the newly opened file onto the previous document's branch.
		CancelAutosave();
		string repoRoot = ResolveRepoRoot(path);
		lock (_sync)
		{
			_state = Lifecycle.stateName(Lifecycle.State.Published);
			_branch = null;
			_baseBranch = null;
			// A newly loaded document carries no draft, so no saved / shared versions either.
			_versionsSaved = 0;
			_versionsShared = 0;
			// Publish the document fields as one matched set under the lock, so a still-running autosave
			// timer can never snapshot a torn (new path, old text) pair and write across documents.
			_text = text;
			_currentPath = path;
			_repoRoot = repoRoot;
			// See the _textGeneration field comment: kept consistent with every other _text assignment
			// site, even though a plain document load never bumps _draftGeneration itself.
			_textGeneration = Interlocked.Read(ref _draftGeneration);
		}

		_logger.LogInformation("Loaded {Path} ({Length} chars); repo root {Root}", path, text.Length, repoRoot);
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.DocLoaded,
			new DocLoadedPayload(path, text, DocRelativeDir())));
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
		if (!IsEditingState())
		{
			_logger.LogDebug("Image paste ignored: not editing (state {State})", _state);
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

	// Route a webview log record into the host logger so native + webview share one log file.
	private void OnLog(IpcMessage message)
	{
		LogPayload? payload = SafeGetPayload<LogPayload>(message);
		if (payload is not null)
		{
			_logBridge.Receive(payload);
		}
	}

	// Offer to save the current log file elsewhere; the bridge reports the outcome (cancelled / exported
	// / failed) through SendError, which the constructor wired in as its notify callback.
	private void OnExportLog() => _logBridge.Export();

	// Compare the working copy (head) against the file's last committed version (base) and send the
	// structural diff for the editors to overlay (PoC-6). Local only — no GitHub. An empty diff (no
	// committed version, or nothing changed) clears any existing overlay. The editor-content version is
	// echoed back so the webview can drop a result it has already edited past.
	private void OnCompare(IpcMessage message)
	{
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

	// Open a link the author clicked in the rendered / formatted view in the OS default handler (a web
	// page in the browser, or a mailto: link in the mail client). The webview only forwards
	// http(s)/mailto links, but it is untrusted, so the URL is re-validated here — a
	// javascript:/file:/data: scheme can never reach the shell.
	private void OnOpenExternal(IpcMessage message)
	{
		OpenExternalPayload? payload = SafeGetPayload<OpenExternalPayload>(message);
		if (payload is null)
		{
			return;
		}

		if (!ExternalLink.TryGetSafeExternalUrl(payload.Url, out string url))
		{
			_logger.LogWarning("Refused to open a link with an unsupported scheme");
			return;
		}

		try
		{
			OpenInBrowser(url);
			_logger.LogInformation("Opened external link {Url}", url);
		}
		catch (Exception ex) when (
			ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException or FileNotFoundException)
		{
			_logger.LogError(ex, "Could not open external link {Url}", url);
			SendError("Could not open the link.");
		}
	}

	// Launch the OS default handler for an (already validated) http(s) or mailto: URL. UseShellExecute
	// hands the URL to the Windows shell's URL handler (browser or mail client); macOS / Linux delegate
	// to open / xdg-open. The URL is passed as a single argument, never built into a shell command
	// string, so there is no injection. The returned handle is disposed immediately — that releases our
	// reference, not the launched app.
	private static void OpenInBrowser(string url)
	{
		if (OperatingSystem.IsWindows())
		{
			Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
		}
		else if (OperatingSystem.IsMacOS())
		{
			Process.Start("open", url)?.Dispose();
		}
		else
		{
			Process.Start("xdg-open", url)?.Dispose();
		}
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

	private static T? SafeGetPayload<T>(IpcMessage message)
	{
		try
		{
			return message.GetPayload<T>();
		}
		catch (JsonException)
		{
			// A payload that does not match the expected shape is treated as "no message".
			return default;
		}
	}
}
