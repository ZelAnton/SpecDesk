# 05 — Live Preview & Markdown Pipeline

The editor offers three view modes — **source** (Markdown in CodeMirror 6), **split**
(source + rendered side by side), and **formatted** (a WYSIWYG view the author types into
directly). Markdown text on disk is always the single source of truth: an edit made in the
formatted view is serialized straight back to Markdown (see "Editor view modes & WYSIWYG editing"
below). The **read/preview** rendering path is native (Markdig) and one-way — it is the canonical
render used by diff, comment anchoring, and image-link rewriting; the webview injects that HTML and
reports scroll/selection. The **formatted-editing** path adds a two-way editor surface on top.

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
- **The WYSIWYG editor is the one principled exception.** A formatted-editing surface must parse
  and serialize Markdown *in the webview* (that is intrinsic to a contenteditable/ProseMirror
  editor — see below). We contain the risk by keeping Markdig **canonical**: the WYSIWYG editor's
  job is only to turn formatted edits into Markdown *text*, which then flows back through the same
  native pipeline as everything else. Diff, comments, and the read-only render are always computed
  from Markdig, never from the editor's internal model — so there is still exactly one source of
  truth for review.

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

## Editor view modes & WYSIWYG editing

The editor exposes three modes the author switches between freely (the model proven by HedgeDoc's
splitter and vditor's mode switch — see [AGENTS.md](../../AGENTS.md) "Reference implementations"):

| Mode | Left | Right | Edits where |
|------|------|-------|-------------|
| **Source** | Markdown (CodeMirror) | — | the source text |
| **Split** | Markdown (CodeMirror) | rendered preview | the source text; preview follows |
| **Formatted** | — | WYSIWYG document | **the rendered document directly** |

In every mode the file on disk is **Markdown**, and it is the single source of truth. The novel
part is the **Formatted** mode: the author types into the rendered document, and each edit is
serialized straight back to the Markdown source. Conceptually:

```
Formatted edit ─► editor document model ─► serialize ─► Markdown text (source of truth)
                                                          │
                                                          ▼  (same path as a source edit)
                                              Markdig parse ─► canonical render / lineMap / diff
```

### The hard requirement: minimal, lossless round-trip

A formatted edit must produce the **smallest local change** to the Markdown that expresses it — it
must **not** reformat unrelated parts of the document. If editing one paragraph rewrote bullet
markers, re-wrapped lines, or reordered attributes elsewhere, every such edit would explode the git
diff and destroy the review experience that is the product's whole point ([01-concept.md](01-concept.md)
design principles; [07-review-experience.md](07-review-experience.md)). This round-trip fidelity is
the **central technical risk** of the WYSIWYG mode and must be proven on real specs before the
pipeline is committed (a spike — see [ROADMAP.md](../ROADMAP.md)).

### Engine

Formatted editing needs a real editor component (a `contenteditable` surface with a structured
document model and a Markdown serializer) — the existing one-way "inject HTML" preview cannot be
typed into. Candidate families, studied as references in [AGENTS.md](../../AGENTS.md):

- **ProseMirror dual-mode** (e.g. `@gravity-ui/markdown-editor`, or Tiptap as in zenmark): a
  ProseMirror document for the formatted view + CodeMirror for source, with markdown-it→PM parse and
  PM→Markdown serialize, and a **decoration** system for anchored overlays. *Leading candidate* — it
  matches our dual-representation and overlay needs most directly.
- **Self-contained engines** — `muya` (MarkText) or Lute (vditor): more independent, but a much
  larger surface to own and (vditor) an opaque wasm engine.

Decision deferred to a spike; recorded under "Decisions to lock" in the roadmap. Whatever is chosen
runs in the webview (it is the editor surface); native Markdig stays canonical for review.

### Overlays in both representations (the core requirement)

Diff highlighting and inline comments must appear in **both** the source and formatted views, driven
by the same review data ([07-review-experience.md](07-review-experience.md)). The anchor that bridges
them is the source line range (the `lineMap`):

- **Source view:** anchor to CodeMirror line/range decorations.
- **Formatted view:** anchor to editor-document positions. Each rendered/editor block keeps its
  source-line provenance (the `data-line-start`/`data-line-end` idea, as HedgeDoc's line markers do,
  or a ProseMirror position↔source-line map), so a comment or a changed-node highlight lands on the
  right place whichever mode is showing.

Switching modes must preserve the active comments/diff overlay and the caret/scroll position.

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
