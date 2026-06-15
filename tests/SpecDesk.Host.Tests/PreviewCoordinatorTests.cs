namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class PreviewCoordinatorTests
{
	[Test]
	public void ShouldRender_AcceptsMonotonicallyIncreasingVersions()
	{
		PreviewCoordinator coordinator = new();

		Assert.Multiple(() =>
		{
			Assert.That(coordinator.ShouldRender(1), Is.True);
			Assert.That(coordinator.ShouldRender(2), Is.True);
			Assert.That(coordinator.ShouldRender(3), Is.True);
			Assert.That(coordinator.Latest, Is.EqualTo(3));
		});
	}

	[Test]
	public void ShouldRender_RejectsStaleOrDuplicateVersions()
	{
		PreviewCoordinator coordinator = new();
		coordinator.ShouldRender(5);

		Assert.Multiple(() =>
		{
			Assert.That(coordinator.ShouldRender(4), Is.False, "an out-of-order older edit is dropped");
			Assert.That(coordinator.ShouldRender(5), Is.False, "a duplicate version is dropped");
			Assert.That(coordinator.Latest, Is.EqualTo(5), "stale frames do not move the latest version");
		});
	}

	[Test]
	public void ShouldEmit_AcceptsTheNewestVersionAndDropsSupersededRenders()
	{
		PreviewCoordinator coordinator = new();
		coordinator.ShouldRender(7);

		Assert.Multiple(() =>
		{
			Assert.That(coordinator.ShouldEmit(7), Is.True, "the render for the newest edit is sent");
			Assert.That(coordinator.ShouldEmit(6), Is.False, "a render superseded by a newer edit is dropped");
		});
	}

	[Test]
	public void ShouldEmit_DropsAnInFlightRenderOnceANewerEditArrives()
	{
		PreviewCoordinator coordinator = new();
		coordinator.ShouldRender(1); // start rendering v1
		coordinator.ShouldRender(2); // v2 arrives while v1 is still in flight

		Assert.Multiple(() =>
		{
			Assert.That(coordinator.ShouldEmit(1), Is.False, "v1 is now stale");
			Assert.That(coordinator.ShouldEmit(2), Is.True, "v2 is the newest");
		});
	}
}
