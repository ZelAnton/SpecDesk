# Contributing to SpecDesk

Thanks for your interest in improving **SpecDesk**.

## Prerequisites

- .NET 10 SDK (the exact band is pinned in [`global.json`](global.json)).
- Node.js 24 (pinned in [`webview/.nvmrc`](webview/.nvmrc)) for the TypeScript webview.

## Build and test (.NET)

```sh
dotnet build SpecDesk.slnx
dotnet test  SpecDesk.slnx
```

The build treats **warnings as errors** and enforces code style on build, so a
clean local build is required before opening a pull request. Run a single test
with:

```sh
dotnet test SpecDesk.slnx --filter "FullyQualifiedName~TestMethodName"
```

## Build and test (webview / TypeScript)

The browser-side bundle lives in [`webview/`](webview). All commands run from
that directory:

```sh
cd webview
npm ci               # install exactly the committed package-lock.json
npm run typecheck    # tsc --noEmit (strict; warnings-as-errors gate for types)
npm run lint         # biome check .
npm run format       # biome format --write .
npm test             # vitest run
npm run build        # tsc -p tsconfig.build.json (type-checked emit)
npm run bundle       # esbuild → ../src/SpecDesk.Host/wwwroot/webview.js
```

## Conventions

- **Formatting** is governed by [`.editorconfig`](.editorconfig) — tabs for
  indentation, LF line endings, file-scoped namespaces (C#). The webview uses
  Biome (2-space). Do not reformat code you are not changing.
- **Dependencies** use Central Package Management — declare .NET versions only in
  [`Directory.Packages.props`](Directory.Packages.props); `PackageReference`
  items carry no `Version`.
- **Cross-project references** use `<ProjectReference>` with build order coming
  from the project graph and `BuildDependency` entries in the `.slnx`.
- **F# compile order is significant** — list `.fs` files in dependency order in
  each `.fsproj`.
- See [`docs/`](docs/README.md) for architecture and design; the
  [roadmap](docs/ROADMAP.md) tracks current milestones.

## Changelog

Every user-visible change ships its [`CHANGELOG.md`](CHANGELOG.md) entry in the
same change set, under `## [Unreleased]`. Write the bullet for a consumer of the
software, not the implementer. Pure internal refactors are exempt.

## Pull requests

- Keep changes focused; unrelated cleanups belong in their own PR.
- Ensure CI (build/test on Linux, Windows, macOS, plus the webview job) and
  CodeQL pass.
- Fill in the pull-request checklist.

## Releasing

Releases are cut by bumping the version and pushing a `vX.Y.Z` tag; the
[`release`](.github/workflows/release.yml) workflow then builds and tests the
tagged commit, publishes the self-contained Windows exe, and creates the GitHub
Release with notes and the exe attached. The full step-by-step procedure —
renaming `## [Unreleased]` under the version number, bumping `<Version>`, and
tagging — is in [`docs/release-process.md`](docs/release-process.md).
