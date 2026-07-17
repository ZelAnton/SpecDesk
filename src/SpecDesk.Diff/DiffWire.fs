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
/// This is the flat intermediate the host reads by field (CLIMutable); the host then projects it to the
/// wire's per-kind discriminated payload (SpecDesk.Contracts ChildDiffPayload subtypes). Built ONLY via
/// the per-kind `Build` helpers below, so fields a kind has no use for hold a neutral sentinel and an
/// illegal combination (a removed child with a head ordinal, a changed child with no base) is never formed.
[<CLIMutable>]
type ChildWireEntry =
    { Kind: string // DiffKind.* — "added" | "removed" | "changed" | "moved"
      ChildIndex: int // head child ordinal (added/changed/moved); -1 for removed
      AnchorIndex: int // for removed: the head child it sat before; -1 otherwise
      RemovedText: string // for removed: the base child's flattened text; "" otherwise
      BaseText: string // for changed: the base child's flattened text (Formatted-pane inline word-diff inside the item)
      BaseSource: string } // for changed: the base child's raw source slice (Code-pane inline word-diff); "" otherwise

/// One changed top-level block — the flat intermediate the host reads by field (CLIMutable) and then
/// projects to the wire's per-kind discriminated payload (SpecDesk.Host.DiffProjection →
/// SpecDesk.Contracts DiffEntryPayload subtypes). Built ONLY through the per-kind `Build` helpers below,
/// which keep each kind's fields consistent (added/changed/moved carry the head range; removed carries the
/// anchor + base text) — so the flat shape can't encode "removed with a range" or "changed with no base".
[<CLIMutable>]
type DiffWireEntry =
    { Kind: string // DiffKind.* — "added" | "removed" | "changed" | "moved"
      LineStart: int // head range start (added/changed/moved); unused for removed
      LineEnd: int // head range end (added/changed/moved); unused for removed
      AnchorLine: int // for removed: the head line it sat before; -1 otherwise
      RemovedText: string // for removed: the base source slice; "" otherwise
      Children: ChildWireEntry[] // per-child diff for a CHANGED list/table; [||] otherwise (whole-block)
      BaseText: string // for a CHANGED plain block: the base rendered text (Formatted-pane inline word-diff)
      BaseSource: string } // for a CHANGED plain block: the base raw source (Code-pane inline word-diff)

/// Per-kind builders for the flat wire DTOs above — the single construction path in this module. Each sets
/// ONLY the fields its kind uses and leaves the rest at their neutral sentinel, so a wire entry can't be
/// built with a range a removed block has no use for, or without the base a changed block needs: the
/// illegal kind/field combinations the flat shape could otherwise encode are unreachable at the call sites.
/// (The host then discriminates these flat records into the wire's per-kind payloads — DiffProjection.)
[<RequireQualifiedAccess>]
module private Build =
    let childAdded (childIndex: int) : ChildWireEntry =
        { Kind = DiffKind.Added
          ChildIndex = childIndex
          AnchorIndex = -1
          RemovedText = ""
          BaseText = ""
          BaseSource = "" }

    let childMoved (childIndex: int) : ChildWireEntry =
        { Kind = DiffKind.Moved
          ChildIndex = childIndex
          AnchorIndex = -1
          RemovedText = ""
          BaseText = ""
          BaseSource = "" }

    let childChanged (childIndex: int) (baseText: string) (baseSource: string) : ChildWireEntry =
        { Kind = DiffKind.Changed
          ChildIndex = childIndex
          AnchorIndex = -1
          RemovedText = ""
          BaseText = baseText
          BaseSource = baseSource }

    let childRemoved (anchorIndex: int) (removedText: string) : ChildWireEntry =
        { Kind = DiffKind.Removed
          ChildIndex = -1
          AnchorIndex = anchorIndex
          RemovedText = removedText
          BaseText = ""
          BaseSource = "" }

    let added (lineStart: int) (lineEnd: int) : DiffWireEntry =
        { Kind = DiffKind.Added
          LineStart = lineStart
          LineEnd = lineEnd
          AnchorLine = -1
          RemovedText = ""
          Children = [||]
          BaseText = ""
          BaseSource = "" }

    let moved (lineStart: int) (lineEnd: int) : DiffWireEntry =
        { Kind = DiffKind.Moved
          LineStart = lineStart
          LineEnd = lineEnd
          AnchorLine = -1
          RemovedText = ""
          Children = [||]
          BaseText = ""
          BaseSource = "" }

    let removed (anchorLine: int) (removedText: string) : DiffWireEntry =
        { Kind = DiffKind.Removed
          LineStart = 0
          LineEnd = 0
          AnchorLine = anchorLine
          RemovedText = removedText
          Children = [||]
          BaseText = ""
          BaseSource = "" }

    let changed
        (lineStart: int)
        (lineEnd: int)
        (children: ChildWireEntry[])
        (baseText: string)
        (baseSource: string)
        : DiffWireEntry =
        { Kind = DiffKind.Changed
          LineStart = lineStart
          LineEnd = lineEnd
          AnchorLine = -1
          RemovedText = ""
          Children = children
          BaseText = baseText
          BaseSource = baseSource }

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

        Some(
            if List.isEmpty header then
                bodyTexts
            else
                tableRowText header :: bodyTexts
        )
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
/// previous head-present child. `baseChildRanges` holds each BASE child's source line range (in base
/// child-ordinal order, from Projection.childLineRanges) so a changed child can carry its base source
/// slice — the inline word-diff the Code pane runs on a row/item, symmetric to a top-level Changed block.
let private childDiff
    (baseLines: string[])
    (baseChildRanges: (int * int) list)
    (baseTexts: string list)
    (headTexts: string list)
    : ChildWireEntry[] =
    let entries = ResizeArray<ChildWireEntry>()
    let baseRanges = List.toArray baseChildRanges
    let mutable lastHeadChild = -1

    // Record a head-present child and advance the anchor cursor past it (a later removed child anchors
    // after the last head-present one).
    let emit (child: ChildWireEntry) (headEnd: int) =
        entries.Add child
        lastHeadChild <- headEnd

    // The base source slice for a base child ordinal — the synthetic node's LineStart IS that ordinal
    // (synthDoc). "" when the ordinal has no range (a childRanges/childTexts count mismatch), so the
    // field degrades to empty rather than slicing the wrong child.
    let baseSourceOf (baseOrdinal: int) : string =
        if baseOrdinal >= 0 && baseOrdinal < baseRanges.Length then
            let ls, le = baseRanges.[baseOrdinal]
            slice baseLines ls le
        else
            ""

    for entry in AstDiff.diff (synthDoc baseTexts) (synthDoc headTexts) do
        match entry with
        | AstDiff.Unchanged node -> lastHeadChild <- node.LineEnd
        | AstDiff.Added node -> emit (Build.childAdded node.LineStart) node.LineEnd
        // A changed child carries its base flattened text (Formatted-pane word-diff) and base source slice
        // (Code-pane word-diff) so the webview can word-diff the row/item inline in either pane.
        | AstDiff.Changed(before, after) ->
            emit
                (Build.childChanged after.LineStart (AstDiff.blockText before.Content) (baseSourceOf before.LineStart))
                after.LineEnd
        | AstDiff.Moved(_, after) -> emit (Build.childMoved after.LineStart) after.LineEnd
        | AstDiff.Removed node -> entries.Add(Build.childRemoved (lastHeadChild + 1) (AstDiff.blockText node.Content))

    entries.ToArray()

/// Build the wire entries for an already-computed diff of base→head (base text needed for its line slices
/// / container-child source ranges). Extracted from {@link toWire} so {@link toWireDetailed} can skip this
/// entirely for an overflowing pair — whose diff is AstDiff's flat Removed+Added fallback — instead of
/// slicing every removed block's full base text into a wire entry only to discard it for the compact
/// overflow signal that replaces them.
let private buildEntries (baseText: string) (diffResult: AstDiff.DocumentDiff) : DiffWireEntry[] =
    let baseLines = baseText.Split('\n')
    // The base's container-child source ranges, keyed by the base container's top-level start line, so a
    // changed child (below) can be sliced back to its base source. A second (cheap, O(n)) parse of the
    // base next to AstDiff's — kept out of the Ast so a block's structural equality (the diff backbone)
    // stays position-independent (see Projection.childLineRanges).
    let baseChildRanges = Projection.childLineRanges baseText
    let entries = ResizeArray<DiffWireEntry>()
    let mutable lastHeadLineEnd = -1

    // Record a head-present block and advance the removed-anchor cursor past it.
    let emit (entry: DiffWireEntry) (headEnd: int) =
        entries.Add entry
        lastHeadLineEnd <- headEnd

    for entry in diffResult do
        match entry with
        | AstDiff.Unchanged node -> lastHeadLineEnd <- node.LineEnd
        | AstDiff.Added node -> emit (Build.added node.LineStart node.LineEnd) node.LineEnd
        | AstDiff.Moved(_, after) -> emit (Build.moved after.LineStart after.LineEnd) after.LineEnd
        | AstDiff.Changed(before, after) ->
            // A changed list/table descends to per-child diffs (highlight the changed rows/items, not the
            // whole container); any other changed block carries its base rendered AND raw-source text so
            // the webview can word-diff it inline in the Formatted and Code panes respectively (or, if too
            // much changed, fall back to a whole-block wash).
            match childTexts before.Content, childTexts after.Content with
            | Some baseChildren, Some headChildren ->
                // The base container's per-child source ranges (base child-ordinal order), looked up by
                // its top-level start line — the same line Projection.childLineRanges keyed them by.
                let childRanges =
                    baseChildRanges |> Map.tryFind before.LineStart |> Option.defaultValue []

                let children = childDiff baseLines childRanges baseChildren headChildren

                if Array.isEmpty children then
                    // The container-level AstDiff classified this as Changed (its real, mark-aware content
                    // differs), but childDiff's own per-child comparison runs on FLATTENED (mark-stripped)
                    // text — so a formatting-only edit inside one item/row (e.g. toggling bold, with no
                    // word actually added/removed) leaves every child looking identical and childDiff finds
                    // nothing to highlight. Fall back to the same whole-block plain-text base a non-container
                    // Changed block carries, so the webview still has something to word-diff against instead
                    // of an empty base (which otherwise reads as "everything is new" and washes the whole
                    // container) — matching text on both sides here means nothing gets highlighted, which is
                    // the correct, minimal signal for a style-only edit.
                    emit
                        (Build.changed
                            after.LineStart
                            after.LineEnd
                            [||]
                            (AstDiff.blockText before.Content)
                            (slice baseLines before.LineStart before.LineEnd))
                        after.LineEnd
                else
                    emit (Build.changed after.LineStart after.LineEnd children "" "") after.LineEnd
            | _ ->
                emit
                    (Build.changed
                        after.LineStart
                        after.LineEnd
                        [||]
                        (AstDiff.blockText before.Content)
                        (slice baseLines before.LineStart before.LineEnd))
                    after.LineEnd
        | AstDiff.Removed node ->
            entries.Add(Build.removed (lastHeadLineEnd + 1) (slice baseLines node.LineStart node.LineEnd))

    entries.ToArray()

/// Diff base→head into wire entries, in document order. The removed anchor is the previous head-present
/// block's end line + 1 (0 when the deletion precedes all head content). For an overflowing pair (see
/// {@link toWireDetailed}) this still returns the flat Removed+Added fallback's full entries — kept
/// unchanged for direct callers/tests of this function; DiffProjection.cs uses {@link toWireDetailed}
/// instead so the host never builds (or ships) that flat listing for the `diff.result` payload.
let toWire (baseText: string) (headText: string) : DiffWireEntry[] =
    buildEntries baseText (AstDiff.diffText baseText headText)

/// Compact overflow signal from {@link toWireDetailed}: whether AstDiff's node-pair guard forced the flat
/// Removed+Added fallback for this pair, and — only when it did — how many base blocks were flatly removed
/// and head blocks flatly added. Deliberately NOT their text (`RemovedText`): the whole point of this type
/// is to avoid building/slicing every removed block's base text only to discard it for a count-only signal.
/// CLIMutable + a sentinel-when-unused shape, the same C#-friendly convention as the flat wire DTOs above
/// (kept an explicit record rather than an F# option, which would leak FSharpOption plumbing into the host).
[<CLIMutable>]
type OverflowSignal =
    { Overflowed: bool
      RemovedCount: int // meaningful only when Overflowed; 0 otherwise
      AddedCount: int } // meaningful only when Overflowed; 0 otherwise

/// The not-overflowing sentinel {@link OverflowSignal} — {@link toWireDetailed} returns this alongside its
/// normal, fully-enumerated entries.
let private noOverflow: OverflowSignal =
    { Overflowed = false
      RemovedCount = 0
      AddedCount = 0 }

/// `toWire`, plus whether AstDiff's node-pair guard forced the flat fallback for this pair — and, when it
/// did, a compact {@link OverflowSignal} INSTEAD of the flat entries (`DiffWireEntry[]` is empty in that
/// case): skips building/slicing every removed block's base text, which the overflow signal replaces.
/// DiffProjection.cs uses this (not `toWire`) for the `diff.result` payload, so an overflowing document
/// never ships every removed block's full text over IPC or paints thousands of decorations in the webview;
/// `toWire` itself keeps its existing, fully-enumerated behavior for a non-overflowing pair (and for its
/// own direct callers/tests, which predate this guard).
let toWireDetailed (baseText: string) (headText: string) : DiffWireEntry[] * OverflowSignal =
    let diffResult, overflow = AstDiff.diffTextDetailed baseText headText

    if overflow then
        let removedCount =
            diffResult
            |> List.sumBy (function
                | AstDiff.Removed _ -> 1
                | _ -> 0)

        let addedCount =
            diffResult
            |> List.sumBy (function
                | AstDiff.Added _ -> 1
                | _ -> 0)

        [||],
        { Overflowed = true
          RemovedCount = removedCount
          AddedCount = addedCount }
    else
        buildEntries baseText diffResult, noOverflow
