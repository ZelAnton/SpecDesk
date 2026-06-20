using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpecDesk.Contracts;
using SpecDesk.Core;
using SpecDesk.Git;
using SpecDesk.Markdown;
// LibGit2Sharp is referenced only for its exception type; do not bring the whole namespace in (it
// defines a LogLevel that collides with Microsoft.Extensions.Logging.LogLevel used here).
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
/// free of ImageSharp / config / I/O and remains unit-testable.
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

	private readonly Func<string, string, Renderer.RenderResult> _render;
	private readonly Action<string> _send;
	private readonly IFileDialogs _dialogs;
	private readonly ImageInserter _inserter;
	private readonly IDocumentVersioning _versioning;
	private readonly ILogger<HostController> _logger;
	private readonly string? _initialDocPath;
	private readonly TimeSpan _autosaveIdle;
	private readonly PreviewCoordinator _coordinator = new();

	// Guards the lifecycle / autosave fields below, which the message thread and the autosave timer
	// callback both touch. _text/_currentPath/_repoRoot are reference assignments (atomic) read via
	// local snapshots, so they stay outside the lock.
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
		TimeSpan? autosaveIdle = null)
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
		_logger = logger;
		_initialDocPath = initialDocPath;
		_autosaveIdle = autosaveIdle ?? DefaultAutosaveIdle;
	}

	/// <summary>The repo working-tree root of the open document — the <c>app://</c> asset root.</summary>
	public string? RepoRoot => _repoRoot;

	/// <summary>Disposes the pending autosave timer.</summary>
	public void Dispose()
	{
		lock (_sync)
		{
			_autosaveTimer?.Dispose();
			_autosaveTimer = null;
		}
	}

	/// <summary>Route one incoming wire envelope. Unknown or malformed frames are ignored.</summary>
	public void OnMessage(string json)
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
			case MessageKinds.ActionOpen:
				OnOpen();
				break;
			case MessageKinds.ActionSave:
				OnSave();
				break;
			case MessageKinds.ActionEdit:
				OnEdit(message);
				break;
			case MessageKinds.ActionSaveVersion:
				OnSaveVersion(message);
				break;
			case MessageKinds.BranchNameRequest:
				OnSuggestBranchName(message);
				break;
			case MessageKinds.VersionNoteRequest:
				OnSuggestVersionNote(message);
				break;
			case MessageKinds.ActionDiscard:
				OnDiscard();
				break;
			case MessageKinds.ImagePaste:
				OnImagePaste(message);
				break;
			case MessageKinds.Log:
				OnLog(message);
				break;
			case MessageKinds.ExportLog:
				OnExportLog();
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

		_text = payload.Text;
		string text = payload.Text;
		string docDir = DocRelativeDir();
		_ = Task.Run(() => RenderAndSend(text, version, docDir));

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

		try
		{
			File.WriteAllText(path, _text);
			_currentPath = path;
			_logger.LogInformation("Saved {Path} to disk ({Length} chars)", path, _text.Length);
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

		if (!_versioning.IsVersioned(_repoRoot))
		{
			SendError("This folder isn't set up for versioning yet.");
			return;
		}

		EditPayload? payload = SafeGetPayload<EditPayload>(message);

		try
		{
			string? toml = TryReadRepoToml(_repoRoot);
			string docSlug = DocSlug(_currentPath);
			// Prefer the author's chosen draft name (sanitized to a valid ref); else the generated one.
			string sanitized = SanitizeBranchName(payload?.BranchName);
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

		if (!_versioning.IsVersioned(repoRoot))
		{
			SendError("This folder isn't set up for versioning yet.");
			return;
		}

		string note = !string.IsNullOrWhiteSpace(payload?.Note)
			? payload!.Note
			: SuggestedVersionNote(repoRoot, path);

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
					_state = next;
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

	// Reply to the webview's request for a version note to prefill the "Save a version" prompt. The
	// reply is correlated by the request id (the webview awaits it).
	private void OnSuggestVersionNote(IpcMessage message)
	{
		string? id = message.Id;
		string? repoRoot = _repoRoot;
		string? path = _currentPath;
		string note = repoRoot is not null && path is not null ? SuggestedVersionNote(repoRoot, path) : string.Empty;
		_send(IpcSerializer.SerializeEvent(
			MessageKinds.VersionNoteSuggested,
			new VersionNoteSuggestedPayload(note),
			id: id));
	}

	// The generated, editable seed for a version note (the commit message), from the repo's
	// .spectool.toml [commit] template (or the default), expanded with the document tokens.
	private static string SuggestedVersionNote(string repoRoot, string docPath)
	{
		string? toml = TryReadRepoToml(repoRoot);
		return WorkflowConfig.commitMessageForHost(toml, DocSlug(docPath), DateTimeOffset.Now);
	}

	// Reply to the webview's request for a draft (branch) name to prefill the Edit prompt. Correlated
	// by the request id (the webview awaits it).
	private void OnSuggestBranchName(IpcMessage message)
	{
		string? id = message.Id;
		string? repoRoot = _repoRoot;
		string? path = _currentPath;
		string name = repoRoot is not null && path is not null
			? WorkflowConfig.branchNameForHost(TryReadRepoToml(repoRoot), DocSlug(path), DateTimeOffset.Now)
			: string.Empty;
		_send(IpcSerializer.SerializeEvent(
			MessageKinds.BranchNameSuggested,
			new BranchNameSuggestedPayload(name),
			id: id));
	}

	// Reduce an author-typed draft name to a valid git branch ref, matching the webview's live
	// cleanup: backslashes become '/', and anything outside letters/digits and '-_/.' becomes '_'.
	// Runs of the separators '-_/' collapse to one, ref-illegal edges are trimmed (leading/trailing
	// '-_/.', a trailing ".lock", and ".."). Returns "" when nothing usable remains, so the caller
	// falls back to the generated name. Defensive: a still-invalid result is caught by BeginEdit and
	// surfaced as a plain error.
	private static string SanitizeBranchName(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return string.Empty;
		}

		System.Text.StringBuilder builder = new(raw.Length);
		foreach (char original in raw.Trim())
		{
			char ch = original == '\\' ? '/' : original;
			bool keep = char.IsLetterOrDigit(ch) || ch is '-' or '_' or '/' or '.';
			char mapped = keep ? ch : '_';
			// Collapse consecutive separators so "a   b" / "a///b" don't produce noisy runs.
			if (mapped is '-' or '_' or '/' && builder.Length > 0 && builder[^1] is '-' or '_' or '/')
			{
				continue;
			}

			builder.Append(mapped);
		}

		string cleaned = builder.ToString().Trim('-', '_', '/', '.').Replace("..", "_", StringComparison.Ordinal);
		if (cleaned.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
		{
			cleaned = cleaned[..^5].Trim('-', '_', '/', '.');
		}

		return cleaned;
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

	private static string DocSlug(string docPath) =>
		Slug.slugify(Slug.Case.Kebab, Path.GetFileNameWithoutExtension(docPath));

	private static string? TryReadRepoToml(string repoRoot)
	{
		string path = Path.Combine(repoRoot, ".spectool.toml");
		try
		{
			return File.Exists(path) ? File.ReadAllText(path) : null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

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
		lock (_sync)
		{
			_state = Lifecycle.stateName(Lifecycle.State.Published);
			_branch = null;
			_baseBranch = null;
		}

		_text = text;
		_currentPath = path;
		_repoRoot = ResolveRepoRoot(path);
		_logger.LogInformation("Loaded {Path} ({Length} chars); repo root {Root}", path, text.Length, _repoRoot);
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
			string? markdown = _inserter(repoRoot, docPath, bytes, originalName, mime);
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
		});
	}

	// Route a webview log record into the host logger so native + webview share one log file.
	private void OnLog(IpcMessage message)
	{
		LogPayload? payload = SafeGetPayload<LogPayload>(message);
		if (payload is null)
		{
			return;
		}

		LogLevel level = payload.Level switch
		{
			"error" => LogLevel.Error,
			"warn" => LogLevel.Warning,
			"info" => LogLevel.Information,
			_ => LogLevel.Debug,
		};

		if (payload.Data is null)
		{
			_logger.Log(level, "[webview] {Message}", payload.Message);
		}
		else
		{
			_logger.Log(level, "[webview] {Message} {Data}", payload.Message, payload.Data);
		}
	}

	// Offer to save the current log file elsewhere. This also exercises the save dialog, whose
	// exception (if any) the dialog layer now logs — so the log explains the dialog's behaviour.
	private void OnExportLog()
	{
		string? destination = _dialogs.PickSaveFile(Path.Combine(Logging.LogDirectory, "specdesk-export.log"));
		if (destination is null)
		{
			_send(IpcSerializer.SerializeEvent(
				MessageKinds.Error,
				new ErrorPayload($"Logs are at {Logging.LogDirectory}")));
			return;
		}

		try
		{
			File.WriteAllText(destination, ReadCurrentLog());
			_logger.LogInformation("Exported log to {Path}", destination);
			_send(IpcSerializer.SerializeEvent(
				MessageKinds.Error,
				new ErrorPayload($"Log exported to {destination}")));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_logger.LogError(ex, "Failed to export log to {Path}", destination);
			_send(IpcSerializer.SerializeEvent(
				MessageKinds.Error,
				new ErrorPayload($"Could not export log: {ex.Message}")));
		}
	}

	// Read the newest rolling log file with shared access (Serilog keeps it open for writing).
	private static string ReadCurrentLog()
	{
		if (!Directory.Exists(Logging.LogDirectory))
		{
			return "(no log directory yet)";
		}

		string[] files = Directory.GetFiles(Logging.LogDirectory, "specdesk-*.log");
		if (files.Length == 0)
		{
			return "(no log file yet)";
		}

		string newest = files.OrderBy(File.GetLastWriteTimeUtc).Last();
		using FileStream stream = new(newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		using StreamReader reader = new(stream);
		return reader.ReadToEnd();
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
