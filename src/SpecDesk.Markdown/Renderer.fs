/// Renders Markdown source to HTML while attaching `data-line-start`/`data-line-end` to each
/// top-level block and emitting a parallel `LineMap`. Both come from one parse so the preview,
/// scroll-sync, diff, and comments all agree on the source↔render mapping
/// (docs/design/05-live-preview.md).
module SpecDesk.Markdown.Renderer

open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open Markdig
open Markdig.Renderers
open Markdig.Renderers.Html
open Markdig.Syntax
open Markdig.Syntax.Inlines

/// One rendered top-level block's 0-based, inclusive source line range. C#-friendly (consumed by
/// the host to build the `preview.html` payload).
type LineSpan = { LineStart: int; LineEnd: int }

/// Rendered HTML plus the ordered line map (parallel to the document's top-level blocks / the DOM).
type RenderResult = { Html: string; LineMap: LineSpan[] }

let private schemeRegex = Regex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*:", RegexOptions.Compiled)

/// Collapse `.`/`..` segments in a forward-slash relative path.
let private normalizeRelative (path: string) : string =
    let stack = List<string>()

    for part in path.Split('/') do
        match part with
        | ""
        | "." -> ()
        | ".." -> if stack.Count > 0 then stack.RemoveAt(stack.Count - 1)
        | segment -> stack.Add segment

    String.concat "/" stack

/// Rewrite a relative image URL to `app://repo/<path relative to repo root>` so the preview can
/// load it via the custom scheme. Absolute (scheme, root-anchored, or anchor) URLs are untouched.
let private rewriteImageUrl (docDir: string) (url: string) : string =
    if url.Length = 0 || schemeRegex.IsMatch url || url.StartsWith "/" || url.StartsWith "#" then
        url
    else
        let combined = if docDir = "" then url else docDir.TrimEnd('/') + "/" + url
        "app://repo/" + normalizeRelative combined

/// Parse once, rewrite relative image links, stamp line attributes onto top-level blocks, render to
/// HTML, and collect the map. <paramref name="docDir"/> is the document's directory relative to the
/// repo root (forward slashes, "" at the root).
let render (docDir: string) (text: string) : RenderResult =
    let doc = Markdown.Parse(text, Pipeline.shared)

    for link in doc.Descendants<LinkInline>() do
        if link.IsImage then
            match link.Url with
            | null -> ()
            | url -> link.Url <- rewriteImageUrl docDir url

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
