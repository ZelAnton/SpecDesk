# 05 — Live Preview & Markdown Pipeline

The editor shows Markdown **source** (CodeMirror 6) with a **rendered preview** beside it.
All Markdown logic is native (Markdig); the webview only injects HTML and reports
scroll/selection.

## Pipeline

```
editor.changed {text, version}   (debounced ~120 ms in webview)
		│
		▼  native
	Markdig.Parse(text)  with UsePreciseSourceLocation
		│
		├──► render HTML   (Markdig HtmlRenderer, image links rewritten to app://)
		└──► build lineMap (node → source line range, from precise source spans)
		│
		▼
	preview.html {html, lineMap, version}   ──► webview injects + enables scroll-sync
```

A newer `version` supersedes older ones: a preview result whose `version` is stale (text has
since changed) is dropped in the webview. Parsing is cancellable so a fast typist does not
queue stale work.

## Why parse natively instead of in the webview

- One parser is the single source of truth for **preview, the semantic diff, comment
  anchoring, and image-link rewriting**. Splitting parsing between JS and native would mean
  two slightly different Markdown interpretations — a bug factory.
- Keeps TypeScript minimal (the project goal).
- Markdig exposes precise source spans, which the whole line-mapping story depends on.

## The F# AST model

Markdig's object model is projected once into a clean F# discriminated union that carries
source spans on every node. This DU is what the diff and comment-anchoring code consume.

```fsharp
type Inline =
	| Text		of string
	| Emphasis	of Inline list
	| Strong	of Inline list
	| Code		of string
	| Link		of text: Inline list * url: string
	| Image		of alt: string * url: string
	| LineBreak

type Block =
	| Heading	of level: int * Inline list
	| Paragraph	of Inline list
	| CodeBlock	of lang: string option * code: string
	| ListBlock	of ordered: bool * items: Block list list
	| Quote		of Block list
	| Table		of header: Inline list list * rows: Inline list list list
	| ThematicBreak

/// Every node carries the source line range it came from.
/// This is the backbone of scroll-sync, comment anchoring, and diff line-mapping.
type Node =
	{
		Content:	Block
		LineStart:	int
		LineEnd:	int
	}

type Document = Node list
```

## Line mapping (`lineMap`)

`lineMap` is the bidirectional bridge between **rendered DOM nodes** and **source lines**.
Each top-level rendered block gets a `data-line-start` / `data-line-end` attribute derived
from the node's source span. The webview keeps an index from these attributes.

Used by:
- **Scroll-sync** — when the source scrolls, find the source line at the top of the viewport,
  look up the rendered node spanning it, scroll the preview to it (and vice-versa).
- **Comment anchoring** — a comment on rendered text resolves to a source line range, which
  is what GitHub review comments need (see [07-review-experience.md](07-review-experience.md)).
- **Diff highlighting** — changed AST nodes map back to source lines and rendered nodes.

Get this right in Phase 1; Phases 5 and 6 both depend on it.

## Scroll-sync algorithm (sketch)

1. webview reports `scroll.sync {side, sourceLine}` (throttled) as either pane scrolls.
2. For source→preview: native (or a small webview index) finds the rendered node whose
   `[LineStart, LineEnd]` contains `sourceLine`, computes a fractional offset within the
   block, and the webview scrolls the preview so that node aligns at the same fraction.
3. Preview→source is the inverse, using the same `lineMap`.
4. A "scroll lock" flag prevents feedback loops (a programmatic scroll must not re-trigger a
   sync the other way).

## Rendering details

- **Image links** `![](path)` are rewritten to `app://repo/<resolved-path>` at render time so
  the preview can load local files (see [02-architecture.md](02-architecture.md)).
- **Markdig extensions** to enable should mirror what the spec repos actually use (tables,
  task lists, footnotes, definition lists, etc.). Keep the enabled set in `.spectool.toml` so
  rendering matches the repo's GitHub rendering as closely as possible.
- **Sanitization:** rendered HTML is injected into a webview the app controls; still, treat
  document content as untrusted and sanitize (no arbitrary script execution from `.md`).

## Performance

- Debounce at ~120 ms; parse on the thread pool; cancel superseded parses.
- For very large specs, consider incremental re-parse of only the changed region later; not
  needed for v1 (Markdig is fast enough for typical spec sizes).
