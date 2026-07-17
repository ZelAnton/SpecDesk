using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

/// <summary>
/// The preferences slice of the controller (HostController.Preferences.cs, T-077): serving the current
/// theme/wrap/view-mode (preferences.request → preferences.state), persisting an author-driven change
/// (preferences.update) through the wired <see cref="PreferencesStore"/> without any reply, and the
/// graceful degradation when no store is wired at all (a test harness that doesn't pass one).
/// </summary>
[TestFixture]
public sealed class HostControllerPreferencesTests
{
	private sealed class NoDialogs : IFileDialogs
	{
		public string? PickOpenFile() => null;
		public string? PickOpenFolder() => null;
		public string? PickSaveFile(string? suggestedPath) => null;
	}

	private static Renderer.RenderResult StubRender(string docDir, string text) => new(string.Empty, []);

	private string _dir = string.Empty;
	private string _path = string.Empty;
	private readonly List<string> _sent = [];
	private readonly object _gate = new();

	[SetUp]
	public void SetUp()
	{
		_dir = Path.Combine(Path.GetTempPath(), "specdesk-prefs-hc-" + Guid.NewGuid().ToString("N"));
		_path = Path.Combine(_dir, "preferences.json");
		lock (_gate)
		{
			_sent.Clear();
		}
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(_dir))
		{
			Directory.Delete(_dir, recursive: true);
		}
	}

	private HostController NewController(PreferencesStore? preferences)
	{
		void Send(string json)
		{
			lock (_gate)
			{
				_sent.Add(json);
			}
		}

		return new HostController(
			StubRender,
			Send,
			new NoDialogs(),
			(_, _, _, _, _) => null,
			new FakeVersioning(),
			NullLogger<HostController>.Instance,
			initialDocPath: null,
			preferences: preferences);
	}

	private PreferencesPayload? LatestPreferencesState()
	{
		lock (_gate)
		{
			for (int index = _sent.Count - 1; index >= 0; index--)
			{
				IpcMessage? message = IpcSerializer.TryDeserialize(_sent[index]);
				if (message?.Kind == MessageKinds.PreferencesState)
				{
					return message.GetPayload<PreferencesPayload>();
				}
			}
			return null;
		}
	}

	[Test]
	public void PreferencesRequest_WithNoStoreWired_EmitsTheSameDefaultsTheWebviewAlreadyAssumed()
	{
		using HostController controller = NewController(preferences: null);

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.PreferencesRequest));

		PreferencesPayload? state = LatestPreferencesState();
		Assert.That(state, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(state!.Theme, Is.Null);
			Assert.That(state.Wrap, Is.True);
			Assert.That(state.ViewMode, Is.EqualTo("split"));
		});
	}

	[Test]
	public void PreferencesRequest_WithAStoreWired_EmitsItsCurrentSavedState()
	{
		PreferencesStore store = new(_path);
		store.Update("dark", wrap: false, "formatted");
		using HostController controller = NewController(store);

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.PreferencesRequest));

		PreferencesPayload? state = LatestPreferencesState();
		Assert.That(state, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(state!.Theme, Is.EqualTo("dark"));
			Assert.That(state.Wrap, Is.False);
			Assert.That(state.ViewMode, Is.EqualTo("formatted"));
		});
	}

	[Test]
	public void PreferencesUpdate_PersistsThroughTheWiredStore_WithoutAnyReply()
	{
		PreferencesStore store = new(_path);
		using HostController controller = NewController(store);

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.PreferencesUpdate, new PreferencesPayload("light", true, "code")));

		// Write-only: unlike workspace.state, an update never triggers a broadcast reply.
		lock (_gate)
		{
			Assert.That(_sent, Is.Empty);
		}
		PreferencesPayload reloaded = new PreferencesStore(_path).State();
		Assert.Multiple(() =>
		{
			Assert.That(reloaded.Theme, Is.EqualTo("light"));
			Assert.That(reloaded.Wrap, Is.True);
			Assert.That(reloaded.ViewMode, Is.EqualTo("code"));
		});
	}

	[Test]
	public void PreferencesUpdate_WithNoStoreWired_IsANoOpRatherThanThrowing()
	{
		using HostController controller = NewController(preferences: null);

		Assert.DoesNotThrow(() => controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.PreferencesUpdate, new PreferencesPayload("dark", false, "split"))));
	}
}
