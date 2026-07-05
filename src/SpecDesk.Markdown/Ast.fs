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
    /// A task-list checkbox marker (`- [ ]` / `- [x]`), the first inline of the list item's
    /// paragraph. Carrying its checked state means toggling it changes the paragraph's (and so
    /// the enclosing list item's) projected content — otherwise the diff cannot tell a checkbox
    /// toggle from a no-op edit.
    | TaskListMarker of isChecked: bool
    /// An in-text footnote reference (`[^label]`), including the auto-generated backlink inline
    /// Markdig appends to a footnote's own body. Carries the label so it contributes visible text.
    | FootnoteRef of label: string

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
    /// A definition list (`Term` / `: definition`), one entry per term(s) + definition body.
    | DefinitionList of items: DefinitionItem list
    /// The document's collected footnote bodies (rendered together at the document end), one
    /// entry per referenced footnote.
    | Footnotes of notes: Footnote list

/// One definition-list entry: the term(s) it defines (usually one, but Markdown allows several
/// consecutive terms sharing a definition) and the definition body's blocks.
and DefinitionItem =
    { Terms: Inline list list
      Body: Block list }

/// One footnote's body, keyed by its reference label (`[^label]`).
and Footnote =
    { Label: string
      Body: Block list }

/// A top-level block plus its 0-based, inclusive source line range.
type Node =
    { Content: Block
      LineStart: int
      LineEnd: int }

/// A parsed document: its ordered top-level nodes.
type Document = Node list
