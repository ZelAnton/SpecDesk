# SpecDesk

Author and review GitHub-hosted Markdown specs from the desktop — automated git, rendered
diffs, inline comments, and AI assist. SpecDesk lets non-technical authors edit Markdown specs
stored in GitHub without ever touching git, branches, or pull requests directly.

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

## GitHub access

SpecDesk uses GitHub's device authorization flow and stores the resulting token with Windows DPAPI. Set
`SPECDESK_GITHUB_CLIENT_ID` to the public client id of the GitHub OAuth App configured for SpecDesk before
starting a development build. No client secret is used or stored. When a disconnected user adds or opens a
repository, SpecDesk opens GitHub's standard authorization page in the system browser and resumes the action
after authorization.

Registered repositories are persisted with the default branch reported by GitHub. The Repositories panel
groups any number of managed local copies beneath each repository and shows only non-default branches under
each copy. **Copy locally** creates another copy in SpecDesk's managed repositories folder.

Selecting the repository itself browses its files directly from GitHub, so a local copy is optional. Online
files open as read-only previews; select **Copy locally** before editing. Local repository trees show all files
(large and binary files are listed but rejected with a plain preview message).
