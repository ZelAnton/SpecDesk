# SpecDesk — Code Review Orientation Guide

> **Purpose of this file.** A fast, token-cheap orientation map for an AI agent doing code review /
> bug-hunting in this repository — so it can skip broad exploration and jump straight to the right
> files, and so it doesn't waste a review pass flagging things that are **intentional** (documented
> conventions, known stubs, deliberately-deferred scope) as if they were bugs. It is a **snapshot**,
> not a live index — verify any specific claim (a file's existence, a function's current behaviour)
> against the actual code before relying on it, the same way you'd treat a memory. Point-in-time
> anchor: **release `v0.1.0`, tagged 2026-07-04**, `CHANGELOG.md`'s `[Unreleased]` section empty as of
> this writing.
>
> This file is a map, not the source of truth. Where it summarizes, the following are authoritative
> and win on conflict: [`AGENTS.md`](../AGENTS.md) (conventions), [`CHANGELOG.md`](../CHANGELOG.md)
> (what shipped), [`docs/ROADMAP.md`](ROADMAP.md) (execution plan/status), the code itself.

## 1. What SpecDesk is (one paragraph)

SpecDesk is a Windows desktop app (Photino + system WebView2) that lets non-technical authors
("managers") edit Markdown specs stored in GitHub **without ever touching git, branches, or pull
requests directly**. It wraps a three-mode Markdown editor (source / split / formatted-WYSIWYG,
Markdown always the source of truth), automated git/GitHub operations behind plain-language buttons,
a rendered semantic diff, inline review comments, automated image handling, and (planned) an embedded
AI agent. `SpecDesk` is an explicitly-placeholder working title (rename before any registry/namespace
work) — do not read significance into the name.

**The one architectural idea that explains most of the codebase:** native (C#/F#) is the brain and
owns *all* logic and UI chrome; the webview is a thin, "dumb but pretty" surface hosting only
CodeMirror 6 (source) and ProseMirror (WYSIWYG) and rendering HTML/decorations the native side
computes. When you're deciding whether logic "should" live in TypeScript vs C#/F#, the answer is
almost always C#/F# — a TypeScript file doing anything beyond DOM/editor glue is a design smell worth
a closer look, not necessarily a bug, but a real signal.

## 2. The plain-language lifecycle (read this before reviewing any UI-facing code)

This mapping is the product's central UX contract (`docs/design/04-git-workflow.md`,
`docs/design/SpecDesk-Design-Concept.md` §8, `docs/design/README.md`). **Authors must never see git
vocabulary** in primary UI — a stray "branch"/"commit"/"push"/"merge"/"rebase" string in author-facing
text is a real bug. There are exactly **two deliberate, documented exceptions** — do not flag these:

| Author sees | Git/GitHub reality | Deliberate exception? |
|---|---|---|
| **Edit** (+ a draft name prompt) | fetch latest, create a working branch from the published version | **Yes** — the draft name *is* the branch name, shown and editable on purpose (sanitized live) |
| **Saved** (continuous, automatic) | working copy written to disk — **no commit** | — |
| **Unsaved changes** | dirty working copy | — |
| **Save a version** (+ a short note) | `git commit`, note = commit message | **Yes** — the version note *is* the commit message, shown on purpose |
| **Send for review** | push branch + open a PR | — |
| **In review** | PR open | — |
| **Changes requested** | PR review = changes requested (sticky until reviewer re-reviews) | — |
| **Update review** | push new versions to the same PR | — |
| **Approved** | PR approved (only for the versions actually reviewed — pushing more reverts to In review) | — |
| **Publish** *(not yet built)* | merge the PR, gated by `allow-author-publish` | — |
| **Sync** (background) | fetch / prune | — |
| "Someone else changed this too" *(not yet built)* | rebase/merge conflict, no `<<<<<<<` markers ever | — |

Two behavioural rules that look like bugs but are **specified, intentional aging**:
- A **Changes-requested** status does not clear just because the author pushed a fix — it only clears
  when the reviewer re-reviews on GitHub.
- An **Approved** status covers only the versions that were actually reviewed — saving/pushing a new
  version after approval automatically reverts the shown status to **In review**.

**Autosave vs. Save-a-version is a deliberate two-tier model**, not redundancy: autosave is silent,
continuous, disk-only, and **must never commit**. An *earlier* implementation pass auto-committed on
idle; this was a deliberate correction (see `CHANGELOG.md` PoC-4 entry and `docs/ROADMAP.md`'s PoC-4
callout). **If you find autosave triggering a commit, that is a real regression** — it is explicitly
the one thing this codebase went out of its way to remove.

If the PR is merged/closed on GitHub out-of-band, SpecDesk deliberately does **not** auto-flip the
document's status from a background poll — it holds the last-known status until the (not-yet-built)
explicit **Publish** action runs. Don't flag "status doesn't update when PR is merged elsewhere" as a
sync bug; it's a designed guard against surprising the author.

## 3. Repository map

```
SpecDesk.slnx                # .NET solution (.slnx format), 8 src + 7 test projects
src/
  SpecDesk.Contracts/         C#  — IPC wire DTOs, no deps                (leaf)
  SpecDesk.Core/              F#  — lifecycle state machine, .spectool.toml, image rule engine
  SpecDesk.Markdown/          F#  — Markdig wrapper, AST DU, lineMap, HTML renderer
  SpecDesk.Diff/              F#  — semantic (AST) diff              → depends on Markdown
  SpecDesk.Git/               C#  — LibGit2Sharp wrapper (local-only ops; GitHub-agnostic) (leaf)
  SpecDesk.GitHub/            C#  — OAuth device-flow auth + hand-rolled HttpClient GitHub API → Contracts
  SpecDesk.Ai/                C#  — STUB (see §5) → Contracts, Core (unused so far)
  SpecDesk.Host/               C# — Exe: Photino bootstrap, IPC router, orchestration → all of the above
tests/                        # one test project per src/ project, NUnit, mirrors names + ".Tests"
webview/
  src/                        # 32 files, flat (no subdirs), ~5,480 LOC, esbuild → SpecDesk.Host/wwwroot/webview.js
  tests/                      # one *.test.ts per src file (Vitest) + tests/contract/ (see §6)
docs/
  README.md                   # docs index
  ROADMAP.md                  # THE execution plan/status tracker (see the disambiguation note below)
  borrowings-from-knowledge.md # reuse map from the author's other product "Knowledge" for PoC-6..10
  design/                     # 01-10 numbered design docs + SpecDesk-Design-Concept.md (UI source of truth)
AGENTS.md                     # authoritative contributor/agent conventions (CLAUDE.md defers to it)
CHANGELOG.md                  # Keep-a-Changelog; detailed per-PoC entries, good source for "what actually shipped"
```

### 3.1 `.NET` projects — dependency graph and size

| Project | Lang | Purpose | Depends on | Size |
|---|---|---|---|---|
| `SpecDesk.Contracts` | C# | IPC envelope (`IpcMessage`), `MessageKinds` constants, payload records — mirrored by `webview/src/protocol.ts` | — | 2 files, 340 LOC |
| `SpecDesk.Core` | F# | `Lifecycle` state machine, `.spectool.toml` reader (`Toml`/`WorkflowConfig`/`ImagesConfig`), slug/token expansion, `ImageProcessing`/`ImageEngine` (SkiaSharp) | — | 8 files, 910 LOC |
| `SpecDesk.Markdown` | F# | Shared Markdig pipeline, `Ast` DU, `Projection`, `lineMap`-carrying `Renderer` | — | 6 files, 432 LOC |
| `SpecDesk.Diff` | F# | `AstDiff` (Unchanged/Added/Removed/Changed/Moved) + `DiffWire` C#-friendly shape | `SpecDesk.Markdown` | 2 files, 421 LOC |
| `SpecDesk.Git` | C# | `IDocumentVersioning` (local-only: branch/save-version/discard) + `IGitPublishing`, via LibGit2Sharp | — | 3 files, 435 LOC |
| `SpecDesk.GitHub` | C# | OAuth device-flow (`GitHubDeviceFlowAuth`), `DpapiTokenProtector`, remote-URL parsing, PR/review status — hand-rolled `HttpClient`, **no Octokit package** | `SpecDesk.Contracts` | 8 files, 1449 LOC |
| `SpecDesk.Ai` | C# | **Stub** — see §5 | `SpecDesk.Contracts`, `SpecDesk.Core` (unused) | 1 file (`Placeholder.cs`) |
| `SpecDesk.Host` | C# (Exe) | Photino bootstrap, `HostController` (IPC dispatch), `AppAssetResolver` (`app://` scheme), `PreviewCoordinator`, `DiffProjection`, `ExternalLink` (URL-scheme gate), Serilog logging, `SampleRepo` seeding | all seven above | 11 files, 2814 LOC — largest project |

Rules that shape this graph (see `AGENTS.md` for the full text — restated here because a naive review
often flags these as problems):
- **F# `.fsproj` files list every `.fs` file explicitly, in dependency order — no globbing.** A new F#
  file that isn't in that list simply won't compile into the project; this is intentional, not an
  oversight if you notice a file "missing" from a `.fsproj`.
- **Every project uses Central Package Management** (`Directory.Packages.props`) — a `PackageReference`
  in a `.csproj`/`.fsproj` with **no `Version` attribute** is correct, not a missing-version bug.
- **`SpecDesk.Git` takes no ProjectReferences** and knows nothing about GitHub (access tokens are
  passed in as plain parameters) — this is deliberate layering so the local-only lifecycle contract
  (`IDocumentVersioning`) can't accidentally depend on network/GitHub concepts.
- **Exception handling style is non-default and enforced**: no one-line `try`/`catch`/`finally` (each
  keyword owns its own brace block), and an empty `catch` block **must** carry a comment explaining
  *why* the exception is safe to swallow — not just "// ignored". A `catch` block that looks unusually
  verbose or comment-heavy is following convention, not padding.

### 3.2 `webview/` — TypeScript UI (flat `src/`, no subdirectories)

| Area | Files |
|---|---|
| Wire/protocol | `protocol.ts` (mirrors `MessageKinds`), `decoders.ts` (defensive runtime narrowing — unknown/drifted shape decodes to `null`, never throws), `ipc.ts` (`window.external` bridge) |
| Entry | `index.ts` — wires everything to IPC events |
| Editors | `editor.ts` (CodeMirror 6, thin), `formatted.ts` (ProseMirror WYSIWYG, largest file), `pm-markdown.ts`/`pm-commands.ts` (PM↔Markdown schema, pure toolbar commands), `md-blocks.ts`/`md-splice.ts` (the **block-splice** round-trip serializer — see §5), `md-format.ts` (Code/Split toolbar text transforms) |
| Preview/diff/review | `preview.ts` (native HTML render sink — **currently CSS-hidden**, kept as scaffolding, not dead code to delete), `diff-marks.ts`, `diff-decoration.ts`, `word-diff.ts`, `review.ts` ("Show changes" overlay state), `reviews-panel.ts` |
| Layout/chrome | `view-mode.ts`, `scroll-sync.ts`, `scroll-geometry.ts`, `height-sync.ts`, `segmented-control.ts`, `format-toolbar.ts`, `lifecycle-chrome.ts`, `dialogs.ts`, `prompt-bar.ts`, `signin.ts` |
| Utilities | `debounce.ts`, `raf.ts`, `dom.ts`, `links.ts`, `log.ts`, `image-capture.ts` |

TypeScript is strict (`noUncheckedIndexedAccess`, `exactOptionalPropertyTypes`, `verbatimModuleSyntax`),
linted/formatted with Biome (2-space, double quotes) — do not propose a `// @ts-ignore` or a widened
`any` as a fix; the stricter alternative is almost always available and is what this codebase expects.

### 3.3 `docs/` — disambiguation you need before citing either roadmap file

- **`docs/ROADMAP.md`** is the **execution plan** — PoC milestones with goal/risk/demo/effort, a
  Mermaid dependency graph, and the authoritative "what's the current status of each milestone" text
  (e.g. it flags PoC-4 as needing rework). **This file wins on execution-order/status disagreements.**
- **`docs/design/03-roadmap.md`** is an earlier, coarser **conceptual phase narrative** (Phase 0-9)
  inside the numbered `01`-`10` design-doc series — describes *what each piece does*, not day-to-day
  status. Don't confuse the two; they're both real, current files serving different purposes.
- ⚠️ **`docs/ROADMAP.md`'s PoC-4 callout is itself stale.** It still reads "Partly built — needs
  rework" (autosave-commit needs replacing with explicit Save-a-version). Per `CHANGELOG.md` and the
  commit history (`c7406fc` "Redesign roadmap: explicit Save-version/push/PR" and the full PoC-4
  changelog entry), **this rework is done** — the current model is already the corrected one. Trust
  the code and `CHANGELOG.md` over this particular roadmap sentence.

## 4. Current status (as of v0.1.0, 2026-07-04)

| Milestone | Status | Notes |
|---|---|---|
| Spike-A — GitHub auth | ✅ Done | folded into PoC-5 |
| Design foundation (design concept adopted) | ✅ Done | tokens, shared stylesheet, CodeMirror theme, components |
| PoC-0 — Shell & IPC | ✅ Done | |
| PoC-1 — `app://` assets | ✅ Done | path-traversal guarded |
| PoC-2 — Editor + preview + `lineMap` ⭐ | ✅ Done | + height-sync, sub-line scroll-sync follow-ups |
| PoC-3 — Images | ✅ Done | |
| PoC-4 — Local versioning + lifecycle | ✅ Done (reworked) | explicit "Save a version" model; see §3.3 stale-doc note |
| PoC-5 — Send for review (GitHub) | ✅ Done | auth, send, update, reviewer assignment, title/description confirm, live status, My reviews panel |
| PoC-6 — Rendered semantic diff | 🟡 Partial | `SpecDesk.Diff` engine + local "Show changes" overlay (working copy vs. last saved version) shipped; diffing an **actual PR** (base vs. head) is not yet built |
| PoC-7 — In-flight PR comparison | ⬜ Not started | |
| PoC-8 — Inline comments + GitHub sync | ⬜ Not started | |
| PoC-9 — AI agent | ⬜ Not started | `SpecDesk.Ai` is a stub, see §5 |
| PoC-10 — Conflict handling + Publish | ⬜ Not started | no reconciliation dialog, no Publish button yet |
| PoC-11 — Editor view modes (Code/Split/Formatted) | ✅ Done | |
| PoC-12 — WYSIWYG formatted editing | ✅ Done (v1 limits) | see §5 for the named limits |

Do not treat PoC-7/8/9/10 absence as missing functionality to flag — it's scoped-out, tracked future
work, not an oversight. Do treat a bug **inside** an already-shipped milestone (rows marked ✅) as a
normal, in-scope finding.

## 5. Known stubs, limits, and drift — don't flag these as bugs (but do watch nearby code)

- **`SpecDesk.Ai` is a deliberate one-file stub**: `Placeholder.cs` defines only `public const string
  Module = "Ai"`. No Microsoft Agent Framework package is referenced yet. Reporting "AI agent isn't
  implemented" is not a useful finding here. (Minor, harmless doc drift: the `.csproj` TODO comment
  and root `README.md` call this "PoC-8"; `docs/ROADMAP.md` numbers it "PoC-9" — same milestone, two
  labels in the wild.)
- **`GitHubAuthOptions.DefaultClientId` is an empty string** (`""`) by design for local/dev builds —
  the real client id is meant to arrive via the `SPECDESK_GITHUB_CLIENT_ID` env var, or be compiled in
  before a real release. Its emptiness means the "Connect to GitHub" affordance simply hides; this is
  documented as a release-readiness checklist item (`docs/ROADMAP.md` PoC-5), not a crash-causing bug —
  though it's worth flagging again if you're specifically reviewing for release-readiness gaps.
- **`.github/CODEOWNERS` is fully commented out** (a placeholder `# * @__GitHubOwner__`) — intentional
  until the repo has a real owner to fill in; don't flag the missing CODEOWNERS enforcement as a gap
  unless asked to audit release-readiness specifically.
- **`docs/design/02-architecture.md` still names Octokit.NET** as the GitHub client library. The actual
  implementation (PoC-5) deliberately dropped Octokit for a hand-rolled `HttpClient` REST/GraphQL client
  (see commit `refactor(github): hand-roll the device-code request and drop the Octokit dependency`).
  An absent Octokit package reference in `SpecDesk.GitHub` is expected, current behaviour — the design
  doc is what's stale, not the code.
- **WYSIWYG (PoC-12) v1, explicitly out of scope for now**: structural table edits (add/remove
  row/column) must be done in the source view; height-alignment/scroll-sync between Formatted and
  Split is top-level-block granularity only; adding/removing a whole top-level block falls back to a
  full re-serialize instead of block-splice. The formatting toolbar's Link/Table/Image buttons are
  explicitly deferred (only bold/italic/strike/H1/H2/lists/quote/code shipped).
- **Markdown round-trip strategy is block-splice, not whole-document reflow — this was a deliberate,
  measured decision** (a whole-document re-serialize was tested and rejected: ~41/48 lines changed on
  a no-op round-trip of `welcome.md`). If you see `md-splice.ts` only re-emitting *changed* top-level
  blocks and leaving the rest byte-identical, that's the design working as intended, not incomplete
  serialization.
- **`preview.ts` (webview) is CSS-hidden but not dead code** — it's kept as scaffolding for future
  diff/comment rendering; Split's visible right pane is the editable ProseMirror surface since PoC-12.
  Don't propose deleting it as unused.
- **`[images] strip-metadata` config key was removed** — re-encoding a pasted image now *always* strips
  EXIF/XMP/ICC (the key "had no runtime effect" per `CHANGELOG.md`, since its value was never read); an
  unrecognized key in `.spectool.toml` is silently ignored by design, not a validation gap.

## 6. Cross-language contract fixtures — a real drift-detector, treat mismatches as bugs

Unlike the "don't flag this" items above, **this mechanism exists precisely to catch real bugs** — a
divergence here is a genuine finding, not a false positive.

| Fixture (`webview/tests/contract/`) | Source of truth / generator | Webview-side check |
|---|---|---|
| `wire-kinds.json` | `MessageKinds` consts → `ContractFixtureTests` (`SpecDesk.Contracts.Tests`) | `contract.test.ts` vs `Kinds` |
| `native-payloads.json` | native→webview payload records → `ContractFixtureTests` | `contract.test.ts` vs the decoders |
| `lifecycle-states.json` | F# `Lifecycle.State` DU → `LifecycleContractTests` (`SpecDesk.Core.Tests`) | `contract.test.ts` vs `STATUS_STATES` |
| `diff-kinds.json` | F# `DiffWire.DiffKind` → `DiffKindContractTests` (`SpecDesk.Diff.Tests`) | `contract.test.ts` vs `DIFF_KINDS` |

Regeneration is **whole-solution only**: `UPDATE_CONTRACT_FIXTURE=1 dotnet test SpecDesk.slnx` (a
filtered `--filter` run regenerates only some fixtures and leaves the rest stale — a common way this
goes wrong). A **missing** fixture file is meant to be a hard test failure, never something silently
regenerated away. The webview→native direction is deliberately **not** fixtured — the host decodes
those payloads defensively (unknown shape → treated as absent/null), and host + webview bundle ship as
one artifact, so there's no independent version skew to guard against on that side.

## 7. Security-sensitive surfaces already hardened — re-check on any change nearby, don't assume solid forever

These were explicitly hardened in past commits; they're the shape of risk to keep checking for if you
touch adjacent code, not confirmation that the area is now risk-free:

- **Path containment**: `AppAssetResolver` (Host, `app://` scheme) and `SpecDesk.Core.ImageEngine`
  both guard against a crafted path/extension or a symlink/junction escaping the repository tree.
  `ContainmentParityTests` (Host.Tests) cross-checks that the C# Host and F# Core guards agree.
- **Untrusted document content**: dangerous URL schemes (`javascript:`, `data:`, …) in rendered
  Markdown links/autolinks are neutralized by the renderer; `ExternalLink` (Host) validates any
  `link.open` IPC request and only opens `http`/`https`/`mailto` — the webview itself can never
  navigate on its own.
- **IPC frame handling**: an oversized frame from the webview is dropped before parsing (DoS guard);
  an unhandled fault in one message handler must not tear down the whole message pump.
- **Token handling**: the GitHub access token is protected at rest via Windows DPAPI
  (`DpapiTokenProtector`, `CurrentUser` scope), is passed as a plain parameter (never a global/ambient
  value) into `SpecDesk.Git`'s publishing calls, and is documented as never logged — worth checking
  that a new log statement near auth/push code doesn't accidentally include it.

## 8. Quick lookup — "I'm reviewing X, where do I look"

| Reviewing... | Primary files |
|---|---|
| IPC / wire protocol | `SpecDesk.Contracts` (`IpcMessage.cs`, `Payloads.cs`, `MessageKinds`) ↔ `webview/src/protocol.ts` + `decoders.ts`; fixtures in §6 |
| Markdown parsing / AST / `lineMap` | `SpecDesk.Markdown` (`Ast.fs`, `Projection.fs`, `Renderer.fs`, `Lines.fs`) |
| Diff engine | `SpecDesk.Diff` (`AstDiff.fs`, `DiffWire.fs`) + Host's `DiffProjection.cs` + webview `diff-marks.ts`/`diff-decoration.ts`/`word-diff.ts` |
| Document lifecycle / versioning | `SpecDesk.Core.Lifecycle` (F#) + `SpecDesk.Git` (`IDocumentVersioning`, `LibGit2DocumentVersioning`) + webview `lifecycle-chrome.ts`/`dialogs.ts` |
| Images | `SpecDesk.Core` (`ImageProcessing.fs`, `ImageEngine.fs`, `Tokens.fs`) + Host's `ImageInsertAdapter.cs` + webview `image-capture.ts` |
| GitHub auth / PR round-trip | `SpecDesk.GitHub` (`GitHubDeviceFlowAuth`, `DeviceFlowApi`, `GitHubReview`, token store/protector) + webview `signin.ts`/`reviews-panel.ts` |
| WYSIWYG / editor engines | webview `editor.ts` (CodeMirror), `formatted.ts` (ProseMirror), `pm-markdown.ts`, `md-blocks.ts`/`md-splice.ts` |
| Scroll-sync / height-sync | webview `scroll-sync.ts`, `scroll-geometry.ts`, `height-sync.ts` |
| Host bootstrap / asset serving / logging | `SpecDesk.Host` (`Program.cs`, `HostController.cs`, `AppAssetResolver.cs`, `Logging.cs`, `LogBridge.cs`) |
| Visual design / theming / component styling | `docs/design/SpecDesk-Design-Concept.md` (§4 tokens, §5 stylesheet, §6 CodeMirror theme, §7 components) — the doc **wins** over any mockup image if they disagree |
| `.spectool.toml` schema | `docs/design/10-repo-config.md` + `SpecDesk.Core` (`Toml.fs`, `WorkflowConfig.fs`, `ImagesConfig.fs`) |

## 9. Verifying a suspected bug cheaply

```sh
dotnet build SpecDesk.slnx          # warnings are errors — a real compile/analyzer issue surfaces here
dotnet test  SpecDesk.slnx          # run all .NET tests
dotnet test  SpecDesk.slnx --filter "FullyQualifiedName~TestMethodName"

cd webview
npm run typecheck && npm run lint && npm test
```

Before reporting a finding as confirmed: check whether an existing test already covers the exact
scenario (test file names generally mirror source file names 1:1 in both `tests/` and
`webview/tests/`), and check `CHANGELOG.md` / recent commits for the area — a surprising-looking
pattern is disproportionately likely to be a documented, deliberate fix for a past incident (the
"Fixed"/"Security" sections of `CHANGELOG.md`'s `[0.1.0]` entry are a good density of these).

## 10. Canonical references (this file summarizes, these decide)

- [`AGENTS.md`](../AGENTS.md) — full contributor/agent conventions (build ordering, exception style,
  changelog discipline, formatting, jujutsu VCS workflow, design-system application rules).
- [`CHANGELOG.md`](../CHANGELOG.md) — the ground truth for what has actually shipped, in detail.
- [`docs/ROADMAP.md`](ROADMAP.md) — execution plan and per-PoC status (see §3.3 for its one known
  stale callout).
- [`docs/design/README.md`](design/README.md) and [`docs/design/SpecDesk-Design-Concept.md`](design/SpecDesk-Design-Concept.md) — product vision and the authoritative visual/UX design system.
- [`docs/borrowings-from-knowledge.md`](borrowings-from-knowledge.md) — reuse map for PoC-6..10 from
  the author's sibling product; useful context for *why* an upcoming feature might be shaped a certain
  way once it lands.
