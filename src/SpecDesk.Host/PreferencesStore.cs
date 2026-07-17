using System.Text.Json;
using SpecDesk.Contracts;

namespace SpecDesk.Host;

/// <summary>The native window's last known geometry (T-077): position, size, and whether it was maximized.
/// Captured from the live <c>PhotinoWindow</c> on move/resize/maximize/restore and again on close, and
/// restored at the next startup only if it is still valid for the current monitor configuration (see
/// <c>WindowGeometryValidator</c> and <c>Program.cs</c>).</summary>
public sealed record WindowGeometry(int X, int Y, int Width, int Height, bool Maximized);

/// <summary>
/// Persisted UI preferences (T-077): the color theme, the editor's line-wrap toggle, the active view mode
/// (Code/Split/Formatted), and the native window's last geometry — a host-owned JSON sidecar under
/// <c>AppPaths.Preferences</c>. Mirrors <see cref="WorkspaceStore"/>: System.Text.Json (camelCase),
/// directory-create on save, an atomic write-then-rename, and a corruption-tolerant load — a missing,
/// empty, or malformed file reads as the same defaults the webview already assumed before this store
/// existed (OS colour scheme, wrap on, Split, no saved window geometry) rather than faulting the app.
/// </summary>
public sealed class PreferencesStore
{
	private const int SaveReplaceAttempts = 6;

	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
	};

	// Guards every field below: the message thread (a preferences.update) and Program.cs's debounced
	// window-geometry persistence can both call into this store concurrently.
	private readonly object _sync = new();
	private readonly string _path;
	private string? _theme;
	private bool _wrap;
	private string _viewMode;
	private WindowGeometry? _window;

	public PreferencesStore(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		_path = path;
		PersistedState? state = Load();
		_theme = NormalizeTheme(state?.Theme);
		_wrap = state?.Wrap ?? true;
		_viewMode = NormalizeViewMode(state?.ViewMode);
		_window = NormalizeWindow(state?.Window);
	}

	/// <summary>The last-saved native window geometry, or <c>null</c> when nothing has been saved yet (or the
	/// saved record failed its basic sanity check on load). <c>Program.cs</c> additionally checks this
	/// against the CURRENT monitor configuration before trusting it (<c>WindowGeometryValidator</c>).</summary>
	public WindowGeometry? Window
	{
		get
		{
			lock (_sync)
			{
				return _window;
			}
		}
	}

	/// <summary>The current theme/wrap/view-mode triple, for <c>preferences.state</c>.</summary>
	public PreferencesPayload State()
	{
		lock (_sync)
		{
			return new PreferencesPayload(_theme, _wrap, _viewMode);
		}
	}

	/// <summary>
	/// Persist an author-driven theme/wrap/view-mode change (<c>preferences.update</c>). <paramref
	/// name="theme"/> of <c>null</c> means "no explicit preference" (fall back to the OS colour scheme); an
	/// unrecognized <paramref name="viewMode"/> is coerced to Split rather than rejected outright, so a stale
	/// build reading a file written by a newer one (a future added mode) degrades gracefully instead of
	/// discarding the whole preference set.
	/// </summary>
	public void Update(string? theme, bool wrap, string viewMode)
	{
		lock (_sync)
		{
			_theme = NormalizeTheme(theme);
			_wrap = wrap;
			_viewMode = NormalizeViewMode(viewMode);
			Save();
		}
	}

	/// <summary>Persist the native window's current geometry/maximized state (<c>Program.cs</c>, on
	/// move/resize/maximize/restore/close).</summary>
	public void SetWindowGeometry(int x, int y, int width, int height, bool maximized)
	{
		lock (_sync)
		{
			_window = new WindowGeometry(x, y, width, height, maximized);
			Save();
		}
	}

	private static string? NormalizeTheme(string? theme) => theme is "light" or "dark" ? theme : null;

	private static string NormalizeViewMode(string? viewMode) =>
		viewMode is "code" or "split" or "formatted" ? viewMode : "split";

	private static WindowGeometry? NormalizeWindow(WindowGeometry? window) =>
		window is { Width: > 0, Height: > 0 } ? window : null;

	// Corruption-tolerant load: a missing, empty, or malformed file reads as null (the defaults above) rather
	// than faulting startup — the same policy as WorkspaceStore/PromptTemplateStore.
	private PersistedState? Load()
	{
		if (!File.Exists(_path))
		{
			return null;
		}

		try
		{
			string json;
			// Close the snapshot handle before parsing so an atomic save is not held up by JSON deserialization.
			using (FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
			using (StreamReader reader = new(stream))
			{
				json = reader.ReadToEnd();
			}
			return JsonSerializer.Deserialize<PersistedState>(json, SerializerOptions);
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
		{
			// A corrupt/unreadable store opens as the defaults: the app starts as though nothing had ever
			// been saved rather than crashing on a tampered or inaccessible file.
			return null;
		}
	}

	// Persist the whole store as one JSON object, creating the parent directory if needed. Always called
	// while holding _sync, so the serialized snapshot is internally consistent. Best-effort: a toggle click or
	// a window-move event must not throw and unwind its caller on a read-only/AV-locked file or a full disk —
	// only the on-disk copy is skipped this time.
	private void Save()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
			// Write-then-rename so a crash or power loss mid-write can't truncate the file: the reader only
			// ever sees the complete old file or the complete new one. File.Move(overwrite) is an atomic
			// replace on the same volume (the temp sits beside the target, so it always is).
			string temp = _path + ".tmp";
			File.WriteAllText(
				temp,
				JsonSerializer.Serialize(new PersistedState(_theme, _wrap, _viewMode, _window), SerializerOptions));
			for (int attempt = 1; ; attempt++)
			{
				try
				{
					File.Move(temp, _path, overwrite: true);
					break;
				}
				catch (Exception ex) when (attempt < SaveReplaceAttempts && IsPersistenceFailure(ex))
				{
					// Windows cannot replace a snapshot while a reader or scanner briefly holds it open.
					Thread.Sleep(attempt * 5);
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Swallowed by design (see above): the preference just isn't durable this time.
		}
	}

	private static bool IsPersistenceFailure(Exception ex) => ex is IOException or UnauthorizedAccessException;

	// The on-disk shape: nullable fields so a hand-edited or partially-written file still loads (coalesced to
	// defaults in the constructor), matching WorkspaceStore's PersistedState.
	private sealed record PersistedState(string? Theme, bool Wrap, string? ViewMode, WindowGeometry? Window);
}
