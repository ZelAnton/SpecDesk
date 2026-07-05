module SpecDesk.Markdown.Tests.ProjectionTests

open NUnit.Framework
open SpecDesk.Markdown
open SpecDesk.Markdown.Ast

let private single (md: string) : Block =
    match Projection.toAst md with
    | [ node ] -> node.Content
    | other -> failwithf "expected a single top-level node, got %A" other

let private paragraphInlines (md: string) : Inline list =
    match single md with
    | Paragraph inlines -> inlines
    | other -> failwithf "expected a paragraph, got %A" other

[<Test>]
let ``heading projects level, text and a single-line range`` () =
    Assert.That(
        Projection.toAst "# Title" = [ { Content = Heading(1, [ Text "Title" ]); LineStart = 0; LineEnd = 0 } ],
        Is.True)

[<Test>]
let ``emphasis is projected`` () =
    Assert.That(paragraphInlines "a *em*" |> List.contains (Emphasis [ Text "em" ]), Is.True)

[<Test>]
let ``strong is projected`` () =
    Assert.That(paragraphInlines "**bold**" |> List.contains (Strong [ Text "bold" ]), Is.True)

// M-01: `~~struck~~` is ALSO an EmphasisInline under the hood (Markdig's EmphasisExtras reuses the
// node type, distinguished only by its `~` delimiter) — without checking DelimiterChar first, this
// would misproject as Strong (same DelimiterCount as `**bold**`), silently conflating strikethrough
// with bold in the semantic diff.
[<Test>]
let ``strikethrough is projected as its own case, not conflated with strong`` () =
    Assert.That(paragraphInlines "~~struck~~" |> List.contains (Strikethrough [ Text "struck" ]), Is.True)

[<Test>]
let ``inline code is projected`` () =
    Assert.That(paragraphInlines "`code`" |> List.contains (Code "code"), Is.True)

[<Test>]
let ``link projects text and url`` () =
    Assert.That(
        paragraphInlines "[label](http://x)" |> List.contains (Link([ Text "label" ], "http://x")),
        Is.True)

[<Test>]
let ``image projects alt and url`` () =
    Assert.That(paragraphInlines "![alt](img.png)" |> List.contains (Image("alt", "img.png")), Is.True)

[<Test>]
let ``a soft line break is projected`` () =
    Assert.That(paragraphInlines "line one\nline two" |> List.contains LineBreak, Is.True)

[<Test>]
let ``fenced code block projects language and code`` () =
    match single "```fsharp\nlet x = 1\n```" with
    | CodeBlock(lang, code) ->
        Assert.That(lang, Is.EqualTo(Some "fsharp"))
        Assert.That(code, Does.Contain "let x = 1")
    | other -> Assert.Fail($"expected a code block, got %A{other}")

[<Test>]
let ``unordered list projects items as blocks`` () =
    Assert.That(
        single "- a\n- b" = ListBlock(false, [ [ Paragraph [ Text "a" ] ]; [ Paragraph [ Text "b" ] ] ]),
        Is.True)

[<Test>]
let ``ordered list sets the ordered flag`` () =
    match single "1. a\n2. b" with
    | ListBlock(ordered, items) ->
        Assert.That(ordered, Is.True)
        Assert.That(List.length items, Is.EqualTo 2)
    | other -> Assert.Fail($"expected a list, got %A{other}")

[<Test>]
let ``blockquote projects its body blocks`` () =
    Assert.That(single "> quoted" = Quote [ Paragraph [ Text "quoted" ] ], Is.True)

[<Test>]
let ``thematic break is projected`` () =
    Assert.That(single "---" = ThematicBreak, Is.True)

[<Test>]
let ``pipe table projects header and rows`` () =
    let md = "| A | B |\n| - | - |\n| 1 | 2 |"

    match single md with
    | Table(header, rows) ->
        Assert.That((header = [ [ Text "A" ]; [ Text "B" ] ]), Is.True)
        Assert.That((rows = [ [ [ Text "1" ]; [ Text "2" ] ] ]), Is.True)
    | other -> Assert.Fail($"expected a table, got %A{other}")

[<Test>]
let ``a multi-line paragraph spans its source line range`` () =
    match Projection.toAst "line one\nline two" with
    | [ node ] ->
        Assert.That(node.LineStart, Is.EqualTo 0)
        Assert.That(node.LineEnd, Is.EqualTo 1)
    | other -> Assert.Fail($"expected one node, got %A{other}")

// The branches below are the silent-drop risks in inlineOf/blockOf: each ends in `| _ -> None`, so a
// regression that stops matching one quietly removes it from the AST the diff and comment anchoring
// consume. Autolinks and HTML entities are the ones most likely to actually occur in a spec.

[<Test>]
let ``an angle-bracket autolink projects a link to its url`` () =
    Assert.That(
        paragraphInlines "<https://example.com>"
        |> List.contains (Link([ Text "https://example.com" ], "https://example.com")),
        Is.True)

[<Test>]
let ``an html entity projects its decoded text`` () =
    Assert.That(paragraphInlines "&amp;" |> List.contains (Text "&"), Is.True)

[<Test>]
let ``a fenced code block keeps only the first info word as the language`` () =
    match single "```fsharp ignore\nlet x = 1\n```" with
    | CodeBlock(lang, _) -> Assert.That(lang, Is.EqualTo(Some "fsharp"))
    | other -> Assert.Fail($"expected a code block, got %A{other}")

[<Test>]
let ``an indented code block projects with no language`` () =
    match single "    let x = 1\n" with
    | CodeBlock(lang, code) ->
        Assert.That(lang, Is.EqualTo None)
        Assert.That(code, Does.Contain "let x = 1")
    | other -> Assert.Fail($"expected a code block, got %A{other}")

[<Test>]
let ``a list nested inside a list item projects a nested ListBlock`` () =
    match single "- a\n  - b\n" with
    | ListBlock(false, [ item ]) ->
        Assert.That(
            item
            |> List.exists (function
                | ListBlock _ -> true
                | _ -> false),
            Is.True)
    | other -> Assert.Fail($"expected a single unordered list, got %A{other}")

// S-08 regression guards: TaskList/FootnoteLink/DefinitionList used to fall into `| _ -> None` in
// inlineOf/blockOf, so toggling a checkbox or editing a footnote/definition body projected to
// IDENTICAL Ast content — invisible to the diff even though the render visibly changed.

// A task-list item's checkbox marker is the first inline of its (single, nested) paragraph —
// `single` / `paragraphInlines` assume a top-level Paragraph, so task lists need their own accessor.
let private taskItemInlines (md: string) : Inline list =
    match single md with
    | ListBlock(false, [ [ Paragraph inlines ] ]) -> inlines
    | other -> failwithf "expected a single-item unordered list with a paragraph, got %A" other

[<Test>]
let ``an unchecked task-list item projects its checkbox marker`` () =
    Assert.That(taskItemInlines "- [ ] todo" |> List.contains (TaskListMarker false), Is.True)

[<Test>]
let ``a checked task-list item projects its checkbox marker`` () =
    Assert.That(taskItemInlines "- [x] todo" |> List.contains (TaskListMarker true), Is.True)

[<Test>]
let ``toggling a task-list checkbox changes the projected list content`` () =
    Assert.That(single "- [ ] todo" <> single "- [x] todo", Is.True)

// Markdig's footnote label retains the leading `^` from `[^1]`.
[<Test>]
let ``a footnote reference projects its label`` () =
    let md = "See note[^1].\n\n[^1]: The note body."
    Assert.That(
        Projection.toAst md
        |> List.exists (fun n -> n.Content = Paragraph [ Text "See note"; FootnoteRef "^1"; Text "." ]),
        Is.True)

let private footnoteGroup (md: string) : Footnote list option =
    Projection.toAst md
    |> List.tryPick (fun n ->
        match n.Content with
        | Footnotes notes -> Some notes
        | _ -> None)

let private hasTextContaining (needle: string) (blocks: Block list) : bool =
    blocks
    |> List.exists (function
        | Paragraph xs -> (Inlines.flatten xs).Contains needle
        | _ -> false)

[<Test>]
let ``the footnote group projects the referenced note bodies`` () =
    let md = "See note[^1].\n\n[^1]: The note body."

    match footnoteGroup md with
    | Some [ note ] ->
        Assert.That(note.Label, Is.EqualTo "^1")
        Assert.That(hasTextContaining "The note body" note.Body, Is.True)
    | other -> Assert.Fail($"expected one footnote, got %A{other}")

[<Test>]
let ``editing a footnote body changes the projected footnote group`` () =
    let original = Projection.toAst "See note[^1].\n\n[^1]: Original body."
    let edited = Projection.toAst "See note[^1].\n\n[^1]: Edited body."
    Assert.That(original <> edited, Is.True)

// The definition marker's body must be indented at least 4 columns from the start of the line
// (":" + 3 spaces here) to be recognized as a DefinitionList rather than folding into a plain
// paragraph — a CommonMark-style continuation-indent quirk of Markdig's definition-list extension.
[<Test>]
let ``a definition list projects its term and definition body`` () =
    let md = "Term\n:   Definition text"

    match single md with
    | DefinitionList [ item ] ->
        Assert.That(item.Terms = [ [ Text "Term" ] ], Is.True)
        Assert.That(hasTextContaining "Definition text" item.Body, Is.True)
    | other -> Assert.Fail($"expected a definition list, got %A{other}")

[<Test>]
let ``editing a definition body changes the projected definition list`` () =
    let original = Projection.toAst "Term\n:   Original definition"
    let edited = Projection.toAst "Term\n:   Edited definition"
    Assert.That(original <> edited, Is.True)
