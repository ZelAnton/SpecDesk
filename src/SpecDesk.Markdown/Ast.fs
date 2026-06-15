/// The clean F# projection of Markdig's object model. Every top-level node carries the source
/// line range it came from — the backbone of scroll-sync (PoC-2), the semantic diff (PoC-6), and
/// comment anchoring (PoC-7). See docs/design/05-live-preview.md.
module SpecDesk.Markdown.Ast

/// Inline-level content. Lists model the children of a span (e.g. the text inside emphasis).
type Inline =
    | Text of string
    | Emphasis of Inline list
    | Strong of Inline list
    | Code of string
    | Link of text: Inline list * url: string
    | Image of alt: string * url: string
    | LineBreak

/// Block-level content. Nested blocks (list items, quote bodies) carry no line range of their
/// own; only top-level blocks do, via the enclosing Node.
type Block =
    | Heading of level: int * Inline list
    | Paragraph of Inline list
    | CodeBlock of lang: string option * code: string
    | ListBlock of ordered: bool * items: Block list list
    | Quote of Block list
    | Table of header: Inline list list * rows: Inline list list list
    | ThematicBreak

/// A top-level block plus its 0-based, inclusive source line range.
type Node =
    { Content: Block
      LineStart: int
      LineEnd: int }

/// A parsed document: its ordered top-level nodes.
type Document = Node list
