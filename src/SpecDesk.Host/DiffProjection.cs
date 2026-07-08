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
        // The base comes straight off the git blob (LibGit2DocumentVersioning.ReadHeadContent) — a CRLF
        // document's committed content still carries "\r", whereas headText is already LF-only (the
        // webview's CodeMirror model normalizes every line break on the way in). Normalize here, the
        // single point on the base→diff path, BEFORE toWire so neither its line-Split base slices
        // (BaseSource/RemovedText) nor the AST it feeds AstDiff.diffText through ever see a "\r": a
        // mismatched "\r\n" vs "\n" pair on every line would otherwise surface as a whitespace add/del
        // pair in the webview's word diff and inflate changeRatio on every multi-line paragraph.
        string? normalizedBase = baseText is null
            ? null
            : baseText.Contains('\r') ? baseText.Replace("\r\n", "\n").Replace("\r", "\n") : baseText;

        DiffWire.DiffWireEntry[] wire = normalizedBase is null ? [] : DiffWire.toWire(normalizedBase, headText);

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
