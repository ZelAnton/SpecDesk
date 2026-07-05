module SpecDesk.Markdown.Tests.InlinesTests

open NUnit.Framework
open SpecDesk.Markdown
open SpecDesk.Markdown.Ast

// Inlines.flatten is the one shared inline-text flattener: it feeds image alt-text projection
// (Projection) AND change-similarity scoring + table-cell text (AstDiff / DiffWire), so a regression
// skews diff matching and alt text together. It is only exercised incidentally through those callers;
// pin each branch (verbatim text/code, the recursive marks, image alt, the line-break space) directly.

[<Test>]
let ``text and code pass through verbatim and concatenate`` () =
    Assert.That(Inlines.flatten [ Text "hello"; Code " world" ], Is.EqualTo "hello world")

[<Test>]
let ``emphasis and strong flatten to their children`` () =
    Assert.That(Inlines.flatten [ Emphasis [ Text "em" ]; Strong [ Text "bold" ] ], Is.EqualTo "embold")

// M-01: strikethrough must flatten mark-free (like Emphasis/Strong), not carry its `~~` markers —
// otherwise the native side's flattened text ("~~struck~~") would mismatch the webview's ProseMirror
// textContent ("struck") for byte-identical Markdown, reporting a phantom word-diff edit on every
// strikethrough word even when nothing actually changed.
[<Test>]
let ``strikethrough flattens to its children, without its markers`` () =
    Assert.That(Inlines.flatten [ Strikethrough [ Text "struck" ] ], Is.EqualTo "struck")

[<Test>]
let ``a link flattens to its text, not its url`` () =
    Assert.That(Inlines.flatten [ Link([ Text "label" ], "http://example.com") ], Is.EqualTo "label")

[<Test>]
let ``an image yields its alt text, not its url`` () =
    Assert.That(Inlines.flatten [ Image("a diagram", "img.png") ], Is.EqualTo "a diagram")

[<Test>]
let ``a line break becomes a single space`` () =
    Assert.That(Inlines.flatten [ Text "x"; LineBreak; Text "y" ], Is.EqualTo "x y")

[<Test>]
let ``nested marks recurse to the leaf text`` () =
    Assert.That(Inlines.flatten [ Strong [ Link([ Text "a" ], "u"); Text "b" ] ], Is.EqualTo "ab")

[<Test>]
let ``an empty inline list flattens to the empty string`` () =
    Assert.That(Inlines.flatten [], Is.EqualTo "")
