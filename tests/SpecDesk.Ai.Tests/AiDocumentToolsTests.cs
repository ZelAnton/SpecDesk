using SpecDesk.Ai;

namespace SpecDesk.Ai.Tests;

// The read-only document tools (getCurrentDoc / getDiff): the diff summary, the bounded, data-framed context
// blocks, and the explicit non-mutating allowlist the assistant is limited to.
[TestFixture]
public sealed class AiDocumentToolsTests
{
	[Test]
	public void Allowlist_IsExactlyTheTwoReadOnlyTools()
	{
		Assert.That(
			AiReadOnlyTools.Allowlist,
			Is.EqualTo(new[] { AiReadOnlyTools.GetCurrentDoc, AiReadOnlyTools.GetDiff }));
		Assert.Multiple(() =>
		{
			Assert.That(AiReadOnlyTools.GetCurrentDoc, Is.EqualTo("getCurrentDoc"));
			Assert.That(AiReadOnlyTools.GetDiff, Is.EqualTo("getDiff"));
		});
	}

	[Test]
	public void DocumentToolset_IsAnImmutableSnapshot_ReturningWhatItWasBuiltWith()
	{
		DocumentContext document = new("spec.md", "docs/spec.md", "body", "repo", "draft/x", "main");
		DocumentDiff diff = DocumentDiff.Between("a\n", "a\nb\n");
		DocumentToolset toolset = new(document, diff);

		Assert.Multiple(() =>
		{
			// Repeated reads never change the snapshot — there is no mutating operation on the tool surface.
			Assert.That(toolset.GetCurrentDocument(), Is.SameAs(document));
			Assert.That(toolset.GetCurrentDocument(), Is.SameAs(document));
			Assert.That(toolset.GetDiff(), Is.SameAs(diff));
		});
	}

	[Test]
	public void Between_WithNoBase_ReportsAllLinesAddedAsANewDocument()
	{
		DocumentDiff diff = DocumentDiff.Between(baseText: null, "line one\nline two\n");

		Assert.Multiple(() =>
		{
			Assert.That(diff.IsNewDocument, Is.True);
			Assert.That(diff.AddedLines, Is.EqualTo(2));
			Assert.That(diff.RemovedLines, Is.EqualTo(0));
			Assert.That(diff.HasChanges, Is.True);
		});
	}

	[Test]
	public void Between_IdenticalText_ReportsNoChanges()
	{
		DocumentDiff diff = DocumentDiff.Between("# Title\n\nBody\n", "# Title\n\nBody\n");

		Assert.Multiple(() =>
		{
			Assert.That(diff.HasChanges, Is.False);
			Assert.That(diff.AddedLines, Is.EqualTo(0));
			Assert.That(diff.RemovedLines, Is.EqualTo(0));
			Assert.That(diff.Preview, Is.Empty);
		});
	}

	[Test]
	public void Between_ChangedMiddle_CountsTheReplacedRegionViaCommonPrefixAndSuffix()
	{
		// Shared first and last line; the middle line is replaced (one removed, one added).
		DocumentDiff diff = DocumentDiff.Between("intro\nold middle\nend\n", "intro\nnew middle\nend\n");

		Assert.Multiple(() =>
		{
			Assert.That(diff.AddedLines, Is.EqualTo(1));
			Assert.That(diff.RemovedLines, Is.EqualTo(1));
			Assert.That(diff.Preview, Does.Contain("- old middle"));
			Assert.That(diff.Preview, Does.Contain("+ new middle"));
			Assert.That(diff.Preview, Does.Not.Contain("intro"), "the shared prefix is not part of the diff");
		});
	}

	[Test]
	public void Between_NormalizesCrlfBase_SoAMatchingDocumentIsNotAllChanged()
	{
		// The committed base carries CRLF (straight off the git blob); the working text is LF-only.
		DocumentDiff diff = DocumentDiff.Between("one\r\ntwo\r\n", "one\ntwo\n");

		Assert.That(diff.HasChanges, Is.False);
	}

	[Test]
	public void DocumentContext_ToContextBlock_FramesTheContentAsDataAndBoundsTheText()
	{
		DocumentContext document = new(
			"billing.md", "docs/billing.md", new string('x', 5_000), "spec-repo", "draft/refunds", "main");

		string block = document.ToContextBlock(500);

		Assert.Multiple(() =>
		{
			Assert.That(block, Does.Contain("getCurrentDoc"));
			Assert.That(block, Does.Contain("not instructions"));
			Assert.That(block, Does.Contain("billing.md"));
			Assert.That(block, Does.Contain("docs/billing.md"));
			Assert.That(block, Does.Contain("[document truncated]"));
			Assert.That(block.Length, Is.LessThan(700), "the block stays near the requested budget");
		});
	}

	[Test]
	public void DocumentDiff_ToContextBlock_FramesTheDiffAsDataAndSummarizesTheCounts()
	{
		DocumentDiff diff = DocumentDiff.Between("a\nb\n", "a\nc\nd\n");

		string block = diff.ToContextBlock(2_000);

		Assert.Multiple(() =>
		{
			Assert.That(block, Does.Contain("getDiff"));
			Assert.That(block, Does.Contain("not instructions"));
			Assert.That(block, Does.Contain("added"));
			Assert.That(block, Does.Contain("removed"));
		});
	}
}
