using SpecDesk.Contracts;

namespace SpecDesk.Host.Tests;

/// <summary>
/// The persisted UI-preferences store (T-077): defaults when nothing was ever saved, a round-trip of a
/// theme/wrap/view-mode update through a real temp file, window-geometry persistence, an unrecognized
/// view mode coercing to Split rather than corrupting the file, and the corruption-tolerant load
/// (missing / corrupt → the same defaults, never a thrown exception).
/// </summary>
[TestFixture]
public sealed class PreferencesStoreTests
{
	private string _dir = string.Empty;
	private string _path = string.Empty;

	[SetUp]
	public void SetUp()
	{
		_dir = Path.Combine(Path.GetTempPath(), "specdesk-prefs-" + Guid.NewGuid().ToString("N"));
		_path = Path.Combine(_dir, "preferences.json");
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(_dir))
		{
			Directory.Delete(_dir, recursive: true);
		}
	}

	[Test]
	public void State_WhenNothingWasEverSaved_IsTheSameDefaultsTheWebviewAlreadyAssumed()
	{
		PreferencesPayload state = new PreferencesStore(_path).State();

		Assert.Multiple(() =>
		{
			// Theme absent: the webview falls back to the OS colour scheme, exactly as before this store existed.
			Assert.That(state.Theme, Is.Null);
			Assert.That(state.Wrap, Is.True);
			Assert.That(state.ViewMode, Is.EqualTo("split"));
		});
	}

	[Test]
	public void Window_WhenNothingWasEverSaved_IsNull()
	{
		Assert.That(new PreferencesStore(_path).Window, Is.Null);
	}

	[Test]
	public void Update_ThenReopened_RoundTripsTheThemeWrapAndViewMode()
	{
		PreferencesStore store = new(_path);
		store.Update("dark", wrap: false, "formatted");

		PreferencesPayload reloaded = new PreferencesStore(_path).State();
		Assert.Multiple(() =>
		{
			Assert.That(reloaded.Theme, Is.EqualTo("dark"));
			Assert.That(reloaded.Wrap, Is.False);
			Assert.That(reloaded.ViewMode, Is.EqualTo("formatted"));
		});
	}

	[Test]
	public void Update_WithANullTheme_ClearsAnyPreviouslySavedOverride()
	{
		PreferencesStore store = new(_path);
		store.Update("light", wrap: true, "code");
		store.Update(null, wrap: true, "code");

		Assert.That(new PreferencesStore(_path).State().Theme, Is.Null);
	}

	[Test]
	public void Update_WithAnUnrecognizedViewMode_CoercesToSplitRatherThanCorruptingTheFile()
	{
		PreferencesStore store = new(_path);
		store.Update("dark", wrap: true, "wide-screen");

		Assert.That(new PreferencesStore(_path).State().ViewMode, Is.EqualTo("split"));
	}

	[Test]
	public void SetWindowGeometry_ThenReopened_RoundTripsPositionSizeAndMaximized()
	{
		PreferencesStore store = new(_path);
		store.SetWindowGeometry(100, 200, 1024, 768, maximized: true);

		WindowGeometry? reloaded = new PreferencesStore(_path).Window;
		Assert.That(reloaded, Is.EqualTo(new WindowGeometry(100, 200, 1024, 768, Maximized: true)));
	}

	[Test]
	public void Load_WhenTheFileIsMissing_IsTheDefaultsRatherThanThrowing()
	{
		PreferencesPayload state = new PreferencesStore(_path).State();
		Assert.That(state, Is.EqualTo(new PreferencesPayload(null, true, "split")));
	}

	[Test]
	public void Load_WhenTheFileIsCorrupt_IsTheDefaultsRatherThanThrowing()
	{
		Directory.CreateDirectory(_dir);
		File.WriteAllText(_path, "{ this is not valid json ]");

		PreferencesPayload state = new PreferencesStore(_path).State();
		Assert.That(state, Is.EqualTo(new PreferencesPayload(null, true, "split")));
	}

	[Test]
	public void Load_WhenTheSavedWindowGeometryHasNonPositiveDimensions_IsDiscardedRatherThanRestored()
	{
		Directory.CreateDirectory(_dir);
		File.WriteAllText(
			_path,
			"""{ "window": { "x": 0, "y": 0, "width": 0, "height": 0, "maximized": false } }""");

		Assert.That(new PreferencesStore(_path).Window, Is.Null);
	}
}
