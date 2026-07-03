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

	// Serializes every repository-mutating call (begin edit, autosave commit, discard) so the
	// message thread and the autosave timer never drive LibGit2Sharp against one repo concurrently
	// — it is not safe for concurrent writes. Never acquired while holding _sync (and vice versa),
	// so the two locks cannot deadlock.
	private readonly object _repoGate = new();
	private string _state = Lifecycle.stateName(Lifecycle.State.Published);
	private string? _branch;
	private string? _baseBranch;
	private Timer? _autosaveTimer;
	private bool _dirty;

	// Monotonic count of versions committed on the current draft ("Save a version"), and how many of
	// those have been pushed to an open review. Guarded by _sync; both reset when the draft changes (begin
	// edit / open a document / discard). "Has versions not yet shared" ⟺ _versionsSaved > _versionsShared —
	// this is what makes Update review meaningful and stops it re-opening review (or losing an Approved
	// status) on a no-op push when nothing new was saved.
	private long _versionsSaved;
	private long _versionsShared;

	// True while a review-publishing round-trip — "Send for review" (push + open PR) or "Update review"
	// (push to the open PR) — is in flight (guarded by _sync). It single-flights both, and across both:
	// the button stays visible until the status settles (after the multi-second push), so without this a
	// double-click would fire a second push (and, for Send, open a second PR). Send and Update are never
	// legal in the same state, but sharing one claim also stops a state change mid-flight racing the two.
	private bool _publishInFlight;

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
				OnSendForReview();
				break;
			case MessageKinds.DocUpdateReview:
				OnUpdateReview();
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
		lock (_sync)
		{
			_text = text;
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
				_send(IpcSerializer.SerializeEvent(
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

		_send(IpcSerializer.SerializeEvent(
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
			_send(IpcSerializer.SerializeEvent(
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

		try
		{
			CancelAutosave();
			lock (_repoGate)
			{
				_versioning.Discard(_repoRoot, branch, baseBranch);
			}

			_logger.LogInformation("Discarded draft on {Branch}", branch);
			// LoadFile resets the lifecycle to Published and re-reads the now-reverted document.
			LoadFile(_currentPath);
			SendLifecycleStatus();
		}
		catch (LibGit2SharpException ex)
		{
			_logger.LogError(ex, "Could not discard draft");
			SendError("Could not discard your draft.");
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
	// NOT clear the dirty flag. Runs on the timer thread, so it must not throw.
	private void RunDiskAutosave()
	{
		string text;
		string path;
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
	private void OnSendForReview()
	{
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
				if (!_auth.IsSignedIn())
				{
					SendError("Connect your GitHub account first, then send for review.");
					return false;
				}

				GitHubRepo? repo = ResolveGitHubReviewRepo(root);
				if (repo is null)
				{
					SendError("This document isn't in a GitHub repository, so it can't be sent for review.");
					return false;
				}

				// The PR title seed and the "is there anything to review" check are the remaining local-git
				// reads (one repo-gated batch).
				string? lastNote;
				bool hasCommits;
				lock (_repoGate)
				{
					lastNote = _publishing.LastVersionNote(root, branchName);
					hasCommits = _publishing.HasCommitsToReview(root, branchName, baseName);
				}

				if (!hasCommits)
				{
					// The draft is level with its base (no saved version), so GitHub would reject the PR as
					// "no commits between base and head" — turn that into actionable plain language rather
					// than a misleading network error. Committing stays the author's explicit "Save a version".
					SendError("Save a version before sending it for review.");
					return false;
				}

				SendTransientStatus("Sending for review…");
				(string title, string body) = ReviewRequestContent(lastNote, docPath);
				// The explicit @user/@team reviewers to request (a plain .spectool.toml read, no repo access);
				// "codeowners" is filtered out upstream and left to GitHub's own auto-request.
				string[] reviewers = WorkflowConfig.reviewersForHost(WorkflowSeeds.TryReadRepoToml(root));

				PullRequest pr = await _auth.WithAccessTokenAsync(
					async (token, innerCt) =>
					{
						// The push is a repo mutation — serialize it with every other libgit2 write. The lock
						// is released before the awaited network call, which needs no repo access.
						lock (_repoGate)
						{
							_publishing.PushBranch(root, branchName, token, cancellationToken: innerCt);
						}

						PullRequest opened = await _review.OpenPullRequestAsync(
							token, repo.Owner, repo.Name, branchName, baseName, title, body, innerCt);
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
	// pull request. The PR tracks the head branch, so pushing is all it takes — no second PR is opened.
	// On success the document (re-)settles at In review (from Changes requested / Approved, the status
	// flips only now, once the push has landed). Needs the GitHub feature wired, a connected account, and
	// a GitHub remote; the token is taken transiently (WithAccessTokenAsync) for the push and never stored
	// or logged. Shares OnSendForReview's single-flight + off-thread scaffold (RunReviewPublish), minus
	// the PR-open step, plus a "nothing new to share" guard.
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

	// Reply to the webview's request for a version note to prefill the "Save a version" prompt. The
	// reply is correlated by the request id (the webview awaits it).
	private void OnSuggestVersionNote(IpcMessage message)
	{
		string? id = message.Id;
		string? repoRoot = _repoRoot;
		string? path = _currentPath;
		string note = repoRoot is not null && path is not null ? WorkflowSeeds.SuggestedVersionNote(repoRoot, path) : string.Empty;
		_send(IpcSerializer.SerializeEvent(
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
		_send(IpcSerializer.SerializeEvent(
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

		_send(IpcSerializer.SerializeEvent(
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

		_send(IpcSerializer.SerializeEvent(MessageKinds.Status, new StatusPayload(state, label, branch)));
	}

	private void SendError(string message) =>
		_send(IpcSerializer.SerializeEvent(MessageKinds.Error, new ErrorPayload(message)));

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
				_send(IpcSerializer.SerializeEvent(
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
		_send(IpcSerializer.SerializeEvent(
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
			_send(IpcSerializer.SerializeEvent(
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
		}

		_logger.LogInformation("Loaded {Path} ({Length} chars); repo root {Root}", path, text.Length, repoRoot);
		_send(IpcSerializer.SerializeEvent(
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
					_send(IpcSerializer.SerializeEvent(
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
		_send(IpcSerializer.SerializeEvent(
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
		_send(IpcSerializer.SerializeEvent(
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
