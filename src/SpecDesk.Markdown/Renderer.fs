/// Renders Markdown source to HTML while attaching `data-line-start`/`data-line-end` to each
/// top-level block and emitting a parallel `LineMap`. Both come from one parse so the preview,
/// scroll-sync, diff, and comments all agree on the source↔render mapping
/// (docs/design/05-live-preview.md).
module SpecDesk.Markdown.Renderer

open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open Markdig
open Markdig.Extensions.DefinitionLists
open Markdig.Extensions.Footnotes
open Markdig.Extensions.Tables
open Markdig.Renderers
open Markdig.Renderers.Html
open Markdig.Syntax
open Markdig.Syntax.Inlines

/// One rendered top-level block's 0-based, inclusive source line range. C#-friendly (consumed by
/// the host to build the `preview.html` payload).
type LineSpan = { LineStart: int; LineEnd: int }

/// Rendered HTML plus the ordered line map (parallel to the document's top-level blocks / the DOM).
type RenderResult = { Html: string; LineMap: LineSpan[] }

let private schemeRegex =
    Regex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*:", RegexOptions.Compiled)

/// The lowercase scheme of a URL (without the trailing colon), or "" when it has none (relative/anchor).
let private schemeOf (url: string) : string =
    let m = schemeRegex.Match url

    if m.Success then
        url.Substring(0, m.Length - 1).ToLowerInvariant()
    else
        ""

/// A clickable link href is kept only for a navigable scheme (or a relative/anchor link). Untrusted
/// document content can otherwise carry `javascript:`/`data:` hrefs that — absent a CSP — would run in
/// the privileged webview if ever activated; neutralize them at this single canonical render point.
let private linkAllowed (url: string) : bool =
    match schemeOf url with
    | ""
    | "http"
    | "https"
    | "mailto"
    | "app" -> true
    | _ -> false

/// An image src may be web/app/relative/data (an <img> does not execute its source); reject only an
/// explicit script scheme.
let private imageAllowed (url: string) : bool =
    match schemeOf url with
    | "javascript"
    | "vbscript" -> false
    | _ -> true

/// Collapse `.`/`..` segments in a forward-slash relative path.
let private normalizeRelative (path: string) : string =
    let stack = List<string>()

    for part in path.Split('/') do
        match part with
        | ""
        | "." -> ()
        | ".." ->
            if stack.Count > 0 then
                stack.RemoveAt(stack.Count - 1)
        | segment -> stack.Add segment

    String.concat "/" stack

/// Rewrite a relative image URL to `app://repo/<path relative to repo root>` so the preview can
/// load it via the custom scheme. Absolute (scheme, root-anchored, or anchor) URLs are untouched.
let private rewriteImageUrl (docDir: string) (url: string) : string =
    if
        url.Length = 0
        || schemeRegex.IsMatch url
        || url.StartsWith "/"
        || url.StartsWith "#"
    then
        url
    else
        let combined = if docDir = "" then url else docDir.TrimEnd('/') + "/" + url
        "app://repo/" + normalizeRelative combined

/// Neutralize dangerous-scheme hrefs and rewrite relative image links — the single canonical sanitize
/// point — mutating the parsed document in place before it reaches the webview DOM.
let private sanitizeLinks (docDir: string) (doc: MarkdownDocument) : unit =
    for link in doc.Descendants<LinkInline>() do
        match link.Url with
        | null -> ()
        | url ->
            if link.IsImage then
                let rewritten = rewriteImageUrl docDir url
                link.Url <- (if imageAllowed rewritten then rewritten else "")
            else
                // Block a dangerous-scheme href before it reaches the webview DOM (defense in depth
                // alongside the click-handler guard and CSP).
                link.Url <- (if linkAllowed url then url else "#")

    // Angle-bracket autolinks (`<javascript:evil>`) parse to AutolinkInline, NOT LinkInline, so the
    // loop above misses them — neutralize a dangerous-scheme autolink href the same way. (AutolinkInline.Url
    // is non-nullable, unlike LinkInline.Url, so no null guard is needed.)
    for auto in doc.Descendants<AutolinkInline>() do
        if not (linkAllowed auto.Url) then
            auto.Url <- "#"

/// Stamp `data-line-*` attributes onto the finest rendered blocks (mutating the document) and return
/// the parallel line map. Both anchor the source↔render mapping the preview/scroll-sync/diff share.
let private stampLineAnchors (text: string) (doc: MarkdownDocument) : LineSpan[] =
    let lines = Lines.build text
    let spans = ResizeArray<LineSpan>()

    // Stamp a scroll-sync anchor (data-line attributes + a line-map entry) onto a syntax node.
    let tag (node: MarkdownObject) (startLine: int) (span: SourceSpan) =
        let endOffset = if span.End >= span.Start then span.End else span.Start
        let endLine = Lines.lineOfOffset lines endOffset
        let attrs = node.GetAttributes()
        attrs.AddProperty("data-line-start", string startLine)
        attrs.AddProperty("data-line-end", string endLine)

        spans.Add(
            { LineStart = startLine
              LineEnd = endLine }
        )

    // Anchor at the finest rendered granularity so each source line aligns with its rendered
    // counterpart — not just the top of a multi-line block. Container blocks are recursed into and
    // never tagged themselves (tagging both a container and its child would place a misaligned
    // spacer); only the leaf rendered elements (<p>, <h*>, <li>, <tr>, <hr>, <pre>) carry anchors.
    let rec tagTree (block: Block) =
        match block with
        // No source-aligned rendered element, so no anchor / LineMap entry — tagging them desyncs the
        // map from the DOM: link reference definitions are consumed into the parser's reference map
        // (they render nothing), and the footnote group is relocated to the document end at render time.
        | :? LinkReferenceDefinitionGroup -> ()
        | :? LinkReferenceDefinition -> ()
        | :? FootnoteGroup -> ()
        | :? Table as table ->
            for rowObject in table do
                match rowObject with
                | :? TableRow as row -> tag row row.Line row.Span
                | _ -> ()
        | :? ListBlock as list ->
            for itemObject in list do
                match itemObject with
                | :? ListItemBlock as item -> tag item item.Line item.Span
                | _ -> ()
        | :? QuoteBlock as quote ->
            for child in quote do
                tagTree child
        | :? DefinitionItem as item ->
            // Markdig's own HTML renderer only ever writes `data-line-*` for the `<dt>` (the
            // DefinitionTerm) — verified empirically: it calls WriteAttributes for the term but renders
            // a definition's body content directly as `<dd>` without ever doing so for it, term-sharing
            // continuation items (a second `:` line under the same term) included. Tagging the body here
            // would add a LineMap entry with no matching attribute in the HTML, desyncing the
            // LineMap↔data-line count invariant every other block family upholds.
            for child in item do
                match child with
                | :? DefinitionTerm -> tag child child.Line child.Span
                | _ -> ()
        | :? LeafBlock -> tag block block.Line block.Span
        | :? ContainerBlock as container ->
            for child in container do
                tagTree child
        | _ -> ()

    for block in doc do
        tagTree block

    spans.ToArray()

/// Render the prepared document to HTML via the shared pipeline.
let private renderHtml (doc: MarkdownDocument) : string =
    use writer = new StringWriter()
    let htmlRenderer = HtmlRenderer(writer)
    Pipeline.shared.Setup(htmlRenderer)
    htmlRenderer.Render(doc) |> ignore
    writer.Flush()
    writer.ToString()

/// Parse once, then run the three render concerns over that one document — sanitize links, stamp the
/// line anchors, emit HTML — so the preview, scroll-sync, diff and comments all agree on the
/// source↔render mapping. <paramref name="docDir"/> is the document's directory relative to the repo
/// root (forward slashes, "" at the root).
let render (docDir: string) (text: string) : RenderResult =
    let doc = Markdown.Parse(text, Pipeline.shared)
    sanitizeLinks docDir doc
    let lineMap = stampLineAnchors text doc

    { Html = renderHtml doc
      LineMap = lineMap }
