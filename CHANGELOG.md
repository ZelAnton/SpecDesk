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
  F# rule engine (format sniff + re-encode/downscale/metadata-strip via ImageSharp, token
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
- Editor highlights and optional wrapping: the source line under the caret is highlighted in both
  panes (prominent), the line under the mouse is highlighted faintly (auxiliary, suppressed when it
  is the caret line), and a toolbar button toggles soft wrapping of long source lines.
- Structured logging built into the host (`Microsoft.Extensions.Logging` over Serilog): an always-on
  rolling daily log file at `%LOCALAPPDATA%\SpecDesk\logs\specdesk-<date>.log` plus a console sink.
  Native call sites log routed messages, key parameters, and exceptions (including native file-dialog
  failures that were previously swallowed); the webview logs to the same file over a `log` IPC
  channel; and an "Export log…" toolbar button writes the current log to a chosen path.

[Unreleased]: https://github.com/ZelAnton/SpecDesk/commits/main
