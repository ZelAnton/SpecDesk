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

## Cross-language contract fixtures

The native↔webview wire contract is hand-mirrored in C#/F# (`SpecDesk.Contracts`, `Lifecycle`, `DiffWire`)
and TypeScript (`webview/src/protocol.ts` + `decoders.ts`), so a rename on one side would drift silently.
Committed JSON fixtures under `webview/tests/contract/` pin each enumerated/structural contract; a .NET
test emits the fixture from the source of truth and a webview test asserts the TS side against the same
file, so any drift breaks CI in the language that didn't follow.

| Fixture (`webview/tests/contract/`) | Source of truth → generator | Webview half |
|---|---|---|
| `wire-kinds.json` | `MessageKinds` consts → `ContractFixtureTests` (Contracts.Tests) | `contract.test.ts` vs `Kinds` |
| `native-payloads.json` | native→webview payload records → `ContractFixtureTests` | `contract.test.ts` vs the decoders |
| `lifecycle-states.json` | F# `Lifecycle.State` DU → `LifecycleContractTests` (Core.Tests) | `contract.test.ts` vs `STATUS_STATES` |
| `diff-kinds.json` | F# `DiffWire.DiffKind` → `DiffKindContractTests` (Diff.Tests) | `contract.test.ts` vs `DIFF_KINDS` |

- After an intentional contract change, regenerate over the **whole solution** (the generators live in
  three test projects behind one opt-in) using `scripts/update-contract-fixtures.cmd`, then update the
  TS mirror to match. The script runs:
  ```sh
  UPDATE_CONTRACT_FIXTURE=1 dotnet test SpecDesk.slnx
  ```
  Regenerating with a narrowed `--filter` rewrites only some fixtures and leaves the rest stale.
- A *missing* fixture is a test failure, never a silent regenerate — deleting one must not disable its guard.
- The webview→native payload shapes are intentionally not fixtured: the host decodes them defensively
  (unknown shape → null) and host + bundle ship as one artifact, so there is no independent-version skew.

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

## Reference implementations (editor research)

For the planned **WYSIWYG / 3-mode editor** (Markdown stays the single source of truth; formatted
edits round-trip to Markdown immediately — see [docs/design/05-live-preview.md](docs/design/05-live-preview.md)),
the following editors were checked out locally under `d:\GitHub\Personal\Temp\` and studied as
references. They are **research references**, not vendored dependencies.

> **License discipline (important).** Architecture and approach may be studied from any of these.
> Source may only be *adapted into* SpecDesk from the **MIT**-licensed ones. **HedgeDoc is
> AGPL-3.0** — study its approach, but do **not** copy its source into our tree (it would impose
> AGPL on SpecDesk).

| Project | Local path | License | Engine | Edits the *formatted* view? | Modes |
|---------|-----------|---------|--------|------------------------------|-------|
| **@gravity-ui/markdown-editor** | `Temp\markdown-editor` | MIT | **ProseMirror** (WYSIWYG) + **CodeMirror 6** (markup) + markdown-it | **Yes** | wysiwyg / markup / split |
| **vditor** | `Temp\vditor` | MIT | **Lute** (Go→wasm/JS), bidirectional MD↔DOM | **Yes** | wysiwyg / IR (instant) / SV (split) |
| **MarkText / muya** | `Temp\marktext` | MIT | custom model + `contenteditable` + snabbdom + `marked` | **Yes** (always formatted) | wysiwyg only |
| **zenmark-editor** | `Temp\zenmark-editor` | MIT | **Tiptap 3** (ProseMirror) + markdown-it/remark | **Yes** | wysiwyg only |
| **HedgeDoc** | `Temp\hedgedoc` | **AGPL-3.0** | CodeMirror 6 + markdown-it (render in iframe) | No — edits source | code / split / render-view |
| **EasyMDE** | `Temp\easy-markdown-editor` | MIT | CodeMirror 5 + `marked` | No — styled source + preview | source + side-by-side/full preview |

What each is most useful for, with the key paths inside its repo:

- **@gravity-ui/markdown-editor** — closest to our target: dual-engine, Markdown as source of truth,
  switch wysiwyg↔markup via MD round-trip, and a built-in overlay system for anchored widgets
  (comments/diff). Study: `packages/editor/src/bundle/Editor.ts` (mode-switch + content sync),
  `packages/editor/src/core/markdown/{MarkdownParser,MarkdownSerializer}.ts` (MD↔ProseMirror),
  `packages/editor/src/extensions/behavior/WidgetDecoration/` (React widgets anchored to PM positions).
- **vditor** — proof that the **3 modes incl. true WYSIWYG** work, with a single engine (Lute) doing
  MD↔DOM both ways. Study: `src/ts/toolbar/EditMode.ts` (mode switch), `src/ts/wysiwyg/`,
  `src/ts/ir/`, `src/ts/sv/`, `src/ts/markdown/getMarkdown.ts`. Comment overlays via `data-cmtids`;
  block semantics via `data-type`/`data-block`. Caveat: the engine is an opaque wasm blob; no
  source line:col tracking.
- **MarkText / muya** — a self-contained, framework-free WYSIWYG engine (`@muyajs/core`,
  `packages/muya/`). Study the state model: `src/state/{markdownToState,stateToMarkdown}.ts`
  (MD↔state tree), `src/block/base/{content,format}.ts` (contenteditable blocks), inline token
  ranges in `src/inlineRenderer/`. No separate source mode; lots of engine to own.
- **zenmark-editor** — the Tiptap route (higher-level ProseMirror): per-node/mark Markdown serialize
  specs. Study: `packages/zenmark-editor/src/extensions/tiptap-markdown/{parse,serialize}/`.
  Simpler to start, but reparses wholesale and tracks no source spans.
- **HedgeDoc** — the **3-mode shell + scroll-sync + line-marker anchoring** (read-only render).
  Study: `frontend/src/components/editor-page/{editor-pane,renderer-pane,splitter,synced-scroll}/`
  and `frontend/src/components/markdown-renderer/extensions/linemarker/` (`data-start-line` /
  `data-end-line` on rendered nodes — the model for anchoring overlays to source lines). AGPL: read only.
- **EasyMDE** — *not* WYSIWYG; a styled Markdown **source** editor with a toolbar + preview. Useful
  only as a reference for the **toolbar→source-edit** command pattern (`src/js/easymde.js`,
  `_toggleBlock` / `_toggleLink` via CodeMirror `replaceRange`).

**Takeaways for SpecDesk's formatted-edit PoC:** two viable families — (1) **ProseMirror dual-mode**
(@gravity-ui or Tiptap) with MD↔PM parse/serialize and PM **decorations** for overlays; (2) a
**custom/contenteditable or wasm engine** (muya, vditor) — more self-contained but much more to own.
The diff/comments-in-both-views requirement maps to PM decorations (formatted view) + CodeMirror
decorations (source view), reconciled through a source-line ↔ document-position map; HedgeDoc's
`data-line` markers are the reference for that anchoring. All true-WYSIWYG candidates are MIT.

## Design system (UI source of truth)

The UI follows a single agreed **design concept** produced by Claude Design, committed under
[`docs/design/`](docs/design/) alongside the prose design docs. The authoritative document is
[`docs/design/SpecDesk-Design-Concept.md`](docs/design/SpecDesk-Design-Concept.md); the `*.dc.html`
files and `docs/design/pages/*.html` are live, themeable mockups and `docs/design/img/*` are
reference renders. **When the text and a mockup disagree, the text wins.**

- **Adhere to the concept as the overall design direction.** Build every surface from its design
  tokens (the §4 CSS custom properties — colour, type, spacing, radii, elevation, motion), its one
  shared **rendered-document stylesheet** (§5, reused by preview / formatted / diff / comparison —
  **do not fork it per surface**), its **CodeMirror editor theme** (§6), and its **component specs**
  (§7: buttons, segmented control, inputs, inline prompt bars, status/lifecycle badges, side panels,
  comment threads, diff chrome). Theme only by toggling `data-theme` on `<html>`; light is the
  baseline and dark must work day one.
- **You may extend the concept.** Add the elements, panels, buttons and controls a feature genuinely
  needs — the concept does not enumerate every future surface. But new UI must stay **within the
  concept's intent**: reuse its tokens and component language, match its calm density and motion, and
  never introduce a one-off colour, font, or control style when a token or §7 component already
  covers it. Significant new surfaces extend §7 rather than inventing a parallel vocabulary.
- **Plain language at the boundary (non-negotiable).** Authors never see git vocabulary; the UI
  speaks only the §8 lifecycle words (Edit / Saved / Save a version / Send for review / In review /
  Changes requested / Approved / Publish). The single deliberate exception is the plain-language
  version note. Conflicts surface as understandable document differences, never raw markers.
- **Accessibility is part of the design**, not a follow-up: WCAG AA contrast, full keyboard reach, an
  always-visible focus ring, `aria-*` on controls, and never encode state by colour alone (§11).
- **Layout is resilient to width.** The window resizes freely, so build for it once, centrally — never
  per element. Two standing rules, both already in `webview/styles.css`: **control labels never wrap**
  (`button { white-space: nowrap }`, so a fixed-height control can't spill its text to a second line),
  and **rows of controls reflow, they don't overflow** (`flex-wrap: wrap` on toolbars and inline
  bars). Put such fixes in the shared stylesheet so they cover every present and future control, not
  in one component. The same instinct applies elsewhere: prefer a single general rule over patching
  each site.

This concept is the visual contract. The roadmap's **design-foundation milestone**
([docs/ROADMAP.md](docs/ROADMAP.md)) brings the existing webview UI in line with it and lays the
token/theme groundwork every later UI surface builds on.

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
