using SpecDesk.Contracts;

namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class WindowChromeControllerTests
{
	[TestCase(100, 100, WindowHitTest.TopLeft)]
	[TestCase(899, 100, WindowHitTest.TopRight)]
	[TestCase(100, 699, WindowHitTest.BottomLeft)]
	[TestCase(899, 699, WindowHitTest.BottomRight)]
	[TestCase(500, 101, WindowHitTest.Top)]
	[TestCase(101, 400, WindowHitTest.Left)]
	[TestCase(898, 400, WindowHitTest.Right)]
	[TestCase(500, 698, WindowHitTest.Bottom)]
	[TestCase(500, 400, WindowHitTest.Client)]
	public void ChromelessHitTestReturnsStandardResizeEdges(int x, int y, int expected)
	{
		WindowHitTest actual = WindowChromeGeometry.HitTest(
			new WindowRect(100, 100, 900, 700),
			new WindowPoint(x, y),
			horizontalBorder: 8,
			verticalBorder: 8);

		Assert.That((int)actual, Is.EqualTo(expected));
	}

	[Test]
	public void MaximizedBoundsUseMonitorRelativeWorkArea()
	{
		MaximizedWindowBounds bounds = WindowChromeGeometry.MaximizedBounds(
			new WindowRect(-1920, 0, 0, 1080),
			new WindowRect(-1920, 0, 0, 1040));

		Assert.That(bounds, Is.EqualTo(new MaximizedWindowBounds(0, 0, 1920, 1040)));
	}

	[TestCase(MessageKinds.WindowMinimize, 0)]
	[TestCase(MessageKinds.WindowToggleMaximize, 1)]
	[TestCase(MessageKinds.WindowClose, 2)]
	[TestCase(MessageKinds.WindowDrag, 3)]
	public void RoutesOnlyTheAllowListedWindowCommand(string kind, int expectedAction)
	{
		List<int> actions = [];
		WindowCommandRouter router = new(
			() => actions.Add(0),
			() => actions.Add(1),
			_ => actions.Add(2),
			() => actions.Add(3));

		bool handled = router.TryHandle(IpcSerializer.SerializeEvent(kind));

		Assert.Multiple(() =>
		{
			Assert.That(handled, Is.True);
			Assert.That(actions, Is.EqualTo(new[] { expectedAction }));
		});
	}

	[Test]
	public void LeavesNonWindowAndMalformedFramesForTheHostController()
	{
		int calls = 0;
		WindowCommandRouter router = new(
			() => calls++,
			() => calls++,
			_ => calls++,
			() => calls++);

		Assert.Multiple(() =>
		{
			Assert.That(router.TryHandle(IpcSerializer.SerializeEvent(MessageKinds.Ready)), Is.False);
			Assert.That(router.TryHandle("not json"), Is.False);
			Assert.That(calls, Is.Zero);
		});
	}

	[Test]
	public void NativeCloseAfterReadyDefersUntilMatchingFlushAcknowledgementAndDraftPersist()
	{
		List<long> requestedFlushes = [];
		List<string> actions = [];
		WindowCloseCoordinator coordinator = new(
			requestId => requestedFlushes.Add(requestId),
			() =>
			{
				actions.Add("persist");
				return true;
			},
			(_, _) => actions.Add("complete"),
			() => actions.Add("close"));
		coordinator.MarkWebviewReady();

		bool deferred = coordinator.HandleNativeClosing();
		bool repeatedCloseDeferred = coordinator.HandleNativeClosing();
		coordinator.HandleWebClose(requestedFlushes.Single());

		Assert.Multiple(() =>
		{
			Assert.That(deferred, Is.True);
			Assert.That(repeatedCloseDeferred, Is.True);
			Assert.That(requestedFlushes, Has.Count.EqualTo(1));
			Assert.That(actions, Has.Count.EqualTo(2));
			Assert.That(actions[0], Is.EqualTo("persist"));
			Assert.That(actions[1], Is.EqualTo("close"));
			Assert.That(coordinator.HandleNativeClosing(), Is.False);
		});
	}

	[Test]
	public void PersistFailureKeepsWindowOpenAndAllowsASecondCloseAttempt()
	{
		List<long> requestedFlushes = [];
		List<(long RequestId, bool Succeeded)> completions = [];
		int closeCalls = 0;
		WindowCloseCoordinator coordinator = new(
			requestId => requestedFlushes.Add(requestId),
			() => false,
			(requestId, succeeded) => completions.Add((requestId, succeeded)),
			() => closeCalls++);
		coordinator.MarkWebviewReady();

		Assert.That(coordinator.HandleNativeClosing(), Is.True);
		long failedRequestId = requestedFlushes.Single();
		coordinator.HandleWebClose(failedRequestId);
		Assert.That(coordinator.HandleNativeClosing(), Is.True);

		Assert.Multiple(() =>
		{
			Assert.That(completions, Has.Count.EqualTo(1));
			Assert.That(completions[0], Is.EqualTo((failedRequestId, false)));
			Assert.That(closeCalls, Is.Zero);
			Assert.That(requestedFlushes, Has.Count.EqualTo(2));
			Assert.That(requestedFlushes[1], Is.GreaterThan(failedRequestId));
		});
	}

	[Test]
	public void StaleFlushAcknowledgementCannotCloseWindow()
	{
		List<long> requestedFlushes = [];
		int persistCalls = 0;
		int closeCalls = 0;
		WindowCloseCoordinator coordinator = new(
			requestId => requestedFlushes.Add(requestId),
			() =>
			{
				persistCalls++;
				return true;
			},
			(_, _) => { },
			() => closeCalls++);
		coordinator.MarkWebviewReady();
		Assert.That(coordinator.HandleNativeClosing(), Is.True);

		coordinator.HandleWebClose(requestedFlushes.Single() + 1);

		Assert.Multiple(() =>
		{
			Assert.That(persistCalls, Is.Zero);
			Assert.That(closeCalls, Is.Zero);
		});
	}

	[Test]
	public void NativeCloseBeforeWebviewReadyIsNotDeferred()
	{
		int flushRequests = 0;
		WindowCloseCoordinator coordinator = new(
			_ => flushRequests++,
			() => true,
			(_, _) => { },
			() => { });

		Assert.Multiple(() =>
		{
			Assert.That(coordinator.HandleNativeClosing(), Is.False);
			Assert.That(flushRequests, Is.Zero);
		});
	}
}
