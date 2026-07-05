/// Structural (AST-level) diff of two Markdown documents' top-level blocks. It classifies each node
/// as unchanged / added / removed / changed (same kind of node, edited) / moved (identical content,
/// relocated), so a review reads as *structure* rather than line noise — a heading-level change is one
/// edit, a reordered paragraph is a move, not a delete + add (docs/design/07-review-experience.md
/// Part B). Pure and input-agnostic: it takes two already-parsed `Ast.Document`s, so PoC-7 can point
/// it at any `(base, head)` pair (PR base/head, working copy, `main`). Rendering the annotated result
/// to HTML is a separate, later concern.
module SpecDesk.Diff.AstDiff

open System
open SpecDesk.Markdown

/// How one top-level node relates the base document to the head document.
type DiffEntry =
    /// Identical content, kept in the unchanged backbone.
    | Unchanged of Ast.Node
    /// Present only in the head (a new node).
    | Added of Ast.Node
    /// Present only in the base (a deleted node).
    | Removed of Ast.Node
    /// The same kind of node, edited: matched across base↔head by kind and text similarity.
    | Changed of before: Ast.Node * after: Ast.Node
    /// Identical content that relocated (a reorder) — not a delete + add.
    | Moved of before: Ast.Node * after: Ast.Node

/// The ordered structural diff of a document's top-level nodes.
type DocumentDiff = DiffEntry list

/// Minimum token-overlap similarity for two same-kind nodes to read as an edit of each other (a
/// Changed) rather than an unrelated Removed + Added.
let private changeSimilarityThreshold = 0.5

/// Above this many (base × head) node pairs, the O(m·n) LCS array and the O(m·n) "every same-kind pair"
/// similarity scoring below would use unbounded memory and CPU on a pathologically large document (a
/// changelog/glossary with thousands of near-identical entries, or a many-thousand-row table) — enough
/// to hang the host or exhaust memory outright. `m * n` bounds BOTH costs at once (the LCS array is
/// exactly `(m+1)*(n+1)` cells; the similarity candidate list is at most `m*n` pairs), so a single guard
/// on it protects both. Chosen generously above any legitimate document (a comfortably-sized five-figure
/// document on each side still passes) while keeping the worst case a bounded, sub-second array/loop.
let internal maxNodePairs = 4_000_000L

/// The unbounded-cost diff falls back to here for a document pair too large to LCS/score safely: no
/// backbone matching at all — every base node is Removed, every head node is Added. Correct but
/// coarse (a huge document that hasn't actually changed would, past this size, still read as "all
/// removed and re-added" rather than "no changes") — an explicit, documented trade-off against hanging
/// or exhausting memory, not a silent behavior change: {@link diff}'s doc comment states it.
let private flatDiff (baseArr: Ast.Node[]) (headArr: Ast.Node[]) : DocumentDiff =
    [ yield! baseArr |> Array.map Removed
      yield! headArr |> Array.map Added ]

/// How a non-backbone base↔head pairing is classified — a typed tag (not a string) so the assembly
/// match is exhaustive and a mislabel can't slip through silently.
type private Pairing =
    | Move
    | Change

/// The discriminated-union case of a block — the granularity at which a `Changed` is matched (a
/// heading stays a heading, a paragraph a paragraph), so an edit is never paired across kinds.
let private kindTag (block: Ast.Block) : string =
    match block with
    | Ast.Heading _ -> "heading"
    | Ast.Paragraph _ -> "paragraph"
    | Ast.CodeBlock _ -> "code"
    | Ast.ListBlock _ -> "list"
    | Ast.Quote _ -> "quote"
    | Ast.Table _ -> "table"
    | Ast.ThematicBreak -> "thematicBreak"
    | Ast.DefinitionList _ -> "definitionList"
    | Ast.Footnotes _ -> "footnotes"

/// The visible text of a block, flattened — the basis for change-similarity scoring.
let rec blockText (block: Ast.Block) : string =
    match block with
    | Ast.Heading(_, xs)
    | Ast.Paragraph xs -> Inlines.flatten xs
    | Ast.CodeBlock(_, code) -> code
    | Ast.ListBlock(_, items) -> items |> List.collect id |> List.map blockText |> String.concat " "
    | Ast.Quote blocks -> blocks |> List.map blockText |> String.concat " "
    | Ast.Table(header, rows) -> header :: rows |> List.collect id |> List.map Inlines.flatten |> String.concat " "
    | Ast.ThematicBreak -> ""
    | Ast.DefinitionList items ->
        items
        |> List.collect (fun item -> (item.Terms |> List.map Inlines.flatten) @ (item.Body |> List.map blockText))
        |> String.concat " "
    | Ast.Footnotes notes ->
        notes |> List.collect (fun note -> note.Body |> List.map blockText) |> String.concat " "

/// Lowercase alphanumeric word tokens of a node's visible text.
let private tokens (node: Ast.Node) : Set<string> =
    let lowered = (blockText node.Content).ToLowerInvariant()
    let normalized = String(lowered |> Seq.map (fun c -> if Char.IsLetterOrDigit c then c else ' ') |> Seq.toArray)
    normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> Set.ofArray

/// Jaccard overlap of two nodes' precomputed token sets, in [0, 1]. Two nodes with NO word tokens
/// score 0, not 1: "no shared evidence" is not an edit — such blocks (e.g. all-punctuation paragraphs,
/// empty code blocks) fall through to Removed/Added unless their Content is actually equal, which the
/// LCS/Move phase already handles. The caller precomputes tokens once per node (the change loop scores
/// every same-kind unmatched pair, so recomputing per pair would re-flatten each node O(m)/O(n) times).
let private similarity (ta: Set<string>) (tb: Set<string>) : float =
    let union = Set.union ta tb |> Set.count
    if union = 0 then 0.0 else float (Set.intersect ta tb |> Set.count) / float union

/// The O(m·n) LCS + pairwise-similarity matching — everything {@link diff} does below the size guard.
/// Content equality drives the unchanged backbone (an LCS, so a shifted-but-identical node stays
/// Unchanged); the remainder is paired as Moved (identical content, reordered) then Changed (same kind,
/// similar text), and whatever is left is Added / Removed.
let private diffBounded (baseArr: Ast.Node[]) (headArr: Ast.Node[]) : DocumentDiff =
    let m = baseArr.Length
    let n = headArr.Length

    // LCS over CONTENT equality (line ranges ignored), the unchanged backbone.
    let lcs = Array2D.zeroCreate (m + 1) (n + 1)

    for i in m - 1 .. -1 .. 0 do
        for j in n - 1 .. -1 .. 0 do
            lcs.[i, j] <-
                if baseArr.[i].Content = headArr.[j].Content then
                    lcs.[i + 1, j + 1] + 1
                else
                    max lcs.[i + 1, j] lcs.[i, j + 1]

    // Backtrack to the matched (base, head) Same pairs, in increasing order.
    let samePairs = ResizeArray<int * int>()
    let mutable i = 0
    let mutable j = 0

    while i < m && j < n do
        if baseArr.[i].Content = headArr.[j].Content then
            samePairs.Add(i, j)
            i <- i + 1
            j <- j + 1
        elif lcs.[i + 1, j] >= lcs.[i, j + 1] then
            i <- i + 1
        else
            j <- j + 1

    let matchedBase = Array.zeroCreate<bool> m
    let matchedHead = Array.zeroCreate<bool> n

    for bi, hi in samePairs do
        matchedBase.[bi] <- true
        matchedHead.[hi] <- true

    // The non-backbone pairing: each remaining base/head index gets a partner (a head/base index plus
    // "move" or "change"), or stays None → Removed / Added.
    let basePartner: (int * Pairing) option[] = Array.create m None
    let headPartner: (int * Pairing) option[] = Array.create n None
    let headFree (hi: int) = not matchedHead.[hi] && (headPartner.[hi]).IsNone
    let baseFree (bi: int) = not matchedBase.[bi] && (basePartner.[bi]).IsNone

    // Moves: an unmatched base whose content equals an unmatched head (a reorder).
    for bi in 0 .. m - 1 do
        if baseFree bi then
            let target =
                [ 0 .. n - 1 ]
                |> List.tryFind (fun hi -> headFree hi && baseArr.[bi].Content = headArr.[hi].Content)

            match target with
            | Some hi ->
                basePartner.[bi] <- Some(hi, Move)
                headPartner.[hi] <- Some(bi, Move)
            | None -> ()

    // Changes: pair remaining unmatched base ↔ head of the same kind by GLOBAL descending text
    // similarity (above the threshold), accepting greedily and skipping a pair whose either side is
    // already taken — so the strongest edit wins regardless of node order, and a head is never claimed
    // by a weaker base when a closer one exists.
    // Precompute each still-unmatched node's tokens once: the loop below scores every same-kind base x
    // head pair, so calling `tokens` inside it would re-flatten each node O(m) / O(n) times (matched
    // nodes are never scored, so they get an unused empty placeholder).
    let baseTokens = Array.init m (fun bi -> if baseFree bi then tokens baseArr.[bi] else Set.empty)
    let headTokens = Array.init n (fun hi -> if headFree hi then tokens headArr.[hi] else Set.empty)

    let changeCandidates =
        [ for bi in 0 .. m - 1 do
              if baseFree bi then
                  for hi in 0 .. n - 1 do
                      if headFree hi && kindTag baseArr.[bi].Content = kindTag headArr.[hi].Content then
                          let s = similarity baseTokens.[bi] headTokens.[hi]

                          if s >= changeSimilarityThreshold then
                              yield s, bi, hi ]
        |> List.sortByDescending (fun (s, _, _) -> s)

    for _, bi, hi in changeCandidates do
        if baseFree bi && headFree hi then
            basePartner.[bi] <- Some(hi, Change)
            headPartner.[hi] <- Some(bi, Change)

    // Assemble in order, anchored on the Same pairs. Within each gap, base-only nodes (Removed) come
    // first, then head-positioned nodes (Added / Moved / Changed); a Moved/Changed base is emitted on
    // the head side, so its base slot stays silent.
    let result = ResizeArray<DiffEntry>()

    let emitBaseGap (lo: int) (hi: int) =
        for b in lo .. hi - 1 do
            if baseFree b then
                result.Add(Removed baseArr.[b])

    let emitHeadGap (lo: int) (hi: int) =
        for h in lo .. hi - 1 do
            match headPartner.[h] with
            | None -> result.Add(Added headArr.[h])
            | Some(b, Move) -> result.Add(Moved(baseArr.[b], headArr.[h]))
            | Some(b, Change) -> result.Add(Changed(baseArr.[b], headArr.[h]))

    let mutable prevB = -1
    let mutable prevH = -1

    for bi, hi in samePairs do
        emitBaseGap (prevB + 1) bi
        emitHeadGap (prevH + 1) hi
        result.Add(Unchanged headArr.[hi])
        prevB <- bi
        prevH <- hi

    emitBaseGap (prevB + 1) m
    emitHeadGap (prevH + 1) n
    List.ofSeq result

/// Diff two documents' top-level nodes. Above {@link maxNodePairs} base×head pairs, falls back to a flat
/// Removed+Added listing ({@link flatDiff}) instead of running {@link diffBounded}'s O(m·n) LCS/similarity
/// — a deliberate, documented trade-off for a pathologically large document (thousands of near-identical
/// entries, or a many-thousand-row table) rather than risking the host hanging or exhausting memory.
let diff (baseDoc: Ast.Document) (headDoc: Ast.Document) : DocumentDiff =
    let baseArr = List.toArray baseDoc
    let headArr = List.toArray headDoc

    if int64 baseArr.Length * int64 headArr.Length > maxNodePairs then
        flatDiff baseArr headArr
    else
        diffBounded baseArr headArr

/// Whether the diff contains any structural change (anything other than Unchanged).
let hasChanges (d: DocumentDiff) : bool =
    d
    |> List.exists (function
        | Unchanged _ -> false
        | _ -> true)

/// Diff two Markdown source strings end to end (parse each to the shared AST, then diff).
let diffText (baseText: string) (headText: string) : DocumentDiff =
    diff (Projection.toAst baseText) (Projection.toAst headText)
