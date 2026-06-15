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
