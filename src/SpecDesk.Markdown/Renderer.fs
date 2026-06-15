/// Renders Markdown source to HTML while attaching `data-line-start`/`data-line-end` to each
/// top-level block and emitting a parallel `LineMap`. Both come from one parse so the preview,
/// scroll-sync, diff, and comments all agree on the source↔render mapping
/// (docs/design/05-live-preview.md).
module SpecDesk.Markdown.Renderer

open System.IO
open Markdig
open Markdig.Renderers
open Markdig.Renderers.Html

/// One rendered top-level block's 0-based, inclusive source line range. C#-friendly (consumed by
/// the host to build the `preview.html` payload).
type LineSpan = { LineStart: int; LineEnd: int }

/// Rendered HTML plus the ordered line map (parallel to the document's top-level blocks / the DOM).
type RenderResult = { Html: string; LineMap: LineSpan[] }

/// Parse once, stamp line attributes onto top-level blocks, render to HTML, and collect the map.
let render (text: string) : RenderResult =
    let doc = Markdown.Parse(text, Pipeline.shared)
    let lines = Lines.build text
    let spans = ResizeArray<LineSpan>()

    for block in doc do
        let startLine = block.Line
        let span = block.Span
        let endOffset = if span.End >= span.Start then span.End else span.Start
        let endLine = Lines.lineOfOffset lines endOffset
        let attrs = block.GetAttributes()
        attrs.AddProperty("data-line-start", string startLine)
        attrs.AddProperty("data-line-end", string endLine)
        spans.Add({ LineStart = startLine; LineEnd = endLine })

    use writer = new StringWriter()
    let htmlRenderer = HtmlRenderer(writer)
    Pipeline.shared.Setup(htmlRenderer)
    htmlRenderer.Render(doc) |> ignore
    writer.Flush()

    { Html = writer.ToString()
      LineMap = spans.ToArray() }
