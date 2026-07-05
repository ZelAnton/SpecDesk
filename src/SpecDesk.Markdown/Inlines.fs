/// Flattens inline content to its visible text — image alt text (Projection), and change-similarity
/// scoring plus table-cell text (AstDiff / DiffWire). Text and Code pass through verbatim,
/// Emphasis / Strong / Link recurse into their children, an Image yields its alt text, and a line
/// break renders as a single space. One shared definition so the Markdown and Diff projects cannot
/// drift on it (they must agree: the same string feeds alt-text projection and diff scoring).
module SpecDesk.Markdown.Inlines

let rec flatten (inlines: Ast.Inline list) : string =
    inlines
    |> List.map (fun i ->
        match i with
        | Ast.Text t
        | Ast.Code t -> t
        | Ast.Emphasis xs
        | Ast.Strong xs
        | Ast.Link(xs, _) -> flatten xs
        | Ast.Image(alt, _) -> alt
        | Ast.LineBreak -> " "
        // Include the checked state / label so toggling a checkbox or editing a footnote
        // reference actually changes the flattened text the diff scores similarity on.
        | Ast.TaskListMarker isChecked -> if isChecked then "[x]" else "[ ]"
        | Ast.FootnoteRef label -> "[^" + label + "]")
    |> String.concat ""
