using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpecDesk.Ai;
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

	/// <summary>Prompt for a folder to open as the workspace; <c>null</c> if the user cancelled.</summary>
	string? PickOpenFolder();

	/// <summary>Prompt for a file to attach without opening it in the editor.</summary>
	string? PickAttachmentFile() => PickOpenFile();

	/// <summary>Prompt for a folder to attach without changing the workspace.</summary>
	string? PickAttachmentFolder() => PickOpenFolder();

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

	// T-079: upper bound on an AI version-note / PR-description suggestion. Deliberately well below the
	// webview's ipc.request timeout (30s): if the provider is slow or hangs, the host abandons the AI draft
	// and answers the prompt with the deterministic template rather than letting the prompt hang.
	private static readonly TimeSpan SuggestionTimeout = TimeSpan.FromSeconds(12);

	// Upper bound on an incoming wire frame (UTF-16 chars). The webview is untrusted, so a single
	// malformed/hostile frame must not be able to exhaust memory. Generous: a large spec plus a
	// base64 image paste fit well under this.
	private const int MaxFrameChars = 64 * 1024 * 1024;

	private readonly Func<string, string, Renderer.RenderResult> _render;
	private readonly Action<string> _send;
	// The "kind → handler" dispatch table (see IpcHandlerRegistry) that replaced the central OnMessage
	// switch. Populated once at construction by RegisterMessageHandlers, where each partial slice registers
	// its own kinds in its own file; DispatchMessage routes through it.
	private readonly IpcHandlerRegistry _messageHandlers = new();
	private readonly object _outboundSync = new();
	private readonly Queue<OutboundEntry> _outboundFrames = new();
	private readonly record struct OutboundEntry(string? Json, Action? Completion);
	private bool _outboundDraining;
	private bool _outboundStopped;
	private bool _outboundSending;
	private int _outboundSenderThreadId;
	private int _disposeStarted;
	private readonly IFileDialogs _dialogs;
	private readonly ImageInserter _inserter;
	private readonly IDocumentVersioning _versioning;
	private readonly IGitHubAuth? _auth;
	private readonly IGitPublishing? _publishing;
	private readonly IGitHubReview? _review;
	// The AI assistant (PoC-8): the chat agent that streams a reply, and the prompt-template library the
	// composer's picker inserts from. Both optional — null leaves the chat/templates handlers inert (they
	// reply with an empty template set / do nothing), the same graceful-degradation pattern as _auth.
	private IChatAgent? _chatAgent;
	private readonly IChatAgentFactory? _chatAgentFactory;
	private readonly SemaphoreSlim _chatAgentGate = new(1, 1);
	// T-079: drafts version notes / PR text from the read-only document tools (getCurrentDoc / getDiff).
	// Optional — null means the "Save a version" / "Send for review" prompts fall back to the deterministic
	// WorkflowSeeds templates, so the base workflow never depends on an AI provider being present.
	private readonly ISuggestionAgent? _suggestionAgent;
	private readonly ITemplateLibrary? _templates;
	// A4: the persisted workspace store (recents / favorites / registered repos). Optional — null leaves the
	// workspace handlers inert (they emit nothing / record nothing), the same graceful-degradation pattern as
	// _auth / _chatAgent. See HostController.Workspace.cs.
	private readonly WorkspaceStore? _workspace;
	// T-077: the persisted UI-preferences store (theme/wrap/view mode/window geometry). Optional — null
	// leaves the preferences handlers inert (preferences.request answers with the same hard-coded defaults
	// the webview assumed before this store existed; preferences.update is a no-op), the same
	// graceful-degradation pattern as _workspace. See HostController.Preferences.cs.
	private readonly PreferencesStore? _preferences;
	// A6: clones a GitHub repo into a managed local folder so it can be opened as a workspace (repo.open).
	// Optional — null leaves OnOpenRepo inert (nothing to clone), the same graceful-degradation pattern as the
	// other injected dependencies. See HostController.Workspace.cs.
	private readonly IRepositoryCloner? _cloner;
	private readonly ILocalRepositoryInspector? _repositoryInspector;
	private readonly IGitHubRepositoryCatalog? _repositoryCatalog;
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
	// auto-load). Only THEN can the in-memory _session (state / branch) be stale relative to reality (a
	// previous session's crash/restart left them unset while the repo's checkout moved on). Every later
	// "Open" during the same running session already has an authoritative in-memory _session — this object tracked
	// every Edit/Discard/Send for review itself — so consulting git again there would be wrong: the repo's
	// single current checkout is a repo-wide, not a per-document, fact, and reusing it for a document
	// opened mid-session (while a draft on a DIFFERENT document is in progress, or its publish is still
	// resolving in the background) would misattribute someone else's checked-out branch to this one.
	private bool _lifecycleResolvedOnce;
	private readonly TimeSpan _autosaveIdle;
	private readonly PreviewCoordinator _coordinator = new();
	private readonly LogBridge _logBridge;
	private readonly TraceBridge _traceBridge;
	private readonly CancellationTokenSource _lifetimeCts = new();

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
	private bool _disposed;

	// Claimed only after the close handshake has proved that no local repository mutation is active. It is
	// coordinated with mutation starts under _clonePublishSync + _sync, then remains set through window teardown.
	private bool _closePreparationClaimed;

	// The whole draft editing session as one immutable snapshot (see the DraftSession record below),
	// swapped atomically as a single reference under _sync. Consolidates what were six separate fields
	// (_state / _branch / _baseBranch / _dirty / _versionsSaved / _versionsShared) plus the _sync-guarded
	// generation companion, so every handler reads one self-consistent snapshot and asks "is this still
	// the same draft?" against DraftSession.Generation / _draftGeneration rather than racing six loose
	// fields. Assigned only under _sync (via `_session = _session with { … }` or a fresh record), except
	// the few best-effort unlocked reads of _session.State that gate an action before it re-checks under
	// _sync (OnEdit / OnSaveVersion's initial tryStep, OnImagePaste) — the same lock-free read _state
	// carried before, now reading one atomic reference instead of a bare string.
	private DraftSession _session = new(
		Lifecycle.stateName(Lifecycle.State.Published),
		Branch: null,
		BaseBranch: null,
		Dirty: false,
		VersionsSaved: 0,
		VersionsShared: 0,
		Generation: 0);

	private Timer? _autosaveTimer;
	private bool _documentRepositoryTransition;
	private bool _documentDiscardTransition;
	private bool _documentOpenTransition;
	private long _documentOpenTransitionRequestId;
	internal Action? DocumentRetirementStateClearedForTest { get; set; }
	internal Action? DocumentRetirementPublishingForTest { get; set; }
	internal Action? OutboundDrainStartingForTest { get; set; }
	internal Action? OutboundFrameDequeuedForTest { get; set; }
	internal Action? OutboundBatchCompletionEnqueuedForTest { get; set; }
	internal Action? DisposeCancellationStartingForTest { get; set; }
	internal ManualResetEventSlim? PendingRepoActionsTakenForTest { get; set; }
	internal ManualResetEventSlim? PendingRepoActionsResumeForTest { get; set; }
	internal ManualResetEventSlim? PendingRepoActionsResumedForTest { get; set; }
	internal ManualResetEventSlim? RepoRegistrationPublishedForTest { get; set; }
	internal ManualResetEventSlim? RepoRegistrationResumeForTest { get; set; }
	// Claimed under _sync before a document-owned file/asset/commit mutation and held through its terminal
	// publication. Identity transitions check the same flag before they can claim the document.
	private bool _documentMutationLeaseClaimed;
	// Monotonic identity of the in-memory document text. Unlike _draftGeneration (checkout identity),
	// this advances for every accepted editor change and every document hydration.
	private long _contentGeneration;
	// Monotonic "which repo checkout is this" token. Bumped only under _repoGate, immediately after a
	// repo mutation that changes what is checked out (BeginEdit in OnEdit; Discard in OnDiscard) —
	// never under _sync alone, and never in CancelAutosave (called by both, but before either's
	// checkout — bumping there would be observable together with _text/_session that haven't caught up).
	// It stays a bare Interlocked counter, NOT a field of the _sync-guarded _session record, precisely
	// because it is mutated under _repoGate: folding it into the record would require writing _session
	// under both _repoGate and _sync (two locks that must never nest), and the resulting lost update is
	// exactly the race this token exists to close.
	//
	// Read with Interlocked from RunDiskAutosave's re-check, which runs while holding _repoGate itself:
	// _repoGate must never be held while also taking _sync (see the _repoGate comment below), so that
	// re-check cannot take _sync to read _text/_session freshly — it can only compare against this counter.
	//
	// That comparison is only meaningful if the snapshot's captured "generation" reflects the checkout
	// _text was actually written against — NOT whatever this live counter happens to read at snapshot
	// time. Between a checkout's bump (still holding _repoGate) and _text catching up under a LATER,
	// separate _sync block (e.g. OnDiscard re-reading the reverted file — I/O — before resetting _text),
	// this counter has already moved even though _text has not. A snapshot landing in that window would,
	// if it captured this field directly, carry the NEW generation paired with the OLD text — a torn
	// pair whose later re-check trivially matches. See DraftSession.Generation for the _sync-guarded
	// companion that closes this, and RunDiskAutosave for how the two are used together.
	private long _draftGeneration;

	// True while a review-publishing round-trip — "Send for review" (push + open PR) or "Update review"
	// (push to the open PR) — is in flight (guarded by _sync). It single-flights both, and across both:
	// the button stays visible until the status settles (after the multi-second push), so without this a
	// double-click would fire a second push (and, for Send, open a second PR). Send and Update are never
	// legal in the same state, but sharing one claim also stops a state change mid-flight racing the two.
	private bool _publishInFlight;
	private long _publishClaimCounter;
	private long _activePublishClaim;

	// A detected but not-yet-resolved share conflict (PoC-10, "Someone else changed this too"), guarded by
	// _sync. Set when a send/update finds a competing published change to the open document and shows the
	// reconciliation dialog INSTEAD of pushing; consumed when the author picks a resolution. Bound to the
	// draft that raised it (Branch + FromState + Generation) so a stale reply — the document moved on while
	// the dialog was open — is safely ignored rather than reconciling a different draft. See
	// HostController.Review.cs (OnResolveConflict / the send+update conflict pre-check).
	private ShareConflictState? _pendingShareConflict;

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
	private long _signInFlowGeneration;
	// One cancellation/generation boundary for every operation authenticated as the current GitHub
	// account. Sign-out rotates it while holding _signInPublishSync -> _sync; late work must cross the
	// same publication gate and match the generation before it can mutate state or emit account data.
	private CancellationTokenSource _accountSessionCts = new();
	private long _accountSessionGeneration;
	private CancellationTokenSource? _accountDetailsCts;
	private long _accountDetailsGeneration;
	private CancellationTokenSource? _repositoryDescriptionCts;
	private long _repositoryDescriptionGeneration;
	private readonly object _repositoryDescriptionPublishSync = new();

	private string _text = string.Empty;
	private string? _currentPath;
	private string? _repoRoot;
	private RemoteDocumentContext? _remoteDocument;
	// The folder opened as the left-rail file navigator's root (a plain disk folder, or a repo the author
	// opened). Independent of _repoRoot (the versioning root of the OPEN document): the author can browse one
	// folder's tree while editing a document elsewhere. Guarded by _sync like the other document fields.
	private string? _workspaceRoot;
	private long _workspaceNavigationIntentSequence;
	private long _workspaceNavigationIntentGeneration;
	private long _workspaceRootGeneration;
	private readonly object _workspaceRootPublicationSync = new();
	private readonly record struct WorkspaceRootPublication(
		string Root, long Generation, long NavigationGeneration);
	private readonly record struct WorkspaceRootClearPublication(
		long Generation, long NavigationGeneration);
	private readonly record struct WorkspaceTreeRequestPublication(
		string? Root, string? DocumentPath, long Generation);
	internal Action? WorkspaceRequestStateCapturedForTest { get; set; }
	internal Action<string>? WorkspaceRootPublishingForTest { get; set; }
	internal Action<string>? WorkspaceRootRemoteInvalidationStartingForTest { get; set; }
	internal Action? WorkspaceRootClearPublishingForTest { get; set; }
	internal Action<string>? WorkspaceTreeRequestCapturedForTest { get; set; }
	internal Func<string, string, bool>? FileDeleteReparseCheckForTest { get; set; }
	internal Action? RemoteBrowseTerminalPublishingForTest { get; set; }
	internal Action? RemoteFileTerminalPublishingForTest { get; set; }
	// The open document's dominant line ending, detected from the RAW file content at load/discard time.
	// `_text` itself is NOT guaranteed LF-only: it starts as that same raw content (LoadFile/Discard) and
	// only becomes LF-only once the webview reports an edit (its editor model normalizes every line break
	// on the way in) — ApplyLineEnding normalizes defensively for exactly this reason, rather than assuming
	// its input already is LF-only. Re-applied at every disk-write site (OnSave, RunDiskAutosave,
	// OnSaveVersion) so a CRLF-authored file round-trips without every line in the diff being rewritten by
	// a single keystroke. Published as one matched set with `_text`/`_currentPath` under `_sync` (LoadFile,
	// OnDiscard), same discipline as `DraftSession.Generation`. Defaults to "\n" for a brand-new/no-newline document.
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
		IGitHubReview? review = null,
		IChatAgent? chatAgent = null,
		IChatAgentFactory? chatAgentFactory = null,
		ISuggestionAgent? suggestionAgent = null,
		ITemplateLibrary? templates = null,
		WorkspaceStore? workspace = null,
		PreferencesStore? preferences = null,
		IRepositoryCloner? cloner = null,
		ILocalRepositoryInspector? repositoryInspector = null,
		IGitHubRepositoryCatalog? repositoryCatalog = null)
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
		_chatAgent = chatAgent;
		_chatAgentFactory = chatAgentFactory;
		_suggestionAgent = suggestionAgent;
		_templates = templates;
		_workspace = workspace;
		_preferences = preferences;
		_cloner = cloner;
		_repositoryInspector = repositoryInspector;
		_repositoryCatalog = repositoryCatalog;
		_logger = logger;
		_initialDocPath = initialDocPath;
		_autosaveIdle = autosaveIdle ?? DefaultAutosaveIdle;
		if (chatAgent is not null && chatAgentFactory is not null)
		{
			throw new ArgumentException("Provide either a fixed chat agent or an authenticated chat-agent factory, not both.");
		}
		_traceBridge = new TraceBridge(_logger, Logging.LogDirectory);
		_logBridge = new LogBridge(
			_logger, _dialogs, SendError, Logging.LogDirectory, () => _traceBridge.RenderTail(200));
		RegisterMessageHandlers();
		RecoverPendingRepositoryRenames();
	}

	/// <summary>The repo working-tree root of the open document — the <c>app://</c> asset root.</summary>
	public string? RepoRoot => _repoRoot;

	/// <summary>Disposes the pending autosave timer and cancels any in-flight sign-in.</summary>
	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
		{
			return;
		}
		Action[] droppedOutboundCompletions = StopOutbound();
		_lifetimeCts.Cancel();
		CancelCurrentAccountSession();
		Timer? autosaveTimer;
		HashSet<CancellationTokenSource> cancellations = [];
		lock (_signInPublishSync)
		{
			lock (_clonePublishSync)
			{
				lock (_remotePublishSync)
				{
					lock (_repositoryDescriptionPublishSync)
					{
						lock (_sync)
						{
							_disposed = true;
							autosaveTimer = _autosaveTimer;
							_autosaveTimer = null;
							if (_signInCts is not null) cancellations.Add(_signInCts);
							_signInCts = null;
							_signInFlowGeneration++;
							_pendingAccountApplication = null;
							_pendingAccountCarryover = null;
							_accountSessionGeneration++;
							cancellations.Add(_accountSessionCts);
							_accountDetailsGeneration++;
							if (_accountDetailsCts is not null) cancellations.Add(_accountDetailsCts);
							_accountDetailsCts = null;
							_repositoryDescriptionGeneration++;
							if (_repositoryDescriptionCts is not null) cancellations.Add(_repositoryDescriptionCts);
							_repositoryDescriptionCts = null;
							TakePendingRepoActions();
							if (_chatCts is not null) cancellations.Add(_chatCts);
							_chatCts = null;
							_cloneGeneration++;
							if (_cloneCts is not null) cancellations.Add(_cloneCts);
							_localRepositoryActionGeneration++;
							if (_localRepositoryActionCts is not null) cancellations.Add(_localRepositoryActionCts);
							_localRepositoryActionCts = null;
							_localRepositoryActionRepoId = null;
							foreach (RepoMetadataLookup lookup in _repoMetadataLookups.Values)
							{
								cancellations.Add(lookup.Cts);
							}
							_repoMetadataLookups.Clear();
							if (_remoteBrowseCts is not null) cancellations.Add(_remoteBrowseCts);
							_remoteBrowseCts = null;
							_remoteBrowseRepoId = null;
							_remoteBrowseIntentRepoId = null;
							if (_remoteFileCts is not null) cancellations.Add(_remoteFileCts);
							_remoteFileCts = null;
							_remoteFileRepoId = null;
						}
					}
				}
			}
		}
		autosaveTimer?.Dispose();
		DisposeCancellationStartingForTest?.Invoke();
		foreach (CancellationTokenSource cancellation in cancellations)
		{
			CancelForDispose(cancellation);
		}
		foreach (Action completion in droppedOutboundCompletions)
		{
			InvokeOutboundCompletion(completion);
		}
		ResetChatAgentAsync().GetAwaiter().GetResult();
		_chatAgentGate.Dispose();
	}
	private static void CancelForDispose(CancellationTokenSource cancellation)
	{
		try
		{
			cancellation.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// The completed operation owns disposal of its token source; reaching that terminal callback
			// concurrently means cancellation is no longer needed and teardown may continue.
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

		bool mutationGuard = IsRemoteMutation(message.Kind);
		bool accountMutationGuard = IsAccountBoundRemoteMutation(message.Kind);
		if (accountMutationGuard)
		{
			// Sign-out takes the same order. Review handlers re-enter this monitor when they capture the
			// account session, so taking it before the remote-document guard prevents a remote -> sign-in
			// inversion against sign-out's sign-in -> remote path.
			Monitor.Enter(_signInPublishSync);
		}
		if (mutationGuard)
		{
			Monitor.Enter(_remotePublishSync);
		}
		try
		{
			bool remote;
			lock (_sync)
			{
				remote = _remoteDocument is not null;
			}
			if (remote && mutationGuard)
			{
				SendError("This is an online preview. Copy the repository locally before editing or saving.");
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

		if (_messageHandlers.TryGetHandler(message.Kind, out Action<IpcMessage> handler))
		{
			handler(message);
		}
		else
		{
			// The switch's former default arm: an unrecognized kind is ignored, never dropped noisily.
			_logger.LogDebug("Ignoring unknown IPC kind {Kind}", message.Kind);
		}
		}
		finally
		{
			if (mutationGuard)
			{
				Monitor.Exit(_remotePublishSync);
			}
			if (accountMutationGuard)
			{
				Monitor.Exit(_signInPublishSync);
			}
		}
	}

	// Builds the "kind → handler" dispatch table once, at construction. This method only NAMES the
	// domains; each slice contributes its own kinds from its own file (Register...Handlers below and in
	// the sibling HostController.*.cs partials). So adding a new kind to an existing domain is a one-line
	// change to that domain's Register...Handlers method and never touches this router — the whole point
	// of the registry. A slice with no incoming kinds (e.g. HostController.Cancellation.cs) registers
	// nothing and is simply absent here. Registration throws on a duplicate kind, so the audit that every
	// former switch case is present exactly once is enforced at startup, not left to inspection.
	private void RegisterMessageHandlers()
	{
		RegisterCoreHandlers();
		RegisterSessionHandlers();
		RegisterReviewHandlers();
		RegisterPullRequestHandlers();
		RegisterReviewCommentHandlers();
		RegisterSignInHandlers();
		RegisterChatHandlers();
		RegisterActivityHandlers();
		RegisterWorkspaceHandlers();
		RegisterRepositoryBrowseHandlers();
		RegisterSearchHandlers();
		RegisterPreferencesHandlers();
	}

	// The cross-cutting diagnostics / link channels whose handlers live in this file (HostController.cs):
	// they have no domain slice, so they self-register here alongside their handlers.
	private void RegisterCoreHandlers()
	{
		_messageHandlers.Register(MessageKinds.Log, OnLog);
		_messageHandlers.Register(MessageKinds.TraceDump, OnTraceDump);
		_messageHandlers.Register(MessageKinds.LogExport, OnExportLog);
		_messageHandlers.Register(MessageKinds.LinkOpen, OnOpenExternal);
	}

	private static bool IsAccountBoundRemoteMutation(string kind) => kind is
		MessageKinds.DocSendForReview
		or MessageKinds.DocUpdateReview;

	private static bool IsRemoteMutation(string kind) => kind is
		MessageKinds.EditorChanged
		or MessageKinds.DocSave
		or MessageKinds.DocSaveVersion
		or MessageKinds.DocSendForReview
		or MessageKinds.DocUpdateReview
		or MessageKinds.DocDiscard
		or MessageKinds.ImagePaste
		// Reconciling a conflict rewrites the working copy, so it is a document mutation — blocked on an
		// online-preview (read-only) document, like every other edit. It is purely local (no network), so it
		// is deliberately NOT an account-bound mutation.
		or MessageKinds.ReviewConflictResolve;

	private void SendLifecycleStatus()
	{
		DraftSession session;
		lock (_sync)
		{
			session = _session;
		}

		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.Status,
			new StatusPayload(session.State, Lifecycle.labelOf(session.State), session.Branch)));
		SendWorkspaceContext();
	}

	private void SendTransientStatus(string label)
	{
		DraftSession session;
		lock (_sync)
		{
			session = _session;
		}

		Emit(IpcSerializer.SerializeEvent(MessageKinds.Status, new StatusPayload(session.State, label, session.Branch)));
	}

	private void SendError(string message) =>
		Emit(IpcSerializer.SerializeEvent(MessageKinds.Error, new ErrorPayload(message)));

	// Single ordered funnel for every outbound frame. Frames created while their caller owns a controller
	// monitor are drained on another thread; calls made outside those monitors retain synchronous delivery
	// when no earlier batch is queued. The drainer never waits for monitors owned by unrelated threads.
	private void Emit(string json, Action? completion = null) =>
		EnqueueOutbound(new OutboundEntry(json, completion));

	// Marks the end of a logical outbound batch whose frames were enqueued by earlier Emit calls. FIFO
	// guarantees the callback runs only after every preceding send attempt has returned.
	private void CompleteOutboundBatch(Action completion)
	{
		EnqueueOutbound(new OutboundEntry(Json: null, completion));
		OutboundBatchCompletionEnqueuedForTest?.Invoke();
	}
	internal void CompleteOutboundBatchForTest(Action completion) => CompleteOutboundBatch(completion);

	private void EnqueueOutbound(OutboundEntry entry)
	{
		bool drain;
		bool stopped;
		lock (_outboundSync)
		{
			stopped = _outboundStopped;
			if (stopped)
			{
				drain = false;
			}
			else
			{
				_outboundFrames.Enqueue(entry);
				drain = !_outboundDraining;
				if (drain)
				{
					_outboundDraining = true;
				}
			}
		}
		if (stopped)
		{
			InvokeOutboundCompletion(entry.Completion);
			return;
		}
		if (!drain)
		{
			return;
		}
		if (IsControllerMonitorEntered())
		{
			_ = Task.Run(DrainOutbound);
		}
		else
		{
			DrainOutbound();
		}
	}

	internal bool IsControllerMonitorEnteredForTest() => IsControllerMonitorEntered();

	private bool IsControllerMonitorEntered() =>
		Monitor.IsEntered(_signInPublishSync)
		|| Monitor.IsEntered(_clonePublishSync)
		|| Monitor.IsEntered(_remotePublishSync)
		|| Monitor.IsEntered(_repositoryDescriptionPublishSync)
		|| Monitor.IsEntered(_sync)
		|| Monitor.IsEntered(_repoGate)
		|| Monitor.IsEntered(_workspaceRootPublicationSync);

	private void DrainOutbound()
	{
		OutboundDrainStartingForTest?.Invoke();
		while (true)
		{
			OutboundEntry entry;
			lock (_outboundSync)
			{
				if (_outboundStopped)
				{
					_outboundFrames.Clear();
					_outboundDraining = false;
					return;
				}
				if (!_outboundFrames.TryDequeue(out entry))
				{
					_outboundDraining = false;
					return;
				}
				_outboundSending = true;
				_outboundSenderThreadId = Environment.CurrentManagedThreadId;
			}
			try
			{
				if (entry.Json is not null)
				{
					OutboundFrameDequeuedForTest?.Invoke();
					SendOutbound(entry.Json);
				}
				InvokeOutboundCompletion(entry.Completion);
			}
			finally
			{
				lock (_outboundSync)
				{
					_outboundSending = false;
					_outboundSenderThreadId = 0;
					Monitor.PulseAll(_outboundSync);
				}
			}
		}
	}
	private void SendOutbound(string json)
	{
		try
		{
			_send(json);
		}
		catch (ObjectDisposedException ex)
		{
			_logger.LogDebug(ex, "Dropped an outbound IPC frame (window torn down)");
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Dropped an outbound IPC frame (webview transport error)");
		}
	}

	private void InvokeOutboundCompletion(Action? completion)
	{
		try
		{
			completion?.Invoke();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "An outbound delivery completion callback failed");
		}
	}

	private Action[] StopOutbound()
	{
		lock (_outboundSync)
		{
			_outboundStopped = true;
			Action[] completions = _outboundFrames
				.Where(entry => entry.Completion is not null)
				.Select(entry => entry.Completion!)
				.ToArray();
			_outboundFrames.Clear();
			int currentThreadId = Environment.CurrentManagedThreadId;
			while (_outboundSending && _outboundSenderThreadId != currentThreadId)
			{
				Monitor.Wait(_outboundSync);
			}
			return completions;
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

	// Persist a webview trace-ring dump beside the log. The webview sends this just before log.export, so
	// the retained dump is available when Export appends its tail. A malformed payload decodes to null and
	// is dropped, so a bad frame can't disrupt the pump.
	private void OnTraceDump(IpcMessage message)
	{
		TraceDumpPayload? payload = SafeGetPayload<TraceDumpPayload>(message);
		if (payload is not null)
		{
			_traceBridge.Receive(payload);
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

	/// <summary>
	/// An immutable snapshot of the draft editing session — the lifecycle <see cref="State"/>, its
	/// working (<see cref="Branch"/>) and base (<see cref="BaseBranch"/>) branch, whether the working copy
	/// is <see cref="Dirty"/>, and how many versions have been <see cref="VersionsSaved"/> vs.
	/// <see cref="VersionsShared"/> — plus the <see cref="Generation"/> token tying it to the checkout its
	/// accompanying text was written against. Held in <see cref="_session"/> and swapped atomically as one
	/// reference under <see cref="_sync"/>, so a handler snapshots the whole session in a single read and
	/// compares generations ("is this still the same draft?") instead of racing six separate fields.
	/// A reference type on purpose: a single reference read/write is atomic, so the few best-effort
	/// unlocked reads of <see cref="_session"/> observe a self-consistent record, never a torn one, and
	/// the swap under <see cref="_sync"/> publishes all fields together.
	/// </summary>
	/// <param name="State">The wire lifecycle state name (see <c>Lifecycle.stateName</c>).</param>
	/// <param name="Branch">The working (draft) branch, or <c>null</c> when not editing.</param>
	/// <param name="BaseBranch">The base branch the draft forked from, or <c>null</c> when not editing.</param>
	/// <param name="Dirty">
	/// Whether the working copy differs from the last saved version. Set on the first edit, held across disk
	/// autosaves (a write, not a commit), cleared only by "Save a version" (or when the draft changes).
	/// </param>
	/// <param name="VersionsSaved">
	/// Monotonic count of versions committed on the current draft ("Save a version"); reset whenever the
	/// draft changes (begin edit / open a document / discard).
	/// </param>
	/// <param name="VersionsShared">
	/// How many of the saved versions have been pushed to an open review. "Has versions not yet shared" ⟺
	/// <see cref="VersionsSaved"/> &gt; <see cref="VersionsShared"/> — this is what makes Update review
	/// meaningful: it guards against a no-op push (and the pointless status refresh that would follow) when
	/// nothing new was saved since the review was last updated.
	/// </param>
	/// <param name="Generation">
	/// The _sync-guarded companion to <see cref="_draftGeneration"/>: set to whatever
	/// <see cref="_draftGeneration"/> currently reads, in the SAME _sync critical section as every
	/// assignment to <c>_text</c> (OnEditorChanged, LoadFile, OnDiscard's post-revert reset), and left
	/// untouched by session mutations that don't rewrite <c>_text</c> (they use <c>with</c>, preserving it).
	/// RunDiskAutosave's snapshot captures THIS value (under _sync, alongside text/path) as its
	/// "generation" — not <see cref="_draftGeneration"/> directly — so the captured value always reflects
	/// the checkout <c>_text</c> was current for, never a checkout whose bump has landed but whose
	/// <c>_text</c> update has not. Any checkout after <c>_text</c> was last written (any _repoGate-scoped
	/// bump to <see cref="_draftGeneration"/> since) leaves this strictly behind, so RunDiskAutosave's
	/// later re-check (comparing this captured value against the LIVE <see cref="_draftGeneration"/> inside
	/// _repoGate) reports a mismatch regardless of exactly when, during the checkout's repoGate section, the
	/// snapshot was taken. That is what closes the window a snapshot reading <see cref="_draftGeneration"/>
	/// directly could not: this and <c>_text</c> change together under _sync, so a snapshot can never
	/// observe "new checkout, old text" — only "old checkout, old text" (correctly stale) or "new checkout,
	/// new text" (also filtered by IsEditingState, since a fresh BeginEdit changes <see cref="State"/> and
	/// OnDiscard's reset sets it to Published). A plain "open a different document" (LoadFile without a
	/// Discard or BeginEdit) never bumps <see cref="_draftGeneration"/> — it doesn't touch the checked-out
	/// branch — yet LoadFile still refreshes this alongside <c>_text</c>, consistent with every other site.
	/// </param>
	internal sealed record DraftSession(
		string State,
		string? Branch,
		string? BaseBranch,
		bool Dirty,
		long VersionsSaved,
		long VersionsShared,
		long Generation);

	/// <summary>
	/// A detected-but-unresolved share conflict (PoC-10). Captured when a send/update finds a competing
	/// published change to the open document, and held (in <see cref="_pendingShareConflict"/>) until the
	/// author picks a reconciliation. <see cref="Mode"/> is <c>send</c> or <c>update</c> (only the plain-
	/// language "…again" wording differs). <see cref="Branch"/>/<see cref="FromState"/>/<see
	/// cref="Generation"/> bind it to the draft that raised it, so a resolution that arrives after the
	/// document moved on is ignored rather than reconciling the wrong draft. <see cref="Theirs"/> is the base
	/// version, kept only to show both sides through the diff surface if the author chooses Combine.
	/// </summary>
	private sealed record ShareConflictState(
		string Mode,
		string RepoRoot,
		string Branch,
		string BaseBranch,
		string Path,
		string RelativePath,
		string FromState,
		long Generation,
		string Theirs);
}
