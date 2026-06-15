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
- Height-synced scroll: the editor now pads each source block with a spacer (or the preview with a
  margin) so a tall rendered block — an image, heading, or wrapped line — lines up vertically with
  its source. The panes track pixel-for-pixel instead of drifting between anchors, recomputing on
  re-render, image load, font load, and window resize.

[Unreleased]: https://github.com/ZelAnton/SpecDesk/commits/main
