# AGENTS.md

## Project

- This repository contains **SpecDesk**, a manager-friendly Windows desktop editor for Markdown specs stored in GitHub.
- It is a desktop application (Photino + WebView2), **not** a publishable library — there is no NuGet packaging and no registry publishing.
- Source lives under `src/`; tests under `tests/`; the browser-side UI under `webview/`.

## Multi-language layout

SpecDesk combines three toolchains in one repository:

- **.NET solution** (`SpecDesk.slnx`) — a mix of C# and F# projects:
  - C#: `SpecDesk.Contracts` (shared types, no deps), `SpecDesk.Git` (LibGit2Sharp), `SpecDesk.GitHub` (Octokit), `SpecDesk.Ai` (agent host, placeholder for now), and `SpecDesk.Host` (the `Exe`, Photino bootstrap, references every other `src/` project).
  - F#: `SpecDesk.Core` (domain), `SpecDesk.Markdown` (Markdig), `SpecDesk.Diff` (diffing).
  - Inter-project dependencies use normal `<ProjectReference>` items; build order comes from the MSBuild project graph and the `<BuildDependency>` entries in `SpecDesk.slnx`.
  - **F# compile order is significant** — every `.fsproj` lists its `.fs` files explicitly, top-to-bottom, in dependency order. There is no globbing.
  - Every F# project (library AND test) must reference `FSharp.Core` explicitly: under Central Package Management the F# SDK's implicit `FSharp.Core` is dropped, and without it the runtime cannot load `FSharp.Core` and NUnit discovers zero tests.
- **`webview/` TypeScript bundle** — the UI rendered inside WebView2. Built with `esbuild` (`npm run bundle`) into `src/SpecDesk.Host/wwwroot/webview.js`, which `SpecDesk.Host` serves to the embedded browser. Type-checking, linting (Biome), and tests (Vitest) run from `webview/`.

## Runtime

- Use .NET (target framework `net10.0` for all projects, including the Host).
- Do not change the target framework unless explicitly asked.
- Use the repository-wide language settings from `Directory.Build.props`.

## Dependencies

- Do not introduce new NuGet or npm packages without explicit approval.
- .NET uses centralized package management: manage versions only in `Directory.Packages.props`; do not put `Version` on individual `PackageReference` items.
- `Directory.Packages.props` is not a fixed allow-list — add the production and test packages the project actually needs. `Microsoft.NET.Test.Sdk` is required for test discovery and execution through `dotnet test`; do not remove it.

## Architecture

- Keep functionality in reusable internal libraries; keep implementation details internal.
- Prefer simple, direct code over new abstractions.
- Do not add dependency injection unless there is a concrete need.

## Project references

- Inter-project dependencies use `<ProjectReference Include="..\..\src\X\X.csproj" />` (relative path) — **not** `<Reference>` + `AssemblySearchPaths`.
- Build ordering is the project graph plus `<BuildDependency>` entries in `SpecDesk.slnx`.

## Build ordering

- Use the `.slnx` solution format.
- Referencing projects must depend on referenced projects; referenced projects build first.
- Build ordering must be explicit and deterministic.

## Repository structure

- Use `SpecDesk.slnx` as the solution file.
- Use `Directory.Build.props` for repository-wide MSBuild configuration.
- Use `Directory.Packages.props` for centralized package versions.
- Keep .NET source under `src/`, .NET tests under `tests/`, and the TypeScript UI under `webview/`.

## MSBuild path properties

- `Directory.Build.props` defines `$(RepoRoot)` — absolute path to the repository root, with a trailing directory separator (resolved from `$(MSBuildThisFileDirectory)`).
- Prefer `$(RepoRoot)` over `..\..\` constructs when a project file needs a path outside its own directory.

## Build and test

```sh
dotnet build SpecDesk.slnx          # validate compilation (warnings are errors)
dotnet test  SpecDesk.slnx          # run all tests

cd webview && npm install
npm run typecheck && npm run lint && npm test
npm run bundle                      # esbuild → ../src/SpecDesk.Host/wwwroot/webview.js
```

- A successful test run must execute the discovered tests, not only complete MSBuild targets.

## Formatting

- `.editorconfig` is the source of truth for indentation and line endings — follow it.
- Use tabs for indentation in C#, F#, MSBuild, and config files; YAML and PowerShell use spaces.
- The webview uses Biome (2-space). Do not mix tabs and spaces within a file.
- Preserve LF line endings, except Windows batch files (`.cmd`/`.bat`) which require CRLF.
- F# is formatted with Fantomas (`dotnet tool restore` then `dotnet fantomas`).

## C# style

- Use file-scoped namespaces; keep nullable and implicit usings enabled; treat warnings as errors.
- Minimize public API surface area; public API changes must be intentional and documented.

### Exception handling style

- **No one-line `try`/`catch`/`finally`.** Every `try`, `catch`, and `finally` keyword must own a brace block on its own lines. Forbidden:
	```csharp
	try { foo(); } catch { }
	try { foo(); } catch (IOException) { /* swallow */ }
	finally { stream.Dispose(); }
	```
	Required:
	```csharp
	try
	{
		foo();
	}
	catch (IOException)
	{
		// swallowed - pipe closed by the OS during teardown; nothing actionable.
	}
	```
- **Empty `catch` blocks must contain a short comment explaining why the exception is swallowed** — "what is being caught" plus "why ignoring is correct here". A bare `catch { }` without a justification comment is not acceptable. The comment must explain the **rationale**, not just restate the catch clause. "// ignored" or "// swallow" alone is not enough.

## Documentation

- All documentation and code comments must be written in English.
- Functional changes must include corresponding README updates when behavior, requirements, usage, or public API changes.

## Changelog

- `CHANGELOG.md` is the single source of truth for release notes.
- **Every user-visible change must be accompanied by a `CHANGELOG.md` update in the same change set.** This is non-negotiable for: new or modified public API, behavioural changes, bug fixes, deprecations, removals. Pure internal refactors that do not alter observable behaviour are the only exemption.
	- The changelog entry is part of the change, not an optional follow-up. Do not split it into a separate commit unless explicitly asked.
	- If a single change set produces multiple user-visible effects, write one bullet per effect — do not bundle.
- Add a manual bullet under `## [Unreleased]` in `CHANGELOG.md`. Use the appropriate subsection:
	- `### Added` — new features
	- `### Changed` — modified behaviour
	- `### Fixed` — bug fixes
	- `### Removed` — removed features
	- `### Deprecated` — features still present but marked for removal
- Write the entry for a consumer, not the implementer. Keep it to one line.
- Replace the placeholder `-` with a real bullet; do not leave placeholder lines alongside real entries.

### Auto-fill fallback

- If `## [Unreleased]` has no real bullets, tooling can auto-generate entries from commits using `git-cliff` (config: `cliff.toml`). Manual entries always win over auto-fill.
- The first word of the commit subject decides the bucket (case-insensitive):
	- `Add`, `Feat` → `### Added`
	- `Fix`, `Bug` → `### Fixed`
	- `Remove`, `Delete`, `Drop` → `### Removed`
	- `Refactor`, `Update`, `Change`, `Rename`, `Perf`, `CI`, `Cleanup`, etc. → `### Changed`
	- `Doc`, `Chore`, `Test`, `Style` → skipped
	- `Release v...` and merge commits → skipped
	- anything unrecognised → `### Changed` (fallback)

## Security scanning

- `.github/workflows/codeql.yml` runs GitHub CodeQL against the C# codebase and the TypeScript webview on pull requests, pushes to `main`, and weekly. It uses the `security-and-quality` query suite.
- The C# job uses `build-mode: manual` (runs `dotnet build SpecDesk.slnx`); the TypeScript job uses `build-mode: none`.
- Do not silence or skip CodeQL by editing the workflow to exclude paths or queries without explicit approval.

## Comments

- Minimize comments. Write them only to explain why something exists, architectural decisions, or non-obvious platform/runtime behavior — not what the code already says.

## Version control (jujutsu)

This repository uses [jujutsu (`jj`)](https://jj-vcs.github.io/jj/) for version control. The repo is colocated with git, but `jj` is the primary tool — use `jj` commands for everything in this workflow, not raw `git`.

### Describing the current change

- When you start a new piece of work, set the change description right away:
	```
	jj describe -m "Concise summary of what this change does"
	```
- For larger work, fold subsequent small edits into the current change without asking the user — keep extending the same change rather than starting a new one for each follow-up.
- If the scope of the current change shifts mid-work, refresh the description with another `jj describe -m "..."`. The description must always reflect what's actually being done.

- **Per-prompt evaluation (mandatory).** Before any edits, run `jj st` and classify the incoming prompt against the current change description:

	| Signal in prompt | Category | Action |
	|---|---|---|
	| Same topic, refinement, follow-up of in-progress work | **Continuation** | Just work. jj auto-folds edits into the current change. |
	| Same change but goal has been refined or expanded | **Scope shift** | `jj describe -m "<refined summary>"`. **Don't** start a new change. |
	| Orthogonal topic, different area, "now do X" | **New work** | If current change is finished → `jj new -m "<summary>"` (descendant). If still in progress → `jj new @- -m "..."` (parallel sibling). |

	Reliable signals: "now" / "next" / "and also" usually mean **new work** or **scope shift**. Imperative follow-ups inside the same scope ("fix this", "continue") mean **continuation**. When in doubt, ask the user.

### Starting unrelated work

When the per-prompt evaluation above lands on **New work**, branch rather than mixing topics into the current change:
- **Current change is complete** → `jj new -m "Description of the new task"` (a descendant).
- **Current change still needs more work** → `jj new @- -m "Description of the unrelated task"` (a parallel change off the same parent).
- Do not silently mix the two — every change must stay coherent.

### Pushing to remote

The user signals "synchronise with remote" with a short trigger word (typically `pull` or `push`). On that signal, run the full sync:
1. `jj git fetch` — pull down remote movement **before** doing anything else.
2. If `main@origin` has moved past the local change, rebase onto it: `jj rebase -r @- -d main@origin` (or `jj rebase -d main@origin` for a stack).
3. Put the work on a **feature bookmark — never advance `main` locally to publish.** First push: `jj bookmark create <topic> -r @` then `jj git push --allow-new -b <topic>`. Later pushes: `jj bookmark move <topic> --to @` then `jj git push -b <topic>`.
4. Open / update a pull request into `main` (`gh pr create --base main --head <topic> --fill`). `main` advances only when the PR merges; afterwards `jj git fetch` and `jj bookmark delete <topic>`.

Never push without an explicit signal from the user.

### Undoing work

When the user decides to abandon work in progress, prefer `jj`'s native undo facilities — they are safer than hand-rolled cleanup:

- **`jj undo`** (alias of `jj op undo`) — reverses the last operation. Repeatable.
- **`jj abandon <rev>`** — drops a specific change entirely. Descendants automatically rebase onto its parent.
- **`jj restore`** — discards working-copy modifications and resets `@` to its parent's tree.
- **`jj op log`** is the reflog equivalent — every operation is reachable via `jj op restore <op-id>`.

Never hide a deliberate undo: if the user asks to "undo the last commit/change", run `jj undo` (or `jj abandon`) and tell them what was reverted.

### Safety

- Do not revert or amend changes the user authored without explicit agreement.
- Do not rewrite unrelated files when making a focused change.

## Command conventions

- Commands and APIs should be idempotent where possible.
- Output should remain concise and script-friendly.
- Breaking changes must be explicit.
