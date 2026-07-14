using SpecDesk.Contracts;

namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class FileTreeBuilderTests
{
	private static readonly string[] RootLevelNames = ["sub", "alpha.markdown", "zeta.md"];
	private static readonly string[] ChildLevelNames = ["inner", "visible.md"];
	private static readonly string[] VisibleFileOnly = ["visible.md"];
	private static readonly string[] RealFileOnly = ["real.md"];
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
	public void Build_ForAMissingFolder_ReturnsAnEmptyCorrelatedTree()
	{
		string missing = Path.Combine(_root, "nope");
		TreePayload tree = FileTreeBuilder.Build(missing, requestId: 17);
		Assert.Multiple(() =>
		{
			Assert.That(tree.Root, Is.EqualTo(Path.GetFullPath(missing)));
			Assert.That(tree.Nodes, Is.Empty);
			Assert.That(tree.RequestId, Is.EqualTo(17));
		});
	}

	[Test]
	public void Build_ReadsOnlyOneLevel_DirectoriesFirst_Sorted()
	{
		Touch("zeta.md");
		Touch("alpha.markdown");
		Touch("sub", "nested", "deep.md");

		TreePayload tree = FileTreeBuilder.Build(_root);

		Assert.That(tree.Nodes.Select(node => node.Name), Is.EqualTo(RootLevelNames));
		TreeNode sub = tree.Nodes[0];
		Assert.Multiple(() =>
		{
			Assert.That(sub.IsDirectory, Is.True);
			Assert.That(sub.HasChildren, Is.True);
			Assert.That(sub.Children, Is.Empty, "opening a root must not recursively scan descendants");
			Assert.That(tree.Nodes.SelectMany(node => node.Children), Is.Empty);
		});
	}

	[Test]
	public void Build_AnExplicitChildRequestReturnsOnlyThatChildrenLevel()
	{
		Touch("outer", "inner", "buried.md");
		Touch("outer", "visible.md");

		TreePayload child = FileTreeBuilder.Build(Path.Combine(_root, "outer"), requestId: 8);

		Assert.That(child.Nodes.Select(node => node.Name), Is.EqualTo(ChildLevelNames));
		Assert.That(child.Nodes[0].Children, Is.Empty);
		Assert.That(child.RequestId, Is.EqualTo(8));
	}

	[Test]
	public void Build_ListsEmptyDirectoriesWithoutProbingThem()
	{
		Directory.CreateDirectory(Path.Combine(_root, "empty"));

		TreeNode directory = FileTreeBuilder.Build(_root).Nodes.Single();

		Assert.Multiple(() =>
		{
			Assert.That(directory.Name, Is.EqualTo("empty"));
			Assert.That(directory.HasChildren, Is.True);
			Assert.That(directory.Children, Is.Empty);
		});
	}

	[Test]
	public void Build_SkipsDotDirectoriesAndNodeModulesAtTheRequestedLevel()
	{
		Touch("visible.md");
		Touch(".git", "notes.md");
		Touch("node_modules", "pkg", "readme.md");

		Assert.That(FileTreeBuilder.Build(_root).Nodes.Select(node => node.Name), Is.EqualTo(VisibleFileOnly));
	}

	[Test]
	public void Build_DoesNotExposeAReparsePoint()
	{
		Touch("real.md");
		string target = Path.Combine(Path.GetTempPath(), "specdesk-tree-target-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(target);
		try
		{
			string link = Path.Combine(_root, "outside");
			try
			{
				Directory.CreateSymbolicLink(link, target);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
			{
				Assert.Ignore("Symbolic links cannot be created in this environment.");
				return;
			}

			Assert.That(FileTreeBuilder.Build(_root).Nodes.Select(node => node.Name), Is.EqualTo(RealFileOnly));
		}
		finally
		{
			Directory.Delete(target, recursive: true);
		}
	}
}
