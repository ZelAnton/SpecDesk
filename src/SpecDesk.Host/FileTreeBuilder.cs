using SpecDesk.Contracts;

namespace SpecDesk.Host;

/// <summary>
/// Builds the Markdown file tree of a workspace folder for the left-rail navigator (<c>tree</c> event).
/// Enumerates directories and Markdown files (<c>.md</c>/<c>.markdown</c>) recursively, skipping noise
/// (dot-directories such as <c>.git</c>, plus <c>node_modules</c>), and prunes any directory that contains
/// no Markdown anywhere beneath it — so the tree shows only what an author can actually open.
///
/// The walk is bounded three ways so a pathological folder can't hang the message thread: it never descends
/// into a reparse point (a symlink/junction — which would otherwise cycle a tree back onto an ancestor), it
/// caps the emitted node count, and it caps the TOTAL number of directories entered (the node cap alone is
/// not enough — a pruned directory refunds its node slot, so without the visit cap a deep noise-only tree
/// would be walked in full).
/// </summary>
public static class FileTreeBuilder
{
	private const int MaxDepth = 32;
	private const int MaxNodes = 5000;
	private const int MaxDirectoriesVisited = 20000;

	private static readonly string[] MarkdownExtensions = [".md", ".markdown"];

	// The two independent budgets for one walk. Nodes is reserved-then-refunded as directories are kept or
	// pruned (so it bounds the RESULT size); Visits only ever decreases (so it bounds the WORK). Passed by
	// reference as a small object so recursion shares one running total.
	private sealed class Budget
	{
		public int Nodes = MaxNodes;
		public int Visits = MaxDirectoriesVisited;
	}

	/// <summary>
	/// Build the tree rooted at <paramref name="root"/>. Returns an empty node list when the folder is
	/// missing, unreadable, or holds no Markdown. Directories sort before files; both alphabetically
	/// (ordinal-ignore-case), so the order is stable and independent of the filesystem's enumeration order.
	/// </summary>
	public static TreePayload Build(string root)
	{
		string full = Path.GetFullPath(root);
		Budget budget = new();
		IReadOnlyList<TreeNode> nodes = Directory.Exists(full)
			? BuildChildren(full, 0, budget)
			: [];
		return new TreePayload(full, nodes);
	}

	private static List<TreeNode> BuildChildren(string dir, int depth, Budget budget)
	{
		List<TreeNode> result = [];
		if (depth >= MaxDepth || budget.Nodes <= 0 || budget.Visits <= 0)
		{
			return result;
		}

		string[] subdirs;
		string[] files;
		try
		{
			subdirs = Directory.GetDirectories(dir);
			files = Directory.GetFiles(dir);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// An unreadable directory contributes nothing rather than aborting the whole tree — the author
			// still gets everything else. (A permission-denied subfolder is common on a real disk.)
			return result;
		}

		Array.Sort(subdirs, static (a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
		foreach (string subdir in subdirs)
		{
			if (budget.Nodes <= 0 || budget.Visits <= 0)
			{
				break;
			}

			string name = Path.GetFileName(subdir);
			// Skip a reparse point (symlink/junction) BEFORE counting the visit: descending into one can loop
			// a tree back onto an ancestor (an unbounded walk the depth cap only softens), and it would also
			// list the same subtree twice.
			if (IsIgnoredDirectory(name) || IsReparsePoint(subdir))
			{
				continue;
			}

			// Entering a directory always spends a visit (never refunded — this is what bounds the walk) and
			// reserves a node slot (refunded below if the directory turns out to hold no Markdown).
			budget.Visits -= 1;
			budget.Nodes -= 1;
			List<TreeNode> children = BuildChildren(subdir, depth + 1, budget);
			if (children.Count == 0)
			{
				// A directory with no Markdown beneath it is noise for a spec navigator — drop it and give
				// its reserved NODE slot back (the visit stays spent; the walk already happened).
				budget.Nodes += 1;
				continue;
			}

			result.Add(new TreeNode(name, subdir, IsDirectory: true, children));
		}

		Array.Sort(files, static (a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
		foreach (string file in files)
		{
			if (budget.Nodes <= 0)
			{
				break;
			}

			if (!IsMarkdown(file))
			{
				continue;
			}

			budget.Nodes -= 1;
			result.Add(new TreeNode(Path.GetFileName(file), file, IsDirectory: false, []));
		}

		return result;
	}

	private static bool IsReparsePoint(string dir)
	{
		try
		{
			return (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// If the attributes can't be read, treat it as a reparse point (skip) rather than risk recursing
			// into something that might cycle — the conservative choice for an unreadable entry.
			return true;
		}
	}

	private static bool IsIgnoredDirectory(string name) =>
		name.StartsWith('.') || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase);

	private static bool IsMarkdown(string file)
	{
		string ext = Path.GetExtension(file);
		foreach (string markdown in MarkdownExtensions)
		{
			if (string.Equals(ext, markdown, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}
}
