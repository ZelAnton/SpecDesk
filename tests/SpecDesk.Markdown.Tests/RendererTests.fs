module SpecDesk.Markdown.Tests.RendererTests

open NUnit.Framework
open SpecDesk.Markdown

let private occurrences (sub: string) (s: string) : int = s.Split(sub).Length - 1

[<Test>]
let ``line map has one entry per rendered anchor (list items counted individually)`` () =
    let result = Renderer.render "" "# H\n\npara\n\n- a\n- b"
    // heading + paragraph + two list items = 4 anchors (lists are anchored per item)
    Assert.That(result.LineMap.Length, Is.EqualTo 4)

[<Test>]
let ``a multi-line block carries its full source range`` () =
    let result = Renderer.render "" "para line one\npara line two\n\n# Heading"
    Assert.That(result.LineMap.[0].LineStart, Is.EqualTo 0)
    Assert.That(result.LineMap.[0].LineEnd, Is.EqualTo 1)
    Assert.That(result.LineMap.[1].LineStart, Is.EqualTo 3)

[<Test>]
let ``rendered html carries data-line attributes`` () =
    let result = Renderer.render "" "# H"
    Assert.That(result.Html, Does.Contain "data-line-start=\"0\"")
    Assert.That(result.Html, Does.Contain "data-line-end=\"0\"")

// The attribute-emission gate: every top-level block type must emit a data-line-start, so the
// webview can index it for scroll-sync. If a renderer ever stops emitting attached attributes,
// this count diverges from the line map length and the test fails.
[<Test>]
let ``every top-level block emits a line attribute`` () =
    let md =
        "# Heading\n\nA paragraph.\n\n- item one\n- item two\n\n> a quote\n\n```\ncode\n```\n\n| A | B |\n| - | - |\n| 1 | 2 |\n\n---\n"

    let result = Renderer.render "" md
    Assert.That(occurrences "data-line-start=\"" result.Html, Is.EqualTo result.LineMap.Length)

[<Test>]
let ``raw html is escaped, not emitted as live markup`` () =
    let result = Renderer.render "" "<script>alert(1)</script>"
    Assert.That(result.Html, Does.Not.Contain "<script>")
    Assert.That(result.Html, Does.Contain "&lt;script&gt;")

[<Test>]
let ``relative image link is rewritten to the app scheme`` () =
    let result = Renderer.render "" "![alt](images/welcome/pic.png)"
    Assert.That(result.Html, Does.Contain "src=\"app://repo/images/welcome/pic.png\"")

[<Test>]
let ``relative image link is resolved against the document directory`` () =
    let result = Renderer.render "docs/specs" "![alt](img/pic.png)"
    Assert.That(result.Html, Does.Contain "src=\"app://repo/docs/specs/img/pic.png\"")

[<Test>]
let ``absolute image links are left untouched`` () =
    let result = Renderer.render "" "![a](https://example.com/x.png) ![b](app://repo/y.png)"
    Assert.That(result.Html, Does.Contain "src=\"https://example.com/x.png\"")
    Assert.That(result.Html, Does.Contain "src=\"app://repo/y.png\"")

[<Test>]
let ``table rows are anchored individually, not the table block`` () =
    // Header row (line 0) + data row (line 2) → two anchors, one per <tr>, none on <table>.
    let result = Renderer.render "" "| A | B |\n| - | - |\n| 1 | 2 |"
    Assert.That(result.LineMap.Length, Is.EqualTo 2)
    Assert.That(result.Html, Does.Contain "<tr data-line-start=\"0\"")
    Assert.That(result.Html, Does.Contain "<tr data-line-start=\"2\"")
    Assert.That(result.Html, Does.Not.Contain "<table data-line-start")

[<Test>]
let ``list items are anchored individually, not the list block`` () =
    let result = Renderer.render "" "- one\n- two\n- three"
    Assert.That(result.LineMap.Length, Is.EqualTo 3)
    Assert.That(result.Html, Does.Contain "<li data-line-start=\"0\"")
    Assert.That(result.Html, Does.Contain "<li data-line-start=\"2\"")
    Assert.That(result.Html, Does.Not.Contain "<ul data-line-start")

[<Test>]
let ``blockquote paragraphs are anchored, not the quote block`` () =
    let result = Renderer.render "" "> quoted line"
    Assert.That(result.Html, Does.Contain "<p data-line-start=\"0\"")
    Assert.That(result.Html, Does.Not.Contain "<blockquote data-line-start")

[<Test>]
let ``a link reference definition adds no line-map entry and does not desync the map`` () =
    // The [s]: ... line renders no element; only the paragraph that uses it does. The map length must
    // stay equal to the rendered anchor count (the bug this guards over-counted by one per ref-def).
    let result = Renderer.render "" "Use [the spec][s].\n\n[s]: https://example.com\n"
    Assert.That(result.LineMap.Length, Is.EqualTo 1)
    Assert.That(occurrences "data-line-start=\"" result.Html, Is.EqualTo result.LineMap.Length)

[<Test>]
let ``a javascript link href is neutralized`` () =
    let result = Renderer.render "" "[click](javascript:evil)"
    Assert.That(result.Html, Does.Not.Contain "javascript:")
    Assert.That(result.Html, Does.Contain "href=\"#\"")

[<Test>]
let ``a data link href is neutralized`` () =
    let result = Renderer.render "" "[x](data:text/html;base64,abc)"
    Assert.That(result.Html, Does.Not.Contain "data:")

[<Test>]
let ``safe link hrefs are kept`` () =
    let result =
        Renderer.render "" "[a](https://example.com) [b](mailto:x@y.com) [c](#anchor) [d](other.md)"

    Assert.That(result.Html, Does.Contain "href=\"https://example.com\"")
    Assert.That(result.Html, Does.Contain "href=\"mailto:x@y.com\"")
    Assert.That(result.Html, Does.Contain "href=\"#anchor\"")
    Assert.That(result.Html, Does.Contain "href=\"other.md\"")

[<Test>]
let ``a javascript image src is neutralized`` () =
    let result = Renderer.render "" "![x](javascript:evil)"
    Assert.That(result.Html, Does.Not.Contain "javascript:")

[<Test>]
let ``a javascript angle-bracket autolink is neutralized`` () =
    // `<javascript:evil>` parses to an AutolinkInline, a different path from a normal link.
    let result = Renderer.render "" "<javascript:evil>"
    Assert.That(result.Html, Does.Not.Contain "javascript:")

[<Test>]
let ``a footnote definition does not desync the line map`` () =
    let result = Renderer.render "" "Text with a note.[^1]\n\n[^1]: the note body\n"
    Assert.That(occurrences "data-line-start=\"" result.Html, Is.EqualTo result.LineMap.Length)
