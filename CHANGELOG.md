# Changelog

All notable changes to **SpecDesk** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- `scripts/update-contract-fixtures.cmd` regenerates all four contract fixture files
  (`webview/tests/contract/{wire-kinds,native-payloads,lifecycle-states,diff-kinds}.json`) in one
  whole-solution `UPDATE_CONTRACT_FIXTURE=1 dotnet test SpecDesk.slnx` run, so an intentional contract
  change can no longer be regenerated with a narrowed `--filter` that silently leaves some fixtures
  stale.

### Changed
- The webview's starting view mode (Code/Split/Formatted) is now declared exactly once, in `#panes`'s
  `data-mode` attribute in `webview/index.html`; `webview/src/index.ts` reads it back at startup instead
  of repeating a `"split"` literal of its own, and reflects it into the mode radiogroup through the same
  `SegmentedControl.setSelected` path a user click/arrow-key uses (the buttons' `aria-checked`/`tabindex`
  in the markup are now inert placeholders). Previously the starting mode was declared three times by
  hand (the TS literal, `data-mode`, and the radiogroup's `aria-checked`/`tabindex`) and had to be kept in
  sync manually; a drift between them ran the Split-only logic (spacer/scroll/height-sync) against panes
  that were not actually both visible. No observable behavior changes when the three literals agreed, as
  they always had.
- The formatted editor's two markdown-it tokenizers — the source-block split (`webview/src/editors/
  md-blocks.ts`) and the ProseMirror parser (`webview/src/editors/pm-markdown.ts`) — now derive from one
  shared config (`webview/src/editors/md-config.ts`) instead of each configuring its own instance
  (previously the default preset with a block-nesting cap of 100 vs commonmark + table + strikethrough
  with a cap of 20). The block-splice round-trip correlates the split's top-level blocks with the parser's
  top-level nodes 1:1, so the two must agree on the top-level boundaries; they used to agree only by
  convention and diverged past nesting depth 20 (where the caps differed), silently forcing the whole-
  document reflow fallback on such documents. Sharing one config makes the agreement constructive at any
  depth. `serializeWithSplice` also no longer re-parses the baseline document twice per `getText()` call —
  the `(original → {doc, blocks})` pair is memoized — and the formatted editor caches its own `getText()`
  result on the (baseline, document) pair so the Split cross-pane mirror's per-tick equality check no
  longer re-parses/serializes an unchanged document. No observable editor behavior changes.
- The product's name and version, and the `%LOCALAPPDATA%\SpecDesk` data root, are now defined once in a
  new `SpecDesk.AppInfo` project (`ProductInfo`, `AppPaths`) instead of being hand-duplicated across the
  tree: the AppData root (previously assembled independently for the sample repo, the GitHub auth/token
  directory, and the log directory), the window title, the rolling log file name prefix, and the local
  git fallback commit identity all read from it now, and `GitHubHttp.UserAgent`'s version is derived from
  `ProductInfo.Version` (the assembly's own build-time `<Version>`) instead of a second hard-coded "1.0"
  that had drifted out of step with the shipped 0.1.0. `AppPaths.Auth`/`SampleRepo`/`Logs` are pinned
  byte-identical to the paths they replace (see `SpecDesk.AppInfo.Tests`), so no existing installation's
  DPAPI-encrypted token directory is orphaned by this change.
- Removed two dead `ProjectReference` edges: `SpecDesk.GitHub` no longer references `SpecDesk.Contracts`
  (nothing in `SpecDesk.GitHub` used its types), and `SpecDesk.Ai` no longer references
  `SpecDesk.Contracts`/`SpecDesk.Core` (the stub project ahead of PoC-9 doesn't consume either yet).
  `docs/CODE-REVIEW-GUIDE.md` §3.1's dependency-graph table and repository-map diagram are corrected to
  match — both projects are now leaves in the graph.
- `diff.request` (webview→native) now carries a `{ base }` payload — `"lastVersion"` (the working copy vs
  the last saved version), with `"published"` and `"pr"` reserved for the upcoming vs-main / vs-PR-head
  compares — instead of implicitly diffing against the local HEAD. The webview's review overlay
  (`ReviewController`) owns the choice of base; the "Show changes" affordance keeps its existing local
  behavior unchanged by always requesting `"lastVersion"`. `HostController.OnCompare` reads the base from
  the payload (falling back to `"lastVersion"` for a missing/malformed payload) and reports "not supported
  yet" for the reserved bases, which aren't implemented.
- `IpcClient` (`webview/src/ipc.ts`) now exposes `subscribe(id, handler)`, a multi-frame
  correlation alongside the existing one-shot `request()`: the pending entry stays registered
  across every frame carrying the id until the caller itself releases it via the returned
  `unsubscribe()` (typically on a terminal frame kind). This is the correlation model the
  planned `chat.delta`/`chat.done` streaming will build on; the existing single-reply
  `request()`/`on()` behavior is unchanged.
- The `BundleWebview` MSBuild target (`SpecDesk.Host.csproj`) is now incremental: it declares
  `Inputs`/`Outputs` covering the webview sources/config and the generated bundle, so a plain
  `dotnet build`/`dotnet test` no longer re-runs `npm run bundle` when nothing in `webview/` changed.
  It also skips entirely during design-time builds (`$(DesignTimeBuild)` — IDE IntelliSense passes),
  which never needed the bundle. A trailing `Touch` normalizes the copied `index.html`/`styles.css`
  timestamps after each real run, since Windows' file-copy preserves the *source* file's timestamp and
  would otherwise make the up-to-date check see a stale output on every invocation.
- `HostController` (`SpecDesk.Host`) is now organized into partial-class files by area of
  responsibility: `HostController.Review.cs` (GitHub review orchestration — send/update/refresh/list
  and the PR-text suggestion), `HostController.SignIn.cs` (device-flow sign-in and the account
  affordance), and `HostController.Session.cs` (the document/editing session — open/save, drafts,
  disk autosave, saving versions, image paste, and compare). `HostController.cs` retains the shared
  fields, locks, constructor, and the IPC message router. This is a purely mechanical move of members
  between files of the same class — no signatures, lock ordering, logic, or observable behavior change.
- `HostController`'s draft editing session (`SpecDesk.Host`) is now held as one immutable `DraftSession`
  snapshot — lifecycle state, working/base branch, dirty flag, saved/shared version counts, and the
  `_sync`-guarded generation token — swapped atomically under `_sync`, replacing six separate mutable
  fields plus the loose `_textGeneration` companion. Handlers snapshot the whole session in a single
  read and compare generations ("is this still the same draft?") instead of racing individual fields.
  The live `_draftGeneration` checkout counter stays a separate `Interlocked` value (it is mutated under
  `_repoGate`, where `_sync` must not be taken), preserving the exact autosave/discard/send race guards
  from earlier fixes. No IPC contract, event ordering, or observable "draft changed under an operation"
  behavior changes.
- The `webview/` TypeScript UI, previously a flat `src/` of ~33 files, is now organized into subfolders
  by area boundary (per `docs/CODE-REVIEW-GUIDE.md` §3.2): `wire/` (protocol/decoders/ipc), `editors/`
  (CodeMirror + ProseMirror engines and the Markdown round-trip), `review/` (preview, diff marks/
  decoration, word-diff, review overlay + panel), `chrome/` (view-mode, toolbars, dialogs, sign-in),
  `sync/` (scroll- and height-sync), and `util/` (debounce, raf, dom, links, log, image capture);
  `index.ts` stays at the `src/` root as the entry point. `webview/tests/` mirrors the new layout 1:1
  (the cross-language `tests/contract/` fixtures and `contract.test.ts`/`index.test.ts` stay at the test
  root). This is a purely mechanical move — file contents are byte-identical apart from the updated
  relative imports; no esbuild/tsconfig/vitest/Biome configuration changed (the bundle entry is still
  `src/index.ts`, and `tsconfig`/Vitest resolve `src`/`tests` by directory), so `npm run typecheck`,
  `npm run lint`, `npm test`, and `npm run bundle` behave identically.
- `index.ts`'s `wire()` bootstrap is split into four nested helpers over a shared prologue —
  `wireEditors` (the two editors, their live cross-mirror/highlight/scroll sync, height-sync, the review
  overlay, the formatting toolbar, image paste, and the preview/diff/`doc.loaded` handlers),
  `wireLifecycle` (the action buttons + status/error stream), `wireGitHub` (sign-in, the reviews panel,
  and the review-status refresh triggers), and `wireViewMode` (the mode switch plus wrap/theme/export/
  skip-link chrome). The shared state, forward-declared editor/controller handles, and the
  `dialogs`/`requestSuggestion`/`syncReviewPolling` helpers stay in the prologue so all four groups close
  over them; the helpers run in dependency order and every cross-group reference is a callback that fires
  after wiring completes, so IPC registration, construction order, and observable behavior are unchanged.
- `SpecDesk.GitHub`'s two hand-rolled `HttpClient` transports (`DeviceFlowApi.cs`, `GitHubReview.cs`) no
  longer each define their own copy of the 30-second per-request timeout, the `SpecDesk/1.0` User-Agent,
  the linked-`CancellationTokenSource` pattern, and the safe `StringOf`/`NumberOf` JSON-field readers.
  Both now share the new internal `GitHubHttp` helper for all four; the "hand-rolled BCL only, no HTTP
  client package" discipline and every observable request/response behavior are unchanged.

### Removed
- Dead release scaffolding: `.gitignore`'s `release-notes.md` entry and `cliff.toml`'s header comment
  referenced a `.github/workflows/release.yml` that never existed in this repo; `Directory.Build.props`
  carried a template comment block (and its now-unreachable conditional `CA1707` `NoWarn`) for a
  `__ProjectName__`/`scripts/init.ps1` project-stamping script that was never added. `release.cmd` and
  the build are unaffected — neither reads `cliff.toml`/`release-notes.md` nor relies on the removed
  `NoWarn`.

### Fixed
- The "Show changes" overlay's inline word diff no longer shows phantom strikethrough/insertion marks at
  every line break of a CRLF-committed document. `ReadHeadContent`'s git blob still carries "\r" for a
  CRLF document, while the webview's head text is always LF-only; `DiffProjection.Build`
  (`src/SpecDesk.Host/DiffProjection.cs`) now normalizes the base to "\n" before the structural diff runs,
  so `BaseText`/`BaseSource`/`RemovedText` on the wire never carry a "\r" that would otherwise mismatch
  every head-side line break as a fake whitespace add/del pair and inflate the change ratio on multi-line
  paragraphs.
- The formatting toolbar's inline wrap (`md-format.ts` — bold, italic, strike) no longer emits Markdown
  that fails to render. A selection with edge whitespace kept the spaces inside the markers (`**word **`),
  which CommonMark's flanking rule leaves as literal asterisks — the markers are now pushed inside the
  whitespace (`**word** `). A selection spanning a blank line produced a single emphasis run across the
  paragraph break (invalid); each paragraph is now wrapped on its own. And a partial selection inside an
  existing wrapper (e.g. `foo` inside `**foo bar**`) nested to `****foo** bar**` because "already
  formatted" was tested only against markers touching the selection edges — the enclosing wrapper is now
  detected on the parsed `@codemirror/lang-markdown` syntax tree, so the toggle unwraps it instead of
  nesting.
- `docs/CODE-REVIEW-PLAN.md`'s T-09 finding claimed `AGENTS.md`/`CLAUDE.md` were gitignored, making every
  tracked doc's `../AGENTS.md` link 404 on GitHub. The premise was real: a `.gitignore` rule excluded both
  files from early in the project's history (`8fa0b6c`) until commit `800ce26`, which removed that rule
  and first tracked both files. At current HEAD/`origin/main` both files are tracked and no longer
  `.gitignore`d, so every tracked `../AGENTS.md` link now resolves; the finding is marked resolved so it
  isn't re-chased.
- The Split view's "Show changes" overlay (webview `index.ts`) no longer renders a false "No changes
  since the last saved version" when the host's `diff.result` reply is malformed (decodes to `null`,
  e.g. a transport/contract glitch). It is now dropped, the same as every other `ipc.on` handler,
  instead of falling through to an empty entries list that washed nothing and reported it as a genuine
  empty diff. Separately, the Split view's cross-pane mirror (an edit in one editor is silently
  mirrored into the other once its own 120ms debounce settles) could lose keystrokes: because each
  pane's debounce is timed independently, editing pane B while pane A's own debounce was still pending
  let A's mirror silently overwrite B's not-yet-reported edit with a stale snapshot once A's debounce
  fired first. `MarkdownEditor`/`FormattedEditor` now expose `hasPendingChange()`, and the mirror
  (`shouldMirrorInto`) skips mirroring into a pane that itself has a pending, not-yet-reported edit —
  its own debounce mirrors its newer text back shortly after, so nothing is lost either way.
- The formatting toolbar's line-prefix toggle (`md-format.ts` — bullet list, ordered list, blockquote)
  no longer doubles the prefix on a mixed selection (some lines already carry the prefix, some don't).
  Toggling now always strips any existing prefix from each line first, then re-adds the target prefix
  when the selection isn't already uniform — normalizing mixed selections instead of stacking a second
  prefix onto the lines that already had one.
- `log.ts` (webview) no longer throws when logged `data` contains a circular reference or a `BigInt`
  — the JSON serializer now renders `BigInt` values as strings and falls back to a placeholder for
  anything it still can't stringify, instead of letting `JSON.stringify` throw into the caller.
- `scrollTopForLine` (webview `scroll-geometry.ts`) no longer risks producing `NaN` for a block whose
  content-line span is zero (a division by zero). It now guards `span == 0` the same way its inverse,
  `lineAtScrollTop`, already guards `height === 0`. Markdown-it's own top-level block tokens always carry
  a source map spanning at least one line, so this is defense-in-depth rather than a fix for an observed
  live scenario — but the interpolation is no longer implicitly relying on that invariant.
- Escape now closes/cancels an open inline prompt bar (draft name, version note, send-for-review)
  regardless of which of its own elements holds focus. Previously the Escape handling lived only on
  the text input/textarea, so a keyboard user focused on the Confirm/Cancel button got no reaction.
- The "My reviews" panel no longer hangs on "Loading your reviews…" forever when the host's review query
  rejects (correlation timeout, transport failure, etc.). The rejection is now caught and renders a
  fallback error state instead.
- `PromptBar.open()` (webview) no longer drops a reopen requested during the "closing in flight" window.
  Previously, calling `close()` while an `open()`'s suggestion request was still in flight invalidated
  that request but left its `opening` latch set until the stale request's own `finally` ran; a fresh
  `open()` issued in between was silently swallowed (an empty click that only self-healed on the next
  click). `close()` now drops the latch immediately, and a stale request only clears the latch if it
  still owns the current generation, so it can never clobber a newer request's latch.
- Pasting/dropping an image into the editor no longer risks inserting its markdown link at the wrong
  spot, or losing it entirely, when the host round-trip (which produces the link) is still in flight:
  the insert position is now tracked through subsequent document edits instead of being captured once
  and reused stale. This also fixes several images pasted/dropped together — previously captured at
  the same position and applied in whatever order their host replies happened to arrive, so they could
  clobber one another; each now resolves to its own, independently tracked position.
- `HostController.LoadFile` no longer always stamps a freshly opened document as Published. The
  lifecycle state lived only in memory, so restarting mid-draft (crash, force quit, or a plain relaunch)
  falsely reported "Published" even though the repository's working tree was still checked out on the
  draft branch — and clicking "Edit" from that wrong state re-ran the forced checkout, silently
  resetting whatever had been autosaved to disk before the restart. The very first document a process
  loads now resolves its starting lifecycle from the repository's actual checked-out branch instead of
  assuming memory is authoritative; every later "Open" during the same session is unaffected (this
  object's own in-memory tracking already covers it). `LibGit2DocumentVersioning.CurrentBranch` also
  now reports a detached HEAD as `null` instead of libgit2's own `"(no branch)"` placeholder, so the
  first-load recovery above correctly resumes as Published on a detached HEAD rather than mistaking
  `"(no branch)"` for a genuine draft branch to resume (and later store as the working branch).
- `LogBridge.Export` now reports a plain-language message ("Could not export the log.") on failure
  instead of surfacing the raw exception text to the author.
- `LogBridge.Receive` now strips embedded CR/LF sequences from the webview-supplied `Message`/`Data`
  log fields before they reach the log template. Previously a crafted payload with line breaks could
  forge additional, falsely-formatted log entries in the shared rolling log.
- `PushBranch` (`SpecDesk.Git`) now detects when the remote rejects a ref update — non-fast-forward,
  a protected branch, a refusing pre-receive hook — via `PushOptions.OnPushStatusError`, and throws
  instead of returning as if the push had succeeded. Previously `Network.Push` returned normally on a
  server-side rejection, so the host reported "Sent/updated for review" and advanced the lifecycle
  even though the reviewer never received the commits.
- `PushBranch` (`SpecDesk.Git`) no longer falls back to `DefaultCredentials()` for a non-GitHub push
  endpoint. That value hands over the current Windows user's Negotiate/NTLM session; a repository whose
  `pushurl` is re-pointed at an attacker-controlled host (e.g. a shared/zipped copy with an edited
  `.git/config`) would have triggered an NTLM challenge/response from the author's session. Any
  endpoint other than HTTPS `github.com` is now refused outright before a credential is ever offered.
- `BeginEdit` (`SpecDesk.Git`) now refuses — throwing `DirtyWorkingTreeException` — to start editing a
  document while the working tree has uncommitted changes that belong to a *different* branch. Editing
  forces a checkout that resets the whole working tree, not just the document being opened; previously,
  switching to "Edit" on document B while document A's autosaved-but-not-saved-as-a-version draft was
  still uncommitted on its own branch silently discarded A's unsaved work. Resuming the same
  document/branch is unaffected. The host now surfaces a plain-language error ("Another document has
  unsaved changes...") instead of losing the draft.
- `app://` asset requests containing an embedded NUL byte (e.g. a spec with `![x](a%00.png)`, which
  renders to `app://repo/a%00.png`) no longer crash the process. `AppAssetResolver` now rejects invalid
  path characters up front instead of letting `Path.GetFullPath` throw; `Program.ServeAsset` (the native
  WebView2 callback) also gained a catch-all safety net, so no exception can escape into the message
  pump regardless of cause — both fall back to the existing broken-resource response.
- Disk autosave (`HostController.RunDiskAutosave`) could resurrect a just-discarded draft as an
  uncommitted change on the published branch: it snapshots `(path, text)` under `_sync`, then writes
  under `_repoGate` without re-checking anything, and `Timer.Dispose()` does not wait for an
  already-firing callback. If that callback was queued behind `_repoGate` (e.g. an image insert
  holding it) while "Discard" ran and released the gate first, the queued write landed after Discard's
  revert. A monotonic `_draftGeneration` token, bumped under `_repoGate` immediately after a checkout
  (`OnEdit`/`OnDiscard`), is now re-checked against a `_textGeneration` companion tag — carried
  alongside `_text` and updated in the same `_sync` block every time `_text` is assigned — instead of
  against a live read of the counter. A live read could already reflect a checkout's bump before that
  checkout's own later `_text` update had caught up, letting a snapshot taken in that gap slip through
  with stale text; tagging the snapshot with the generation `_text` was actually written against closes
  that regardless of timing. `OnDiscard` also no longer detours through a separate `LoadFile` call —
  reading the reverted file while still holding `_repoGate` keeps that read from racing anything.
- "Discard" (`HostController.OnDiscard`) computed its lifecycle-transition check from an unlocked read
  of `_state`, then separately re-entered `_sync` only to check `_publishInFlight` — unlike
  `OnSendForReview`/`OnUpdateReview`, which already gate both atomically. A "Send for review" that fully
  settles (advancing `_state` to In review, then clearing `_publishInFlight`, in two further separate
  `lock(_sync)` acquisitions of its own) between those two reads could leave a stale-but-still-valid
  `next` unrechecked, so the flag-only check would find it already false and let Discard delete the
  local branch a just-opened pull request now depends on. `tryStep` is now re-derived inside the same
  `lock(_sync)` as the `_publishInFlight` check, matching the existing pattern.
- The semantic diff (`SpecDesk.Diff`/`SpecDesk.Markdown`) could not see a task-list checkbox toggle, a
  footnote body edit, or a definition-list body edit: `Projection.fs` fell into `| _ -> None` for the
  `TaskList`/`FootnoteLink` inlines and the `DefinitionList`/`FootnoteGroup` blocks Markdig's pipeline
  already parses (`Pipeline.fs` enables `UseTaskLists`/`UseFootnotes`/`UseDefinitionLists`), so a
  `- [ ]` → `- [x]` edit — or a footnote/definition body edit — projected to identical `Ast` content and
  the "Show changes" overlay reported no changes for a real, rendered edit. `Ast.Inline` gained
  `TaskListMarker`/`FootnoteRef` cases and `Ast.Block` gained `DefinitionList`/`Footnotes` cases (with
  `DefinitionItem`/`Footnote` records for their bodies); `Projection.fs` now projects all four instead of
  dropping them, and `AstDiff.fs`'s exhaustive `kindTag`/`blockText` matches cover the new cases.
- A `.spectool.toml` `[branch] pattern` with a `{date:FMT}` whose format specifier .NET does not
  recognize (e.g. `{date:q}`) threw `FormatException` straight out of `WorkflowConfig.expandOrDefault`
  — only TOML *parsing* was guarded, not token expansion — so "Edit" silently did nothing (its catch
  doesn't filter `FormatException`) and `OnSuggestBranchName`/`OnSuggestVersionNote` never replied (the
  webview's request timed out after 30s). Separately, `{date:}` (an empty format) expanded via the
  general date format (e.g. "07/04/2026 09:30:00 +00:00") — spaces and a colon, invalid in a git ref —
  and passed through unvalidated. Expansion is now wrapped in the same try/with style guard
  `ImageEngine.insertForHost` already uses on the image side, and a branch-name expansion is validated
  against a conservative git-ref-character check before being accepted; either failure falls back to
  the default pattern (itself validated the same way) instead of breaking the workflow. The
  commit-message template is unaffected — it is free text, not a ref.
- A document whose Markdown starts with a leading blank line or a leading link reference
  definition (no rendered node) made `md-blocks.ts` add a synthetic leading block, so
  `blocks.length` was permanently one more than the ProseMirror document's `childCount`. In the
  formatted (WYSIWYG) editor this meant every `getText()` took the whole-document fallback —
  reflowing hard-wrapped paragraphs, list markers and heading style on the very first edit, or
  even just switching from Formatted back to Code with no edit at all — and desynced the
  block-index-keyed active/hover highlight and scroll-sync mapping by one block. Leading blank
  lines and reference definitions now fold into the first real block's own "head" content instead
  of forming a block of their own, so `blocks.length` stays 1:1 with `childCount` and an unedited
  document round-trips byte-for-byte.
- A document checked out with CRLF line endings (`core.autocrlf=true`, the Windows Git installer
  default) had every line ending rewritten to LF the moment the author typed a single character.
  CodeMirror's editor model normalizes every line break to "\n" internally, so the text it reports
  back is always LF-only regardless of what was on disk, and the host wrote that text back verbatim.
  `HostController` now detects the document's dominant line-ending style from the raw file content at
  load (and after Discard re-reads the reverted file) and re-applies it at every disk-write site
  (Save, the idle disk-autosave, Save a version), so a CRLF file's untouched lines stay CRLF instead
  of every line in the next "Save a version"/PR diff being a spurious line-ending change.
- Editing a document in the formatted (WYSIWYG) view could silently delete every link reference
  definition (`[id]: url`) it contained. `md-splice.ts`'s whole-document fallback — taken whenever a
  top-level block is added or removed, e.g. simply pressing Enter to start a new paragraph — serialized
  only the ProseMirror document's nodes; a reference definition has no node at all (markdown-it resolves
  it into its reference map instead), so it vanished outright, turning any reference-style link that used
  it into a plain inline link and dropping unused definitions entirely. The fallback now re-appends
  whatever such non-node content the original file had, verbatim, as a trailing section, so a definition
  survives the fallback (repositioned to the end of the file) instead of disappearing.
- Fixing a typo in a table cell in the formatted (WYSIWYG) view rewrote the whole table with no column
  alignment at all, even when the original had one (e.g. a right-aligned numbers column). The schema
  had nowhere to keep a column's GFM alignment (`:---`, `---:`, `:---:`), so re-serializing an edited
  table always emitted a plain `---` separator regardless of what the source had. `table_cell` now
  carries an `align` attribute (parsed from markdown-it's own per-column `text-align` style), and the
  table serializer writes the matching separator marker back, so a text-only cell edit no longer
  strips the table's alignment.
- Clicking a formatting-toolbar button (heading, list, quote, code) with the caret at the very start of
  a document that itself begins with a blank line (e.g. right after Ctrl+Home) crashed with
  `RangeError: Invalid change range 1 to 0`. The "find the current line's start" computation searched
  for a newline just before the caret; at the document's first position that search point clamps to 0
  instead of "before the string", so it wrongly matched the document's own leading newline and produced
  an inverted edit range. The caret-at-0 case is now handled directly, so every block-format command
  works from the very start of such a document instead of throwing.
- Pasting clipboard content that carried both an image and non-empty plain text (an Excel cell, a Word
  snippet, a screenshot with alt/HTML text, …) inserted BOTH — the pasted text (from CodeMirror's own
  default paste handling) and a stray image link, since the image capture listener had no way to know
  the paste had already been handled by the time it ran. It now skips capturing an image whenever the
  clipboard also carries non-empty `text/plain`, deferring to whatever CodeMirror already inserted so a
  single paste yields exactly one representation.
- The native Markdown pipeline (preview, semantic diff, comment anchoring) parsed `~~struck~~` as
  literal text — it didn't enable Markdig's strikethrough extension, even though the formatted
  editor's toolbar already emits that exact syntax for its strikethrough button. The preview showed
  literal tildes instead of struck-through text, and comparing the native and formatted views'
  flattened text reported a phantom word-level edit on every strikethrough word, even with no real
  change. The pipeline now parses `~~…~~` as strikethrough (rendering as `<del>…</del>`), projected as
  its own `Ast.Strikethrough` case (kept distinct from bold, which shares the same delimiter count)
  and flattened mark-free like the other inline styles.
- Pasted-image insertion had two related robustness gaps in `ImageEngine.fs`. First, de-duplication
  matched an existing file only by its hash8-suffixed name, never its content, and the write itself was
  a single non-atomic `File.WriteAllBytes` — a crash or power loss mid-write could leave a truncated
  file under that exact name, which every later insert of the same image would then silently "reuse"
  forever. The write now goes through a same-directory temp file plus rename, so the final name only
  ever appears once the write is complete. Second, an image folder pattern that expands to a path
  containing a space, parenthesis, or `#` produced an invalid or wrongly-resolved Markdown link (e.g.
  `![image](../my images/x.png)`, which most renderers stop parsing at the space). Such characters (and
  a literal `%`, escaped first to keep the scheme unambiguous) are now percent-encoded in the emitted
  link, while the on-disk path and returned `RelativePath` stay as before.
- Pasting or dropping an SVG that starts with a UTF-8 byte-order mark (BOM) — common from editors and
  export tools — was rejected with "Could not read the image". The BOM decodes to U+FEFF, which plain
  `TrimStart()` does not remove (.NET does not treat it as whitespace), so neither the `<svg` nor the
  `<?xml` prefix check matched and the file fell through to the raster decode path, which cannot read
  SVG at all. The BOM is now stripped before that check, so a BOM-prefixed SVG is recognized and passed
  through unchanged, like any other SVG.
- `.spectool.toml`'s hand-rolled reader (`Toml.fs`) mistracked quoted values containing an escaped
  quote (`\"`): its quote tracker flipped on every literal `"`, treating an escaped one as a real close,
  so — depending on how many escaped quotes came before it — a `#` or `]` that was actually still
  inside the string could be read as bare, silently truncating the value (e.g. `template = "a\"#b"`
  lost everything from the `#` onward). Separately, `\"` was never un-escaped, so a value that DID
  round-trip still kept its literal backslashes in commit text. Quote tracking is now escape-aware, and
  quoted values un-escape `\"`, `\\`, `\n`, `\t`, and `\r`.
- Editing only the *formatting* of a list item or table cell (e.g. toggling bold, with no word actually
  added or removed) reported an empty diff base for the whole list/table. `childDiff` compares rows/items
  by their flattened (mark-stripped) text, so a formatting-only edit leaves every child looking identical
  and finds nothing to highlight — but the top-level diff still classifies the container as changed (its
  real, mark-aware content differs), and the wire entry carried neither a per-row diff nor a base to
  word-diff against. The container's whole-block plain text is now supplied as a fallback in that case, so
  the review overlay has something to compare against instead of an empty base.
- The semantic diff's block matching (an O(m·n) LCS, plus scoring every same-kind base×head pair by text
  similarity) had no size limit — a pathologically large document (a changelog/glossary with thousands
  of near-identical entries) could make comparing two versions take many seconds or exhaust memory,
  since neither cost is bounded by the document's actual size. Above 4,000,000 base×head node pairs, the
  diff now skips that matching entirely and reports a flat "everything removed, everything added"
  listing instead — correct but coarse, and a deliberate, documented trade-off against hanging or running
  out of memory on documents far beyond any realistic size.
- A definition list's body (the implicit `<dd>` paragraph under a `Term`/`:` definition) got a
  `data-line-*` scroll-sync attribute stamped onto it, but Markdig's own HTML renderer never actually
  writes that attribute for a `<dd>` — only for the `<dt>` term — so the internal line map ended up with
  more entries than the rendered HTML had matching attributes for, a mismatch no other supported block
  family has. Only a definition's term is anchored now, matching what Markdig actually renders.
- `IGitHubAuth.AwaitAuthorizationAsync`'s doc comment claimed a token-persistence failure after a
  successful authorization surfaced as a thrown exception; the implementation always returned it as a
  normal `SignInResult` (`SignInOutcome.StorageFailed`), never throwing. The doc now states the actual,
  already-correct contract, so callers don't wrap this call in a try/catch that can never trigger.
- Confirmed that `SaveVersion`/`Initialize`'s `Commands.Stage(repo, "*")` already respects `.gitignore`
  (`StageOptions.IncludeIgnored` defaults to false, the same default `git add -A` uses) — a repository
  whose `.gitignore` lists a build-artifact directory does not have it swept into a saved version. Pinned
  with a regression test and a code comment so the behaviour can't silently regress.
- `TryAdvanceReview` keyed a completing Send/Update-review push only on `(state, branch name)`. Since
  branch names are date-deterministic, a draft discarded and recreated the same day (e.g. by reopening
  the document while the previous push was still resolving in the background — `LoadFile` resets the
  draft fields without checking whether a publish is in flight, unlike Discard, which refuses outright)
  could reproduce the exact same `(state, branch)` pair the stale push captured. The stale push would
  then wrongly jump the brand-new, never-sent draft straight to "In review" and stamp its own unrelated
  version as already shared, so its own later "Update review" falsely reported "No new versions". The
  check now also compares the existing per-checkout generation counter, which a discard/recreate always
  advances, so a stale push can no longer land on a draft it didn't itself publish.
- A cancelled GitHub sign-in's background task could close a *newer* sign-in's device-code prompt.
  `OnGitHubSignIn` cancels the previous flow's token before starting a new one but does not wait for it
  to unwind, and that stale flow's cancellation fallback (`SendCurrentAccount`, folding its own
  cancellation to a plain signed-out state) fired unconditionally whenever its token was cancelled — even
  after a newer flow had already replaced `_signInCts` and shown its own code. The stale flow's
  unconditional "signed out" frame then reached the webview after the newer flow's `GitHubCode`, closing
  its still-pending prompt. The fallback now only emits when `ReferenceEquals(_signInCts, cts)` still
  holds for that flow, so a stale, already-superseded cancellation stays quiet.
- `SampleRepo.EnsureSeeded` treated `welcome.md`'s mere existence as proof that the bundled sample copy
  had fully completed, but `Directory.GetFiles` makes no copy-order guarantee, so a crash partway through
  copying (after `welcome.md` happened to land but before every sibling file did) would permanently skip
  re-seeding on every later launch, leaving the repo committed with a partial tree. Completion is now
  recorded by a dedicated marker file, written last via a temp file + atomic rename, and re-seeding is
  gated on that marker (or an already-versioned repo) instead of `welcome.md` alone, so an interrupted
  copy is retried in full on the next launch.
- `OnReady` reloaded `_initialDocPath` from disk on every "ready" event, not just the first. A WebView2
  recovery or a page reload re-fires "ready", so this could silently switch the author away from the
  document they currently had open (discarding any in-progress draft on it) and re-stamp the reloaded
  file back to Published. "Ready" now only auto-loads the initial document once.
- `PhotinoFileDialogs.OnUiThread` (`Program.cs`) could block its calling thread forever: Photino's
  native `Invoke()` blocks on an untimed condition variable and never checks whether its `PostMessage`
  actually reached a still-alive window, so a window torn down between the check and the post left
  nothing to ever wake the wait. The window's closing handler now arms a short (2s) grace period the
  first time closing begins; `OnUiThread` abandons the wait (returning as if cancelled) once that grace
  period elapses instead of hanging indefinitely, while a dialog still in flight when closing starts
  keeps its normal, unbounded wait until then.
- `wordDiff`'s (webview) pathological-input guard now caps the actual LCS-table cost (the product of
  token counts), not just raw character length: an adversarial input alternating single-character tokens
  and whitespace (e.g. "x x x x...") could turn a modest-length string into thousands of tokens, blowing
  past the char-length cap and allocating/filling a many-million-cell table. Also removed an unreachable
  second `flushDel()` call left over from an earlier refactor, which recomputed nothing but was dead code.
- The change-annotation pill on the Formatted pane's first block (`top: -0.72rem`, above the block it
  labels) no longer gets clipped off by the pane's own top edge when that first block is the changed
  one — `#formatted` now reserves top padding for it. `.review-meta`/`.review-state` in the "My reviews"
  panel now shrink with an ellipsis instead of overflowing the row on a long `owner/name`, and
  `#reviews-panel-head` gains `flex-wrap` per the design concept's "reflow rather than overflow" rule.
- The Code and Formatted panes no longer disagree on how to bound the active-line highlight once the
  document shrinks out from under a synced (but now stale) line index — e.g. a Split mirror re-applying
  the last active line across a whole-document `setText` after the sibling pane deleted trailing content.
  The source editor (`editor.ts` `activeLineField`) previously pinned the highlight to the document's
  last line, while the Formatted pane (`formatted.ts` `blockIndexForLine`) already cleared it; the source
  editor now also clears it, so both panes agree.
- `IpcClient.on` (webview `ipc.ts`) now throws instead of silently replacing an already-registered
  handler for the same message kind. Previously a second `on()` call for a kind that already had a
  handler quietly disconnected the first one — every current caller registers each kind exactly once,
  so a re-registration is always a bug, and it now surfaces immediately instead of dropping a handler
  with no signal.

### Changed
- `webview/tests/reviews-panel.test.ts` and `webview/tests/preview.test.ts` no longer use an unchecked
  `as` cast to reach a typed DOM element/stub — they now follow the instanceof-narrowing helper pattern
  already used in `dialogs.test.ts` (throws locally if the test's own markup/stub ever drifts from what
  it's asserting against, instead of trusting an assertion that could silently paper over that drift).
- `Dialogs` (`webview/src/chrome/dialogs.ts`), `SignInController` (`webview/src/chrome/signin.ts`), and
  `ReviewsPanel` (`webview/src/review/reviews-panel.ts`) no longer query `document` directly for their own
  elements — they now receive them via constructor deps, the same injection pattern already used by
  `lifecycle-chrome.ts`/`segmented-control.ts`/`format-toolbar.ts`. `index.ts` (the sole caller) queries
  the elements once and passes them in; no observable behavior change. The three modules' tests now build
  their fake elements from the test markup and pass them in, instead of the module reaching into `document`
  itself.

### Security
- The stored GitHub token is now DPAPI-protected with app-specific additional entropy, not just plain
  `CurrentUser` scoping — raising the bar against another process running as the same Windows user that
  happens to know the token file's path from also being able to decrypt it. A token already saved by an
  earlier version (with no entropy) still decrypts after the upgrade, so existing sessions survive.

## [0.1.0] - 2026-07-04

First tagged release — the PoC-0 … PoC-5 milestones: the native↔webview editor, the
Markdown pipeline, the local version lifecycle, and the full GitHub review round-trip
(connect an account, Send for review, Update review, live status, and browse your reviews).

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
- PoC-5 — connect a GitHub account: a **Connect to GitHub** button signs you in without leaving the app.
  It shows a short code to enter on GitHub (one click opens the page), waits while you authorize, then
  shows **Sign out @your-handle**. A brief network blip is ridden out; an expired code or a connection
  problem is reported in plain words. The affordance appears only when the app is configured with a
  GitHub OAuth App client id (env `SPECDESK_GITHUB_CLIENT_ID` or a compiled default); this is the
  foundation for *Send for review* in a later step. Your access stays on your machine and is never shown.
- PoC-5 — send a draft for review on GitHub: with a draft open, **Send for review** publishes your
  working branch to GitHub and opens a pull request, then the document moves to **In review**. The
  request title is your last version note (or the document name); the body names the document. It needs
  a connected GitHub account and a GitHub remote — if either is missing, or the network fails, it says so
  in plain words and leaves the draft untouched. The access token is used only for the push and the API
  call and is never stored on the host side or written to logs. (The button shows only while drafting.)
- PoC-5 — update a review with newer versions: once a document is **In review**, save further versions and
  click **Update review** to push them to the same pull request — no second request is opened. It's always
  explicit (nothing is pushed on its own). The status itself follows GitHub's review decision (see below),
  so a change request stays **Changes requested** until the reviewer re-reviews. If nothing new has been
  saved since the last update it says so and pushes nothing, so an accidental click can't churn the review.
  Like *Send for review* it needs a connected account and a GitHub remote, reports any problem in plain
  words, and never stores or logs the access token. (The button shows only while a review is open.)
- PoC-5 — browse your reviews: once a GitHub account is connected, a **My reviews** button lists the open
  pull requests you authored or were asked to review (most recently updated first), each with its repo, your
  role, and its live status — click one to open it on GitHub. You can also open any review by pasting its
  link. Plain words throughout: a pull request is a "review". Loading is best-effort and reports a plain
  reason if it can't reach GitHub.
- PoC-5 — the review status now reflects GitHub: while a document is under review, the status shows the
  live decision — **In review**, **Changes requested**, or **Approved** — read from GitHub. It refreshes
  automatically while you're on the window (polled, and when you return to it — the usual "check the review
  on GitHub, come back" rhythm), so a reviewer approving or asking for changes is picked up without a manual
  step. A change request stays until the reviewer re-reviews; an approval covers only the versions that were
  reviewed, so updating the review with new versions returns it to **In review** (you never treat unseen
  content as approved). If the pull request is merged or closed on GitHub, SpecDesk keeps the last-known
  status rather than changing the document from under you — merging is a deliberate step. Reading the status
  is best-effort: a hiccup leaves the last-known one.
- PoC-5 — confirm the review's title and description before sending: **Send for review** now opens an
  inline prompt seeded with a suggested title (your last version note, or the document name) and a short
  description, which you can edit before the review opens — the outward-facing text is yours to confirm.
  Enter (or **Send for review**) submits; a blank title falls back to the generated one, and the
  description is optional.
- PoC-5 — assign reviewers when sending for review: reviewers listed under `[review] reviewers` in
  `.spectool.toml` (e.g. `["@alice", "@org/team"]`) are requested on the pull request as soon as it opens.
  The special value `"codeowners"` defers to the repository's own CODEOWNERS (GitHub requests them
  automatically); explicit entries override it. Assignment is best-effort — if a reviewer can't be
  requested (not a collaborator, a team needing extra permissions, a network blip) the review still opens
  and the author can add reviewers on GitHub.

### Changed
- Image metadata stripping is now documented as automatic rather than a config toggle. The
  `[images] strip-metadata` key is removed: re-encoding a pasted image (the default) always drops
  EXIF/XMP/ICC — so EXIF/GPS never leak from screenshots — while the verbatim pass-through formats
  (SVG, GIF) keep their bytes. The key had no runtime effect (its value was never read), so behaviour
  is unchanged; an unknown key in `.spectool.toml` is simply ignored.
- UI restyled to the agreed design concept (`docs/design/SpecDesk-Design-Concept.md`): a CSS
  design-token system now drives every surface (light, warm, and dark token sets); the rendered
  preview reads like a typeset document (serif headings, hairline tables, a soft accent caret-block
  highlight in place of the former yellow); the CodeMirror editor gains a token-based theme with
  markdown syntax colours; and the toolbar buttons, the Code/Split/Formatted segmented control, the
  inline prompt bars, and the status badge are rebuilt from the shared component styles.
- The toolbar's lifecycle status dot now reflects the state by colour (in review, changes requested,
  approved, draft, published) using the design concept's per-state token family, instead of staying an
  inert grey; the file path and error messages keep the neutral dot.
- The toolbar's view switch (Code / Split / Formatted) is now an ARIA radiogroup with full keyboard
  support — a single tab stop, arrow keys move and select, and `aria-checked` tracks the choice —
  matching the design concept's segmented-control spec (§7/§11), rather than three independent toggles.
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
- Send for review no longer strands a document in Draft when a review already exists for it: if the
  pull request was opened earlier (e.g. sent, app restarted, then re-sent the same day), GitHub's
  "a pull request already exists" is now reconciled to **In review** instead of a misleading "check
  your connection" error.
- A sign-in that authorizes on GitHub but can't save the token on this device (a disk/encryption fault)
  now says exactly that, instead of the wrong "Couldn't reach GitHub". An unreadable saved token (denied
  by file permissions, say) now reads as "signed out" rather than faulting the account display.
- Discarding a draft is refused while it is being sent for review, so a race can't delete the local
  draft after the pull request has already opened (which would orphan it on GitHub). Saving a version
  at the moment a send completes no longer reverts the status back to Draft.
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
