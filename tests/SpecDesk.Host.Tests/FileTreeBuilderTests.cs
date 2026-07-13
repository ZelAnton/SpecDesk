using SpecDesk.Contracts;

namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class FileTreeBuilderTests
{
	private string _root = string.Empty;

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), "specdesk-tree-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_root);
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	private void Touch(params string[] segments)
	{
		string path = Path.Combine([_root, .. segments]);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, "x");
	}

	[Test]
	public void Build_ForAMissingFolder_ReturnsAnEmptyTreeRootedAtTheFullPath()
	{
		string missing = Path.Combine(_root, "nope");
		TreePayload tree = FileTreeBuilder.Build(missing);
		Assert.That(tree.Root, Is.EqualTo(Path.GetFullPath(missing)));
		Assert.That(tree.Nodes, Is.Empty);
	}

	[Test]
	public void Build_IncludesAllFiles_DirectoriesFirst_Sorted()
	{
		Touch("zeta.md");
		Touch("alpha.markdown");
		Touch("readme.txt");
		Touch("sub", "b.md");
		Touch("sub", "a.md");

		TreePayload tree = FileTreeBuilder.Build(_root);

		// Directory ("sub") before files ("alpha.markdown", "zeta.md"); files alphabetical (case-insensitive).
		string[] topLevel = ["sub", "alpha.markdown", "readme.txt", "zeta.md"];
		string[] subChildren = ["a.md", "b.md"];
		Assert.That(tree.Nodes.Select(n => n.Name), Is.EqualTo(topLevel));
		TreeNode sub = tree.Nodes[0];
		Assert.That(sub.IsDirectory, Is.True);
		Assert.That(sub.Path, Is.EqualTo(Path.Combine(_root, "sub")));
		Assert.That(sub.Children.Select(n => n.Name), Is.EqualTo(subChildren));
		Assert.That(tree.Nodes[1].IsDirectory, Is.False);
		Assert.That(tree.Nodes[1].Children, Is.Empty);
	}

	[Test]
	public void Build_KeepsDirectoriesWithAnyFilesBeneath()
	{
		Touch("keep.md");
		// Non-Markdown files are navigable too.
		Touch("assets", "images", "logo.png");
		Touch("empty-dir", "deeper", "data.json");

		TreePayload tree = FileTreeBuilder.Build(_root);

		string[] expected = ["assets", "empty-dir", "keep.md"];
		Assert.That(tree.Nodes.Select(n => n.Name), Is.EqualTo(expected));
	}

	[Test]
	public void Build_SkipsDotDirectoriesAndNodeModules()
	{
		Touch("visible.md");
		Touch(".git", "notes.md");
		Touch("node_modules", "pkg", "readme.md");

		TreePayload tree = FileTreeBuilder.Build(_root);

		string[] onlyVisible = ["visible.md"];
		Assert.That(tree.Nodes.Select(n => n.Name), Is.EqualTo(onlyVisible));
	}

	[Test]
	public void Build_DoesNotDescendIntoASymlinkCycle()
	{
		Touch("real.md");
		Directory.CreateDirectory(Path.Combine(_root, "sub"));
		string loop = Path.Combine(_root, "sub", "loop");
		try
		{
			// A symlink pointing back at the root — descending into it would cycle forever without the guard.
			Directory.CreateSymbolicLink(loop, _root);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			Assert.Ignore("Symbolic links can't be created here (needs privilege / OS support).");
			return;
		}

		// The whole point: this returns (bounded) rather than hanging, and never lists the symlinked cycle.
		TreePayload tree = FileTreeBuilder.Build(_root);

		Assert.That(tree.Nodes.Select(n => n.Name), Does.Contain("real.md"));
		// "sub" holds only the reparse-point "loop" (skipped), so it prunes away entirely.
		Assert.That(tree.Nodes.Select(n => n.Name), Does.Not.Contain("sub"));
	}

	[Test]
	public void Build_DoesNotExposeAFileSymlinkOutsideTheWorkspace()
	{
		string target = Path.Combine(Path.GetTempPath(), "specdesk-tree-target-" + Guid.NewGuid().ToString("N") + ".txt");
		File.WriteAllText(target, "outside");
		try
		{
			string link = Path.Combine(_root, "outside.txt");
			try
			{
				File.CreateSymbolicLink(link, target);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
			{
				Assert.Ignore("File symbolic links can't be created here (needs privilege / OS support).");
				return;
			}

			TreePayload tree = FileTreeBuilder.Build(_root);

			Assert.That(tree.Nodes.Select(node => node.Name), Does.Not.Contain("outside.txt"));
		}
		finally
		{
			File.Delete(target);
		}
	}

	[Test]
	public void Build_KeepsADirectoryThatHasAFileOnlyDeepInside()
	{
		Touch("outer", "inner", "buried.md");

		TreePayload tree = FileTreeBuilder.Build(_root);

		string[] outerLevel = ["outer"];
		string[] innerLevel = ["inner"];
		string[] buriedLevel = ["buried.md"];
		Assert.That(tree.Nodes.Select(n => n.Name), Is.EqualTo(outerLevel));
		TreeNode outer = tree.Nodes[0];
		Assert.That(outer.Children.Select(n => n.Name), Is.EqualTo(innerLevel));
		Assert.That(outer.Children[0].Children.Select(n => n.Name), Is.EqualTo(buriedLevel));
	}
}
