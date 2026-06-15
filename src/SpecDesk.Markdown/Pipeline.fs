/// The single shared Markdig pipeline. One parser configuration is the source of truth for the
/// preview, the semantic diff, comment anchoring, and (later) image-link rewriting — splitting
/// parsing between native and webview would be a bug factory (docs/design/05-live-preview.md).
module SpecDesk.Markdown.Pipeline

open Markdig

/// Built once and reused. `UsePreciseSourceLocation` is what makes the line map possible;
/// `DisableHtml` is PoC-2's sanitization stance — raw HTML/`<script>` in a spec is escaped to
/// text, never executed in the preview webview.
// TODO(PoC-3+): drive the enabled extension set (and a real HTML sanitizer) from .spectool.toml
// so rendering matches each repo's GitHub rendering.
let shared: MarkdownPipeline =
    MarkdownPipelineBuilder()
        .UsePreciseSourceLocation()
        .DisableHtml()
        .UsePipeTables()
        .UseTaskLists()
        .UseFootnotes()
        .UseDefinitionLists()
        .UseAutoLinks()
        .Build()
