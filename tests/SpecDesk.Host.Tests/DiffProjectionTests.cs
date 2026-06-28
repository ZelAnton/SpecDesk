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
}
