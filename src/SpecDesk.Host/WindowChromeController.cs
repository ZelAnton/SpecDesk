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

/// <summary>Owns the Windows non-client behavior that Photino intentionally leaves to a chromeless host.
/// The native window remains a normal resizable top-level window; this adapter only supplies the missing
/// edge/corner hit tests and work-area-aware maximized bounds.</summary>
internal sealed class NativeWindowChrome : IDisposable
{
	private const int GwlpWndProc = -4;
	private const uint WmGetMinMaxInfo = 0x0024;
	private const uint WmNcHitTest = 0x0084;
	private const uint MonitorDefaultToNearest = 0x00000002;
	private const int SmCxSizeFrame = 32;
	private const int SmCySizeFrame = 33;
	private const int SmCxPaddedBorder = 92;
	private const int DefaultDpi = 96;

	private readonly nint _windowHandle;
	private readonly WindowProcedure _procedure;
	private readonly nint _previousProcedure;
	private bool _disposed;

	private NativeWindowChrome(nint windowHandle)
	{
		_windowHandle = windowHandle;
		_procedure = WindowProc;
		Marshal.SetLastPInvokeError(0);
		_previousProcedure = SetWindowProcedure(
			windowHandle,
			GwlpWndProc,
			Marshal.GetFunctionPointerForDelegate(_procedure));
		if (_previousProcedure == nint.Zero)
		{
			throw new InvalidOperationException(
				$"Could not attach the native window chrome handler (Win32 error {Marshal.GetLastPInvokeError()}).");
		}
	}

	public static NativeWindowChrome? Attach(nint windowHandle)
	{
		if (!OperatingSystem.IsWindows() || windowHandle == nint.Zero)
		{
			return null;
		}
		return new NativeWindowChrome(windowHandle);
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		if (IsWindow(_windowHandle) && _previousProcedure != nint.Zero)
		{
			_ = SetWindowProcedure(_windowHandle, GwlpWndProc, _previousProcedure);
		}
		GC.KeepAlive(_procedure);
	}

	private nint WindowProc(nint windowHandle, uint message, nuint wParam, nint lParam)
	{
		if (message == WmNcHitTest && !IsZoomed(windowHandle) && GetWindowRect(windowHandle, out NativeRect rect))
		{
			int x = unchecked((short)((long)lParam & 0xffff));
			int y = unchecked((short)(((long)lParam >> 16) & 0xffff));
			uint dpi = GetDpiForWindow(windowHandle);
			if (dpi == 0)
			{
				dpi = DefaultDpi;
			}
			int horizontal = MetricForDpi(SmCxSizeFrame, dpi) + MetricForDpi(SmCxPaddedBorder, dpi);
			int vertical = MetricForDpi(SmCySizeFrame, dpi) + MetricForDpi(SmCxPaddedBorder, dpi);
			WindowHitTest hit = WindowChromeGeometry.HitTest(
				new WindowRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
				new WindowPoint(x, y),
				Math.Max(1, horizontal),
				Math.Max(1, vertical));
			if (hit != WindowHitTest.Client)
			{
				return (nint)(int)hit;
			}
		}
		else if (message == WmGetMinMaxInfo && lParam != nint.Zero)
		{
			nint monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
			NativeMonitorInfo info = new() { Size = (uint)Marshal.SizeOf<NativeMonitorInfo>() };
			if (monitor != nint.Zero && GetMonitorInfo(monitor, ref info))
			{
				MaximizedWindowBounds bounds = WindowChromeGeometry.MaximizedBounds(
					new WindowRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom),
					new WindowRect(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom));
				NativeMinMaxInfo minMax = Marshal.PtrToStructure<NativeMinMaxInfo>(lParam);
				minMax.MaxPosition = new NativePoint(bounds.X, bounds.Y);
				minMax.MaxSize = new NativePoint(bounds.Width, bounds.Height);
				Marshal.StructureToPtr(minMax, lParam, false);
				return nint.Zero;
			}
		}

		return CallWindowProc(_previousProcedure, windowHandle, message, wParam, lParam);
	}

	private static int MetricForDpi(int index, uint dpi)
	{
		try
		{
			return GetSystemMetricsForDpi(index, dpi);
		}
		catch (EntryPointNotFoundException)
		{
			return GetSystemMetrics(index);
		}
	}

	private static nint SetWindowProcedure(nint windowHandle, int index, nint value) =>
		nint.Size == 8
			? SetWindowLongPtr64(windowHandle, index, value)
			: new nint(SetWindowLong32(windowHandle, index, value.ToInt32()));

	[UnmanagedFunctionPointer(CallingConvention.Winapi)]
	private delegate nint WindowProcedure(nint windowHandle, uint message, nuint wParam, nint lParam);

	[StructLayout(LayoutKind.Sequential)]
	private struct NativePoint(int x, int y)
	{
		public int X = x;
		public int Y = y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeRect
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeMinMaxInfo
	{
		public NativePoint Reserved;
		public NativePoint MaxSize;
		public NativePoint MaxPosition;
		public NativePoint MinTrackSize;
		public NativePoint MaxTrackSize;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeMonitorInfo
	{
		public uint Size;
		public NativeRect Monitor;
		public NativeRect Work;
		public uint Flags;
	}

	#pragma warning disable SYSLIB1054 // The callback and mutable Win32 structs require classic P/Invoke.
	[DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
	private static extern nint SetWindowLongPtr64(nint windowHandle, int index, nint value);

	[DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
	private static extern int SetWindowLong32(nint windowHandle, int index, int value);

	[DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
	private static extern nint CallWindowProc(
		nint previousProcedure, nint windowHandle, uint message, nuint wParam, nint lParam);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetWindowRect(nint windowHandle, out NativeRect rect);

	[DllImport("user32.dll")]
	private static extern uint GetDpiForWindow(nint windowHandle);

	[DllImport("user32.dll")]
	private static extern int GetSystemMetricsForDpi(int index, uint dpi);

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int index);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsZoomed(nint windowHandle);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool IsWindow(nint windowHandle);

	[DllImport("user32.dll")]
	private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

	[DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetMonitorInfo(nint monitor, ref NativeMonitorInfo info);
	#pragma warning restore SYSLIB1054
}

internal readonly record struct WindowPoint(int X, int Y);
internal readonly record struct WindowRect(int Left, int Top, int Right, int Bottom);
internal readonly record struct MaximizedWindowBounds(int X, int Y, int Width, int Height);

internal enum WindowHitTest
{
	Client = 1,
	Left = 10,
	Right = 11,
	Top = 12,
	TopLeft = 13,
	TopRight = 14,
	Bottom = 15,
	BottomLeft = 16,
	BottomRight = 17,
}

internal static class WindowChromeGeometry
{
	internal static WindowHitTest HitTest(
		WindowRect window, WindowPoint pointer, int horizontalBorder, int verticalBorder)
	{
		bool left = pointer.X >= window.Left && pointer.X < window.Left + horizontalBorder;
		bool right = pointer.X < window.Right && pointer.X >= window.Right - horizontalBorder;
		bool top = pointer.Y >= window.Top && pointer.Y < window.Top + verticalBorder;
		bool bottom = pointer.Y < window.Bottom && pointer.Y >= window.Bottom - verticalBorder;

		return (top, bottom, left, right) switch
		{
			(true, _, true, _) => WindowHitTest.TopLeft,
			(true, _, _, true) => WindowHitTest.TopRight,
			(_, true, true, _) => WindowHitTest.BottomLeft,
			(_, true, _, true) => WindowHitTest.BottomRight,
			(true, _, _, _) => WindowHitTest.Top,
			(_, true, _, _) => WindowHitTest.Bottom,
			(_, _, true, _) => WindowHitTest.Left,
			(_, _, _, true) => WindowHitTest.Right,
			_ => WindowHitTest.Client,
		};
	}

	internal static MaximizedWindowBounds MaximizedBounds(WindowRect monitor, WindowRect work) =>
		new(
			work.Left - monitor.Left,
			work.Top - monitor.Top,
			Math.Max(0, work.Right - work.Left),
			Math.Max(0, work.Bottom - work.Top));
}
