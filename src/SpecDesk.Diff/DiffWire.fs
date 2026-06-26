/// Flattens an AST diff into a C#-friendly wire shape for the host's `diff.result` payload: one entry
/// per CHANGED top-level block (Unchanged omitted), carrying the HEAD source-line range (added / changed
/// / moved) or, for a removed block (absent from the head), the head line it sat before plus its base
/// source text for a marker. Kept beside the diff engine so the head-line / removed-anchor walk is
/// unit-tested with it (docs/design/07-review-experience.md Part B).
module SpecDesk.Diff.DiffWire

/// One changed top-level block, ready for the wire. CLIMutable so the C# host can read its fields.
[<CLIMutable>]
type DiffWireEntry =
    { Kind: string // "added" | "removed" | "changed" | "moved"
      LineStart: int // head range start (added/changed/moved); unused for removed
      LineEnd: int // head range end (added/changed/moved); unused for removed
      AnchorLine: int // for removed: the head line it sat before; -1 otherwise
      RemovedText: string // for removed: the base source slice; "" otherwise
    }

/// The base source slice for an inclusive 0-based line range, clamped to the array.
let private slice (lines: string[]) (lineStart: int) (lineEnd: int) : string =
    let lo = max 0 lineStart
    let hi = min (lines.Length - 1) lineEnd
    if hi < lo then "" else lines.[lo..hi] |> String.concat "\n"

/// Diff base→head into wire entries, in document order. The removed anchor is the previous head-present
/// block's end line + 1 (0 when the deletion precedes all head content).
let toWire (baseText: string) (headText: string) : DiffWireEntry[] =
    let baseLines = baseText.Split('\n')
    let entries = ResizeArray<DiffWireEntry>()
    let mutable lastHeadLineEnd = -1

    let added kind (node: SpecDesk.Markdown.Ast.Node) =
        entries.Add(
            { Kind = kind
              LineStart = node.LineStart
              LineEnd = node.LineEnd
              AnchorLine = -1
              RemovedText = "" })

        lastHeadLineEnd <- node.LineEnd

    for entry in AstDiff.diffText baseText headText do
        match entry with
        | AstDiff.Unchanged node -> lastHeadLineEnd <- node.LineEnd
        | AstDiff.Added node -> added "added" node
        | AstDiff.Changed(_, after) -> added "changed" after
        | AstDiff.Moved(_, after) -> added "moved" after
        | AstDiff.Removed node ->
            entries.Add(
                { Kind = "removed"
                  LineStart = 0
                  LineEnd = 0
                  AnchorLine = lastHeadLineEnd + 1
                  RemovedText = slice baseLines node.LineStart node.LineEnd })

    entries.ToArray()
