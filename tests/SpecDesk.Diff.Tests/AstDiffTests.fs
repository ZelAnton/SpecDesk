module SpecDesk.Diff.Tests.AstDiffTests

open NUnit.Framework
open SpecDesk.Markdown
open SpecDesk.Diff.AstDiff

let private count predicate (d: DocumentDiff) =
    d |> List.filter predicate |> List.length

let private isUnchanged =
    function
    | Unchanged _ -> true
    | _ -> false

let private isAdded =
    function
    | Added _ -> true
    | _ -> false

let private isRemoved =
    function
    | Removed _ -> true
    | _ -> false

let private isChanged =
    function
    | Changed _ -> true
    | _ -> false

let private isMoved =
    function
    | Moved _ -> true
    | _ -> false

[<Test>]
let ``identical documents are all unchanged`` () =
    let d = diffText "# Title\n\nA paragraph.\n" "# Title\n\nA paragraph.\n"
    Assert.That(d |> List.forall isUnchanged, Is.True)
    Assert.That(hasChanges d, Is.False)
    Assert.That(d.Length, Is.EqualTo 2)

[<Test>]
let ``an appended block is one Added`` () =
    let d = diffText "# Title\n" "# Title\n\nNew paragraph.\n"
    Assert.That(count isUnchanged d, Is.EqualTo 1)
    Assert.That(count isAdded d, Is.EqualTo 1)
    Assert.That(count isRemoved d, Is.EqualTo 0)
    Assert.That(hasChanges d, Is.True)

[<Test>]
let ``a deleted block is one Removed`` () =
    let d = diffText "# Title\n\nGone soon.\n" "# Title\n"
    Assert.That(count isUnchanged d, Is.EqualTo 1)
    Assert.That(count isRemoved d, Is.EqualTo 1)
    Assert.That(count isAdded d, Is.EqualTo 0)

[<Test>]
let ``a heading level change is one Changed, not delete + add`` () =
    let d = diffText "## Overview\n\nBody text.\n" "### Overview\n\nBody text.\n"
    Assert.That(count isChanged d, Is.EqualTo 1)
    Assert.That(count isRemoved d, Is.EqualTo 0)
    Assert.That(count isAdded d, Is.EqualTo 0)
    Assert.That(count isUnchanged d, Is.EqualTo 1)

    // The Changed carries the before/after headings with their differing levels.
    match d |> List.find isChanged with
    | Changed(before, after) ->
        match before.Content, after.Content with
        | Ast.Heading(b, _), Ast.Heading(a, _) ->
            Assert.That(b, Is.EqualTo 2)
            Assert.That(a, Is.EqualTo 3)
        | _ -> Assert.Fail "Changed should pair two headings"
    | _ -> Assert.Fail "expected a Changed entry"

[<Test>]
let ``an edited paragraph with high overlap is Changed`` () =
    let d =
        diffText "# H\n\nThe quick brown fox jumps.\n" "# H\n\nThe quick brown fox leaps high.\n"

    Assert.That(count isChanged d, Is.EqualTo 1)
    Assert.That(count isRemoved d, Is.EqualTo 0)
    Assert.That(count isAdded d, Is.EqualTo 0)

[<Test>]
let ``a wholly rewritten paragraph is Removed + Added, not Changed`` () =
    let d =
        diffText "# H\n\nalpha beta gamma delta.\n" "# H\n\ntotally unrelated wording here.\n"

    Assert.That(count isChanged d, Is.EqualTo 0)
    Assert.That(count isRemoved d, Is.EqualTo 1)
    Assert.That(count isAdded d, Is.EqualTo 1)

[<Test>]
let ``a reordered block is Moved, not delete + add`` () =
    let d = diffText "# Alpha\n\n# Beta\n\n# Gamma\n" "# Beta\n\n# Gamma\n\n# Alpha\n"
    Assert.That(count isMoved d, Is.EqualTo 1)
    Assert.That(count isRemoved d, Is.EqualTo 0)
    Assert.That(count isAdded d, Is.EqualTo 0)
    Assert.That(count isUnchanged d, Is.EqualTo 2)

[<Test>]
let ``two text-free paragraphs that share no characters are Removed + Added, not Changed`` () =
    // Both paragraphs have no word tokens; "no shared evidence" must not read as an edit.
    let d = diffText "# H\n\n!!!\n" "# H\n\n???\n"
    Assert.That(count isChanged d, Is.EqualTo 0)
    Assert.That(count isRemoved d, Is.EqualTo 1)
    Assert.That(count isAdded d, Is.EqualTo 1)

[<Test>]
let ``change matching pairs the most similar block, not the first in order`` () =
    // The head paragraph overlaps the SECOND base paragraph (line 4) far more than the first (line 2).
    let d =
        diffText "# H\n\naa bb cc.\n\naa bb cc dd ee.\n" "# H\n\naa bb cc dd ee ff.\n"

    Assert.That(count isChanged d, Is.EqualTo 1)
    Assert.That(count isRemoved d, Is.EqualTo 1)
    Assert.That(count isAdded d, Is.EqualTo 0)

    // The Changed's BEFORE is the closer (second) base paragraph, not the first.
    match d |> List.find isChanged with
    | Changed(before, _) -> Assert.That(before.LineStart, Is.EqualTo 4)
    | _ -> Assert.Fail "expected a Changed entry"

[<Test>]
let ``diff from an empty base is all Added`` () =
    let d = diffText "" "# H\n\npara\n"
    Assert.That(d |> List.forall isAdded, Is.True)
    Assert.That(d.Length, Is.EqualTo 2)

[<Test>]
let ``diff to an empty head is all Removed`` () =
    let d = diffText "# H\n\npara\n" ""
    Assert.That(d |> List.forall isRemoved, Is.True)
    Assert.That(d.Length, Is.EqualTo 2)

// S-08 regressions: TaskList/FootnoteLink/DefinitionList used to project to `None`/identical content,
// so these real, rendered edits used to diff as Unchanged — the overlay reported "no changes".

[<Test>]
let ``toggling a task-list checkbox is Changed, not silently Unchanged`` () =
    let d = diffText "- [ ] todo\n" "- [x] todo\n"
    Assert.That(count isChanged d, Is.EqualTo 1)
    Assert.That(hasChanges d, Is.True)

[<Test>]
let ``editing a footnote body is Changed, not silently invisible`` () =
    let baseText = "See note[^1].\n\n[^1]: Original body text here.\n"
    let headText = "See note[^1].\n\n[^1]: Edited body text here.\n"
    let d = diffText baseText headText
    Assert.That(count isChanged d, Is.EqualTo 1)
    Assert.That(hasChanges d, Is.True)

[<Test>]
let ``editing a definition-list body is Changed, not silently invisible`` () =
    let baseText = "Term\n:   Original definition text here.\n"
    let headText = "Term\n:   Edited definition text here.\n"
    let d = diffText baseText headText
    Assert.That(count isChanged d, Is.EqualTo 1)
    Assert.That(hasChanges d, Is.True)

// M-07 regression guards: the O(m·n) LCS array and "every same-kind pair" similarity scoring are
// unbounded — a pathologically large, near-identical document (a changelog/glossary with thousands of
// entries) could hang the host or exhaust memory. Above `maxNodePairs` base×head node pairs, `diff` must
// skip that matching entirely and fall back to a flat Removed+Added listing instead.

let private paragraphDoc (count: int) : Ast.Document =
    [ for i in 0 .. count - 1 ->
          { Ast.Content = Ast.Paragraph [ Ast.Text(sprintf "Item %d" i) ]
            Ast.LineStart = i
            Ast.LineEnd = i } ]

/// Same shape as {@link paragraphDoc}, but with text that never exactly matches ANY entry of a plain
/// `paragraphDoc` of the same size — no node exactly-equals another, so the LCS backbone stays empty
/// and every base×head pair of this same "paragraph" kind reaches the expensive Jaccard-similarity
/// scoring loop. This is the actual dangerous shape (near-identical, not identical) the finding
/// describes — unlike two byte-identical documents, where every node matches the LCS on the first pass
/// and the costly similarity loop never runs at all.
let private paragraphDocNearIdentical (count: int) : Ast.Document =
    [ for i in 0 .. count - 1 ->
          { Ast.Content = Ast.Paragraph [ Ast.Text(sprintf "Item %d changed" i) ]
            Ast.LineStart = i
            Ast.LineEnd = i } ]

[<Test>]
let ``a document pair over the node-pair limit falls back to a flat Removed+Added listing`` () =
    // Comfortably over the limit (size² > maxNodePairs with margin), and IDENTICAL on both sides — if
    // the O(m·n) LCS ran, every node would match by content equality and the whole diff would read as
    // unchanged (hasChanges = false). The size guard must skip that matching unconditionally once the
    // pair count is this large, regardless of how similar the documents actually are.
    let size = int (sqrt (float maxNodePairs)) + 50
    let baseDoc = paragraphDoc size
    let headDoc = paragraphDoc size

    let d = diff baseDoc headDoc

    Assert.That(count isUnchanged d, Is.EqualTo 0)
    Assert.That(count isRemoved d, Is.EqualTo size)
    Assert.That(count isAdded d, Is.EqualTo size)
    Assert.That(hasChanges d, Is.True)

[<Test>]
let ``a large-but-under-the-limit identical document pair is still all Unchanged (no false fallback)`` () =
    // The guard must not fire just because a document is "large" in absolute terms — only once the
    // node-PAIR count actually crosses the limit. A comfortably-sized document (well under the limit
    // even squared) must still get the real LCS-based match.
    let size = 200
    Assert.That(int64 size * int64 size, Is.LessThan maxNodePairs)
    let baseDoc = paragraphDoc size
    let headDoc = paragraphDoc size

    let d = diff baseDoc headDoc

    Assert.That(d |> List.forall isUnchanged, Is.True)
    Assert.That(hasChanges d, Is.False)

[<Test>]
let ``diffing a near-identical pair over the node-pair limit completes quickly (bounded time)`` () =
    // Near-identical (not byte-identical) content: no node matches the LCS backbone, so every base×head
    // pair of the same kind would reach the expensive Jaccard-similarity scoring without the guard —
    // this is the actual worst case the finding describes, not the cheaper "everything matches on the
    // first LCS pass" shape of the fully-identical test above. Comfortably above the minimum size that
    // trips the guard (double it) so the unguarded O(m·n) cost is unambiguously, not just marginally,
    // over the bound below.
    let size = int (sqrt (float maxNodePairs)) * 2
    let baseDoc = paragraphDoc size
    let headDoc = paragraphDocNearIdentical size

    let sw = System.Diagnostics.Stopwatch.StartNew()
    diff baseDoc headDoc |> ignore
    sw.Stop()

    // Generous margin (the guarded fallback is O(m+n) — this should take well under a second in
    // practice); a regression that reintroduces the unbounded O(m·n) path at this size would take
    // vastly longer, not just a little longer.
    Assert.That(sw.Elapsed, Is.LessThan(System.TimeSpan.FromSeconds 5.0))
