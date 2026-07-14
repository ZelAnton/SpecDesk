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
	private const int SaveReplaceAttempts = 6;

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
	private readonly Dictionary<string, long> _repositoryEpochs = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, long> _repositoryCloneEpochs = new(StringComparer.OrdinalIgnoreCase);
	private long _revision;

	internal sealed record RepositoryRegistrationSnapshot(
		string Id,
		long Epoch,
		long CloneEpoch,
		RegisteredRepo? Repository);
	internal sealed record WorkspaceStateSnapshot(long Revision, WorkspaceStatePayload State);

	public WorkspaceStore(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		_path = path;
		PersistedState? state = Load();
		// Coalesce so a partially-written file (a list absent or explicitly null) still yields a usable store.
		_recent = state?.Recent?
			.Select(NormalizeItem)
			.Where(item => item is not null && item.Kind == "local")
			.Select(item => item!)
			.ToList() ?? [];
		_favorites = state?.Favorites?
			.Select(NormalizeItem)
			.Where(item => item is not null)
			.Select(item => item!)
			.ToList() ?? [];
		_repositories = state?.Repositories?.Select(NormalizeRepo).ToList() ?? [];
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

			_revision++;
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
		WorkspaceItem? normalized = NormalizeItem(item);
		if (normalized is null)
		{
			return;
		}
		item = normalized;
		lock (_sync)
		{
			bool changed;
			if (favorite)
			{
				changed = !_favorites.Exists(existing => SameFavoriteIdentity(existing, item));
				if (changed)
				{
					_favorites.Add(item);
				}
			}
			else
			{
				changed = _favorites.RemoveAll(existing => SameFavoriteIdentity(existing, item)) > 0;
			}

			// Only persist a real change — re-favoriting or un-favoriting a no-op shouldn't touch the disk
			// (matching RegisterRepo/UnregisterRepo).
			if (changed)
			{
				_revision++;
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
				_repositories.Add(NormalizeRepo(repo));
				AdvanceRepositoryEpoch(repo.Id);
				_revision++;
				Save();
			}
		}
	}

	public RegisteredRepo? FindRepo(string id)
	{
		lock (_sync)
		{
			return FindRepoLocked(id);
		}
	}

	internal string? FindCloneName(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}

		lock (_sync)
		{
			return _repositories
				.SelectMany(repo => repo.Clones)
				.FirstOrDefault(clone => SameCanonicalPath(clone.Path, path))
				?.Id;
		}
	}

	internal RepositoryRegistrationSnapshot CaptureRepoRegistration(string id)
	{
		lock (_sync)
		{
			return new RepositoryRegistrationSnapshot(
				id, RepositoryEpoch(id), RepositoryCloneEpoch(id), FindRepoLocked(id));
		}
	}

	internal bool IsRepoRegistrationCurrent(
		RepositoryRegistrationSnapshot expected,
		bool requirePresent)
	{
		lock (_sync)
		{
			return RegistrationMatches(expected, requirePresent, FindRepoLocked(expected.Id));
		}
	}

	internal bool IsRepoRegistrationPresenceCurrent(RepositoryRegistrationSnapshot expected)
	{
		lock (_sync)
		{
			RegisteredRepo? current = FindRepoLocked(expected.Id);
			return RepositoryEpoch(expected.Id) == expected.Epoch
				&& expected.Repository is not null
				&& current is not null
				&& string.Equals(current.Url, expected.Repository.Url, StringComparison.OrdinalIgnoreCase);
		}
	}

	internal bool TryRegisterRepo(
		RepositoryRegistrationSnapshot expected,
		RegisteredRepo repo,
		out WorkspaceStateSnapshot state)
	{
		ArgumentNullException.ThrowIfNull(expected);
		ArgumentNullException.ThrowIfNull(repo);
		lock (_sync)
		{
			RegisteredRepo? current = FindRepoLocked(expected.Id);
			bool expectedPresent = expected.Repository is not null;
			if (RepositoryEpoch(expected.Id) != expected.Epoch
				|| expectedPresent != (current is not null)
				|| (expectedPresent
					&& !string.Equals(current!.Url, expected.Repository!.Url, StringComparison.OrdinalIgnoreCase)))
			{
				state = StateSnapshotLocked();
				return false;
			}
			if (current is null)
			{
				_repositories.Add(NormalizeRepo(repo));
				AdvanceRepositoryEpoch(expected.Id);
				_revision++;
				Save();
			}
			state = StateSnapshotLocked();
			return true;
		}
	}

	internal bool TrySetRepoDefaultBranch(
		RepositoryRegistrationSnapshot expected,
		string defaultBranch,
		out WorkspaceStateSnapshot state)
	{
		ArgumentNullException.ThrowIfNull(expected);
		ArgumentException.ThrowIfNullOrWhiteSpace(defaultBranch);
		lock (_sync)
		{
			RegisteredRepo? current = FindRepoLocked(expected.Id);
			if (RepositoryEpoch(expected.Id) != expected.Epoch
				|| expected.Repository is null
				|| current is null
				|| !string.Equals(current.Url, expected.Repository.Url, StringComparison.OrdinalIgnoreCase))
			{
				state = StateSnapshotLocked();
				return false;
			}
			int index = _repositories.FindIndex(repo => ReferenceEquals(repo, current));
			_repositories[index] = current with { DefaultBranch = defaultBranch };
			_revision++;
			Save();
			state = StateSnapshotLocked();
			return true;
		}
	}

	internal bool TryRollbackNewRepoRegistration(
		RepositoryRegistrationSnapshot expected,
		out WorkspaceStateSnapshot state)
	{
		ArgumentNullException.ThrowIfNull(expected);
		lock (_sync)
		{
			RegisteredRepo? current = FindRepoLocked(expected.Id);
			if (!RegistrationMatches(expected, requirePresent: true, current)
				|| !ReferenceEquals(current, expected.Repository))
			{
				state = StateSnapshotLocked();
				return false;
			}

			AdvanceRepositoryEpoch(expected.Id);
			AdvanceRepositoryCloneEpoch(expected.Id);
			_repositories.Remove(current!);
			_favorites.RemoveAll(item =>
				string.Equals(item.RepositoryId, expected.Id, StringComparison.OrdinalIgnoreCase));
			_revision++;
			Save();
			state = StateSnapshotLocked();
			return true;
		}
	}

	internal bool TryCommitRepoClone(
		RepositoryRegistrationSnapshot expected,
		bool requirePresent,
		RegisteredRepo seed,
		RegisteredClone clone,
		string inferredDefaultBranch,
		out WorkspaceStateSnapshot state)
	{
		ArgumentNullException.ThrowIfNull(expected);
		ArgumentNullException.ThrowIfNull(seed);
		ArgumentNullException.ThrowIfNull(clone);
		lock (_sync)
		{
			RegisteredRepo? current = FindRepoLocked(expected.Id);
			if (!RegistrationMatches(expected, requirePresent, current))
			{
				state = StateSnapshotLocked();
				return false;
			}

			int repoIndex = current is null
				? -1
				: _repositories.FindIndex(repo => ReferenceEquals(repo, current));
			RegisteredRepo baseRepo = current ?? NormalizeRepo(seed);
			List<RegisteredClone> clones = [.. baseRepo.Clones];
			int cloneIndex = clones.FindIndex(existing => SamePath(existing.Path, clone.Path));
			if (cloneIndex >= 0)
			{
				clones[cloneIndex] = clone;
			}
			else
			{
				clones.Add(clone);
			}
			string defaultBranch = string.IsNullOrWhiteSpace(baseRepo.DefaultBranch)
				? inferredDefaultBranch
				: baseRepo.DefaultBranch;
			RegisteredRepo updated = baseRepo with { DefaultBranch = defaultBranch, Clones = clones };
			if (repoIndex >= 0)
			{
				_repositories[repoIndex] = updated;
			}
			else
			{
				_repositories.Add(updated);
				AdvanceRepositoryEpoch(expected.Id);
			}
			AdvanceRepositoryCloneEpoch(expected.Id);
			_revision++;
			Save();
			state = StateSnapshotLocked();
			return true;
		}
	}

	public void UpdateRepo(RegisteredRepo repo)
	{
		ArgumentNullException.ThrowIfNull(repo);
		lock (_sync)
		{
			int index = _repositories.FindIndex(existing =>
				string.Equals(existing.Id, repo.Id, StringComparison.OrdinalIgnoreCase));
			if (index < 0)
			{
				_repositories.Add(NormalizeRepo(repo));
				AdvanceRepositoryEpoch(repo.Id);
			}
			else
			{
				_repositories[index] = NormalizeRepo(repo);
			}
			AdvanceRepositoryCloneEpoch(repo.Id);
			_revision++;
			Save();
		}
	}

	public void SetRepoDefaultBranch(RegisteredRepo seed, string defaultBranch)
	{
		ArgumentNullException.ThrowIfNull(seed);
		ArgumentException.ThrowIfNullOrWhiteSpace(defaultBranch);
		lock (_sync)
		{
			int index = _repositories.FindIndex(existing =>
				string.Equals(existing.Id, seed.Id, StringComparison.OrdinalIgnoreCase));
			RegisteredRepo current = index >= 0 ? _repositories[index] : NormalizeRepo(seed);
			RegisteredRepo updated = current with { DefaultBranch = defaultBranch };
			if (index >= 0)
			{
				_repositories[index] = updated;
			}
			else
			{
				_repositories.Add(updated);
				AdvanceRepositoryEpoch(seed.Id);
			}
			_revision++;
			Save();
		}
	}

	public void UpsertRepoClone(RegisteredRepo seed, RegisteredClone clone, string inferredDefaultBranch)
	{
		ArgumentNullException.ThrowIfNull(seed);
		ArgumentNullException.ThrowIfNull(clone);
		lock (_sync)
		{
			int repoIndex = _repositories.FindIndex(existing =>
				string.Equals(existing.Id, seed.Id, StringComparison.OrdinalIgnoreCase));
			RegisteredRepo current = repoIndex >= 0 ? _repositories[repoIndex] : NormalizeRepo(seed);
			List<RegisteredClone> clones = [.. current.Clones];
			int cloneIndex = clones.FindIndex(existing => SamePath(existing.Path, clone.Path));
			if (cloneIndex >= 0)
			{
				clones[cloneIndex] = clone;
			}
			else
			{
				clones.Add(clone);
			}

			string defaultBranch = string.IsNullOrWhiteSpace(current.DefaultBranch)
				? inferredDefaultBranch
				: current.DefaultBranch;
			RegisteredRepo updated = current with { DefaultBranch = defaultBranch, Clones = clones };
			if (repoIndex >= 0)
			{
				_repositories[repoIndex] = updated;
			}
			else
			{
				_repositories.Add(updated);
				AdvanceRepositoryEpoch(seed.Id);
			}
			AdvanceRepositoryCloneEpoch(seed.Id);
			_revision++;
			Save();
		}
	}

	public bool TryUpdateRepoClone(
		string id,
		string expectedUrl,
		string clonePath,
		string expectedCloneId,
		RegisteredClone updatedClone,
		string inferredDefaultBranch)
	{
		lock (_sync)
		{
			int repoIndex = _repositories.FindIndex(existing =>
				string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(existing.Url, expectedUrl, StringComparison.OrdinalIgnoreCase));
			if (repoIndex < 0)
			{
				return false;
			}
			RegisteredRepo repo = _repositories[repoIndex];
			List<RegisteredClone> clones = [.. repo.Clones];
			int cloneIndex = clones.FindIndex(existing =>
				SamePath(existing.Path, clonePath)
				&& string.Equals(existing.Id, expectedCloneId, StringComparison.Ordinal));
			if (cloneIndex < 0)
			{
				return false;
			}
			clones[cloneIndex] = updatedClone;
			string defaultBranch = string.IsNullOrWhiteSpace(repo.DefaultBranch)
				? inferredDefaultBranch
				: repo.DefaultBranch;
			_repositories[repoIndex] = repo with { Clones = clones, DefaultBranch = defaultBranch };
			AdvanceRepositoryCloneEpoch(id);
			_revision++;
			Save();
			return true;
		}
	}
	public bool TryRemoveRepoClone(
		string id,
		string expectedUrl,
		string clonePath,
		string expectedCloneId)
	{
		lock (_sync)
		{
			int repoIndex = _repositories.FindIndex(existing =>
				string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(existing.Url, expectedUrl, StringComparison.OrdinalIgnoreCase));
			if (repoIndex < 0)
			{
				return false;
			}
			RegisteredRepo repo = _repositories[repoIndex];
			int cloneIndex = repo.Clones.ToList().FindIndex(clone =>
				SamePath(clone.Path, clonePath)
				&& string.Equals(clone.Id, expectedCloneId, StringComparison.Ordinal));
			if (cloneIndex < 0)
			{
				return false;
			}
			List<RegisteredClone> clones = [.. repo.Clones];
			clones.RemoveAt(cloneIndex);
			_repositories[repoIndex] = repo with { Clones = clones };
			_favorites.RemoveAll(item =>
				string.Equals(item.RepositoryId, id, StringComparison.OrdinalIgnoreCase)
				&& (SamePath(item.Path, clonePath)
					|| PathIsInside(item.Path, clonePath)));
			_recent.RemoveAll(item =>
				string.Equals(item.Kind, "local", StringComparison.OrdinalIgnoreCase)
				&& (SamePath(item.Path, clonePath) || PathIsInside(item.Path, clonePath)));
			AdvanceRepositoryCloneEpoch(id);
			_revision++;
			Save();
			return true;
		}
	}
	public void RemoveRepoClone(string id, string clonePath)
	{
		lock (_sync)
		{
			int repoIndex = _repositories.FindIndex(existing =>
				string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase));
			if (repoIndex < 0)
			{
				return;
			}
			RegisteredRepo repo = _repositories[repoIndex];
			List<RegisteredClone> clones = repo.Clones
				.Where(clone => !SamePath(clone.Path, clonePath))
				.ToList();
			bool cloneRemoved = clones.Count != repo.Clones.Count;
			bool changed = cloneRemoved;
			if (cloneRemoved)
			{
				_repositories[repoIndex] = repo with { Clones = clones };
				AdvanceRepositoryCloneEpoch(id);
			}
			changed |= _favorites.RemoveAll(item =>
				string.Equals(item.RepositoryId, id, StringComparison.OrdinalIgnoreCase)
				&& (SamePath(item.Path, clonePath)
					|| PathIsInside(item.Path, clonePath))) > 0;
			changed |= _recent.RemoveAll(item =>
				string.Equals(item.Kind, "local", StringComparison.OrdinalIgnoreCase)
				&& (SamePath(item.Path, clonePath) || PathIsInside(item.Path, clonePath))) > 0;
			if (changed)
			{
				_revision++;
				Save();
			}
		}
	}

	public void RemoveRepoBranch(string id, string clonePath, string branch)
	{
		lock (_sync)
		{
			int repoIndex = _repositories.FindIndex(existing =>
				string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase));
			bool changed = false;
			if (repoIndex >= 0)
			{
				RegisteredRepo repo = _repositories[repoIndex];
				List<RegisteredClone> clones = [.. repo.Clones];
				int cloneIndex = clones.FindIndex(existing => SamePath(existing.Path, clonePath));
				if (cloneIndex >= 0)
				{
					RegisteredClone clone = clones[cloneIndex];
					RegisteredBranch[] branches = clone.Branches
						.Where(existing => !string.Equals(existing.Name, branch, StringComparison.Ordinal))
						.ToArray();
					if (branches.Length != clone.Branches.Count)
					{
						clones[cloneIndex] = clone with { Branches = branches };
						_repositories[repoIndex] = repo with { Clones = clones };
						AdvanceRepositoryCloneEpoch(id);
						changed = true;
					}
				}
			}
			changed |= _favorites.RemoveAll(item =>
				string.Equals(item.RepositoryId, id, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(item.Branch, branch, StringComparison.Ordinal)
				&& SamePath(item.Path, clonePath)) > 0;
			if (changed)
			{
				_revision++;
				Save();
			}
		}
	}

	/// <summary>Remove any registered repository whose id matches <paramref name="id"/>. Persists.</summary>
	public void UnregisterRepo(string id) => UnregisterRepoWithSnapshot(id);

	internal WorkspaceStateSnapshot UnregisterRepoWithSnapshot(string id)
	{
		lock (_sync)
		{
			// The epoch advances even when the repository is already absent. An open/clone intent that captured
			// the prior absence must not add it after an explicit forget request (the absent -> absent ABA case).
			AdvanceRepositoryEpoch(id);
			AdvanceRepositoryCloneEpoch(id);
			bool changed = _repositories.RemoveAll(existing =>
				string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
			changed |= _favorites.RemoveAll(item =>
				string.Equals(item.RepositoryId, id, StringComparison.OrdinalIgnoreCase)) > 0;
			_revision++;
			if (changed)
			{
				Save();
			}
			return StateSnapshotLocked();
		}
	}

	/// <summary>A snapshot of the current workspace state as fresh lists, so a caller holding the payload can
	/// never mutate the store's own lists.</summary>
	public WorkspaceStatePayload State()
	{
		lock (_sync)
		{
			return StateLocked();
		}
	}

	internal WorkspaceStateSnapshot StateWithRevision()
	{
		lock (_sync)
		{
			return StateSnapshotLocked();
		}
	}

	private WorkspaceStatePayload StateLocked() =>
		new([.. _recent], [.. _favorites], [.. _repositories]);

	private WorkspaceStateSnapshot StateSnapshotLocked() => new(_revision, StateLocked());

	private RegisteredRepo? FindRepoLocked(string id) =>
		_repositories.FirstOrDefault(repo =>
			string.Equals(repo.Id, id, StringComparison.OrdinalIgnoreCase));

	private bool RegistrationMatches(
		RepositoryRegistrationSnapshot expected,
		bool requirePresent,
		RegisteredRepo? current)
	{
		if (RepositoryEpoch(expected.Id) != expected.Epoch)
		{
			return false;
		}
		if (requirePresent)
		{
			return RepositoryCloneEpoch(expected.Id) == expected.CloneEpoch
				&& expected.Repository is not null
				&& current is not null
				&& string.Equals(current.Id, expected.Repository.Id, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(current.Url, expected.Repository.Url, StringComparison.OrdinalIgnoreCase);
		}
		return expected.Repository is null && current is null;
	}

	private long RepositoryEpoch(string id) =>
		_repositoryEpochs.TryGetValue(id, out long epoch) ? epoch : 0;

	private void AdvanceRepositoryEpoch(string id) =>
		_repositoryEpochs[id] = checked(RepositoryEpoch(id) + 1);

	private long RepositoryCloneEpoch(string id) =>
		_repositoryCloneEpochs.TryGetValue(id, out long epoch) ? epoch : 0;

	private void AdvanceRepositoryCloneEpoch(string id) =>
		_repositoryCloneEpochs[id] = checked(RepositoryCloneEpoch(id) + 1);

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
			string json;
			// Close the snapshot handle before parsing so an atomic save is not held up by JSON deserialization.
			using (FileStream stream = new(
				_path, FileMode.Open, FileAccess.Read, FileShare.Read))
			using (StreamReader reader = new(stream))
			{
				json = reader.ReadToEnd();
			}
			return JsonSerializer.Deserialize<PersistedState>(json, SerializerOptions);
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
			for (int attempt = 1; ; attempt++)
			{
				try
				{
					File.Move(temp, _path, overwrite: true);
					break;
				}
				catch (Exception ex) when (
					attempt < SaveReplaceAttempts && IsPersistenceFailure(ex))
				{
					// Windows cannot replace a snapshot while a reader or scanner briefly holds it open.
					Thread.Sleep(attempt * 5);
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Swallowed by design (see above): the workspace history just isn't durable this time. Nothing to
			// log to here — the store is deliberately dependency-free (no logger), matching PromptTemplateStore.
		}
	}

	private static bool IsPersistenceFailure(Exception ex) =>
		ex is IOException or UnauthorizedAccessException;

	// Windows filesystem paths are case-insensitive, and the same file/folder can reach the store under
	// different casing (an open-dialog result vs a tree-click path), so dedup must ignore case.
	private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

	private static bool SameCanonicalPath(string a, string b)
	{
		try
		{
			return SamePath(
				Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
				Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)));
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}

	private static bool PathIsInside(string candidate, string root)
	{
		if (!Path.IsPathFullyQualified(candidate) || !Path.IsPathFullyQualified(root))
		{
			return false;
		}
		string relative = Path.GetRelativePath(root, candidate);
		return relative != ".."
			&& !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
			&& !Path.IsPathFullyQualified(relative);
	}

	private static bool SameFavoriteIdentity(WorkspaceItem left, WorkspaceItem right)
	{
		if (!string.Equals(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase)
			|| !string.Equals(left.RepositoryId, right.RepositoryId, StringComparison.OrdinalIgnoreCase)
			|| !string.Equals(left.Branch, right.Branch, StringComparison.Ordinal))
		{
			return false;
		}
		return string.Equals(
			left.Path, right.Path,
			string.Equals(left.Kind, "remote", StringComparison.OrdinalIgnoreCase)
				? StringComparison.Ordinal
				: StringComparison.OrdinalIgnoreCase);
	}

	private static WorkspaceItem? NormalizeItem(WorkspaceItem item)
	{
		string kind = string.IsNullOrWhiteSpace(item.Kind) ? "local" : item.Kind.ToLowerInvariant();
		if (string.IsNullOrWhiteSpace(item.Path) || string.IsNullOrWhiteSpace(item.Label))
		{
			return null;
		}
		if (kind == "local")
		{
			if (item.RepositoryId is not null || item.Branch is not null || !Path.IsPathFullyQualified(item.Path))
			{
				return null;
			}
			try
			{
				return item with { Kind = kind, Path = Path.GetFullPath(item.Path) };
			}
			catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
			{
				return null;
			}
		}
		if (kind is "clone" or "branch")
		{
			if (!item.IsFolder
				|| !IsRepositoryId(item.RepositoryId)
				|| !Path.IsPathFullyQualified(item.Path)
				|| (kind == "clone" && item.Branch is not null)
				|| (kind == "branch" && !IsRemoteBranch(item.Branch)))
			{
				return null;
			}
			try
			{
				return item with { Kind = kind, Path = Path.GetFullPath(item.Path) };
			}
			catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
			{
				return null;
			}
		}		if (kind == "remote")
		{
			return IsRepositoryId(item.RepositoryId)
				&& IsRemoteBranch(item.Branch)
				&& IsRemoteRelativePath(item.Path)
				? item with { Kind = kind }
				: null;
		}
		if (kind == "repository")
		{
			return item.IsFolder
				&& item.Branch is null
				&& IsRepositoryId(item.RepositoryId)
				&& string.Equals(item.Path, item.RepositoryId, StringComparison.OrdinalIgnoreCase)
				? item with { Kind = kind, Path = item.RepositoryId! }
				: null;
		}
		return null;
	}

	private static bool IsRepositoryId(string? id)
	{
		if (string.IsNullOrWhiteSpace(id) || id.Length > 256)
		{
			return false;
		}
		string[] segments = id.Split('/');
		return segments.Length == 2 && IsGitHubOwner(segments[0]) && IsGitHubRepoName(segments[1]);
	}

	private static bool IsGitHubOwner(string owner) =>
		owner.Length > 0
		&& owner[0] != '-'
		&& owner[^1] != '-'
		&& owner.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

	private static bool IsGitHubRepoName(string name) =>
		name is not ("" or "." or "..")
		&& name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

	private static bool IsRemoteBranch(string? branch) =>
		!string.IsNullOrWhiteSpace(branch)
		&& branch.Length <= 1024
		&& !branch.Any(char.IsControl);

	private static bool IsRemoteRelativePath(string path)
	{
		if (path.Length > 4096 || path.Any(char.IsControl))
		{
			return false;
		}
		string[] segments = path.Split('/');
		return segments.Length <= 64 && segments.All(segment => segment is not ("" or "." or ".."));
	}

	private static RegisteredRepo NormalizeRepo(RegisteredRepo repo) =>
		repo with
		{
			// A legacy descriptor may not have recorded this field. Keep it unknown until metadata or a
			// clone identifies the actual default; guessing "main" would misclassify master/trunk repos.
			DefaultBranch = repo.DefaultBranch ?? string.Empty,
			Clones = (repo.Clones ?? []).Select(NormalizeClone).ToArray(),
		};

	private static RegisteredClone NormalizeClone(RegisteredClone clone) =>
		clone with
		{
			Branches = (clone.Branches ?? [])
				.Where(branch => branch is not null && !string.IsNullOrWhiteSpace(branch.Name))
				.Select(branch => branch with { Status = branch.Status ?? RepositoryStatusPayload.Empty })
				.ToArray(),
			Status = clone.Status ?? RepositoryStatusPayload.Empty,
		};

	// The on-disk shape: one JSON object holding the three lists. A private record (not the wire payload) so
	// its lists can be nullable — a hand-edited or partially-written file with a missing/null list still loads.
	private sealed record PersistedState(
		List<WorkspaceItem>? Recent,
		List<WorkspaceItem>? Favorites,
		List<RegisteredRepo>? Repositories);
}
