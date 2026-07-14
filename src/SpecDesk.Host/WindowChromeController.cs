using System.Runtime.InteropServices;
using SpecDesk.Contracts;

namespace SpecDesk.Host;

/// <summary>Routes the small, allow-listed set of in-content window commands to native window actions.</summary>
internal sealed class WindowCommandRouter(
	Action minimize,
	Action toggleMaximize,
	Action<long> requestClose,
	Action beginDrag)
{
	public bool TryHandle(string json)
	{
		IpcMessage? message = IpcSerializer.TryDeserialize(json);
		if (message is null)
		{
			return false;
		}

		switch (message.Kind)
		{
			case MessageKinds.WindowMinimize:
				minimize();
				return true;
			case MessageKinds.WindowToggleMaximize:
				toggleMaximize();
				return true;
			case MessageKinds.WindowClose:
				try
				{
					requestClose(message.GetPayload<WindowClosePayload>()?.RequestId ?? 0);
				}
				catch (System.Text.Json.JsonException)
				{
					// A malformed close acknowledgement cannot be trusted; keep the window open.
				}
				return true;
			case MessageKinds.WindowDrag:
				beginDrag();
				return true;
			default:
				return false;
		}
	}
}

/// <summary>Coordinates every native and in-content close through a webview flush and synchronous draft
/// persist. The first native close is vetoed; only the programmatic close after a matching acknowledgement
/// is allowed through the native closing callback.</summary>
internal sealed class WindowCloseCoordinator(
	Action<long> requestEditorFlush,
	Func<bool> persistPendingDraft,
	Action<long, bool> completeHandshake,
	Action closeWindow)
{
	private readonly object _sync = new();
	private long _requestSequence;
	private long? _pendingRequestId;
	private bool _webviewReady;
	private bool _programmaticClose;
	private bool _persisting;

	public void MarkWebviewReady()
	{
		lock (_sync)
		{
			_webviewReady = true;
		}
	}

	/// <summary>Return true to veto/defer this native close, or false only when no editor can be active yet or
	/// this is the coordinator's own post-persist programmatic close.</summary>
	public bool HandleNativeClosing()
	{
		long? requestId;
		lock (_sync)
		{
			if (_programmaticClose)
			{
				return false;
			}
			if (!_webviewReady)
			{
				return false;
			}
			requestId = BeginRequestLocked();
		}
		if (requestId is long id)
		{
			requestEditorFlush(id);
		}
		return true;
	}

	/// <summary>A zero id is an in-content close intent; a positive id is the webview acknowledgement after
	/// both editor debounces have flushed.</summary>
	public void HandleWebClose(long requestId)
	{
		if (requestId <= 0)
		{
			long? started;
			lock (_sync)
			{
				started = BeginRequestLocked();
			}
			if (started is long id)
			{
				requestEditorFlush(id);
			}
			return;
		}

		lock (_sync)
		{
			if (_pendingRequestId != requestId || _programmaticClose || _persisting)
			{
				return;
			}
			_persisting = true;
		}

		if (!persistPendingDraft())
		{
			lock (_sync)
			{
				if (_pendingRequestId == requestId)
				{
					_pendingRequestId = null;
				}
				_persisting = false;
			}
			completeHandshake(requestId, false);
			return;
		}

		lock (_sync)
		{
			if (_pendingRequestId != requestId)
			{
				return;
			}
			_pendingRequestId = null;
			_persisting = false;
			_programmaticClose = true;
		}
		closeWindow();
	}

	private long? BeginRequestLocked()
	{
		if (_pendingRequestId is not null || _programmaticClose)
		{
			return null;
		}
		long requestId = ++_requestSequence;
		_pendingRequestId = requestId;
		return requestId;
	}
}
/// <summary>Starts the standard Windows caption drag for a chromeless Photino window.</summary>
internal static class NativeWindowDrag
{
	private const uint WmNcLButtonDown = 0x00A1;
	private const nuint HtCaption = 2;

	internal static void Begin(nint windowHandle)
	{
		if (!OperatingSystem.IsWindows() || windowHandle == nint.Zero)
		{
			return;
		}

		_ = ReleaseCapture();
		_ = SendMessage(windowHandle, WmNcLButtonDown, HtCaption, nint.Zero);
	}

	#pragma warning disable SYSLIB1054 // LibraryImport requires unsafe source generation; these stable Win32 calls stay narrowly scoped.
	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ReleaseCapture();

	[DllImport("user32.dll", EntryPoint = "SendMessageW")]
	private static extern nint SendMessage(nint windowHandle, uint message, nuint wParam, nint lParam);
	#pragma warning restore SYSLIB1054
}
