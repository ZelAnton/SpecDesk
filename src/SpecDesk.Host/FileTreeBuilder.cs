using SpecDesk.Contracts;

namespace SpecDesk.Host;

/// <summary>
/// Reads one directory level for the Folder panel. Descendants are deliberately not traversed: the webview
/// requests a directory only when the author expands it, so opening a large repository has bounded cost.
/// </summary>
public static class FileTreeBuilder
{
	private const int MaxEntriesExamined = 50000;

	/// <summary>
	/// Build one level rooted at <paramref name="root"/>. Directories sort before files; reparse points,
	/// dot-directories, and <c>node_modules</c> are excluded. A directory advertises lazy children without
	/// probing below it; an empty expansion is therefore a valid terminal response.
	/// </summary>
	public static TreePayload Build(string root, long requestId = 0)
	{
		string full = Path.GetFullPath(root);
		IReadOnlyList<TreeNode> nodes = Directory.Exists(full) ? BuildLevel(full) : [];
		return new TreePayload(full, nodes, requestId);
	}

	private static IReadOnlyList<TreeNode> BuildLevel(string directory)
	{
		List<TreeNode> directories = [];
		List<TreeNode> files = [];
		int examined = 0;
		try
		{
			foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
			{
				if (++examined > MaxEntriesExamined)
				{
					break;
				}
				try
				{
					FileAttributes attributes = File.GetAttributes(entry);
					if ((attributes & FileAttributes.ReparsePoint) != 0)
					{
						continue;
					}
					bool isDirectory = (attributes & FileAttributes.Directory) != 0;
					string name = Path.GetFileName(entry);
					if (isDirectory && IsIgnoredDirectory(name))
					{
						continue;
					}
					TreeNode node = new(name, entry, isDirectory, [], HasChildren: isDirectory);
					(isDirectory ? directories : files).Add(node);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					// Entries that disappear or become unreadable while enumerating are omitted from this snapshot.
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// An unreadable level is represented as empty; sibling directories remain independently usable.
			return [];
		}

		directories.Sort(CompareNodes);
		files.Sort(CompareNodes);
		return [.. directories, .. files];
	}

	private static int CompareNodes(TreeNode left, TreeNode right) =>
		string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);

	private static bool IsIgnoredDirectory(string name) =>
		name.StartsWith('.') || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase);
}
