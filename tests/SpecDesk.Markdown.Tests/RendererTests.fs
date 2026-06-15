module SpecDesk.Markdown.Tests.RendererTests

open NUnit.Framework
open SpecDesk.Markdown

let private occurrences (sub: string) (s: string) : int = s.Split(sub).Length - 1

[<Test>]
let ``line map has one entry per top-level block`` () =
    let result = Renderer.render "# H\n\npara\n\n- a\n- b"
    // heading, paragraph, list = 3 top-level blocks
    Assert.That(result.LineMap.Length, Is.EqualTo 3)

[<Test>]
let ``a multi-line block carries its full source range`` () =
    let result = Renderer.render "para line one\npara line two\n\n# Heading"
    Assert.That(result.LineMap.[0].LineStart, Is.EqualTo 0)
    Assert.That(result.LineMap.[0].LineEnd, Is.EqualTo 1)
    Assert.That(result.LineMap.[1].LineStart, Is.EqualTo 3)

[<Test>]
let ``rendered html carries data-line attributes`` () =
    let result = Renderer.render "# H"
    Assert.That(result.Html, Does.Contain "data-line-start=\"0\"")
    Assert.That(result.Html, Does.Contain "data-line-end=\"0\"")

// The attribute-emission gate: every top-level block type must emit a data-line-start, so the
// webview can index it for scroll-sync. If a renderer ever stops emitting attached attributes,
// this count diverges from the line map length and the test fails.
[<Test>]
let ``every top-level block emits a line attribute`` () =
    let md =
        "# Heading\n\nA paragraph.\n\n- item one\n- item two\n\n> a quote\n\n```\ncode\n```\n\n| A | B |\n| - | - |\n| 1 | 2 |\n\n---\n"

    let result = Renderer.render md
    Assert.That(occurrences "data-line-start=\"" result.Html, Is.EqualTo result.LineMap.Length)

[<Test>]
let ``raw html is escaped, not emitted as live markup`` () =
    let result = Renderer.render "<script>alert(1)</script>"
    Assert.That(result.Html, Does.Not.Contain "<script>")
    Assert.That(result.Html, Does.Contain "&lt;script&gt;")
