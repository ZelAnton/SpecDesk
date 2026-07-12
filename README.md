# SpecDesk

Author and review GitHub-hosted Markdown specs from the desktop — automated git, rendered
diffs, inline comments, and AI assist. SpecDesk lets non-technical authors edit Markdown specs
stored in GitHub without ever touching git, branches, or pull requests directly.

The assistant's right-panel composer supports multi-line prompts: press Enter for a new line and
Ctrl+Enter (Cmd+Enter on macOS) to send.
Use **Attach** beside the composer to include the open file, current folder, or a registered repository
as context for the next message; attachments can be removed before sending.
The right panel also exposes the selected document's saved versions, comments, and change history.

> **Working title.** `SpecDesk` is a placeholder name; rename before any registry/namespace work.

## Documentation

- **[docs/ROADMAP.md](docs/ROADMAP.md)** — the PoC-driven execution plan we work by.
- **[docs/design/](docs/design/README.md)** — concept, architecture, and feature design docs.

## Repository layout

Multi-language monorepo: a .NET solution (C# + F#) plus a TypeScript webview bundle.

```
SpecDesk.slnx              # .NET solution — all C#/F# projects
src/
  SpecDesk.Contracts/      # C#  — IPC message DTOs
  SpecDesk.Core/           # F#  — domain, lifecycle state machine, image rules
  SpecDesk.Markdown/       # F#  — Markdig wrapper, AST DU, HTML render
  SpecDesk.Diff/           # F#  — semantic (AST) diff
  SpecDesk.Git/            # C#  — LibGit2Sharp wrapper
  SpecDesk.GitHub/         # C#  — GitHub OAuth device-flow auth (BCL HttpClient)
  SpecDesk.Ai/             # C#  — Microsoft Agent Framework (PoC-8)
  SpecDesk.Host/           # C#  — Photino bootstrap, IPC router (the exe)
tests/
  SpecDesk.Core.Tests/     # F#
  SpecDesk.Markdown.Tests/ # F#
  SpecDesk.Diff.Tests/     # F#
webview/                   # TS  — CodeMirror editor, preview, IPC client (esbuild)
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for build/test/lint commands and contributor conventions.

## Quick start

```sh
dotnet build SpecDesk.slnx          # build the .NET side
dotnet test SpecDesk.slnx           # run F# tests
cd webview && npm install && npm run build   # build the webview bundle
```

Requires .NET SDK 10 and Node 24.
