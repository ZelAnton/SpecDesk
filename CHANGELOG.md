# Changelog

All notable changes to **SpecDesk** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Initial multi-language scaffold: a .NET solution (`SpecDesk.slnx`) with C# and F# projects
  under `src/`/`tests/`, plus a TypeScript `webview/` bundle (esbuild).
- Concept and architecture documentation under `docs/design/`, and a PoC-driven execution
  plan in `docs/ROADMAP.md`.
- Cross-platform CI (Linux/Windows/macOS) building and testing the .NET solution and the
  webview, with NuGet dependency auditing, CodeQL (C# + TypeScript), and Dependabot.
- PoC-0 — native↔webview IPC: a typed JSON envelope and router (`SpecDesk.Contracts`), a
  Photino host wiring the bridge, and a webview echo round-trip correlated by message `id`.
  The `SpecDesk.Host` build now auto-bundles the webview (esbuild); skip with
  `-p:SkipWebview=true`.
- PoC-1 — `app://` asset scheme: a Photino custom-scheme handler serves local files to the
  webview, with path-traversal protection (`AppAssetResolver`). Lets the preview load local
  images without `file://` CORS issues.
- PoC-2 — editor + live preview + `lineMap`: a CodeMirror 6 source editor with a natively
  rendered (Markdig) preview and bidirectional scroll-sync. `SpecDesk.Markdown` projects Markdig
  to a line-stamped F# AST and renders HTML carrying `data-line-*` attributes plus a parallel line
  map; the host (`HostController`/`PreviewCoordinator`) debounces, versions, and drops stale
  renders so a fast typist never sees an out-of-date preview. Plain filesystem open/save included.
- PoC-3 — image drop/paste rule engine: pasting or dragging an image into the editor saves it
  into the repo working tree, named by a `.spectool.toml [images]` rule, and inserts a
  document-relative `![](…)` link that the preview resolves via `app://`. `SpecDesk.Core` is the
  F# rule engine (format sniff + re-encode/downscale/metadata-strip via SkiaSharp, token
  expansion, slugified naming, `{hash8}` de-duplication, repository containment); the Markdown
  renderer rewrites relative image links to `app://`. Git staging is deferred to PoC-4.
- Height-synced scroll: the editor pads each source block with a spacer so a taller rendered block —
  an image, heading, table row, or wrapped line — lines up vertically with its source. The preview is
  the fixed reference; only the editor adapts, so toggling wrap never shifts the rendered side. The
  panes track pixel-for-pixel instead of drifting between anchors, recomputing on re-render, image
  load, font load, and window resize. The synthetic spacer rows are marked with a faint cross-diagonal
  hatch so they read as service padding, not document content.
- Sub-line-precise scroll-sync: the editor reports a fractional top line (how far the viewport has
  scrolled into a block, not just which line), and the preview interpolates within the matching
  rendered block. When scrolling stops the preview re-snaps exactly to the editor's top — the code
  pane is the reference — removing the small residual drift that remained after a momentum scroll.
  Echo suppression now uses a **direction lock** (the actively-scrolled pane stays authoritative for
  a short rolling window) instead of a single "ignore the next event" flag, eliminating the brief
  two-way fight that made the preview visibly judder mid-scroll. The follow itself is now a pure
  **pixel→pixel map**: height-sync publishes the aligned per-block anchors and scroll-sync
  interpolates between them, so a scroll position maps straight to the other pane's `scrollTop` with
  no `posAtCoords`, no per-frame layout reads, and a fractional (device-pixel-snapped) result —
  removing the residual stutter that the line-based remap left behind.
- Editor highlights and optional wrapping: the source line under the caret is highlighted in both
  panes (prominent), the line under the mouse is highlighted faintly (auxiliary, suppressed when it
  is the caret line), and a toolbar button toggles soft wrapping of long source lines.
- PoC-4 — local versioning + document lifecycle: a document opens **read-only**; clicking **Edit**
  prompts for a **draft name** (prefilled with a generated default, editable; sanitized to a valid
  ref), forks a working branch with it from the published base, makes the editor writable, and enters
  **Draft**. Typing autosaves to the working copy on disk (status shows **Unsaved changes**) but
  **never commits on its own**. Committing is the author's explicit **Save version**: the app proposes
  a plain-language note (the commit message), the author edits it, and only then is a commit made
  (document and any pasted images together) — status **Version saved**.
  **Discard** drops the draft and reverts to the published version. No git vocabulary surfaces beyond
  the editable note and draft name — commit SHAs and the word "branch" stay invisible. `SpecDesk.Core`
  gains a pure, unit-tested lifecycle state machine (`Lifecycle`) and the `[repo]`/`[branch]`/
  `[commit]` config reader (`WorkflowConfig`, sharing a small hand-rolled `Toml` reader);
  `SpecDesk.Git` wraps LibGit2Sharp behind `IDocumentVersioning` (init, branch, save-version, discard).
  Entirely local — push/PR/GitHub come in PoC-5. On first run a writable sample spec repo is seeded
  under `%LOCALAPPDATA%\SpecDesk\sample-repo` (git-initialized) so Edit/Save version work out of the
  box without touching SpecDesk's own working tree.
- Structured logging built into the host (`Microsoft.Extensions.Logging` over Serilog): an always-on
  rolling daily log file at `%LOCALAPPDATA%\SpecDesk\logs\specdesk-<date>.log` plus a console sink.
  Native call sites log routed messages, key parameters, and exceptions (including native file-dialog
  failures that were previously swallowed); the webview logs to the same file over a `log` IPC
  channel; and an "Export log…" toolbar button writes the current log to a chosen path.
- PoC-11 — editor view modes: a toolbar control switches the editor between **Code** (source only),
  **Split** (source + rendered preview, the default), and **Formatted** (rendered preview only,
  read-only for now). Switching preserves the reading position (carried across in the source-line
  coordinate so it survives the width reflow) and the active-line highlight; height-sync and
  scroll-sync run only in split and re-align automatically on return to it. Typing into the formatted
  view (WYSIWYG) comes in PoC-12.
- Light/dark theme: the UI follows the OS colour scheme on launch, with a toolbar toggle to switch.
- Single-file Windows release build via a `win-x64` publish profile:
  `dotnet publish src/SpecDesk.Host -p:PublishProfile=win-x64 -p:DebugType=none` produces one
  self-contained `SpecDesk.Host.exe` (no .NET install needed on the target; the target still needs
  the WebView2 runtime, pre-installed on Windows 11 and most Windows 10).
- PoC-12 — WYSIWYG editing: the **Formatted** view is an editable ProseMirror surface, and **Split**
  now pairs the source editor with that editable WYSIWYG — both panes are editable and synced live —
  in place of the old read-only preview pane. Edits serialize back to Markdown by **block-splice**:
  only the top-level blocks you actually changed are re-emitted; every untouched block (including its
  hard-wrapping and list markers) stays byte-identical, so an edit makes a tight, reviewable diff
  instead of reflowing the file. Markdig stays canonical for the diff/comments render (computed, no
  longer shown as a pane). GFM tables render as real tables (cell text editable; an untouched table is
  preserved verbatim), and the active line (caret) and the line under the mouse are highlighted in
  both panes and **synchronized** in Split — interacting in one pane highlights the matching line in
  the source editor and the matching block in the WYSIWYG (a table row or list item rather than the
  whole table/list when the caret is inside one). Images render in the WYSIWYG (relative links
  resolve to the `app://repo/…` scheme against the document's folder, the same rewrite the native
  preview applies), and the source editor is padded with spacers so each source block lines up with its
  rendered block in the formatted pane. (v1 limits: structural table edits — add/remove rows/columns —
  are done in the source view; height alignment and scroll-sync between the two panes are at top-level
  block granularity; adding or removing a whole top-level block falls back to a full re-serialize.)
- PoC-12 — formatting toolbar: a second toolbar row, shown while a draft is being edited, with
  **bold, italic, strikethrough, H1, H2, bullet list, numbered list, quote, and code block**. It
  applies to the pane you last worked in — ProseMirror commands in the Formatted (WYSIWYG) view,
  Markdown text transforms in Code/Split — and the buttons light up to show the formatting active at
  the caret in the WYSIWYG. (Link, Table, and Image buttons are deferred.)
- Clicking a link in the document now opens it instead of doing nothing: a web (`http(s)`) link in the
  formatted (WYSIWYG) view, the preview, or (with Ctrl/Cmd-click) the source editor opens in your
  default browser, and an email (`mailto:`) link in the formatted view or preview opens in your mail
  client. While editing the formatted view, use Ctrl/Cmd-click so a plain click can still place the
  caret; in read mode a plain click opens it. Only validated http/https/mailto links are opened —
  other schemes (e.g. `javascript:`) are ignored, and the webview can never navigate itself.

### Changed
- UI restyled to the agreed design concept (`docs/design/SpecDesk-Design-Concept.md`): a CSS
  design-token system now drives every surface (light, warm, and dark token sets); the rendered
  preview reads like a typeset document (serif headings, hairline tables, a soft accent caret-block
  highlight in place of the former yellow); the CodeMirror editor gains a token-based theme with
  markdown syntax colours; and the toolbar buttons, the Code/Split/Formatted segmented control, the
  inline prompt bars, and the status badge are rebuilt from the shared component styles.
- The host loads its web (`wwwroot`) and sample assets from the application base directory rather
  than the current working directory, so it runs correctly when launched from any folder (and as a
  single-file exe), not only from the project/output directory.
- Split view scroll-sync is now sub-block: scrolling either pane tracks the other smoothly *within* a
  tall block (a big table, long list, or wrapped paragraph) instead of snapping at block boundaries.
  The formatted pane interpolates the source editor's fractional top line across the matching block's
  height, and reports the line actually at its own viewport top rather than the block's first line.
- PoC-6 groundwork — semantic (AST-level) diff engine (`SpecDesk.Diff`): given two versions of a
  document it classifies each top-level block as unchanged / added / removed / changed (same kind of
  node, edited — e.g. a heading-level change) / moved (identical content, reordered), so a review can
  read as structure rather than line noise. Pure and input-agnostic (any base/head text pair).
- PoC-6 — "Show changes" review overlay: a toolbar toggle diffs the working copy against the document's
  last saved version (fully local, no GitHub) and washes the changed content in place — added (green),
  edited (amber), and moved (violet) blocks, with a marker standing in for removed blocks. Each hue is
  distinct from the caret block's own accent wash, a kind bar sits flush at the highlight's left edge,
  and the Formatted pane tags each change with a small "Added/Updated/Moved/Deleted by you" pill above
  its top-left (the local compare attributes to you; multi-author attribution follows with the review-
  against-others flow). The Code (source) pane highlights the changed lines and the Formatted (WYSIWYG)
  pane highlights the changed blocks; in Split both panes show the overlay at once, so the existing
  Code/Split/Formatted switch doubles as the raw↔rendered diff toggle. The native host computes the
  structural diff (`SpecDesk.Diff`) and the editors decorate the head document; a real edit clears the
  overlay (the snapshot is stale — click again to recompute), and a stale result is dropped by version.
- "Show changes" — finer granularity: a changed **list or table** highlights the individual rows / items
  that changed rather than washing the whole container, and a changed **paragraph / heading** highlights
  the specific changed words inline (added/changed words washed, deleted words shown struck-through) —
  unless too much of it changed, in which case it falls back to washing the whole block as "edited". The
  inline word highlighting works in **both** panes (the Code pane word-diffs the raw source, the Formatted
  pane the rendered text) and **inside a changed list item** too; a removed row/item's marker sits between
  its neighbours rather than at the container edge. The native diff descends into a changed container's
  children; the webview word-diffs where positions are natural in each pane.
- "Show changes" — empty result: when the working copy matches the last saved version (or there is no
  saved version yet), a calm "No changes since the last saved version" notice appears below the toolbar
  instead of silently washing nothing, so the author isn't left wondering why nothing is highlighted. It
  clears the moment there are changes to show or the overlay is turned off.
- Accessibility — skip-to-editor link: a keyboard user's first Tab on the page reveals a "Skip to editor"
  link that jumps focus past the toolbar straight into the editing surface visible in the current mode
  (the source editor in Code/Split, the formatted editor in Formatted). It is parked off-screen until
  focused, so it stays in the tab order without cluttering the layout.

### Fixed
- Split view: selecting a line in one pane now scrolls the other pane just enough to reveal the
  matching highlighted line when block-height drift had pushed it outside the visible area (the
  highlight was applied but off-screen). The pane you are working in is never scrolled, and a pane
  that already shows the line stays put. The reveal stands down while scroll-sync is already moving
  the other pane (e.g. holding an arrow key), so the two no longer fight and judder.
- Discarding a draft no longer deletes the published branch when the draft name collides with it;
  starting a draft on a detached HEAD is refused cleanly, and re-initializing a repository that
  already has commits no longer repoints HEAD at a non-existent branch.
- Pasting an image while a save / discard / autosave runs no longer races the git working tree (which
  could corrupt the staged asset), and an image whose insertion fails now surfaces a plain error
  instead of leaving the paste hanging.
- Autosave can no longer write one document's text into another document's file when documents are
  switched quickly.
- The rendered line map no longer over-counts on link reference definitions and footnotes (which
  produce no source-ordered element), keeping scroll-sync and diff/comment anchoring aligned.

### Security
- Image paste/drop writes are confined to the repository: a malicious `.spectool.toml` image rule can
  no longer use a crafted format extension, or a folder reached through a symlink/junction, to write a
  file outside the repo. The `app://` asset server likewise refuses to follow a symlink/junction out
  of the repo when serving a file.
- Dangerous-scheme links and autolinks (`javascript:`, `data:`, …) in untrusted document content are
  neutralized when the Markdown is rendered, so they cannot run in the webview. An oversized IPC frame
  from the webview is dropped before it is parsed (a denial-of-service guard), and an unexpected
  handler fault can no longer tear down the message pump.

[Unreleased]: https://github.com/ZelAnton/SpecDesk/commits/main
