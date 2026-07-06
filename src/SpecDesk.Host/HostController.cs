using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpecDesk.Contracts;
using SpecDesk.Core;
using SpecDesk.Git;
using SpecDesk.GitHub;
using SpecDesk.Markdown;

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
public sealed partial class HostController : IDisposable
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
	// Latches the initial-document auto-load to a single attempt. A WebView2 recovery / page reload
	// re-fires "ready", and without this latch OnReady would reload _initialDocPath from disk again —
	// discarding whatever document the author has since opened (and any in-progress draft on it) and
	// re-stamping it Published. Set once OnReady has run its initial-load attempt, whether or not the
	// file actually existed, so a later ready never retries it either.
	private bool _initialDocLoadAttempted;
	// M-16: guards the git-based lifecycle recovery in LoadFile (see ResolveInitialLifecycle) to only the
	// FIRST document this process loads — whichever LoadFile call that turns out to be (the auto-loaded
	// _initialDocPath on OnReady, or the author's first explicit "Open" if the app started with nothing to
	// auto-load). Only THEN can the in-memory _state/_branch be stale relative to reality (a previous
	// session's crash/restart left them unset while the repo's checkout moved on). Every later "Open"
	// during the same running session already has an authoritative in-memory _state — this object tracked
	// every Edit/Discard/Send for review itself — so consulting git again there would be wrong: the repo's
	// single current checkout is a repo-wide, not a per-document, fact, and reusing it for a document
	// opened mid-session (while a draft on a DIFFERENT document is in progress, or its publish is still
	// resolving in the background) would misattribute someone else's checked-out branch to this one.
	private bool _lifecycleResolvedOnce;
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
	// The open document's dominant line ending, detected from the RAW file content at load/discard time.
	// `_text` itself is NOT guaranteed LF-only: it starts as that same raw content (LoadFile/Discard) and
	// only becomes LF-only once the webview reports an edit (its editor model normalizes every line break
	// on the way in) — ApplyLineEnding normalizes defensively for exactly this reason, rather than assuming
	// its input already is LF-only. Re-applied at every disk-write site (OnSave, RunDiskAutosave,
	// OnSaveVersion) so a CRLF-authored file round-trips without every line in the diff being rewritten by
	// a single keystroke. Published as one matched set with `_text`/`_currentPath` under `_sync` (LoadFile,
	// OnDiscard), same discipline as `_textGeneration`. Defaults to "\n" for a brand-new/no-newline document.
	private string _lineEnding = "\n";

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
