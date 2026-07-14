using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class HostControllerContentGenerationTests
{
	private sealed class NoDialogs : IFileDialogs
	{
		public string? PickOpenFile() => null;
		public string? PickOpenFolder() => null;
		public string? PickSaveFile(string? suggestedPath) => null;
	}

	private static Renderer.RenderResult StubRender(string docDir, string text) => new(string.Empty, []);

	private string _root = string.Empty;
	private string _path = string.Empty;

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), "specdesk-content-generation-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_root);
		_path = Path.Combine(_root, "spec.md");
		File.WriteAllText(_path, "# Published");
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	[Test]
	public void AutosaveSnapshotCannotBeSupersededBySaveVersionWhileQueued()
	{
		FakeVersioning versioning = new();
		using HostController controller = NewController(versioning);
		OpenDraft(controller, "# Autosave snapshot", version: 1);
		object repoGate = PrivateField<object>(controller, "_repoGate");
		Task? autosave = null;
		Monitor.Enter(repoGate);
		try
		{
			autosave = Task.Run(controller.RunDiskAutosave);
			Assert.That(SpinUntil(() => PrivateField<bool>(controller, "_documentMutationLeaseClaimed")), Is.True);
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.DocSaveVersion,
				new SaveVersionPayload("must wait")));
			Assert.That(versioning.SaveVersionCalls, Is.Zero);
		}
		finally
		{
			Monitor.Exit(repoGate);
		}
		Assert.That(autosave!.Wait(TimeSpan.FromSeconds(5)), Is.True);
		Assert.That(File.ReadAllText(_path), Is.EqualTo("# Autosave snapshot"));

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.EditorChanged,
			new EditorChangedPayload("# Newer version"),
			version: 2));
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.DocSaveVersion,
			new SaveVersionPayload("newer")));

		Assert.Multiple(() =>
		{
			Assert.That(versioning.SaveVersionCalls, Is.EqualTo(1));
			Assert.That(File.ReadAllText(_path), Is.EqualTo("# Newer version"));
		});
	}

	[Test]
	public void CloseWaitsForQueuedAutosaveAndThenPersistsTheSameContentVersion()
	{
		using HostController controller = NewController(new FakeVersioning());
		OpenDraft(controller, "# Pending close", version: 1);
		object repoGate = PrivateField<object>(controller, "_repoGate");
		Task? autosave = null;
		bool firstClose;
		Monitor.Enter(repoGate);
		try
		{
			autosave = Task.Run(controller.RunDiskAutosave);
			Assert.That(SpinUntil(() => PrivateField<bool>(controller, "_documentMutationLeaseClaimed")), Is.True);
			firstClose = controller.TryPersistPendingLocalDraftForClose();
		}
		finally
		{
			Monitor.Exit(repoGate);
		}
		Assert.That(autosave!.Wait(TimeSpan.FromSeconds(5)), Is.True);
		bool secondClose = controller.TryPersistPendingLocalDraftForClose();

		Assert.Multiple(() =>
		{
			Assert.That(firstClose, Is.False);
			Assert.That(secondClose, Is.True);
			Assert.That(File.ReadAllText(_path), Is.EqualTo("# Pending close"));
		});
	}

	[TestCase("_documentMutationLeaseClaimed")]
	[TestCase("_documentOpenTransition")]
	[TestCase("_documentRepositoryTransition")]
	[TestCase("_documentDiscardTransition")]
	[TestCase("_closePreparationClaimed")]
	public void EditorChangeQueuedAtAnIdentityBoundaryIsRejectedAtomically(string boundaryField)
	{
		using HostController controller = NewController(new FakeVersioning());
		OpenDraft(controller, "# Accepted", version: 1);
		object sync = PrivateField<object>(controller, "_sync");
		FieldInfo boundary = typeof(HostController).GetField(
			boundaryField, BindingFlags.Instance | BindingFlags.NonPublic)!;
		long before = PrivateField<long>(controller, "_contentGeneration");
		Task? changed = null;
		Monitor.Enter(sync);
		try
		{
			changed = Task.Run(() => controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.EditorChanged,
				new EditorChangedPayload("# Rejected"),
				version: 2)));
			Assert.That(SpinUntil(() => changed.Status == TaskStatus.Running), Is.True);
			boundary.SetValue(controller, true);
		}
		finally
		{
			Monitor.Exit(sync);
		}
		Assert.That(changed!.Wait(TimeSpan.FromSeconds(5)), Is.True);
		lock (sync)
		{
			boundary.SetValue(controller, false);
		}
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSave));

		Assert.Multiple(() =>
		{
			Assert.That(PrivateField<long>(controller, "_contentGeneration"), Is.EqualTo(before));
			Assert.That(File.ReadAllText(_path), Is.EqualTo("# Accepted"));
		});

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.EditorChanged,
			new EditorChangedPayload("# Accepted after boundary"),
			version: 2));
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSave));

		Assert.Multiple(() =>
		{
			Assert.That(PrivateField<long>(controller, "_contentGeneration"), Is.EqualTo(before + 1));
			Assert.That(File.ReadAllText(_path), Is.EqualTo("# Accepted after boundary"));
		});
	}

	[Test]
	public void EditorFrameQueuedBeforeOpenIsPersistedBeforeTheNewIdentityLoads()
	{
		using HostController controller = NewController(new FakeVersioning());
		OpenDraft(controller, "# Accepted", version: 1);
		string nextPath = Path.Combine(_root, "next.md");
		File.WriteAllText(nextPath, "# Next");
		object sync = PrivateField<object>(controller, "_sync");
		object documentMessages = PrivateField<object>(controller, "_remotePublishSync");
		Task? changed = null;
		Task? opened = null;
		Monitor.Enter(sync);
		try
		{
			changed = Task.Run(() => controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.EditorChanged,
				new EditorChangedPayload("# Last frame"),
				version: 2)));
			Assert.That(SpinUntil(() => MonitorIsHeldByAnotherThread(documentMessages)), Is.True);
			opened = Task.Run(() => controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.DocOpen,
				new DocOpenPayload(nextPath, RequestId: 991))));
		}
		finally
		{
			Monitor.Exit(sync);
		}
		Assert.That(Task.WaitAll([changed!, opened!], TimeSpan.FromSeconds(5)), Is.True);

		Assert.Multiple(() =>
		{
			Assert.That(File.ReadAllText(_path), Is.EqualTo("# Last frame"));
			Assert.That(PrivateField<string?>(controller, "_currentPath"), Is.EqualTo(nextPath));
		});
	}
	[Test]
	public void FailedOpenRearmsTheOldDraftBeforePublishingCompletion()
	{
		using HostController controller = NewController(new FakeVersioning());
		OpenDraft(controller, "# Still editing", version: 1);
		string missingPath = Path.Combine(_root, "missing.md");

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.DocOpen,
			new DocOpenPayload(missingPath, RequestId: 993)));

		Assert.Multiple(() =>
		{
			Assert.That(PrivateField<string?>(controller, "_currentPath"), Is.EqualTo(_path));
			Assert.That(PrivateField<bool>(controller, "_documentOpenTransition"), Is.False);
			Assert.That(PrivateField<Timer?>(controller, "_autosaveTimer"), Is.Not.Null);
		});
	}
	[Test]
	public void OpenDoesNotHoldRemotePublicationWhileWaitingForRepositoryPublication()
	{
		using HostController controller = NewController(new FakeVersioning());
		string nextPath = Path.Combine(_root, "next.md");
		File.WriteAllText(nextPath, "# Next");
		object clonePublication = PrivateField<object>(controller, "_clonePublishSync");
		object remotePublication = PrivateField<object>(controller, "_remotePublishSync");
		using ManualResetEventSlim repositoryHasClone = new(false);
		using ManualResetEventSlim allowRepositoryRemote = new(false);
		bool repositoryAcquiredRemote = false;

		Task repositoryPublication = Task.Run(() =>
		{
			lock (clonePublication)
			{
				repositoryHasClone.Set();
				if (!allowRepositoryRemote.Wait(TimeSpan.FromSeconds(5)))
				{
					return;
				}
				repositoryAcquiredRemote = Monitor.TryEnter(remotePublication, TimeSpan.FromSeconds(2));
				if (repositoryAcquiredRemote)
				{
					Monitor.Exit(remotePublication);
				}
			}
		});
		Assert.That(repositoryHasClone.Wait(TimeSpan.FromSeconds(5)), Is.True);

		Task open = Task.Run(() => controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.DocOpen,
			new DocOpenPayload(nextPath, RequestId: 992))));
		Assert.That(SpinUntil(() => open.Status == TaskStatus.Running), Is.True);
		Thread.Sleep(50);
		allowRepositoryRemote.Set();

		Assert.Multiple(() =>
		{
			Assert.That(repositoryPublication.Wait(TimeSpan.FromSeconds(5)), Is.True);
			Assert.That(repositoryAcquiredRemote, Is.True,
				"repository publication must be able to take remote publication while Open waits for clone publication");
			Assert.That(open.Wait(TimeSpan.FromSeconds(5)), Is.True);
			Assert.That(PrivateField<string?>(controller, "_currentPath"), Is.EqualTo(nextPath));
		});
	}
	private HostController NewController(FakeVersioning versioning) => new(
		StubRender,
		_ => { },
		new NoDialogs(),
		(_, _, _, _, _) => null,
		versioning,
		NullLogger<HostController>.Instance,
		_path,
		TimeSpan.FromMinutes(10));

	private static void OpenDraft(HostController controller, string text, long version)
	{
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.EditorChanged,
			new EditorChangedPayload(text),
			version: version));
	}

	private static T PrivateField<T>(HostController controller, string name) =>
		(T)typeof(HostController)
			.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(controller)!;

	private static bool MonitorIsHeldByAnotherThread(object monitor)
	{
		if (!Monitor.TryEnter(monitor))
		{
			return true;
		}
		Monitor.Exit(monitor);
		return false;
	}
	private static bool SpinUntil(Func<bool> condition)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
		{
			if (condition())
			{
				return true;
			}
			Thread.Sleep(5);
		}
		return condition();
	}
}