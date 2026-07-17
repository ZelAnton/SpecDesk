namespace SpecDesk.Host.Tests;

/// <summary>
/// The pure window-geometry validity check (T-077): a saved rectangle is trusted when it sufficiently
/// overlaps the current virtual screen and is a sane size; it is rejected when a monitor was removed/moved
/// (the saved position is now off-screen), when it is absurdly small/large (a hand-edited/corrupt file), or
/// when the virtual screen itself is unreported.
/// </summary>
[TestFixture]
public sealed class WindowGeometryValidatorTests
{
	// A typical single 1920x1080 monitor at the origin — the common case for every "still fits" test below.
	private const int VirtualLeft = 0;
	private const int VirtualTop = 0;
	private const int VirtualWidth = 1920;
	private const int VirtualHeight = 1080;

	[Test]
	public void IsValid_ARectangleFullyOnTheCurrentVirtualScreen_IsValid()
	{
		Assert.That(
			WindowGeometryValidator.IsValid(
				100, 100, 1280, 800, VirtualLeft, VirtualTop, VirtualWidth, VirtualHeight),
			Is.True);
	}

	[Test]
	public void IsValid_ARectangleEntirelyOffTheRemovedSecondMonitor_IsInvalid()
	{
		// Was positioned on a second monitor to the right (x starting at 1920) that is no longer part of the
		// virtual screen after it was unplugged.
		Assert.That(
			WindowGeometryValidator.IsValid(
				2000, 100, 1280, 800, VirtualLeft, VirtualTop, VirtualWidth, VirtualHeight),
			Is.False);
	}

	[Test]
	public void IsValid_ARectangleMostlyAboveTheVirtualScreen_IsInvalid()
	{
		// Only a sliver at the very bottom edge overlaps — not enough to be usable.
		Assert.That(
			WindowGeometryValidator.IsValid(
				100, -1200, 1280, 800, VirtualLeft, VirtualTop, VirtualWidth, VirtualHeight),
			Is.False);
	}

	[Test]
	public void IsValid_ARectangleClippingJustTheScreenEdge_IsStillValid()
	{
		// Half on-screen, half off the left edge — still a large, clearly usable overlap.
		Assert.That(
			WindowGeometryValidator.IsValid(
				-640, 100, 1280, 800, VirtualLeft, VirtualTop, VirtualWidth, VirtualHeight),
			Is.True);
	}

	[TestCase(0, 800)]
	[TestCase(1280, 0)]
	[TestCase(50, 800)]
	[TestCase(1280, 50)]
	public void IsValid_ADimensionBelowTheMinimum_IsInvalid(int width, int height)
	{
		Assert.That(
			WindowGeometryValidator.IsValid(
				100, 100, width, height, VirtualLeft, VirtualTop, VirtualWidth, VirtualHeight),
			Is.False);
	}

	[Test]
	public void IsValid_AnAbsurdlyLargeSavedSize_IsInvalid()
	{
		// A hand-edited or corrupted file must not be restored as a comically oversized window.
		Assert.That(
			WindowGeometryValidator.IsValid(
				0, 0, 100_000, 100_000, VirtualLeft, VirtualTop, VirtualWidth, VirtualHeight),
			Is.False);
	}

	[Test]
	public void IsValid_WhenTheVirtualScreenIsUnreported_NeverTrustsTheSavedRectangleBlind()
	{
		Assert.That(
			WindowGeometryValidator.IsValid(100, 100, 1280, 800, 0, 0, 0, 0),
			Is.False);
	}

	[Test]
	public void IsValid_ASecondMonitorToTheRightOfAnExtendedVirtualScreen_IsValid()
	{
		// A saved position on a second 1920-wide monitor to the right is still within a 3840-wide virtual
		// screen (two 1920x1080 monitors side by side) — the common multi-monitor case.
		Assert.That(
			WindowGeometryValidator.IsValid(2000, 100, 1280, 800, 0, 0, 3840, 1080),
			Is.True);
	}
}
