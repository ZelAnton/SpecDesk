/// Projects Markdig's mutable object model into the clean immutable `Ast` discriminated union,
/// stamping every top-level node with its 0-based source line range. This is what the diff
/// (PoC-6) and comment anchoring (PoC-7) consume — they never touch Markdig directly.
module SpecDesk.Markdown.Projection

open Markdig
open Markdig.Syntax
open Markdig.Syntax.Inlines
open Markdig.Extensions.Tables

// `Block`/`Inline` below refer to Markdig's types (from the opens); the F# DU is reached via the
// `Ast.` prefix throughout to keep the two models unambiguous.

let rec private inlineOf (inl: Inline) : Ast.Inline option =
    match inl with
    | :? LiteralInline as lit -> Some(Ast.Text(lit.Content.ToString()))
    | :? EmphasisInline as em ->
        let children = inlinesOf em
        if em.DelimiterCount >= 2 then Some(Ast.Strong children) else Some(Ast.Emphasis children)
    | :? CodeInline as code -> Some(Ast.Code code.Content)
    | :? LinkInline as link ->
        let url = defaultArg (Option.ofObj link.Url) ""
        if link.IsImage then
            Some(Ast.Image(plainText (inlinesOf link), url))
        else
            Some(Ast.Link(inlinesOf link, url))
    | :? AutolinkInline as auto -> Some(Ast.Link([ Ast.Text auto.Url ], auto.Url))
    | :? HtmlEntityInline as ent -> Some(Ast.Text(ent.Transcoded.ToString()))
    | :? LineBreakInline -> Some Ast.LineBreak
    | _ -> None

and private inlinesOf (container: ContainerInline | null) : Ast.Inline list =
    match container with
    | null -> []
    | c ->
        [ for child in c do
              match inlineOf child with
              | Some i -> yield i
              | None -> () ]

/// Flatten inline content to its visible text — used for image alt text, which the DU stores
/// as a plain string.
and private plainText (inlines: Ast.Inline list) : string =
    inlines
    |> List.map (fun i ->
        match i with
        | Ast.Text t -> t
        | Ast.Code c -> c
        | Ast.Emphasis xs
        | Ast.Strong xs -> plainText xs
        | Ast.Link(xs, _) -> plainText xs
        | Ast.Image(alt, _) -> alt
        | Ast.LineBreak -> " ")
    |> String.concat ""

let rec private blockOf (block: Block) : Ast.Block option =
    match block with
    | :? HeadingBlock as h -> Some(Ast.Heading(h.Level, inlinesOf h.Inline))
    | :? ParagraphBlock as p -> Some(Ast.Paragraph(inlinesOf p.Inline))
    | :? Table as t -> Some(tableOf t)
    // FencedCodeBlock derives from CodeBlock, so it must be matched first.
    | :? FencedCodeBlock as fc ->
        let lang =
            match fc.Info with
            | null
            | "" -> None
            | info -> Some(info.Split(' ').[0])

        Some(Ast.CodeBlock(lang, codeText fc))
    | :? CodeBlock as c -> Some(Ast.CodeBlock(None, codeText c))
    | :? ListBlock as l ->
        let items =
            [ for item in l do
                  match item with
                  | :? ListItemBlock as li -> yield blocksOf li
                  | _ -> () ]

        Some(Ast.ListBlock(l.IsOrdered, items))
    | :? QuoteBlock as q -> Some(Ast.Quote(blocksOf q))
    | :? ThematicBreakBlock -> Some Ast.ThematicBreak
    | _ -> None

and private blocksOf (container: ContainerBlock) : Ast.Block list =
    [ for child in container do
          match blockOf child with
          | Some b -> yield b
          | None -> () ]

and private codeText (code: CodeBlock) : string = code.Lines.ToString()

and private tableOf (table: Table) : Ast.Block =
    let rows =
        [ for r in table do
              match r with
              | :? TableRow as tr -> yield tr
              | _ -> () ]

    let cellInlines (cell: TableCell) : Ast.Inline list =
        [ for b in cell do
              match b with
              | :? ParagraphBlock as p -> yield! inlinesOf p.Inline
              | _ -> () ]

    let rowInlines (row: TableRow) : Ast.Inline list list =
        [ for c in row do
              match c with
              | :? TableCell as cell -> yield cellInlines cell
              | _ -> () ]

    let header =
        match rows |> List.tryFind (fun r -> r.IsHeader) with
        | Some hr -> rowInlines hr
        | None -> []

    let bodyRows = rows |> List.filter (fun r -> not r.IsHeader) |> List.map rowInlines
    Ast.Table(header, bodyRows)

/// The 0-based, inclusive end line of a block, from its source span.
let private endLine (lines: Lines.Index) (block: Block) : int =
    let span = block.Span
    let endOffset = if span.End >= span.Start then span.End else span.Start
    Lines.lineOfOffset lines endOffset

/// Parse Markdown source and project it to the line-stamped F# AST.
let toAst (text: string) : Ast.Document =
    let doc = Markdown.Parse(text, Pipeline.shared)
    let lines = Lines.build text

    [ for block in doc do
          match blockOf block with
          | Some content ->
              yield
                  { Ast.Content = content
                    Ast.LineStart = block.Line
                    Ast.LineEnd = endLine lines block }
          | None -> () ]
