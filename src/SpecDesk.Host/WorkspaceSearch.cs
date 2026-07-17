using System.Diagnostics;

namespace SpecDesk.Host;

/// <summary>One search hit: an absolute file path, its 1-based line, and a bounded snippet of the matching
/// line (native-only shape; <see cref="SpecDesk.Contracts.SearchResultPayload"/> is the wire projection).</summary>
public sealed record WorkspaceSearchHit(string Path, int Line, string Snippet);

/// <summary>The bounded result of one workspace search. <see cref="Truncated"/> is true when a limit
/// (filesystem entries examined, elapsed time, or the result cap) stopped the search before it exhausted
/// the tree.</summary>
public sealed record WorkspaceSearchOutcome(IReadOnlyList<WorkspaceSearchHit> Hits, bool Truncated);

/// <summary>
/// Host-side search across the Markdown files under one authorized workspace root (docs/design/09-ipc-protocol.md,
/// <c>search.request</c>). Shares its directory perimeter and ignore rules with <see cref="FileTreeBuilder"/>
/// (dot-directories, <c>node_modules</c>, and reparse points excluded), but walks the WHOLE tree recursively
/// and reads file content, unlike that one-level, lazy navigator. Bounded on every axis so a large tree or a
/// query with many hits can't block the caller's background task indefinitely or balloon memory: a
/// filesystem-entries cap, a per-file size cap (an oversized file is skipped, never read), a wall-clock time
/// budget, and a total-hits cap. Any bound tripped sets <see cref="WorkspaceSearchOutcome.Truncated"/>
/// instead of failing — a partial, fast answer beats none.
/// </summary>
public static class WorkspaceSearch
{
	internal const int MaxEntriesExamined = 20_000;
	internal const int MaxResults = 200;
	internal const long MaxFileBytes = 4 * 1024 * 1024;
	internal static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(2);

	// How much of the matching line surrounds the hit on each side; a short line is returned whole.
	private const int SnippetRadius = 60;

	/// <summary>Search <paramref name="root"/>'s Markdown files for <paramref name="query"/> (a plain,
	/// case-insensitive substring match — no globs/regex). A blank query or a missing root yields an empty,
	/// non-truncated outcome (nothing to search, not a failure).</summary>
	public static WorkspaceSearchOutcome Search(string root, string query) =>
		Search(root, query, MaxDuration, MaxEntriesExamined, MaxResults);

	// The budgets are parameters so tests can pin small, deterministic limits instead of racing the wall
	// clock or growing a huge fixture tree — Search(root, query) above is the one real callers use.
	internal static WorkspaceSearchOutcome Search(
		string root, string query, TimeSpan maxDuration, int maxEntriesExamined, int maxResults)
	{
		List<WorkspaceSearchHit> hits = [];
		if (string.IsNullOrWhiteSpace(query) || !Directory.Exists(root))
		{
			return new WorkspaceSearchOutcome(hits, Truncated: false);
		}

		Stopwatch stopwatch = Stopwatch.StartNew();
		int entriesExamined = 0;
		bool truncated = false;
		Queue<string> pendingDirectories = new();
		pendingDirectories.Enqueue(root);

		while (pendingDirectories.Count > 0 && !truncated)
		{
			string directory = pendingDirectories.Dequeue();
			string[] entries;
			try
			{
				entries = Directory.GetFileSystemEntries(directory);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// An unreadable level is simply skipped; sibling directories remain independently searchable.
				continue;
			}

			foreach (string entry in entries)
			{
				if (++entriesExamined > maxEntriesExamined || stopwatch.Elapsed >= maxDuration)
				{
					truncated = true;
					break;
				}

				FileAttributes attributes;
				try
				{
					attributes = File.GetAttributes(entry);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					continue;
				}
				if ((attributes & FileAttributes.ReparsePoint) != 0)
				{
					continue;
				}

				string name = Path.GetFileName(entry);
				if ((attributes & FileAttributes.Directory) != 0)
				{
					if (!IsIgnoredDirectory(name))
					{
						pendingDirectories.Enqueue(entry);
					}
					continue;
				}
				if (!IsMarkdownFile(name))
				{
					continue;
				}
				if (!SearchFile(entry, query, hits, maxResults))
				{
					truncated = true;
					break;
				}
				if (hits.Count >= maxResults)
				{
					truncated = true;
					break;
				}
			}
		}

		return new WorkspaceSearchOutcome(hits, truncated);
	}

	// Returns false once maxResults was reached while scanning this file — the caller stops the whole search.
	private static bool SearchFile(string path, string query, List<WorkspaceSearchHit> hits, int maxResults)
	{
		try
		{
			FileInfo info = new(path);
			if (!info.Exists || info.Length > MaxFileBytes)
			{
				return true;
			}
			int lineNumber = 0;
			foreach (string line in File.ReadLines(path))
			{
				lineNumber++;
				int index = line.IndexOf(query, StringComparison.OrdinalIgnoreCase);
				if (index < 0)
				{
					continue;
				}
				hits.Add(new WorkspaceSearchHit(path, lineNumber, Snippet(line, index, query.Length)));
				if (hits.Count >= maxResults)
				{
					return false;
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// An unreadable file is skipped; sibling files remain independently searchable.
		}
		return true;
	}

	// A bounded window of the matching line around the hit, trimmed and ellipsized on either truncated side.
	private static string Snippet(string line, int matchIndex, int matchLength)
	{
		string trimmed = line.Trim();
		if (trimmed.Length <= (SnippetRadius * 2) + matchLength)
		{
			return trimmed;
		}
		int leading = line.Length - line.TrimStart().Length;
		int index = Math.Max(0, matchIndex - leading);
		int start = Math.Max(0, index - SnippetRadius);
		int end = Math.Min(trimmed.Length, index + matchLength + SnippetRadius);
		string slice = trimmed[start..end];
		return (start > 0 ? "…" : string.Empty) + slice + (end < trimmed.Length ? "…" : string.Empty);
	}

	private static bool IsIgnoredDirectory(string name) =>
		name.StartsWith('.') || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase);

	private static bool IsMarkdownFile(string name) =>
		name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
		|| name.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);
}
