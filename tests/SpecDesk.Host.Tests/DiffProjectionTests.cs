using SpecDesk.Contracts;

namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class DiffProjectionTests
{
    [Test]
    public void Build_WithNoBaseVersion_IsAnEmptyDiffThatClearsTheOverlay()
    {
        DiffResultPayload payload = DiffProjection.Build(null, "# Head\n\nbody\n");
        Assert.That(payload.Entries, Is.Empty);
    }

    [Test]
    public void Build_MapsAChangedParagraphToAWholeBlockEntry()
    {
        DiffResultPayload payload = DiffProjection.Build(
            "The refund window is 14 days.\n", "The refund window is 30 days.\n");

        DiffEntryPayload? changed = payload.Entries.FirstOrDefault(e => e.Kind == "changed");
        Assert.That(changed, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(changed!.BaseText, Does.Contain("14 days"));
            // A plain block washes whole — it carries no per-child entries.
            Assert.That(changed.Children, Is.Empty);
        });
    }

    [Test]
    public void Build_MapsAChangedListItemToAChangedChild()
    {
        DiffResultPayload payload = DiffProjection.Build(
            "- one\n- two\n- three\n", "- one\n- two changed\n- three\n");

        DiffEntryPayload? container = payload.Entries.FirstOrDefault(e => e.Children.Count > 0);
        Assert.That(container, Is.Not.Null);
        ChildDiffPayload child = container!.Children[0];
        Assert.Multiple(() =>
        {
            Assert.That(child.Kind, Is.EqualTo("changed"));
            Assert.That(child.ChildIndex, Is.EqualTo(1)); // the second item
            Assert.That(child.BaseText, Does.Contain("two"));
        });
    }

    // T-079: `ReadHeadContent` hands back the raw git blob, which for a CRLF-committed document still
    // carries "\r" — while `headText` (from the webview's CodeMirror model) is always LF-only. A "\r" left
    // in on the base side would mismatch every LF in the head at each line break, surfacing as a phantom
    // whitespace add/del pair in the webview's word diff (word-diff.ts tokenizes "\r\n" and "\n" as
    // different whitespace runs) and inflating changeRatio on any multi-line paragraph.

    [Test]
    public void Build_NormalizesACrlfBaseBeforeDiffing_ChangedEntryCarriesNoCarriageReturn()
    {
        string crlfBase = "Line one of the paragraph.\r\nLine two of the paragraph.\r\nLine three, unchanged.\r\n";
        string head = "Line one of the paragraph.\nLine two CHANGED.\nLine three, unchanged.\n";

        DiffResultPayload payload = DiffProjection.Build(crlfBase, head);

        DiffEntryPayload? changed = payload.Entries.FirstOrDefault(e => e.Kind == "changed");
        Assert.That(changed, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(changed!.BaseText, Does.Not.Contain('\r'));
            Assert.That(changed.BaseSource, Does.Not.Contain('\r'));
            Assert.That(changed.BaseSource, Does.Contain("Line two of the paragraph."));
        });
    }

    [Test]
    public void Build_NormalizesACrlfBaseBeforeDiffing_RemovedEntryCarriesNoCarriageReturn()
    {
        string crlfBase = "# Heading\r\n\r\ngone paragraph\r\nspanning two lines\r\n\r\nkeep\r\n";
        string head = "# Heading\n\nkeep\n";

        DiffResultPayload payload = DiffProjection.Build(crlfBase, head);

        DiffEntryPayload? removed = payload.Entries.FirstOrDefault(e => e.Kind == "removed");
        Assert.That(removed, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(removed!.RemovedText, Does.Not.Contain('\r'));
            Assert.That(removed.RemovedText, Does.Contain("gone paragraph"));
        });
    }

    [Test]
    public void Build_NormalizesACrlfBaseBeforeDiffing_ChangedCodeBlockCarriesNoCarriageReturn()
    {
        // AstDiff.blockText for a CodeBlock returns the fenced content verbatim — check it does not
        // smuggle a "\r" into BaseText/BaseSource in a way that bypasses DiffProjection's normalization.
        string crlfBase = "```text\r\nfirst code line\r\nsecond code line\r\n```\r\n";
        string head = "```text\nfirst code line\nsecond code line CHANGED\n```\n";

        DiffResultPayload payload = DiffProjection.Build(crlfBase, head);

        DiffEntryPayload? changed = payload.Entries.FirstOrDefault(e => e.Kind == "changed");
        Assert.That(changed, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(changed!.BaseText, Does.Not.Contain('\r'));
            Assert.That(changed.BaseSource, Does.Not.Contain('\r'));
            Assert.That(changed.BaseText, Does.Contain("second code line"));
        });
    }

    [Test]
    public void Build_NormalizesACrlfBaseBeforeDiffing_ChangedListChildCarriesNoCarriageReturn()
    {
        string crlfBase = "- one\r\n- two\r\n- three\r\n";
        string head = "- one\n- two changed\n- three\n";

        DiffResultPayload payload = DiffProjection.Build(crlfBase, head);

        DiffEntryPayload? container = payload.Entries.FirstOrDefault(e => e.Children.Count > 0);
        Assert.That(container, Is.Not.Null);
        ChildDiffPayload child = container!.Children[0];
        Assert.That(child.BaseText, Does.Not.Contain('\r'));
    }
}
