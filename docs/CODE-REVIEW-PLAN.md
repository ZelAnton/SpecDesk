# SpecDesk — Code Review Findings & Remediation Plan

> **Status:** findings only — nothing in the code has been changed. This is a triage/plan document.
> **Scope:** the *code* of the whole repo at release `v0.1.0` (documentation was deliberately out of
> scope). Reviewed by six parallel adversarial passes: F# domain (`Core`/`Markdown`/`Diff`),
> `Git`/`GitHub` C#, `Host` C#, webview editor core, webview UI/sync/chrome, and repo
> structure/architecture.
> **Method note:** the six most severe findings (S-01, S-02, S-04, S-06, S-08, S-13) were
> re-verified directly against the source after the review; each is marked **[verified]** below. The
> rest are as reported by the review pass and worth a quick confirm before the fix lands.
> **Context:** the product has no users yet, so structure/architecture/interfaces are all freely
> changeable — the [Structure] tier proposes moves that would be expensive later.

## How to read a finding

Each has: an ID, a one-line defect, the `path:line`, a concrete failure scenario, and a suggested
direction. IDs are stable so you can say "do S-04 and S-06 first". Severities:

- **[Serious]** — a real bug, security defect, or data-loss/correctness failure with an articulable trigger.
- **[Minor]** — robustness gap, smell, house-style violation, or an unconfirmed suspicion (phrased as a question).
- **[Structure]** — simplification / architecture / repo-shape opportunity; no defect.

---

## Triage summary

**14 Serious**, ~30 Minor, ~15 Structure. The Serious tier clusters into five themes:

| Theme | Findings | Gist |
|---|---|---|
| **Block-splice / round-trip integrity** | S-11, S-12, S-13, S-14, S-15 | CRLF normalization defeats minimal-diff; fallback serialize deletes content; leading blank line breaks alignment; table alignment stripped; caret-at-0 crash |
| **Git push / credential safety** | S-01, S-02 | Rejected push reported as success; Windows SSO creds leaked to non-GitHub push endpoints |
| **Concurrency / lifecycle races** | S-05, S-06 | Autosave resurrects discarded draft; Discard orphans a just-opened PR |
| **Crash / DoS from document content** | S-04 | `app://…%00…` crashes the process |
| **Diff blindness & config faults** | S-08, S-09, S-03 | Semantic diff blind to task/footnote/deflist edits; invalid `{date:}` breaks Edit; forced checkout loses autosaved work |

**Suggested first cut (highest leverage / lowest effort):** S-01, S-02, S-04, S-08, S-09 (each is
small and self-contained), then the block-splice cluster S-11..S-15 (shared root causes: CRLF and the
leading-blank-line alignment invariant), then the two races S-05/S-06.

---

## SERIOUS

### Git / GitHub

**S-01 — [verified] `PushBranch` reports success when the server rejects the ref update.**
`src/SpecDesk.Git/LibGit2DocumentVersioning.cs:229-248`. `PushOptions` sets `CredentialsProvider` and
`OnPushTransferProgress` but **not `OnPushStatusError`** — LibGit2Sharp reports a per-ref rejection
(non-fast-forward, protected branch, declining pre-receive hook) *only* through that callback and
`Network.Push` returns normally. Failure: the draft branch already exists on the remote with divergent
history (two machines, or two authors the same day — default draft names are date-to-the-day
deterministic, so collisions are by design) → GitHub rejects → `PushBranch` returns clean →
`HostController` (`:840`, `:954`) reports "Sent/Updated the review" and advances the lifecycle while the
reviewer never receives the commits. The interface doc (`GitPublishing.cs:30-31`) actively claims it
throws on failure. **Direction:** set `OnPushStatusError` (and/or inspect push results), throw on any
rejected ref. Add a rejected-push test (current test only pushes to a fresh local bare repo).

**S-02 — [verified] Non-GitHub push endpoint receives the user's Windows SSO credentials.**
`src/SpecDesk.Git/LibGit2DocumentVersioning.cs:238-241`. The credentials callback returns
`new DefaultCredentials()` for any non-`github.com` URL — this is libgit2's `GIT_CREDENTIAL_DEFAULT`
(Windows Negotiate/NTLM of the logged-in user), directly contradicting the adjacent comment ("gets no
credential"). Failure: a repo whose `.git/config` `pushurl` points at an attacker host (a repo received
as a zip/shared folder — the exact threat the comment contemplates) → "Send for review" → attacker
answers `401 WWW-Authenticate: NTLM` → libgit2's WinHTTP transport performs the NTLM handshake as the
author → NetNTLMv2 challenge/response captured or relayed. §7's token hardening holds, but this leaks a
*different, more valuable* credential. **Direction:** return `null` (or throw) instead of
`DefaultCredentials()` so a non-GitHub push simply fails.

**S-03 — Forced checkout in `BeginEdit` destroys autosaved-but-uncommitted work (cross-document).**
`src/SpecDesk.Git/LibGit2DocumentVersioning.cs:101-108` with `src/SpecDesk.Host/HostController.cs:1674-1685`.
The forced checkout resets the whole working tree, not just the doc being edited. Failure: draft A,
autosave writes disk-only edits ("Saved" in the UI), never "Save a version"; open doc B (host resets to
Published, leaves A's branch checked out dirty); click Edit on B → forced checkout resets A's file to
its committed tip, and no editor buffer holds A's text → unrecoverable. Collides with the product's
"Saved (continuous, automatic)" promise. (See also S-07, the same root across a restart.) **Direction:**
refuse/stash when dirty paths ≠ the document being edited, or persist unsaved buffers before checkout.

### Host — crash / concurrency / lifecycle

**S-04 — [verified] `app://` request with an encoded NUL crashes the process.**
`src/SpecDesk.Host/AppAssetResolver.cs:34` decodes `uri.AbsolutePath` (`%00` → `\0`) and `:51` passes
it to `Path.GetFullPath`, which throws `ArgumentException` on an embedded null. `Program.cs:100-123`
(`ServeAsset`) catches only `IOException`/`UnauthorizedAccessException`, despite its own comment "must
never let an exception escape into the message pump." Failure: a spec containing `![x](a%00.png)`
renders → webview requests `app://repo/a%00.png` → exception propagates through the WebView2
reverse-P/Invoke callback → crash, from hostile *document content*. **Direction:** catch broadly (or
validate for control chars) in `ServeAsset`; add an invalid-path-character test and a `ServeAsset`
no-throw test.

**S-05 — Autosave disk write is not re-validated after acquiring `_repoGate` — a discarded draft can be resurrected onto the published branch.**
`src/SpecDesk.Host/HostController.cs:603-646` (`RunDiskAutosave`) snapshots `(path,text)` under `_sync`
then writes under `_repoGate` with no re-check; `OnDiscard` cancels the timer but `Timer.Dispose()`
does not drain an in-flight callback, and .NET monitors are not FIFO. Failure: an image insert holds
`_repoGate`; a fired autosave callback passes its state check and queues on the gate; Discard queues too
and acquires first (force-checkout of base + branch delete), then the callback writes the draft text
over the reverted file → "discarded" content persists as a dirty working copy on the *published* branch
while the UI shows Published. This is the direct successor of the already-fixed "autosave vs document
switch" race. **Direction:** re-validate (generation token or state+path identity) inside `_repoGate`,
or drain via `Timer.Dispose(WaitHandle)` in `CancelAutosave` (see also T-13 immutable session record).

**S-06 — [verified] `OnDiscard`'s lifecycle gate is not atomic with its publish-in-flight check — Discard at the send's settle edge can orphan the just-opened PR.**
`src/SpecDesk.Host/HostController.cs:509` computes `Lifecycle.tryStep(_state,"discard")` from an
*unlocked* read; the `lock(_sync)` at `:517-530` checks only `_publishInFlight`. In `RunReviewPublish`,
`TryAdvanceReview`, `SendLifecycleStatus`, and `ClearPublishInFlight` are three separate `_sync`
acquisitions (`:1191-1222`), so there is a window where state is already InReview and the flag already
cleared; a Discard whose stale read saw Draft deletes the local branch whose PR just opened.
`OnSendForReview`/`OnUpdateReview` already gate transition + claim atomically under one lock — `OnDiscard`
predates that discipline. **Direction:** compute `tryStep` inside the same `_sync` block as the
`_publishInFlight` check.

### F# domain — config fault / diff blindness

**S-08 — [verified] Semantic diff is blind to task-list, footnote, and definition-list edits (the shipped "Show changes" overlay's only data source drops them).**
`src/SpecDesk.Markdown/Projection.fs:14-30, 41-66`. `Pipeline.fs` enables `UseTaskLists`,
`UseFootnotes`, `UseDefinitionLists`, but `TaskList` inlines, `FootnoteLink`/`FootnoteGroup`, and
`DefinitionList` blocks all fall through the `| _ -> None` arms (verified: no arms exist for them).
Failures through the shipped PoC-6 overlay: toggling `- [ ] x` → `- [x] x` projects to identical
`ListBlock`s → **"no change at all"** shown for a real, rendered edit; editing a footnote body or a
definition term/definition is invisible to the diff. **Direction:** project these constructs into the
AST (extend the `Ast` DU or map task state onto list items); add task/footnote/deflist diff tests.

**S-09 — [verified] An invalid `{date:FMT}` in `.spectool.toml` throws out of the "never break" facades and silently disables Edit.**
`src/SpecDesk.Core/Tokens.fs:32` (`ctx.Date.ToString(fmt, …)`) is called by
`WorkflowConfig.expandOrDefault` (`:63-68`), which is *not* wrapped in `try/with` (only the TOML *read*
is). Failure: `[branch] pattern = "spec/{docSlug}-{date:q}"` → `FormatException` escapes
`branchNameForHost`/`commitMessageForHost`; `OnEdit`'s catch filters only
`LibGit2SharpException|InvalidOperationException`, so Edit silently no-ops, and
`OnSuggestVersionNote`/`OnSuggestBranchName` never reply (webview waits out its 30 s timeout).
Sub-case: `{date:}` (empty format) expands via "G" to `07/04/2026 09:30:00 +00:00` — spaces/colons are
illegal in git refs but `expandOrDefault` accepts it (checks only leftover `{` / blank) → every Edit
fails with the generic error. Contradicts the module's own "invalid config must never break the
workflow" doc. **Direction:** wrap expansion in the same guard `ImageEngine.insertForHost` already has;
validate the expanded ref, fall back to default on any failure; add an invalid-`{date:FMT}` test.

### Webview — block-splice / round-trip integrity

**S-11 — CRLF documents: every `editor.changed` silently rewrites the whole file to LF, voiding the minimal-diff design for the default Windows checkout.**
`webview/src/editor.ts:528-530` (`getText` = `doc.toString()`, verified LF-joined) — CodeMirror 6
normalizes line separators on input, so the full LF text is echoed to the host ~120 ms after load and
on every edit; the host writes it verbatim (`HostController.cs:603-631`, `File.WriteAllText`) and
nothing re-instates CRLF. Failure: repo cloned on Windows with `core.autocrlf=true` (installer default)
→ `.md` is CRLF → Edit → type one character → autosave rewrites every line ending → "Save a
version"/PR diff touches every line — exactly the whole-file-diff block-splice exists to prevent. No
`\r`/CRLF fixture exists anywhere in `webview/tests/`. **Direction:** detect and preserve the
document's dominant EOL (capture at load on the host or webview side; re-apply on write); add CRLF
round-trip fixtures for `md-blocks`/`md-splice`.

**S-12 — The whole-document fallback serialize silently *deletes* link-reference definitions.**
`webview/src/md-splice.ts:44-54`. On block-count mismatch the fallback is `serializer.serialize(edited)`;
ref-defs have no ProseMirror node (markdown-it resolves them into the parser env), so the output drops
every `[id]: url` line, converts used reference links to inline, and deletes unused definitions. §5
documents that add/remove-block *falls back to a full re-serialize* — it does **not** license content
loss. Trigger is mundane: press Enter in the WYSIWYG to start a new paragraph in any doc with a ref-def
→ definitions vanish from the saved Markdown. The fallback test asserts nothing about preservation.
**Direction:** carry ref-defs (and other env-resolved constructs) through the fallback verbatim, or
refuse the fallback when the source contains them; add a fallback-preserves-content assertion.

**S-13 — [verified] A leading blank line (or a doc starting with a ref-def) permanently breaks 1:1 block↔node alignment — every WYSIWYG edit, and even a no-edit Formatted→Code switch, reflows the entire document.**
`webview/src/md-blocks.ts:91-94` unshifts a synthetic block at line 0 when the first token starts later,
so `blocks.length = childCount + 1` forever and `md-splice.ts:52` takes the whole-doc fallback on every
`getText()`. Consequences: (a) one keystroke rewrites the whole file (hard-wrap reflow, list-marker
rewrite, setext→ATX, table realignment, plus S-12's ref-def deletion); (b) `index.ts:399-403` reads
`formatted.getText()` when leaving Formatted mode and overwrites the source editor with the reflowed
text — the user sees a changed document without editing; (c) `formatted.ts:395-404` indexes `blocks[i]`
parallel to PM children, so highlights/hover/scroll-sync are off by one block. Every test fixture
conveniently starts with a heading. **Direction:** make the synthetic leading block a real, addressable
node (or offset the block↔child mapping so it stays 1:1); add leading-blank-line and leading-ref-def
fixtures. **This and S-11/S-12 share the block-splice root — plan them together.**

**S-14 — Editing any table cell's text discards the table's column alignment.**
`webview/src/pm-markdown.ts:63-72` (parser ignores markdown-it's alignment attrs — no `align` in the
schema) and `:104-107` (serializer always emits `| --- |`). Cell-*text* editing is in scope for v1
(only *structural* edits are deferred per §5), so: fix a typo in one cell of a right-aligned column →
whole table rewritten with all alignment stripped. Pinned nowhere (only an unaligned table is tested).
**Direction:** carry alignment as a cell/column attribute through parse→serialize; add an aligned-table
round-trip test.

**S-15 — Block-format toolbar command throws with the caret at position 0 of a doc whose first character is a newline.**
`webview/src/md-format.ts:114-118` — for `from=0` with `doc[0]==="\n"`, the computed edit is
`{from:1, to:0}`; `editor.ts:498-505` dispatches it unguarded → CodeMirror throws
`RangeError: Invalid change range 1 to 0`, an uncaught exception in the toolbar click handler. Trigger:
doc with a leading blank line, Ctrl+Home, click H1/H2/bullet/ordered/quote/code. **Direction:** clamp
the range / guard the empty-line case; add a caret-at-0 test.

### Webview — input

**S-16 — One Ctrl+V can insert the pasted content twice (text + image) in an editing state.**
`webview/src/image-capture.ts:55-76`. CodeMirror's own paste handler is registered on the same
`contentDOM` *before* `attachImageCapture`, so it inserts `text/plain` and `preventDefault()`s first;
image-capture then independently finds an `image/*` item and fires `image.paste`, whose host reply
inserts a Markdown image link. For clipboards carrying *both* flavors — copying cells from Excel, a Word
fragment, an image+HTML browser selection — the author gets the text **and** an image link.
image-capture's own `preventDefault()` (`:74`) runs too late. Office-style paste is exactly this
product's core gesture. **Direction:** skip image capture when a non-empty `text/plain` flavor is
present, or intercept in the capture phase and choose one flavor; add paste/drop wiring tests (only
`stripDataUrlPrefix` is currently tested).

---

## MINOR

### F# domain
- **M-01** `Pipeline.fs:13-22` — native parser lacks `UseEmphasisExtras`, so `~~strike~~` (which the shipped toolbar emits) parses as literal text natively → phantom `~~`-word edits in the Formatted word-diff and literal tildes in the native preview. Strike is shipped, not a listed v1 limit.
- **M-02** `ImageEngine.fs:135-142,164` — dedup matches by filename suffix only (content never verified; `hash8` is 32-bit) and `File.WriteAllBytes` is non-atomic, so a truncated `…{hash8}.png` from a mid-write kill is "reused" forever. Use temp-file + rename, or re-hash the match.
- **M-03** `ImageEngine.fs:80-84` — emitted link isn't valid CommonMark when the relative path contains a space/`(`/`#` (folder comes verbatim from config): `![image](../my images/x.png)` renders literally. Wrap in `<…>` or percent-encode.
- **M-04** `ImageProcessing.fs:62-70` — a UTF-8-BOM'd SVG is rejected (`﻿` isn't stripped by `TrimStart()`, so `StartsWith "<?xml"` fails → raster path → "Could not read the image").
- **M-05** `Toml.fs:13-44,121-125` — escaped quotes `\"` flip the quote tracker and `unquote` never unescapes, so `template = "Say \"hi\""` mis-tracks a later `#`/`]` and returns literal backslashes into the commit message. Is escape support deliberately out of scope?
- **M-06** `DiffWire.fs:155-164` — a formatting-only change inside a list/table emits `Changed` with empty `Children`/`BaseText`, so the webview word-diffs against an empty base and washes the whole table instead of the changed row. Emit the container branch with plain-block fallback texts.
- **M-07** `AstDiff.fs:87,146-158` — unbounded O(m·n) LCS memory + all-pairs similarity list; a pathological large doc (glossary/changelog with many near-identical blocks, or a several-thousand-row table) can freeze/OOM the host. Cap size / fall back to Removed+Added.
- **M-08** `Renderer.fs:115-136` — (question) do definition lists keep the `LineMap`↔`data-line` count invariant? Markdig's `<dt>`/implicit-`<dd>`-paragraph attribute emission is unverified and untested (the one enabled block family without a regression test).

### Git / GitHub
- **M-09** `IGitHubAuth.cs:106-107` — doc says token-persistence failure surfaces as a thrown exception, but `CompleteAuthorizationAsync` returns `SignInResult.StorageFailed()` (doesn't throw). Stale doc could prompt a redundant host-side try/catch.
- **M-10** `GitHubReview.cs:447-465` — (question) the per-request 30 s CTS plus the refresh path's *second* timeout (`HostController.cs:1002-1003`) — confirm the double-cancel doesn't produce a confusing classification. Connect/handshake stall is bounded only by the OS socket timeout (same limitation as PushBranch).
- **M-11** `TokenStore.cs:36-41` / `TokenProtector.cs:22-40` — token file relies solely on DPAPI `CurrentUser` (no filesystem ACL tightening, `optionalEntropy = null`); readable by any process in the same session. Acceptable per §7, but app-specific entropy would harden against another same-user app that knows the path.
- **M-12** `LibGit2DocumentVersioning.cs:55,122` — `Commands.Stage(repo, "*")` in `Initialize` (and `SaveVersion`) stages everything with no `.gitignore` guarantee beyond libgit2 defaults; a repo containing a build-output dir would commit it into the version.

### Host
- **M-13** `HostController.cs:1235-1248` — `TryAdvanceReview` keys the draft on `(state, branch-name)`; a same-named draft recreated mid-publish gets mis-stamped, making the next Update review falsely report "No new versions". A monotonic draft-generation token would fix it (see T-13).
- **M-14** `HostController.cs:1578,1593` — a cancelled sign-in flow emits an uncorrelated signed-out frame that closes a *newer* flow's device-code prompt. Guard the fallback emit with `ReferenceEquals(_signInCts, cts)`.
- **M-15** `HostController.cs:299-308` — a re-fired `ready` (WebView2 recovery / page reload) reloads `_initialDocPath`, switching the author away from the open document and re-stamping Published. Add an "already loaded once" latch?
- **M-16** `HostController.cs:1674-1691` — lifecycle state is in-memory only; restart mid-draft shows Published while HEAD is on the draft branch, and the next Edit's forced checkout loses autosaved typing (cross-session variant of S-03).
- **M-17** `LogBridge.cs:35-54` — untrusted webview `Message`/`Data` written verbatim into the shared rolling log; embedded newlines forge log records (the `{Message:lj}` template renders raw). Diagnostic-file impact only; no token leakage found.
- **M-18** `LogBridge.cs:77` — `"Could not export log: {ex.Message}"` surfaces a raw exception message in the author-facing channel (the one non-plain-language author-visible string in Host).
- **M-19** `SampleRepo.cs:28-40` — partial-seed hazard: the sentinel is `welcome.md` existence, but copy order is filesystem-dependent, so a mid-copy fault after `welcome.md` lands skips re-copy forever and the next `Initialize` commits the partial tree.
- **M-20** `Program.cs:157-176` — `PhotinoFileDialogs.OnUiThread` blocks the message thread with no timeout; if the window tears down before `Invoke` runs, the pump can wedge. Does Photino guarantee the invoke queue drains on close?

### Webview
- **M-21** `image-capture.ts:33-46` + `index.ts:454-470` — insert `pos` is captured at paste time but applied after the async host round-trip; interim typing displaces the insert point, and multiple pasted files share one `pos` so out-of-order replies scramble order. Map the position through document changes at reply time.
- **M-22** `prompt-bar.ts:26-41` — a second `open()` inside the close-during-flight window is swallowed by the `opening` latch (dead click, no request); self-heals on the next click.
- **M-23** `reviews-panel.ts:46-67` — a rejecting `requestReviews` leaves the panel stuck on "Loading your reviews…" forever (the rejection escapes as an unhandled rejection; only index.ts's never-rejecting wrapper saves it today). Add a `catch` rendering the fallback.
- **M-24** `dialogs.ts:100-158` — Escape works only while focus is in a text field; a keyboard user on Confirm/Cancel gets nothing.
- **M-25** `word-diff.ts:48` — the guard bounds chars (4,000) not tokens²; worst-case alternating tokens → ~16M-entry LCS (~128 MB transient), computed twice per changed block. Add a token-count cap.
- **M-26** `word-diff.ts:106` — dead code: the second `flushDel()` can never fire.
- **M-27** `scroll-geometry.ts:33-36` — no `span==0` guard in `scrollTopForLine` (its inverse guards `height===0`); a zero-line block yields `NaN` → pane jumps to top. Can markdown-it emit `map[1]===map[0]` for a top-level token?
- **M-28** `log.ts:12-18` — `JSON.stringify(data)` throws on circular/BigInt data, crashing the caller. Also missing `tests/log.test.ts` and `tests/raf.test.ts` despite the 1:1 convention.
- **M-29** `styles.css:1016-1033` — the change-annotation pill (`::before` at `top:-0.72rem`) clips at the pane top when the first block is a changed paragraph (`#formatted` has no top padding).
- **M-30** `styles.css:487-500` — `.review-meta`/`.review-state` are `nowrap` with no shrink/ellipsis, so a long `owner/name` overflows the review row; `#reviews-panel-head` (`:409`) lacks `flex-wrap` (against design §12).
- **M-31** `index.ts:483-486` — a malformed (decoder-`null`) `diff.result` becomes an *empty* diff → false "no changes to show" (every other handler drops a `null` payload).
- **M-32** `index.ts:254-279` — cross-pane 120 ms debounce race: editing pane B before pane A's debounce fires can clobber A's not-yet-reported edit via the silent `setText` mirror.
- **M-33** `md-format.ts:157-174` — line-prefix toggles double-prefix mixed selections (`1. a`+`b` → `1. 1. a`; `- - a`); all-or-nothing toggle amplifies instead of normalizing.
- **M-34** `editor.ts:110-116` vs `formatted.ts:349-356` — stale active-line clamping disagrees (source pins to EOF, formatted clears), so the panes show different highlights after the doc shrinks.
- **M-35** `ipc.ts:164-168` — `on()` is last-writer-wins; a second `on(kind,…)` silently unhooks the first (fine with today's single registrar, latent hazard).
- **M-36** House-style `as` casts in tests: `reviews-panel.test.ts:123`, `preview.test.ts:7` (the repo's own instanceof-helper pattern exists in `dialogs.test.ts`). No `as`/`!` escapes in in-scope src.

---

## STRUCTURE / ARCHITECTURE

> Big moves are worth doing *before* PoC-6..8 land; cheap wins are near-free now. The product has no
> users, so interface/structure changes cost nothing but the edit.

### Big moves (do before the next PoC wave)
- **T-01** `HostController.cs` (1,937 LOC) is one PoC from a god-object — it owns the IPC router, the document/editing session, the whole GitHub review orchestration (~700 LOC), sign-in, image paste, compare, and external links, all under two locks. Every remaining PoC adds message kinds + background tasks + single-flight flags to the *same* class and lock. **Direction:** keep `HostController` as router+transport; extract `DocumentSession` (the `_sync`-guarded state), `ReviewCoordinator` (send/update/refresh/list/suggest), and `SignInFlow`. Cheapest first step (zero behavior risk): partial-class file split (`HostController.Review.cs`, `.SignIn.cs`, `.Session.cs`). **Effort:** S (split) / M (real extraction).
- **T-13** The draft session is six loose fields (`_state/_branch/_baseBranch/_dirty/_versionsSaved/_versionsShared`) snapshotted ad-hoc per handler — the root of the stale-read race family (S-05, S-06, M-13). **Direction:** one immutable session record + generation counter, swapped atomically under `_sync`, so "is this still the same draft" becomes exact. **Effort:** M. **High leverage — structurally closes several Serious/Minor races.**
- **T-02** `ipc.ts` request correlation is single-reply-only (`:105-113`): an id-bearing frame resolves+deletes the waiter and never reaches `on()`. PoC-9's planned `chat.delta`/`chat.done` streaming is the one wire pattern it can't express. **Direction:** decide now (a `subscribe(kind,id)` keeping the entry alive to a terminal frame, or route id-bearing frames to `on()` when no waiter matches), implement at PoC-9. **Effort:** S now / M at PoC-9.
- **T-03** The compare/diff wire hard-wires "one base = local HEAD" (`OnCompare` always `ReadHeadContent`; `diff.request` has no payload), though the F# engine underneath is already base-agnostic. PoC-6 (PR base vs head) and PoC-7 (vs working copy / vs main / vs another PR) will otherwise multiply message kinds. **Direction:** give `diff.request` a `{ base: "lastVersion" | "published" | { pr } }` payload and make the webview overlay own "which base". **Effort:** S if folded into PoC-6.
- **T-04** Flat 32-file `webview/src` is still navigable but will hit 40+ with PoC-6/8's comment/compare files. **Direction:** fold into `wire/ editors/ review/ chrome/ sync/` (matching the real seams) in one mechanical commit *just before* the PoC-6/7 file wave (esbuild/vitest need no config change); mirror in `tests/`. Companion: split `index.ts`'s 645-line `wire()` into `wireEditors/wireLifecycle/wireGitHub/wireViewMode` helpers. **Effort:** S–M.

### Cheap wins
- **T-05** `BundleWebview` MSBuild target runs `npm run bundle` on *every* build/`dotnet test` (no `Inputs`/`Outputs`, so no incrementality) and fires during IDE design-time builds. **Direction:** add `Inputs`/`Outputs` for incremental skip and `And '$(DesignTimeBuild)' != 'true'`. **Effort:** S. Highest annoyance-per-line in the repo.
- **T-06** Three dead `ProjectReference` edges: `SpecDesk.GitHub → Contracts` (zero usage — and CODE-REVIEW-GUIDE §3.1 documents it as real, so it misleads), and `SpecDesk.Ai → Contracts,Core` (+ Host→Ai ships a dead assembly). **Direction:** remove the GitHub→Contracts edge now; for `Ai`, either delete the stub project until PoC-9 or drop its two unused references. **Effort:** S.
- **T-07** Product-identity strings are scattering: `%LOCALAPPDATA%\SpecDesk` built independently in three places; `User-Agent ("SpecDesk","1.0")` defined *twice* and hard-codes "1.0" while `Version=0.1.0`; fallback git identity, window title, log prefix, env var, package.json. The name is an explicit placeholder, and the AppData root + env var are *behavioral* (a rename strands the DPAPI token dir). **Direction:** one `ProductInfo`/`AppPaths` static consumed everywhere (UA derived from `Version`). **Effort:** S.
- **T-08** Dead release scaffolding: `.gitignore`/`cliff.toml` reference a `release.yml` workflow that never existed; `Directory.Build.props` carries a `__ProjectName__`/`scripts/init.ps1` comment block for a non-existent `scripts/` dir (its conditional `NoWarn` can never fire). `run.cmd`/`release.cmd` duplication is a non-issue (run.cmd is a gitignored personal launcher; release.cmd single-sources the pubxml). **Direction:** delete the template residue. **Effort:** S.
- **T-09** ~~`AGENTS.md`/`CLAUDE.md` are gitignored, yet ~10 tracked/published docs link to `../AGENTS.md` as the authoritative conventions reference~~ — **verified closed, premise was real until recently:** a `.gitignore` rule excluded both files from early in the project's history (added in `8fa0b6c`, "Initialize SpecDesk repo…") until commit `800ce26` ("Add coder workflow scaffolding: AGENTS.md, CLAUDE.md, review docs and task queue"), which removed that rule and, in the same commit, first added both files to the tree. As of current HEAD/`origin/main`, both files are git-tracked and neither `.gitignore`d nor excluded by any local/global ignore rule; `git ls-tree origin/main` confirms both are present on the remote. Every tracked `../AGENTS.md`/`../../AGENTS.md` link resolves on GitHub. No action needed.
- **T-10** Duplicated GitHub HTTP scaffolding: `DeviceFlowApi.cs` and `GitHubReview.cs` each redefine the 30 s timeout, the `SpecDesk/1.0` UA, the linked-CTS pattern, and near-identical `StringOf`/`NumberOf` JSON narrowers — two copies of the safe-read discipline that must stay in lockstep. **Direction:** extract an internal `GitHubHttp` helper (keeps the hand-rolled-BCL decision intact). **Effort:** S.
- **T-11** No committed script for the footgun-prone whole-solution fixture regeneration (the "filtered run leaves stale fixtures" hazard is documented in three places, automated in none). **Direction:** add `scripts/update-contract-fixtures.cmd` running `UPDATE_CONTRACT_FIXTURE=1 dotnet test SpecDesk.slnx`. **Effort:** S.
- **T-12** `md-blocks.ts` uses markdown-it's *default* preset (maxNesting 100) while `pm-markdown.ts` uses *commonmark* + table + strikethrough (maxNesting 20); block-splice correctness rests on their top-level boundaries agreeing, but nothing pins it (>20-deep nesting already tokenizes differently). Also `serializeWithSplice` re-parses `original` twice per `getText()`, and `index.ts:259` calls `formatted.getText()` on every source debounce tick just for the mirror-equality check. **Direction:** one shared tokenizer config from one module; cache the `(original → {doc, blocks})` pair. **Effort:** M.
- **T-14** Two DOM-acquisition styles in the chrome layer: `Dialogs`/`ReviewsPanel`/`SignInController` self-query `document`; `LifecycleChrome`/`SegmentedControl`/`FormatToolbar` take elements via deps. **Direction:** converge on element-injection (the majority pattern, and what tests fake most cleanly). **Effort:** S.

### "Leave it — current shape is right" (recorded so these aren't re-litigated)
- The **8-project .NET split** earns its keep: the F#/C# boundary forces ≥5 projects anyway; `Git` vs `GitHub` isolation is what makes the token-as-plain-parameter discipline *enforceable* (Git literally cannot reference GitHub); `Contracts` as a leaf anchors the fixture generators. Only `Ai` is marginal (T-06).
- The **hand-mirrored contract + 4 JSON fixtures** is the right cost/benefit — codegen would add a dotnet→node build crossing (against the no-deps bias), flatten the deliberate decoder policy asymmetries, and destroy the human JSDoc. Fixtures catch drift in CI; mirror cost is minutes per PoC. (Add T-11's regen script.)
- **1:1 test-project mirroring** is fine; coverage tracks risk (the two riskiest areas have the two biggest suites). Nothing hollow.
- **CI matrix** (3-OS .NET + `SkipWebview`, separate Node job, SHA-pinned actions, CodeQL) is sound.
- **`Toml.fs` is right-sized** for its documented scope — but the tripwire is clear: if PoC-8/10 config needs nested tables/dotted keys/dates, switch to a real TOML library rather than growing it (and see M-05 on escapes).
- **`IDocumentVersioning` local-only / stateless-per-call** (re-opens `Repository` each call) is a defensible thread-safety choice under the host's `_repoGate`; PoC-10's fetch/merge should go in a *new* `IGitSync` or on `IGitPublishing`, never leak into `IDocumentVersioning`.

---

## Verification status

Spot-checked directly against the source and confirmed: **S-01** (no `OnPushStatusError`), **S-02**
(`DefaultCredentials()` at line 241), **S-04** (`%00` decode → `GetFullPath` throw; `ServeAsset` catch
too narrow), **S-08** (`Projection` has no arms for footnote/deflist/task-list → `| _ -> None`),
**S-09** (`Tokens.expand` unguarded `Date.ToString(fmt)` reached via `expandOrDefault` with no
`try/with`), **S-13** (`getText` returns LF-joined `doc.toString()`; leading-block unshift confirmed).
The remaining findings are as reported by the review passes and should be reproduced/confirmed as each
fix is picked up — every one carries a concrete trigger to reproduce from.
