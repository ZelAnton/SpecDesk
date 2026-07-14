using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class HostControllerOutboundTests
{
	private sealed class NoDialogs : IFileDialogs
	{
		public string? PickOpenFile() => null;
		public string? PickOpenFolder() => null;
		public string? PickSaveFile(string? suggestedPath) => null;
	}

	private static readonly MethodInfo SendErrorMethod = typeof(HostController).GetMethod(
		"SendError", BindingFlags.Instance | BindingFlags.NonPublic)!;

	private static readonly string[] ExpectedErrors = ["first", "second", "reentered"];
	private static readonly string[] ExpectedBatchOrder = ["first", "callback", "reentered"];
	private static Renderer.RenderResult StubRender(string docDir, string text) => new(string.Empty, []);

	private static HostController NewController(Action<string> send) => new(
		StubRender,
		send,
		new NoDialogs(),
		(_, _, _, _, _) => null,
		new FakeVersioning(),
		NullLogger<HostController>.Instance);

	private static void SendError(HostController controller, string message) =>
		SendErrorMethod.Invoke(controller, [message]);

	private static object CloneGate(HostController controller) =>
		ControllerGate(controller, "_clonePublishSync");

	private static object ControllerGate(HostController controller, string name) =>
		typeof(HostController).GetField(
			name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;

	[Test]
	public void SendOutsideControllerMonitorsRemainsSynchronous()
	{
		int sends = 0;
		using HostController controller = NewController(_ => sends++);

		SendError(controller, "synchronous");

		Assert.That(sends, Is.EqualTo(1));
	}

	[Test]
	public void QueuedFramesDrainInFifoOrderWithoutControllerMonitorsAndAllowReentry()
	{
		List<string> errors = [];
		List<bool> monitorStates = [];
		using ManualResetEventSlim drainStarting = new(false);
		using ManualResetEventSlim releaseDrain = new(false);
		using ManualResetEventSlim complete = new(false);
		HostController? controllerRef = null;
		using HostController controller = controllerRef = NewController(json =>
		{
			monitorStates.Add(controllerRef!.IsControllerMonitorEnteredForTest());
			IpcMessage? message = IpcSerializer.TryDeserialize(json);
			string? error = message?.Kind == MessageKinds.Error
				? message.GetPayload<ErrorPayload>()?.Message
				: null;
			if (error is null)
			{
				return;
			}
			errors.Add(error);
			if (error == "first")
			{
				SendError(controllerRef, "reentered");
			}
			if (errors.Count == 3)
			{
				complete.Set();
			}
		});

		controller.OutboundDrainStartingForTest = () =>
		{
			drainStarting.Set();
			releaseDrain.Wait();
		};
		object gate = CloneGate(controller);
		lock (gate)
		{
			SendError(controller, "first");
			SendError(controller, "second");
		}
		bool drainWasScheduled = drainStarting.Wait(TimeSpan.FromSeconds(2));
		bool noSendBeforeRelease = errors.Count == 0;
		releaseDrain.Set();

		Assert.That(complete.Wait(TimeSpan.FromSeconds(5)), Is.True);
		Assert.Multiple(() =>
		{
			Assert.That(drainWasScheduled, Is.True);
			Assert.That(noSendBeforeRelease, Is.True);
			Assert.That(errors, Is.EqualTo(ExpectedErrors));
			Assert.That(monitorStates, Has.All.False);
		});
	}

	[Test]
	public void SendOutsideControllerMonitorsDoesNotWaitForARepoGateOwnedByAnotherThread()
	{
		int sends = 0;
		using HostController controller = NewController(_ => Interlocked.Increment(ref sends));
		using ManualResetEventSlim gateHeld = new(false);
		using ManualResetEventSlim releaseGate = new(false);
		object repoGate = ControllerGate(controller, "_repoGate");
		Task holder = Task.Run(() =>
		{
			lock (repoGate)
			{
				gateHeld.Set();
				releaseGate.Wait();
			}
		});
		Assert.That(gateHeld.Wait(TimeSpan.FromSeconds(2)), Is.True);

		Task emit = Task.Run(() => SendError(controller, "do not wait"));
		bool completedWithoutWaiting = emit.Wait(TimeSpan.FromMilliseconds(500));
		releaseGate.Set();
		Assert.That(Task.WaitAll([holder, emit], TimeSpan.FromSeconds(2)), Is.True);

		Assert.Multiple(() =>
		{
			Assert.That(completedWithoutWaiting, Is.True);
			Assert.That(sends, Is.EqualTo(1));
		});
	}

	[Test]
	public void DisposeWaitsForADequeuedFrameBeforeReturning()
	{
		int order = 0;
		int sendOrder = 0;
		int disposeReturnOrder = 0;
		using ManualResetEventSlim dequeued = new(false);
		using ManualResetEventSlim releaseSend = new(false);
		HostController controller = NewController(_ => sendOrder = Interlocked.Increment(ref order));
		controller.OutboundFrameDequeuedForTest = () =>
		{
			dequeued.Set();
			releaseSend.Wait();
		};

		Task emit = Task.Run(() => SendError(controller, "already dequeued"));
		Assert.That(dequeued.Wait(TimeSpan.FromSeconds(2)), Is.True);
		Task dispose = Task.Run(() =>
		{
			controller.Dispose();
			disposeReturnOrder = Interlocked.Increment(ref order);
		});
		bool disposeWaited = !dispose.Wait(TimeSpan.FromMilliseconds(200));
		releaseSend.Set();
		Assert.That(Task.WaitAll([emit, dispose], TimeSpan.FromSeconds(2)), Is.True);

		Assert.Multiple(() =>
		{
			Assert.That(disposeWaited, Is.True);
			Assert.That(sendOrder, Is.GreaterThan(0));
			Assert.That(disposeReturnOrder, Is.GreaterThan(sendOrder));
		});
	}

	[Test]
	public void ReentrantDisposeFromSendDoesNotDeadlock()
	{
		using ManualResetEventSlim disposed = new(false);
		HostController? controller = null;
		controller = NewController(_ =>
		{
			controller!.Dispose();
			disposed.Set();
		});

		Task emit = Task.Run(() => SendError(controller!, "dispose reentrantly"));
		Assert.Multiple(() =>
		{
			Assert.That(emit.Wait(TimeSpan.FromSeconds(2)), Is.True);
			Assert.That(disposed.IsSet, Is.True);
		});
	}
	[Test]
	public void OutboundBatchCompletionRunsAfterEarlierSendAndAllowsReentry()
	{
		List<string> order = [];
		List<bool> monitorStates = [];
		using ManualResetEventSlim complete = new(false);
		HostController? controllerRef = null;
		using HostController controller = controllerRef = NewController(json =>
		{
			string? error = IpcSerializer.TryDeserialize(json)?.GetPayload<ErrorPayload>()?.Message;
			if (error is not null)
			{
				order.Add(error);
				monitorStates.Add(controllerRef!.IsControllerMonitorEnteredForTest());
				if (error == "reentered")
				{
					complete.Set();
				}
			}
		});

		object gate = CloneGate(controller);
		lock (gate)
		{
			SendError(controller, "first");
			controller.CompleteOutboundBatchForTest(() =>
			{
				order.Add("callback");
				monitorStates.Add(controller.IsControllerMonitorEnteredForTest());
				SendError(controller, "reentered");
			});
		}

		Assert.That(complete.Wait(TimeSpan.FromSeconds(2)), Is.True);
		Assert.Multiple(() =>
		{
			Assert.That(order, Is.EqualTo(ExpectedBatchOrder));
			Assert.That(monitorStates, Has.All.False);
		});
	}

	[Test]
	public void DisposeSettlesAQueuedBatchCompletionOutsideLocksWithoutSendingItsFrame()
	{
		int sends = 0;
		int completionCalls = 0;
		bool callbackMonitorEntered = true;
		bool callbackCloseResult = true;
		using ManualResetEventSlim drainStarting = new(false);
		using ManualResetEventSlim releaseDrain = new(false);
		HostController controller = NewController(_ => Interlocked.Increment(ref sends));
		controller.OutboundDrainStartingForTest = () =>
		{
			drainStarting.Set();
			releaseDrain.Wait();
		};
		object gate = CloneGate(controller);
		lock (gate)
		{
			SendError(controller, "drop before send");
			controller.CompleteOutboundBatchForTest(() =>
			{
				callbackMonitorEntered = controller.IsControllerMonitorEnteredForTest();
				callbackCloseResult = controller.TryPersistPendingLocalDraftForClose();
				Interlocked.Increment(ref completionCalls);
			});
		}
		Assert.That(drainStarting.Wait(TimeSpan.FromSeconds(2)), Is.True);

		Task dispose = Task.Run(controller.Dispose);
		Assert.That(dispose.Wait(TimeSpan.FromSeconds(2)), Is.True);
		releaseDrain.Set();
		Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref sends) != 0, 200), Is.False);
		controller.Dispose();

		Assert.Multiple(() =>
		{
			Assert.That(completionCalls, Is.EqualTo(1));
			Assert.That(callbackMonitorEntered, Is.False);
			Assert.That(callbackCloseResult, Is.False);
		});
	}
	[Test]
	public void DisposeDropsFramesStillQueuedBeforeTheDrainerDequeuesThem()
	{
		int sends = 0;
		using ManualResetEventSlim drainStarting = new(false);
		using ManualResetEventSlim releaseDrain = new(false);
		HostController controller = NewController(_ => Interlocked.Increment(ref sends));
		controller.OutboundDrainStartingForTest = () =>
		{
			drainStarting.Set();
			releaseDrain.Wait();
		};
		object gate = CloneGate(controller);
		lock (gate)
		{
			SendError(controller, "drop me");
			Assert.That(drainStarting.Wait(TimeSpan.FromSeconds(2)), Is.True);
			controller.Dispose();
		}
		releaseDrain.Set();

		Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref sends) != 0, 200), Is.False);
	}
}