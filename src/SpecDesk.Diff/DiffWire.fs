/// Flattens an AST diff into a C#-friendly wire shape for the host's `diff.result` payload: one entry
/// per CHANGED top-level block (Unchanged omitted), carrying the HEAD source-line range (added / changed
/// / moved) or, for a removed block (absent from the head), the head line it sat before plus its base
/// source text for a marker. A changed list/table also descends to per-child (row/item) diffs so the UI
/// can highlight the changed rows/items individually rather than washing the whole container. Kept beside
/// the diff engine so the head-line / removed-anchor walk is unit-tested with it
/// (docs/design/07-review-experience.md Part B).
module SpecDesk.Diff.DiffWire

open SpecDesk.Markdown

/// The wire `Kind` discriminator values for a diff entry / child entry — a projection of the AstDiff
/// classification (Unchanged is omitted from the wire). The single source for these strings: the C# host
/// passes them through verbatim and the webview keys its styling / labels / inline-word-diff off them, so
/// they are pinned across languages by SpecDesk.Diff.Tests' DiffKindContractTests →
/// webview/tests/contract/diff-kinds.json (mirrored by webview's DIFF_KINDS).
[<RequireQualifiedAccess>]
module DiffKind =
    [<Literal>]
    let Added = "added"

    [<Literal>]
    let Removed = "removed"

    [<Literal>]
    let Changed = "changed"

    [<Literal>]
    let Moved = "moved"

    /// Every wire kind, for the cross-language parity guard (order-independent).
    let all = [| Added; Removed; Changed; Moved |]

/// One changed child (list item / table row) of a changed container, in HEAD child-ordinal space — the
/// same ordinals the webview's per-container `childLineStarts` uses, so the UI maps index → row/item.
[<CLIMutable>]
type ChildWireEntry =
    { Kind: string // DiffKind.* — "added" | "removed" | "changed" | "moved"
      ChildIndex: int // head child ordinal (added/changed/moved); -1 for removed
      AnchorIndex: int // for removed: the head child it sat before; -1 otherwise
      RemovedText: string // for removed: the base child's flattened text; "" otherwise
      BaseText: string // for changed: the base child's flattened text (inline word-diff inside the item)
    }

/// One changed top-level block, ready for the wire. CLIMutable so the C# host can read its fields.
[<CLIMutable>]
type DiffWireEntry =
    { Kind: string // DiffKind.* — "added" | "removed" | "changed" | "moved"
      LineStart: int // head range start (added/changed/moved); unused for removed
      LineEnd: int // head range end (added/changed/moved); unused for removed
      AnchorLine: int // for removed: the head line it sat before; -1 otherwise
      RemovedText: string // for removed: the base source slice; "" otherwise
      Children: ChildWireEntry[] // per-child diff for a CHANGED list/table; [||] otherwise (whole-block)
      BaseText: string // for a CHANGED plain block: the base rendered text (Formatted-pane inline word-diff)
      BaseSource: string // for a CHANGED plain block: the base raw source (Code-pane inline word-diff)
    }

/// The base source slice for an inclusive 0-based line range, clamped to the array.
let private slice (lines: string[]) (lineStart: int) (lineEnd: int) : string =
    let lo = max 0 lineStart
    let hi = min (lines.Length - 1) lineEnd
    if hi < lo then "" else lines.[lo..hi] |> String.concat "\n"

/// The flattened text of one list item (its blocks joined).
let private listItemText (item: Ast.Block list) : string =
    item |> List.map AstDiff.blockText |> String.concat " "

/// The flattened text of one table row (its cells joined).
let private tableRowText (cells: Ast.Inline list list) : string =
    cells |> List.map Inlines.flatten |> String.concat " | "

/// The flattened child texts of a container block, in the SAME order the webview's `childLineStarts`
/// reports (list items in order; a table's header row first, then its body rows). None for a non-container.
let private childTexts (block: Ast.Block) : string list option =
    match block with
    | Ast.ListBlock(_, items) -> Some(items |> List.map listItemText)
    | Ast.Table(header, rows) ->
        let bodyTexts = rows |> List.map tableRowText
        Some(if List.isEmpty header then bodyTexts else tableRowText header :: bodyTexts)
    | _ -> None

/// Wrap flattened child texts as synthetic single-paragraph nodes whose line range IS the child ordinal,
/// so reusing `AstDiff.diff` yields each head child's classification keyed by its ordinal.
let private synthDoc (texts: string list) : Ast.Document =
    texts
    |> List.mapi (fun i t ->
        { Ast.Content = Ast.Paragraph [ Ast.Text t ]
          Ast.LineStart = i
          Ast.LineEnd = i })

/// Diff a container's children (base vs head) into per-child wire entries, in child order. Mirrors the
/// top-level walk but in child-ordinal space: Unchanged omitted; a removed child anchors after the
/// previous head-present child.
let private childDiff (baseTexts: string list) (headTexts: string list) : ChildWireEntry[] =
    let entries = ResizeArray<ChildWireEntry>()
    let mutable lastHeadChild = -1

    let emit kind (after: Ast.Node) (baseText: string) =
        entries.Add(
            { Kind = kind
              ChildIndex = after.LineStart
              AnchorIndex = -1
              RemovedText = ""
              BaseText = baseText })

        lastHeadChild <- after.LineEnd

    for entry in AstDiff.diff (synthDoc baseTexts) (synthDoc headTexts) do
        match entry with
        | AstDiff.Unchanged node -> lastHeadChild <- node.LineEnd
        | AstDiff.Added node -> emit DiffKind.Added node ""
        // A changed child carries its base text so the webview can word-diff the row/item inline.
        | AstDiff.Changed(before, after) -> emit DiffKind.Changed after (AstDiff.blockText before.Content)
        | AstDiff.Moved(_, after) -> emit DiffKind.Moved after ""
        | AstDiff.Removed node ->
            entries.Add(
                { Kind = DiffKind.Removed
                  ChildIndex = -1
                  AnchorIndex = lastHeadChild + 1
                  RemovedText = AstDiff.blockText node.Content
                  BaseText = "" })

    entries.ToArray()

/// Diff base→head into wire entries, in document order. The removed anchor is the previous head-present
/// block's end line + 1 (0 when the deletion precedes all head content).
let toWire (baseText: string) (headText: string) : DiffWireEntry[] =
    let baseLines = baseText.Split('\n')
    let entries = ResizeArray<DiffWireEntry>()
    let mutable lastHeadLineEnd = -1

    let emit kind (node: Ast.Node) (children: ChildWireEntry[]) (baseText: string) (baseSource: string) =
        entries.Add(
            { Kind = kind
              LineStart = node.LineStart
              LineEnd = node.LineEnd
              AnchorLine = -1
              RemovedText = ""
              Children = children
              BaseText = baseText
              BaseSource = baseSource })

        lastHeadLineEnd <- node.LineEnd

    for entry in AstDiff.diffText baseText headText do
        match entry with
        | AstDiff.Unchanged node -> lastHeadLineEnd <- node.LineEnd
        | AstDiff.Added node -> emit DiffKind.Added node [||] "" ""
        | AstDiff.Moved(_, after) -> emit DiffKind.Moved after [||] "" ""
        | AstDiff.Changed(before, after) ->
            // A changed list/table descends to per-child diffs (highlight the changed rows/items, not the
            // whole container); any other changed block carries its base rendered AND raw-source text so
            // the webview can word-diff it inline in the Formatted and Code panes respectively (or, if too
            // much changed, fall back to a whole-block wash).
            match childTexts before.Content, childTexts after.Content with
            | Some baseChildren, Some headChildren ->
                emit DiffKind.Changed after (childDiff baseChildren headChildren) "" ""
            | _ ->
                emit
                    DiffKind.Changed
                    after
                    [||]
                    (AstDiff.blockText before.Content)
                    (slice baseLines before.LineStart before.LineEnd)
        | AstDiff.Removed node ->
            entries.Add(
                { Kind = DiffKind.Removed
                  LineStart = 0
                  LineEnd = 0
                  AnchorLine = lastHeadLineEnd + 1
                  RemovedText = slice baseLines node.LineStart node.LineEnd
                  Children = [||]
                  BaseText = ""
                  BaseSource = "" })

    entries.ToArray()
