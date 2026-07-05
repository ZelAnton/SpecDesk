# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository. It is a short primer — [AGENTS.md](AGENTS.md) is the authoritative, detailed reference.

## What SpecDesk is

A manager-friendly Windows desktop editor (Photino + WebView2) for Markdown specs stored in GitHub. It is an application, not a publishable library — there is no NuGet packaging or registry publishing.

## Multi-language layout

- **.NET solution** `SpecDesk.slnx` — C# and F# projects under `src/`, tests under `tests/`. Inter-project deps use `<ProjectReference>`. **F# compile order is significant** and every F# project references `FSharp.Core` explicitly (required under Central Package Management). The exe is `SpecDesk.Host` (references all other `src/` projects).
- **`webview/`** — the TypeScript UI rendered in WebView2, bundled with esbuild into `src/SpecDesk.Host/wwwroot/webview.js`.

## Commands

```bash
# .NET — warnings are errors
dotnet build SpecDesk.slnx
dotnet test  SpecDesk.slnx
dotnet test  SpecDesk.slnx --filter "FullyQualifiedName~TestMethodName"

# webview (from webview/)
cd webview && npm install
npm run typecheck && npm run lint && npm test
npm run bundle   # esbuild → ../src/SpecDesk.Host/wwwroot/webview.js
```

## Conventions (see AGENTS.md for the full set)

- Central Package Management: .NET versions live only in `Directory.Packages.props`; `PackageReference` items carry no `Version`.
- Cross-project references use `<ProjectReference>`; build order from the project graph + `<BuildDependency>` in the `.slnx`.
- Exception handling: no one-line `try`/`catch`/`finally`; empty `catch` blocks must carry a rationale comment. See [AGENTS.md](AGENTS.md) → "Exception handling style".
- Changelog: every user-visible change ships a `CHANGELOG.md` entry under `## [Unreleased]` in the same change set.

## Design

The UI follows the agreed **design concept** in [`docs/design/`](docs/design/) —
[`docs/design/SpecDesk-Design-Concept.md`](docs/design/SpecDesk-Design-Concept.md) is authoritative
(text wins over mockups). Build UI from its design tokens, the one shared rendered-document stylesheet, the
CodeMirror editor theme, and its component specs; theme via `data-theme` on `<html>` (light + dark).
You may add new elements/panels/controls a feature needs, but keep them **within the concept's
intent** (reuse its tokens and component language; no one-off styles). Never leak git vocabulary —
authors see only the plain-language lifecycle words. Full guidance in [AGENTS.md](AGENTS.md) →
"Design system (UI source of truth)".

## Version control

The repo uses [jujutsu (`jj`)](https://jj-vcs.github.io/jj/) (colocated with git). Use `jj` commands; describe early, fold follow-ups into the current change, push only on the user's explicit trigger via a feature bookmark + PR into `main`. Full workflow in [AGENTS.md](AGENTS.md) → "Version control (jujutsu)".
