using SpecDesk.Contracts;
using SpecDesk.Diff;

namespace SpecDesk.Host;

/// <summary>
/// Projects a (base, head) text pair to the <c>diff.result</c> wire payload: runs the F# structural
/// diff (<see cref="DiffWire"/>) and maps its entries to the C# <see cref="DiffResultPayload"/> shapes.
/// Pure and stateless — extracted from <c>HostController.OnCompare</c> so the to-be-frozen diff wire
/// shape (entries + nested children) is directly testable, not only reachable through the controller.
/// </summary>
internal static class DiffProjection
{
    /// <summary>
    /// Build the <c>diff.result</c> payload for a (base, head) pair. A null base — no committed version
    /// (a new file / unborn repo) — yields an empty diff, which clears any overlay in the webview.
    /// </summary>
    public static DiffResultPayload Build(string? baseText, string headText)
    {
        DiffWire.DiffWireEntry[] wire = baseText is null ? [] : DiffWire.toWire(baseText, headText);

        List<DiffEntryPayload> entries = new(wire.Length);
        foreach (DiffWire.DiffWireEntry w in wire)
        {
            ChildDiffPayload[] children = w.Children.Length == 0
                ? []
                : Array.ConvertAll(
                    w.Children,
                    c => new ChildDiffPayload(c.Kind, c.ChildIndex, c.AnchorIndex, c.RemovedText, c.BaseText));
            entries.Add(new DiffEntryPayload(
                w.Kind, w.LineStart, w.LineEnd, w.AnchorLine, w.RemovedText, children, w.BaseText, w.BaseSource));
        }

        return new DiffResultPayload(entries);
    }
}
