module SpecDesk.Diff.Tests.AstDiffTests

open NUnit.Framework
open SpecDesk.Markdown
open SpecDesk.Diff.AstDiff

let private count predicate (d: DocumentDiff) = d |> List.filter predicate |> List.length

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
    let d = diffText "# H\n\nThe quick brown fox jumps.\n" "# H\n\nThe quick brown fox leaps high.\n"
    Assert.That(count isChanged d, Is.EqualTo 1)
    Assert.That(count isRemoved d, Is.EqualTo 0)
    Assert.That(count isAdded d, Is.EqualTo 0)

[<Test>]
let ``a wholly rewritten paragraph is Removed + Added, not Changed`` () =
    let d = diffText "# H\n\nalpha beta gamma delta.\n" "# H\n\ntotally unrelated wording here.\n"
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
    let d = diffText "# H\n\naa bb cc.\n\naa bb cc dd ee.\n" "# H\n\naa bb cc dd ee ff.\n"
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
