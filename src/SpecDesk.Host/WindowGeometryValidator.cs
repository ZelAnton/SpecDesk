using System.Runtime.InteropServices;

namespace SpecDesk.Host;

/// <summary>
/// Whether a saved window rectangle (T-077) is still usable on the CURRENT monitor configuration — a
/// laptop undocked since the last run, a monitor unplugged, or a resolution change can all leave a saved
/// position mostly or entirely off every visible screen. Rather than enumerating individual monitors (whose
/// arrangement doesn't matter here), this checks the saved rectangle against the Windows *virtual screen*
/// (the bounding box of every monitor combined, <c>SM_XVIRTUALSCREEN</c>/<c>SM_CXVIRTUALSCREEN</c>/…): if the
/// rectangle overlaps it by at least a usable margin, it is trusted; otherwise <c>Program.cs</c> falls back
/// to the previous default (1280x800, centered).
/// </summary>
internal static class WindowGeometryValidator
{
	private const int SmXvirtualscreen = 76;
	private const int SmYvirtualscreen = 77;
	private const int SmCxvirtualscreen = 78;
	private const int SmCyvirtualscreen = 79;

	// A saved window narrower/shorter than this (or absurdly large — a corrupted/hand-edited file) is
	// rejected outright rather than restored unusably small or comically oversized.
	private const int MinDimension = 200;
	private const int MaxDimension = 16000;

	// The saved rectangle must overlap the virtual screen by at least this many pixels in both axes — a
	// window barely clipping the very edge of a screen is still usable; one that's almost entirely off every
	// monitor is not.
	private const int MinVisibleOverlap = 120;

	/// <summary>True if (<paramref name="x"/>, <paramref name="y"/>, <paramref name="width"/>,
	/// <paramref name="height"/>) is still a sane, sufficiently on-screen rectangle for the CURRENT monitor
	/// configuration, queried live via Win32 (non-Windows always returns false — window geometry restore is
	/// Windows-only, matching the rest of the native window chrome).</summary>
	internal static bool IsValidForCurrentMonitors(int x, int y, int width, int height) =>
		OperatingSystem.IsWindows()
		&& IsValid(
			x, y, width, height,
			GetSystemMetrics(SmXvirtualscreen), GetSystemMetrics(SmYvirtualscreen),
			GetSystemMetrics(SmCxvirtualscreen), GetSystemMetrics(SmCyvirtualscreen));

	/// <summary>The pure geometry check, factored out so it is unit-testable without a live Win32 call.</summary>
	internal static bool IsValid(
		int x, int y, int width, int height,
		int virtualLeft, int virtualTop, int virtualWidth, int virtualHeight)
	{
		if (width < MinDimension || height < MinDimension || width > MaxDimension || height > MaxDimension)
		{
			return false;
		}
		if (virtualWidth <= 0 || virtualHeight <= 0)
		{
			// GetSystemMetrics failing/reporting nothing usable — never trust the saved rectangle blind.
			return false;
		}

		int overlapLeft = Math.Max(x, virtualLeft);
		int overlapTop = Math.Max(y, virtualTop);
		int overlapRight = Math.Min(x + width, virtualLeft + virtualWidth);
		int overlapBottom = Math.Min(y + height, virtualTop + virtualHeight);
		return overlapRight - overlapLeft >= MinVisibleOverlap && overlapBottom - overlapTop >= MinVisibleOverlap;
	}

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int index);
}
