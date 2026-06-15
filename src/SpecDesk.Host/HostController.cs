using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpecDesk.Contracts;
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
public sealed class HostController
{
	private readonly Func<string, string, Renderer.RenderResult> _render;
	private readonly Action<string> _send;
	private readonly IFileDialogs _dialogs;
	private readonly ImageInserter _inserter;
	private readonly ILogger<HostController> _logger;
	private readonly string? _initialDocPath;
	private readonly PreviewCoordinator _coordinator = new();

	private string _text = string.Empty;
	private string? _currentPath;
	private string? _repoRoot;

	public HostController(
		Func<string, string, Renderer.RenderResult> render,
		Action<string> send,
		IFileDialogs dialogs,
		ImageInserter inserter,
		ILogger<HostController> logger,
		string? initialDocPath = null)
	{
		ArgumentNullException.ThrowIfNull(render);
		ArgumentNullException.ThrowIfNull(send);
		ArgumentNullException.ThrowIfNull(dialogs);
		ArgumentNullException.ThrowIfNull(inserter);
		ArgumentNullException.ThrowIfNull(logger);
		_render = render;
		_send = send;
		_dialogs = dialogs;
		_inserter = inserter;
		_logger = logger;
		_initialDocPath = initialDocPath;
	}

	/// <summary>The repo working-tree root of the open document — the <c>app://</c> asset root.</summary>
	public string? RepoRoot => _repoRoot;

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
			_logger.LogInformation("Saved {Path} ({Length} chars)", path, _text.Length);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_logger.LogError(ex, "Could not save {Path}", path);
			_send(IpcSerializer.SerializeEvent(
				MessageKinds.Error,
				new ErrorPayload("Could not save the file.")));
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

		_text = text;
		_currentPath = path;
		_repoRoot = ResolveRepoRoot(path);
		_logger.LogInformation("Loaded {Path} ({Length} chars); repo root {Root}", path, text.Length, _repoRoot);
		_send(IpcSerializer.SerializeEvent(
			MessageKinds.DocLoaded,
			new DocLoadedPayload(path, text)));
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
