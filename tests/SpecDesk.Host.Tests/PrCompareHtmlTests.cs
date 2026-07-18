using SpecDesk.Contracts;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

/// <summary>
/// The PoC-7 Part C compare-HTML renderer (<see cref="PrCompareHtml"/>). It reuses the same structural diff as
/// the local "Show changes" overlay and renders it into the self-contained HTML the <c>pr.compare.rendered</c>
/// reply carries — a changed/added/moved block is tagged <c>data-diff</c> in the rendered view and styled in
/// the raw view, and a removed base block is shown as a marker. The real Markdig renderer is used for the
/// rendered mode so the <c>data-line-start</c> ↔ line-map parallelism the annotator relies on is exercised.
/// </summary>
[TestFixture]
public sealed class PrCompareHtmlTests
{
	// Blank-line-separated paragraphs, so each is its own top-level block (consecutive non-blank lines would
	// parse as one paragraph and the diff would be block-, not line-, granular).
	private const string Base = "# Title\n\nPara one.\n\nPara two.\n";
	private const string HeadChangedAndAdded = "# Title\n\nPara one changed.\n\nPara two.\n\nPara three added.\n";

	[Test]
	public void Raw_StylesChangedAndAddedHeadLines_AndKeepsUnchangedAsContext()
	{
		string html = PrCompareHtml.Build(Base, HeadChangedAndAdded, PrCompareModes.Raw, "", Renderer.render);

		Assert.Multiple(() =>
		{
			Assert.That(html, Does.Contain("pr-compare--raw"));
			Assert.That(html, Does.Contain("<div class=\"cmp-line cmp-changed\">Para one changed.</div>"));
			Assert.That(html, Does.Contain("<div class=\"cmp-line cmp-added\">Para three added.</div>"));
			Assert.That(html, Does.Contain("<div class=\"cmp-line cmp-context\">Para two.</div>"));
		});
	}

	[Test]
	public void Rendered_TagsChangedAndAddedBlocksWithDataDiff()
	{
		string html = PrCompareHtml.Build(Base, HeadChangedAndAdded, PrCompareModes.Rendered, "", Renderer.render);

		Assert.Multiple(() =>
		{
			Assert.That(html, Does.Contain("pr-compare--rendered"));
			Assert.That(html, Does.Contain("data-diff=\"changed\""));
			Assert.That(html, Does.Contain("data-diff=\"added\""));
			// The changed paragraph's new text is present (the head is what is rendered).
			Assert.That(html, Does.Contain("Para one changed."));
		});
	}

	[Test]
	public void Raw_ShowsRemovedBaseLinesInline()
	{
		string html = PrCompareHtml.Build(
			"Para one.\n\nPara two.\n", "Para one.\n", PrCompareModes.Raw, "", Renderer.render);

		Assert.That(html, Does.Contain("<div class=\"cmp-line cmp-removed\">Para two.</div>"));
	}

	[Test]
	public void Rendered_ShowsRemovedBaseBlockAsAMarker()
	{
		string html = PrCompareHtml.Build(
			"Para one.\n\nPara two.\n", "Para one.\n", PrCompareModes.Rendered, "", Renderer.render);

		Assert.Multiple(() =>
		{
			Assert.That(html, Does.Contain("cmp-removed"));
			Assert.That(html, Does.Contain("<del>Para two.</del>"));
		});
	}

	[Test]
	public void Build_WithNoBaseline_ShowsTheHeadWithNoDiffMarks()
	{
		// A null base (the file doesn't exist on the chosen base) yields an empty diff — the head is shown
		// plainly, with no data-diff annotations, rather than everything reading as "added".
		string html = PrCompareHtml.Build(null, "Para one.\n", PrCompareModes.Rendered, "", Renderer.render);

		Assert.Multiple(() =>
		{
			Assert.That(html, Does.Contain("Para one."));
			Assert.That(html, Does.Not.Contain("data-diff="));
		});
	}

	[Test]
	public void Build_NormalizesCrlfSoNoPhantomWhitespaceDiff()
	{
		// A CRLF head against an LF base (both are the same content) must produce no changed blocks — the
		// normalization guards against a phantom per-line whitespace diff.
		string html = PrCompareHtml.Build(
			"Para one.\n", "Para one.\r\n", PrCompareModes.Rendered, "", Renderer.render);

		Assert.That(html, Does.Not.Contain("data-diff="));
	}
}
