namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class WorkspaceSearchTests
{
	private string _root = string.Empty;

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), "specdesk-search-" + Guid.NewGuid().ToString("N"));
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

	private void Write(string relativePath, string text)
	{
		string path = Path.Combine(_root, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);
	}

	[Test]
	public void Search_ForABlankQuery_ReturnsAnEmptyNonTruncatedOutcome()
	{
		Write("billing.md", "The refund window is 30 days.");

		WorkspaceSearchOutcome outcome = WorkspaceSearch.Search(_root, "   ");

		Assert.That(outcome.Hits, Is.Empty);
		Assert.That(outcome.Truncated, Is.False);
	}

	[Test]
	public void Search_ForAMissingRoot_ReturnsAnEmptyNonTruncatedOutcome()
	{
		WorkspaceSearchOutcome outcome = WorkspaceSearch.Search(Path.Combine(_root, "nope"), "refund");

		Assert.That(outcome.Hits, Is.Empty);
		Assert.That(outcome.Truncated, Is.False);
	}

	[Test]
	public void Search_AcrossSeveralNestedMarkdownFiles_FindsEachMatchWithItsLineAndSnippet()
	{
		Write("billing.md", "# Billing\n\nThe refund window is 30 days.\n");
		Write(Path.Combine("specs", "refunds.md"), "See the REFUND policy for details.");
		Write("notes.txt", "refund"); // not Markdown — must be ignored

		WorkspaceSearchOutcome outcome = WorkspaceSearch.Search(_root, "refund");

		Assert.That(outcome.Truncated, Is.False);
		Assert.That(outcome.Hits, Has.Count.EqualTo(2), "the case-insensitive match in refunds.md must also count");
		WorkspaceSearchHit billing = outcome.Hits.Single(hit => hit.Path.EndsWith("billing.md", StringComparison.Ordinal));
		Assert.Multiple(() =>
		{
			Assert.That(billing.Line, Is.EqualTo(3));
			Assert.That(billing.Snippet, Is.EqualTo("The refund window is 30 days."));
			Assert.That(outcome.Hits.Any(hit => hit.Path.EndsWith("refunds.md", StringComparison.Ordinal)), Is.True);
		});
	}

	[Test]
	public void Search_SkipsDotDirectoriesAndNodeModules()
	{
		Write(".git/notes.md", "refund");
		Write("node_modules/pkg/readme.md", "refund");
		Write("visible.md", "refund");

		WorkspaceSearchOutcome outcome = WorkspaceSearch.Search(_root, "refund");

		Assert.That(outcome.Hits, Has.Count.EqualTo(1));
		Assert.That(outcome.Hits[0].Path, Does.EndWith("visible.md"));
	}

	[Test]
	public void Search_DoesNotFollowAReparsePointDirectory()
	{
		Write("real.md", "refund");
		string target = Path.Combine(Path.GetTempPath(), "specdesk-search-target-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(target);
		File.WriteAllText(Path.Combine(target, "outside.md"), "refund");
		try
		{
			string link = Path.Combine(_root, "linked");
			try
			{
				Directory.CreateSymbolicLink(link, target);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
			{
				Assert.Ignore("Symbolic links cannot be created in this environment.");
				return;
			}

			WorkspaceSearchOutcome outcome = WorkspaceSearch.Search(_root, "refund");

			Assert.That(outcome.Hits, Has.Count.EqualTo(1));
			Assert.That(outcome.Hits[0].Path, Does.EndWith("real.md"));
		}
		finally
		{
			Directory.Delete(target, recursive: true);
		}
	}

	[Test]
	public void Search_StopsAndReportsTruncatedOnceTheEntryBudgetIsExhausted()
	{
		Write("a.md", "refund");
		Write("b.md", "refund");

		WorkspaceSearchOutcome outcome = WorkspaceSearch.Search(
			_root, "refund", WorkspaceSearch.MaxDuration, maxEntriesExamined: 1, WorkspaceSearch.MaxResults);

		Assert.That(outcome.Truncated, Is.True);
	}

	[Test]
	public void Search_StopsAndReportsTruncatedOnceTheTimeBudgetIsExhausted()
	{
		Write("a.md", "refund");

		WorkspaceSearchOutcome outcome = WorkspaceSearch.Search(
			_root, "refund", TimeSpan.Zero, WorkspaceSearch.MaxEntriesExamined, WorkspaceSearch.MaxResults);

		Assert.That(outcome.Truncated, Is.True);
	}

	[Test]
	public void Search_StopsAndReportsTruncatedOnceTheResultCapIsReached()
	{
		Write("a.md", "refund one");
		Write("b.md", "refund two");

		WorkspaceSearchOutcome outcome = WorkspaceSearch.Search(
			_root, "refund", WorkspaceSearch.MaxDuration, WorkspaceSearch.MaxEntriesExamined, maxResults: 1);

		Assert.That(outcome.Truncated, Is.True);
		Assert.That(outcome.Hits, Has.Count.EqualTo(1));
	}

	[Test]
	public void Search_ClipsALongMatchingLineToABoundedSnippetAroundTheHit()
	{
		string padding = new('x', 200);
		Write("long.md", $"{padding} refund {padding}");

		WorkspaceSearchOutcome outcome = WorkspaceSearch.Search(_root, "refund");

		Assert.That(outcome.Hits, Has.Count.EqualTo(1));
		string snippet = outcome.Hits[0].Snippet;
		Assert.That(snippet.Length, Is.LessThan(padding.Length * 2), "the snippet must be far shorter than the full line");
		Assert.That(snippet, Does.Contain("refund"));
		Assert.That(snippet, Does.StartWith("…"));
		Assert.That(snippet, Does.EndWith("…"));
	}
}
