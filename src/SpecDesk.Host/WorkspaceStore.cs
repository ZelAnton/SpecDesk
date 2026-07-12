using System.Text.Json;
using SpecDesk.Contracts;

namespace SpecDesk.Host;

/// <summary>
/// The persisted workspace-state store (A4): the author's recently-opened files/folders, their favorites, and
/// the GitHub repositories they registered — kept in one host-owned JSON file (see <c>AppPaths.Workspace</c>).
/// Mirrors <c>SpecDesk.Ai.PromptTemplateStore</c>: System.Text.Json (camelCase), directory-create on save, and
/// a corruption-tolerant load — a missing, empty, partially-written, or malformed file reads as an empty
/// workspace rather than faulting the app. Loaded once into memory on construction; every public method is
/// guarded by a private lock, because recents can be recorded on the message thread from more than one handler
/// (open a file, open a folder) so each read-modify-write-persist must stay atomic. A4 only STORES registered
/// repos — no GitHub cloning yet (that comes later); URL parsing lives in the caller, not here.
/// </summary>
public sealed class WorkspaceStore
{
	// Most-recent-first, capped so the list can't grow without bound. The oldest entries fall off the tail.
	private const int MaxRecent = 20;

	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
	};

	// Guards the three lists below: the message thread can drive AddRecent (from two open handlers),
	// SetFavorite, RegisterRepo/UnregisterRepo, and State() concurrently, so the whole read-modify-write and
	// the snapshot must be serialized.
	private readonly object _sync = new();
	private readonly string _path;
	private readonly List<WorkspaceItem> _recent;
	private readonly List<WorkspaceItem> _favorites;
	private readonly List<RegisteredRepo> _repositories;

	public WorkspaceStore(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		_path = path;
		PersistedState? state = Load();
		// Coalesce so a partially-written file (a list absent or explicitly null) still yields a usable store.
		_recent = state?.Recent ?? [];
		_favorites = state?.Favorites ?? [];
		_repositories = state?.Repositories ?? [];
	}

	/// <summary>
	/// Record a freshly opened file/folder as the most recent: remove any earlier entry with the same
	/// <see cref="WorkspaceItem.Path"/> (case-insensitive), insert this one at the front, then cap the list at
	/// 20 (dropping the oldest tail). Persists.
	/// </summary>
	public void AddRecent(WorkspaceItem item)
	{
		ArgumentNullException.ThrowIfNull(item);
		lock (_sync)
		{
			_recent.RemoveAll(existing => SamePath(existing.Path, item.Path));
			_recent.Insert(0, item);
			if (_recent.Count > MaxRecent)
			{
				_recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);
			}

			Save();
		}
	}

	/// <summary>
	/// Add (<paramref name="favorite"/> true) or remove (false) the file/folder at <paramref name="item"/>'s
	/// path from the favorites, de-duplicated by path. Adding one already present, or removing one not there,
	/// is a no-op. Persists.
	/// </summary>
	public void SetFavorite(WorkspaceItem item, bool favorite)
	{
		ArgumentNullException.ThrowIfNull(item);
		lock (_sync)
		{
			bool changed;
			if (favorite)
			{
				changed = !_favorites.Exists(existing => SamePath(existing.Path, item.Path));
				if (changed)
				{
					_favorites.Add(item);
				}
			}
			else
			{
				changed = _favorites.RemoveAll(existing => SamePath(existing.Path, item.Path)) > 0;
			}

			// Only persist a real change — re-favoriting or un-favoriting a no-op shouldn't touch the disk
			// (matching RegisterRepo/UnregisterRepo).
			if (changed)
			{
				Save();
			}
		}
	}

	/// <summary>
	/// Register a GitHub repository, de-duplicated by <see cref="RegisteredRepo.Id"/> — a repo whose id is
	/// already registered is left untouched. Persists. The URL is parsed/validated by the caller (the host);
	/// this only stores an already-validated entry.
	/// </summary>
	public void RegisterRepo(RegisteredRepo repo)
	{
		ArgumentNullException.ThrowIfNull(repo);
		lock (_sync)
		{
			if (!_repositories.Exists(existing => string.Equals(existing.Id, repo.Id, StringComparison.OrdinalIgnoreCase)))
			{
				_repositories.Add(repo);
				Save();
			}
		}
	}

	/// <summary>Remove any registered repository whose id matches <paramref name="id"/>. Persists.</summary>
	public void UnregisterRepo(string id)
	{
		lock (_sync)
		{
			if (_repositories.RemoveAll(existing => string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase)) > 0)
			{
				Save();
			}
		}
	}

	/// <summary>A snapshot of the current workspace state as fresh lists, so a caller holding the payload can
	/// never mutate the store's own lists.</summary>
	public WorkspaceStatePayload State()
	{
		lock (_sync)
		{
			return new WorkspaceStatePayload([.. _recent], [.. _favorites], [.. _repositories]);
		}
	}

	// Corruption-tolerant load: a missing, empty, or malformed file reads as null (an empty workspace) rather
	// than faulting — the same policy as PromptTemplateStore/FileTokenStore. The record's nullable lists let a
	// partially-written file coalesce to empty in the constructor.
	private PersistedState? Load()
	{
		if (!File.Exists(_path))
		{
			return null;
		}

		try
		{
			using FileStream stream = File.OpenRead(_path);
			return JsonSerializer.Deserialize<PersistedState>(stream, SerializerOptions);
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
		{
			// A corrupt/unreadable store opens as empty: the app starts with no history rather than crashing
			// on a tampered or inaccessible file. (Same policy as PromptTemplateStore.)
			return null;
		}
	}

	// Persist the whole store as one JSON object, creating the parent directory if needed. Always called
	// while holding _sync, so the serialized snapshot is internally consistent. Best-effort: recents are
	// recorded from the MIDDLE of the open path (LoadFile / OnOpenFolder), so a persistence failure (a
	// read-only or AV-locked workspace.json, a full disk) must NOT throw and unwind the rest of the open —
	// the in-memory state is already updated and emitted; only the on-disk copy is skipped this time.
	private void Save()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
			// Write-then-rename so a crash or power loss mid-write can't truncate workspace.json and lose ALL
			// curated state (favorites + registered repos, not just discardable recents): the reader only ever
			// sees the complete old file or the complete new one. File.Move(overwrite) is an atomic replace on
			// the same volume (the temp sits beside the target, so it always is).
			string temp = _path + ".tmp";
			File.WriteAllText(
				temp,
				JsonSerializer.Serialize(new PersistedState(_recent, _favorites, _repositories), SerializerOptions));
			File.Move(temp, _path, overwrite: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Swallowed by design (see above): the workspace history just isn't durable this time. Nothing to
			// log to here — the store is deliberately dependency-free (no logger), matching PromptTemplateStore.
		}
	}

	// Windows filesystem paths are case-insensitive, and the same file/folder can reach the store under
	// different casing (an open-dialog result vs a tree-click path), so dedup must ignore case.
	private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

	// The on-disk shape: one JSON object holding the three lists. A private record (not the wire payload) so
	// its lists can be nullable — a hand-edited or partially-written file with a missing/null list still loads.
	private sealed record PersistedState(
		List<WorkspaceItem>? Recent,
		List<WorkspaceItem>? Favorites,
		List<RegisteredRepo>? Repositories);
}
