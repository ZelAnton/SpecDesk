using System.Text.Json;
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
/// Owns the PoC-2 editor session: the current document, the latest edit, the preview
/// version-guard, and the Markdown renderer. Dispatches incoming IPC envelopes and emits preview
/// / doc events. Photino-agnostic — the transport (send callback) and dialogs are injected, so
/// the parse/version orchestration is exercisable without a window.
/// </summary>
public sealed class HostController
{
	private readonly Func<string, Renderer.RenderResult> _render;
	private readonly Action<string> _send;
	private readonly IFileDialogs _dialogs;
	private readonly string? _initialDocPath;
	private readonly PreviewCoordinator _coordinator = new();

	private string _text = string.Empty;
	private string? _currentPath;

	public HostController(
		Func<string, Renderer.RenderResult> render,
		Action<string> send,
		IFileDialogs dialogs,
		string? initialDocPath = null)
	{
		ArgumentNullException.ThrowIfNull(render);
		ArgumentNullException.ThrowIfNull(send);
		ArgumentNullException.ThrowIfNull(dialogs);
		_render = render;
		_send = send;
		_dialogs = dialogs;
		_initialDocPath = initialDocPath;
	}

	/// <summary>Route one incoming wire envelope. Unknown or malformed frames are ignored.</summary>
	public void OnMessage(string json)
	{
		IpcMessage? message = IpcSerializer.TryDeserialize(json);
		if (message is null)
		{
			return;
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
			default:
				// Unknown kinds are ignored — forward compatibility with later PoCs.
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
		_ = Task.Run(() => RenderAndSend(text, version));
	}

	private void RenderAndSend(string text, long version)
	{
		Renderer.RenderResult result;
		try
		{
			result = _render(text);
		}
		catch (Exception)
		{
			// A parser fault must never crash the background task / message pump; the author sees
			// a plain-language notice instead of a stale or broken preview — but only if this
			// render is still the newest, so a superseded failure stays silent.
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
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
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
			_send(IpcSerializer.SerializeEvent(
				MessageKinds.Error,
				new ErrorPayload("Could not open the file.")));
			return;
		}

		_text = text;
		_currentPath = path;
		_send(IpcSerializer.SerializeEvent(
			MessageKinds.DocLoaded,
			new DocLoadedPayload(path, text)));
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
