# Changelog

All notable changes to **SpecDesk** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- A performance harness for large documents guards against interactivity regressions: BenchmarkDotNet micro-benchmarks (Markdown reparse and AST diff on 5–10k-line generated specs) plus a Layer 1 budget scenario that checks reconciliation and scroll-sync stay within threshold and do not grow quadratically, run as an opt-in nightly stage that never slows ordinary builds.
- Inline document comments now synchronize with an open change request's comments on GitHub: a comment on a line that is part of the change can be posted to the review from its thread and then reads as being on GitHub, comments left on GitHub appear inline in the document as read-only threads, and a comment on an unchanged line stays local with a plain "not yet on GitHub" label instead of failing. Synced threads re-anchor as the document is edited and when a newer version is shared for review.

### Changed

- Markdown toolbars now stay on one row and move only commands that do not fit into an accessible measured overflow menu.
- SpecDesk now starts with Navigator selected on the collapsed left rail, regardless of the previously used panel mode.
- The left rail now orders Navigator, Repositories, Change requests, and Disk before the contextual Outline mode.
- Repository, local-copy, file, and change-request context now appears in compact panels above the active central view and links to the matching left-panel mode.
- The bottom panel is now fully hidden by default and opens from one dedicated toggle at the foot of the right rail instead of leaving a collapsed bottom toolbar.
- The in-app change-request document now presents safe Markdown descriptions, people, proposed and destination versions, chronological saved versions with plain-language checks, and comments in a responsive manager-focused layout.
- Empty favorite stars now appear only while their entity is hovered or keyboard-focused in every list, while existing favorites remain visible.
- Split temporarily keeps Code at its natural height without inserting alignment spacers.
- Selected-text formatting now uses a stationary, descriptive toolbar in both Code and Formatted views.
- Inline comment creation, editing, and replies now stay at the selected document anchor, grow downward with their Markdown content, and remain isolated to the connected account.
- Local comment persistence now writes bounded per-document anchor fingerprints outside the typing path, waits for pending saves before reloading the same document, labels ambiguous or deleted anchors as detached, keeps every failed snapshot visible across navigation, prevents stale retries from replacing newer work, and locks mutations after a failed load until recovery.
- Window close now waits for pending comment creation, edits, replies, and deletion across signed-in sessions, and keeps SpecDesk open with Retry if any comment snapshot cannot be saved safely.
- Repository, local-copy, and working-line rows now keep compact actions inline, reveal secondary actions on hover or keyboard focus, and offer the same valid operations from an accessible context menu.
- The repository copy form is always visible, fills the local name from the repository name, and enables Clone only after the exact current repository is resolved successfully.
- Local working lines now list the repository's actual main line first, while manual Get updates and Share changes controls are removed in preparation for automatic synchronization.
- Every destructive action now requires an inline **Confirm deletion** step directly beneath the chosen action before SpecDesk can remove anything.
- `GitHubRepositoryCatalog`'s organization, repository, metadata, tree, folder, and file requests (including the paginated organization/repository fetches) now share the same 30-second per-request timeout and User-Agent as SpecDesk's other GitHub transports, instead of relying on the shared `HttpClient`'s longer default timeout with no per-request bound; a stalled GitHub request during sign-in or repository browsing now fails and can be retried sooner instead of appearing to hang.
- The "Show changes" compare overlay now explicitly requests the last-saved-version base it has always used, instead of the request path hard-coding that literal at the call site — laying the groundwork for future compare affordances against other bases without changing today's behavior.

### Fixed

- Toggling the bullet or numbered-list toolbar command on a list item nested inside a blockquote no longer prepends a duplicate marker in front of the quote.
- Closing the app can no longer leave a file-dialog request stuck in Photino's native UI dispatch.
- The title-bar search, account control, and Windows-standard caption buttons now retain their intended slots at restored, narrow, and maximized window sizes.
- Context panels now appear immediately from document identity, enrich safely when workspace details arrive, and never show a misleading `File No document` placeholder.
- Opening another document now clears the previous document's inline comments before the new comment snapshot begins loading.
- Disk deletion now rejects case-only directory siblings on case-sensitive Windows filesystems while accepting normal casing differences in drive letters and UNC server/share names.
- Deleting a case-only sibling in Disk no longer closes the active document or removes its distinct recent and favorite entries.
- Case-distinct local folders in Disk now keep independent expansion state and show only their own loaded children.
- SpecDesk now applies one documented rule for deciding whether two paths mean the same document, so the folder tree, the open-document editing and save checks, and Disk deletion no longer disagree when the same file is reached through a path that differs only in letter case.
- Pull-request details now load from GitHub instead of failing because of a malformed GraphQL document.
- The right-panel resize divider now stops above an expanded bottom panel instead of leaving a bright vertical seam through it.
- Requesting your reviews list with a malformed request no longer leaves the request stuck until it times out.
- `.spectool.toml`'s hand-rolled reader (`Toml.fs`) split a string-array entry containing an escaped
  quote (e.g. `reviewers = ["Say \"hi\""]`) into multiple wrong elements at the `\"`, and never
  un-escaped it, unlike the single-string reading path. Array-element parsing is now escape-aware, and
  each element is un-escaped the same way `getString` already did.
- Pull requests opened from My reviews or pasted GitHub links now use SpecDesk's review document instead of opening GitHub in a browser.
- A comments-service failure no longer hides an otherwise available pull-request description and history.
- Switching GitHub accounts while My reviews is loading now starts a fresh lookup and ignores the retired account's result.
- Cancelling a GitHub sign-in that cannot update its saved authorization no longer closes the sign-in code of a newer sign-in started in its place.
- Remote-only working lines and lines with protected work no longer offer local rename actions that cannot succeed.
- Interrupted or failed local-copy and working-line renames now recover the matching Git state, saved registration, favorites, and recent paths instead of leaving an unusable partial rename.
- The borderless Windows shell now restores native edge and corner resizing around the WebView while restored, keeps maximized edges border-free and non-resizable, and maximizes within the monitor work area without covering the taskbar.
- Clone controls now look disabled as well as remaining inert until a repository name has been entered and verified.
- The right panel now ends at the bottom panel instead of overlapping its workspace.
- Bounded pull-request histories now disclose when earlier commits are not shown instead of appearing complete.
- Disconnecting or changing GitHub accounts now immediately closes private online documents and clears their Folder tree and context.
- Online repository folders with more than 1,000 direct entries now load completely instead of silently hiding the remainder.
- Double-clicking the in-content title bar now maximizes or restores the window instead of starting a second drag.
- Local-work deletion warnings now keep both the cancel and delete actions inside narrow repository panels.
- Repository entry now keeps a usable text field and moves copy actions to the next row in narrow panels.
- Reopening a specification while an older review request is still finishing no longer blocks editing the reopened document.
- Completing an older repository operation no longer replaces a repository, folder, or file opened later.
- Concurrent workspace refreshes no longer restore an older repository or favorites list.
- A slower repository open can no longer replace the navigator tree chosen by a newer open.
- Removing the open working line now closes its specification and navigator instead of silently switching their files.
- Repository activity no longer stalls unrelated interface updates while another local Git operation is running.
- Closing SpecDesk now finishes an in-progress interface update and drops updates that are still queued.
- Forgetting a repository now prevents delayed local-copy opens, clones, or actions from restoring or changing its registration.
- Disconnecting GitHub now prevents an unfinished repository lookup from leaving a partial registration behind.
- Repository metadata now remains saved when workspace state is read concurrently during registration.
- Removing a local copy or working line now prevents delayed inspection from restoring deleted entries.
- A failed working-line change now closes the affected document atomically before window close can save stale text.
- Changing or discarding a working line now preserves local-only files that would be overwritten.
- Disconnecting from GitHub now always removes the saved authorization, and cancelling sign-in or replacing a repository lookup, browse, or file preview no longer skips its remaining work, even when a finishing background task retires the shared cancellation at the same instant.

### Added

- A new `docs/user-guide.md` walks authors through the whole in-app workflow end to end — starting SpecDesk, opening a repository or folder, editing and saving versions, sending a draft for review, inline comments, the change-request document, the AI assistant, Disk and favorites, and what to do if something goes wrong — entirely in the app's plain-language vocabulary.
- Connected accounts can refresh newly approved GitHub organizations and repositories from the avatar menu, with an automatic throttled check when the window regains focus.
- Individual files can now be deleted from Disk with handle-bound, root-contained native validation, exact recent/favorite cleanup, and automatic closing when the deleted file is open.
- Selected text can carry persistent local comment threads with replies, editing, and confirmed deletion in Code and Formatted views, anchored after complete blocks and kept out of the Markdown file.
- Local copies can create a new working line, and local copies and non-main working lines can be renamed while favorites and the active context follow the new identity.
- The status bar now identifies the active local copy, working line, and filename without repeating the full path.
- The account avatar now shows the connected GitHub profile image with a neutral signed-out fallback and carries the notification-count badge.
- Pull requests now open as in-app review documents with their description, author, reviewers, conversation, commits, checks, and draft state.
- Pull-request conversations now combine general and file-review comments with replies, editing your own comments, and a focused bottom-panel reader.
- The bottom Log now records bounded GitHub requests, view changes, context changes, and user actions without recording message contents or credentials.
- Hovering selected text in Code and comment editors now offers an anchored compact Markdown formatting palette.
- Local copies and working lines now show available upstream updates and known conflicts alongside local-work indicators.
- Repositories now has one Refresh action that checks every registered local copy for upstream updates.
- The Start page now shows favorite repositories, folders, and specs alongside recent work.
- Selecting a favorite GitHub repository now reveals and highlights it in the Repositories panel.
- Local copies and working lines now show distinct indicators for unshared versions, unsaved edits, and work held safely for another branch.
- Local copies and local working lines can be removed safely without deleting GitHub repositories or remote branches; risky local work is explained before confirmation.
- Favorites now accept GitHub repositories, individual local copies, and exact branches alongside files and folders.
- Local repository copies can be named independently, so the same GitHub repository can be copied more than once; occupied names offer to open the existing copy.
- The always-visible Markdown formatting toolbar now includes H3, inline code, links, starter tables, image references, and dividers alongside the existing headings, lists, styles, quotes, and code blocks.
- The Windows title bar now lives inside SpecDesk, with native drag, double-click maximize, and accessible window controls.
- Split view now mirrors the line or formatted block under the pointer in both panes with a distinct sand highlight.
- The Copilot panel now uses a roomy VS Code-style composer card with context and template actions,
  assistant/model indicators, an icon send action, and live GitHub connection status in one compact footer.
- The right panel now follows the active context: Chat is always available, Comments appears for a review, History for a repository branch, Outline for Markdown, and Versions for repository files.
- GitHub repository entry now suggests accessible personal and organization repositories by name.
- Public GitHub repositories outside the connected account's suggestions can be entered as `owner/repository`.
- Repository entry now offers **Clone…** to managed storage and **Clone to folder…** with collision-safe destinations.
- The exact managed clone destination is shown before a repository copy starts.
- Repository copies now require Yes/No confirmation with a persisted **Do not show again** option.
- Repository descriptions and availability are now shown before repository copy actions are enabled.
- The main toolbar can connect or disconnect GitHub, and the bottom status bar now shows the connected
  username and organizations available to that authorization.
- The left-panel Pull Requests mode now lists open requests you authored or participated in, combining and
  deduplicating both relationships while keeping closed and merged work out of the active queue.
- The left-panel Review mode now lists open reviews assigned to you directly or through a visible GitHub
  team, with refresh, connection, loading, empty, and error states.
- Clicking the notification icon now opens a dedicated Notifications list in the main workspace; the
  placeholder explains where review requests and mentions will appear as notification sources are added.
- Repositories, folders, and files can now be starred as favorites and reopened later, including exact online
  branches and paths for repositories that have not been copied locally.
- Selecting a registered repository now browses its complete folder/file tree directly from GitHub even
  without a local copy; selecting a text file opens a read-only preview, while local trees now include all files.
- Registered repositories now show their managed local copies and each copy's non-default working branches;
  you can create more than one local copy, and SpecDesk remembers the repository's actual default branch.
- Repository copies can now be given a local name, with duplicate names offering to open the existing copy
  while different names allow several copies of the same GitHub repository.
- Selecting a local repository working line now protects unfinished files, restores that line's previously
  protected work, and opens the selected copy without exposing the recovery steps.
- Local repository copies and non-main working lines can now be removed, with state-bound warnings for
  unfinished edits, unshared versions, and protected work snapshots before anything is discarded; GitHub
  repositories and remote branches are never deleted.
- Local repository copies and working lines now expose distinct status for unfinished edits, unshared saved
  versions, and protected work snapshots.
- Adding or opening a GitHub repository while disconnected now starts sign-in with SpecDesk's built-in
  public OAuth identity, opens GitHub's authorization page in your normal browser, and continues the
  requested action after access is granted.
- The right panel now includes Versions, Comments, and History for the selected document. Versions
  and history come from the document's saved repository history; Comments lists the selected file's inline
  GitHub review comments when connected and shows an honest empty state otherwise.
- The assistant now has an Attach menu for the open file, current folder, and registered repositories;
  selected context appears as removable chips and is sent with the next message.
- The assistant message box is now visibly multi-line: Enter writes a new line, Ctrl+Enter (or Cmd+Enter)
  sends, and the box can be resized vertically for longer prompts.
- You can now open a GitHub repository that isn't on your machine yet by clicking one in the Repositories
  list. SpecDesk copies it into a local folder and opens it as
  your workspace. Opening a folder or a repository now also reveals the file navigator (and opens the left
  panel if it was collapsed), so you immediately see what you opened.
- The left panel now has **Recent**, **Favorites**, and **Repositories** views. Recent and Favorites list the
  files and folders you opened or starred — click one to open it, and use the star to add or remove a
  favorite. Repositories lists the GitHub repositories you registered: add one by `owner/name` or URL, open
  it, or remove it. The Start screen also lists your most recent items as quick shortcuts.
- The app now remembers the files and folders you recently opened, lets you keep the ones you use most as
  favorites, and lets you register the GitHub repositories you work with often — so they're at hand next time.
  Your recents, favorites, and registered repositories are saved between runs.
- An AI assistant chat in the right panel: type a question about the document and the reply streams in as
  it's written. Assistant replies are shown as quiet text; your own messages sit in a subtle bubble. A
  prompt-template picker (▤) inserts a ready-made prompt into the message box — from your personal library
  (a local file) and a shared library fetched from a configured URL — which you can edit before sending.
  Connect your GitHub account to use GitHub Copilot; when disconnected, the panel asks you to connect instead
  of generating a placeholder reply. Nothing in the document is ever changed without your confirmation.
- Collapsible side and bottom panels around the editor: a left rail, a right rail, and a full-width bottom
  dock. Click the active mode icon to collapse or expand its panel; choosing another icon opens that mode.
  Collapsed side rails keep their vertical icons visible, while the collapsed bottom panel becomes a
  horizontal toolbar. Panels remain resizable by dragging their edge (or with the arrow keys when the divider
  is focused), and each header names the active mode.
  Whether each panel is open, its size, and its active mode are remembered across restarts. Most tools
  inside are placeholders for now, and the editing area re-measures as the panels open, close, or resize so
  the split view stays aligned.
- The Markdown formatting toolbar is now part of the editing area — it appears directly above the editor
  panes and spans only their width — instead of a full-width row above the panels, and it stays with the
  editor when a panel replaces the centre with another view.
- The left panel's navigator switches the main area between views: "Document" edits the spec, "Start" shows
  a calm open-a-spec screen. It's the first navigation tool that replaces the centre's content; the
  navigator highlights wherever the centre currently is, and the view-mode switch (Code / Split / Formatted)
  is disabled while a non-document view is shown.
- The Start screen now offers **Open a file** and **Open a folder**, and the left panel has a **Files**
  navigator: open a folder and its Markdown tree (nested folders and `.md`/`.markdown` files) appears there —
  folders expand and collapse, and clicking a file opens it. The tree also follows the open document's folder
  so it's useful even without explicitly opening one. (Connecting a GitHub repository joins these next.)
- Groundwork for opening specs from anywhere: the app can now open a specific file by path, open a whole
  folder as a workspace, and read that folder's Markdown file tree (nested folders and `.md`/`.markdown`
  files, skipping `.git`/`node_modules` and folders with no Markdown inside). This is the plumbing the Start
  screen and the folder navigator build on.
- The right panel now has an **Outline** tool that lists the open document's headings as a nested tree and
  keeps up as you edit. Clicking a heading scrolls the editor to it (bringing the document view back first
  if the Start screen was showing). A long heading truncates with its full text on hover, and the nesting
  is real list structure so a screen reader announces the hierarchy.
- `SPECDESK_DATA_ROOT` redirects the app's entire local data root — the sample repo, the GitHub auth
  token directory, and the logs — to a chosen directory (for a dev run, or an isolated full-app test
  against a disposable copy). Unset, the default `%LOCALAPPDATA%\SpecDesk` is unchanged.
- Exporting the log now also captures the webview's diagnostic trace: it writes the in-page trace ring to
  a timestamped JSON file beside the log and appends the trace's tail (wall-clock-stamped, so it lines up
  with the native log entries) to the exported file — so a single exported log shows what the UI did and
  why, native and webview together.
- The log directory and file verbosity are now environment-overridable: `SPECDESK_LOG_DIR` redirects the
  rolling log file, and `SPECDESK_LOG_LEVEL` (verbose/debug/info/warning/error/fatal) sets the file
  sink's minimum level — so a dev run or a test harness can point logs at a known location and dial
  verbosity without a rebuild. Unset, the defaults are unchanged (`%LOCALAPPDATA%\SpecDesk\logs` at Debug).
- Setting `SPECDESK_DEVTOOLS=1` enables the WebView2 devtools and right-click context menu for
  interactive debugging; a shipped app exposes neither by default.
- The webview now keeps an always-on in-page diagnostic trace of the editor's hot paths and captures
  previously-unhandled `window.onerror` / unhandled promise rejections into the log, so a rendering,
  formatting or scroll-sync misbehaviour leaves a record of *why* it happened. The trace is readable via
  `window.__specdeskTrace` (for the E2E harness and interactive debugging) and never leaves the page except
  on demand; unhandled errors are additionally forwarded to the log channel (rate-limited so a render-loop
  error can't flood it).
- Formatting shortcuts now work in both Code and Formatted panes, with shortcut hints shown on toolbar buttons.
- A Split scroll-sync delivery gate (`webview/tests/split-delivery/`) now drives the actually-built
  `wwwroot/webview.js` — the same artifact the host serves — through the real Split wiring, so a regression
  that removes or mis-wires the coordinator, height-sync or the editors in the shipped bundle fails CI even
  though the isolated unit suites still pass. It runs the standard bundle process, checks the T-107 content
  manifest (and that the loaded bundle's fingerprint matches it), then proves real non-zero source spacers
  appear at each semantic boundary (including individual table rows and list items) with the Code and
  Formatted tops aligned within one CSS pixel, and that a synthetic user scroll of the real CodeMirror
  scroller couples the sibling pane in both directions. Wired into CI after `npm run bundle` and runnable as
  `npm run test:delivery`.
- A fail-closed working-copy currency gate stops planning, building, or running SpecDesk from a
  repository main working copy that has drifted behind the local published `main` (which would silently
  use the old, pre-merge UI). The read-only `scripts/assert-main-current.ps1` reports the role (main
  working copy vs isolated task worktree, decided from VCS structure) and whether the checkout still
  includes `main`; the `SpecDesk.Host` build refuses to compile a stale main copy and `MainWorktreeGuard`
  refuses to launch one before the WebView loads, while isolated task worktrees and published apps are
  exempt. Escape hatches: `-p:SkipMainCurrentGuard=true` (build) and `SPECDESK_ALLOW_STALE_WORKTREE` (run).
- `scripts/update-contract-fixtures.cmd` regenerates all four contract fixture files
  (`webview/tests/contract/{wire-kinds,native-payloads,lifecycle-states,diff-kinds}.json`) in one
  whole-solution `UPDATE_CONTRACT_FIXTURE=1 dotnet test SpecDesk.slnx` run, so an intentional contract
  change can no longer be regenerated with a narrowed `--filter` that silently leaves some fixtures
  stale.
- A drift-guard regression suite (`tests/SpecDesk.GitHub.Tests/GitHubHttpTests.cs`) pins the shared
  `GitHubHttp` plumbing that `DeviceFlowApi` and `GitHubReview` already consolidated onto: the 30-second
  per-request timeout, the `ProductInfo`-derived User-Agent (no second hard-coded `SpecDesk/1.0` out of step
  with the product version), and the linked-`CancellationTokenSource` timeout pattern; it also drives both
  transports through a stub handler to prove each tags its real outgoing request with the exact shared
  User-Agent. The pre-existing per-transport tests only assert the header *contains* `SpecDesk`, which a
  future re-hard-coded divergent value would still pass — this suite catches that drift. Test-only
  regression coverage for the already-completed consolidation; no behavior change.

### Changed

- Switching between Markdown view modes now preserves edits that are still waiting to synchronize from either pane.

- Local copies now provide safe Get updates and Share actions: updates are fast-forward-only, local work is
  never overwritten, remote-ahead lines must be updated before sharing, and sharing never force-pushes.
- Local copies and working lines now distinguish saved versions to share, updates available from GitHub,
  unfinished edits, protected snapshots, and known overlapping changes.
- The Repositories panel can now refresh every registered local copy in one background batch, continuing past
  unavailable copies and updating repository status once when the batch completes.
- GitHub device authorization now starts by copying the one-time code before opening the standard GitHub
  verification page, while keeping the visible code as a fallback when clipboard access is unavailable.
- Repositories now uses a calmer card hierarchy with an at-a-glance repository/copy count, collapsible add controls, and named-copy creation that always goes through the editable clone form.
- Open Repository on the Start page now focuses repository search as soon as the panel opens.
- The left rail is now Navigator, Repositories, Folders, and PRs; Favorites and History live inside Navigator, while review requests and participating pull requests share PRs.
- Repository copies and branches are now direct navigation choices; choosing a branch safely switches it before showing that copy's files.
- Repository cloning now uses a split button: **Clone** performs the usual managed copy immediately, while its arrow opens the destination menu.
- The Markdown editor toolbar now uses the same grey surface as side-panel headers.
- Start and Notifications now hide inactive document breadcrumbs and search, leaving a focused global toolbar.
- The right-side saved-change tool is now labeled simply **History**.
- SpecDesk now opens on Start with all optional panels collapsed while retaining each panel's preferred mode and size.
- The dark status bar now matches the side rails, and the bottom panel leaves the right rail continuously accessible.
- The Assistant button is now the first mode on the right toolbar, ahead of document-specific tools.
- Assistant replies now come from GitHub Copilot after GitHub sign-in instead of the offline echo preview;
  disconnecting or changing accounts cancels the active turn and discards that account's chat session.
- The Start screen now opens the Repositories panel with one `Open Repository` button instead of asking for a
  repository address itself.
- Markdown actions, wrapping, change highlighting, and the Code / Split / Formatted switch now live directly
  above the editor. The global toolbar now shows the current repository, version line, document path, working
  document search, notifications, and an accessible account menu with Settings, Help, and Sign out.
- Side panels now use a clearer grey hierarchy: dark mode rails, light panel bodies, and slightly stronger
  headers keep tools visually separate from the document in both light and dark themes.
- Split scroll synchronization is now driven by a single coordinator over one line↔px map instead of three
  mutually-suppressing mechanisms. The former per-frame line-based scroll-sync (with its `ScrollSync` driver
  lock), the caret reveal, and the mode-switch restore shared a web of timing heuristics (`suppress`/`drive`/
  `syncedRecently`, a double `requestAnimationFrame`) to keep each from echoing the others. A new
  `SplitSync` (`webview/src/sync/sync-coordinator.ts`) is the sole writer of each pane's `scrollTop`: it
  couples the panes through a pure piecewise-linear line↔px map (`webview/src/sync/scroll-map.ts`) built from
  the same block anchors height-sync measures, and suppresses its own echo deterministically — a scroll that
  settles on the value it just wrote is ignored, so the two panes cannot ping-pong, with no timing window to
  tune and no driver lock. Because coupling is by source LINE (read the active pane's top line, map it to the
  sibling's pixels), height-sync's documented non-negative-spacer drift no longer leaks into where the two
  viewports track each other. The old `ScrollSync` is removed; the one timing fallback that remains is a
  short guard that stands a caret reveal down while a scroll just coupled the panes (anti-judder), and the
  mode-switch relayout wait is kept solely as an explicit fallback for CodeMirror's asynchronous re-measure.
- Split scroll synchronization is now pixel-accurate and symmetric in both directions. Following the
  formatted (WYSIWYG) pane's smooth scroll no longer moves the source editor in whole-line steps: the
  formatted pane now reports a FRACTIONAL viewport-top line (`lineAtScrollTop` no longer floors), the same
  sub-line precision the source pane already reported, so the coordinator's line↔px map couples the two
  panes sub-line in either direction — including smoothly interpolating through a tall height-sync spacer,
  with no freeze and no jump. A view-mode switch now keeps the exact fractional reading position instead of
  snapping to the nearest whole line, and a first block carrying leading blank lines or link reference
  definitions (which render no pixels) maps its rendered height from where its content actually begins, so
  those non-rendered lines no longer skew the alignment. The two panes' scroll-to-line methods now share one
  contract — a fractional 0-based source line — so a fractional line reveals the block it falls in rather
  than being rejected.
- The `diff.result` wire contract is now discriminated by `kind` instead of one flat record padded with
  sentinels (`anchorLine: -1`, `removedText: ""`, empty `children`, `""` bases for the cases that don't use
  them). Each kind carries only its own fields on the wire — a removed block/child has an anchor and text
  but no line range; a changed block/child has a base but no anchor — so "a removed block with a range" or
  "a changed block with no base" is now unrepresentable. On the C# side the payloads are `[JsonPolymorphic]`
  hierarchies (`AddedDiffEntry`/`ChangedDiffEntry`/`RemovedDiffEntry`/… and the `ChildDiffPayload` peers);
  the F# `DiffWire` producer builds through per-kind smart constructors; the webview mirrors them as
  discriminated unions (`DiffEntryPayload`, `ChildDiffPayload`, and the editors' `DiffMark`) so both panes
  gate their rendering on an exhaustive `kind` match rather than a mix of `kind` / `sub` / `!== undefined`
  checks. Behavior is unchanged; the change closes the class of drift where the two panes disagreed on how
  to read the flat shape.
- A changed per-child diff entry (`ChangedChildDiff` / the F# `ChildWireEntry`) now also carries the
  changed table row / list item's base raw source in a new `baseSource` field — symmetric to a whole
  changed block's `baseSource` — so the Code pane's inline word-diff can extend to individual rows/items,
  not just whole blocks (the webview that consumes it lands separately). A container's per-child base
  source ranges are derived from the same Markdig parse as a sidecar to the `Ast` (`Projection.childLineRanges`),
  which keeps nested blocks range-free so a block's structural equality stays position-independent — the
  AstDiff backbone that keeps an unchanged-but-shifted list/table Unchanged.
- The Code pane now shows an inline word-diff for a changed table row / list item too, not just a whole
  changed block — symmetric with the Formatted pane. The webview decodes the child `baseSource` field
  above and threads it through `expandDiffMarks`'s sub-`DiffMark`s to the existing Code-pane inline-diff
  gate (`buildDiffDecorations`), which already applied to whole blocks.
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
- The formatted editor's line↔block↔ProseMirror-node↔DOM correspondence now lives in one `BlockMap`
  abstraction (`webview/src/editors/block-map.ts`) instead of being re-derived by a bare
  `blocks[i]`/`doc.child(i)` index in five separate places (`blockGeometry`, `topVisibleSourceLine`,
  the caret/hover/overlay `nodeRangeForLine`, `scrollToSourceLine`, and the review overlay `pushDiff`)
  plus a duplicated "last block starting at or before this line" scan (thrice in `formatted.ts`, and
  `blockForLine` in `webview/src/review/preview.ts`, which now shares the single search). The map pairs
  each source block with its ProseMirror node once per frame and, crucially, DETECTS a markdown-it vs
  ProseMirror top-level split divergence (a parse mismatch) — exposing no entries and logging a
  one-per-occurrence diagnostic, so every consumer degrades to a safe no-op (no geometry/spacers, no
  highlight, no scroll target) rather than silently pairing a source range with the wrong DOM element
  and corrupting height-sync/scroll-sync. For consistent documents (the norm — both sides share one
  tokenizer config) behavior is unchanged; the divergence path is now a detected fallback instead of
  quiet corruption. This is also the shared foundation for the upcoming geometry cache and sync
  coordinator.
- The Split scroll/reconcile hot path no longer does O(n) work with forced layout on every frame. The
  formatted pane's per-block rendered geometry now lives in a scroll-invariant cache
  (`webview/src/editors/block-geometry.ts`): `topVisibleSourceLine` and `scrollToSourceLine`
  binary-search it for the viewport-top / target block instead of measuring every block's
  `nodeDOM`+`getBoundingClientRect` up to the viewport each scroll frame, and the cache is measured once
  per relayout (invalidated on an edit, a whole-document set, a review-overlay marker, an image decode,
  and a width refresh; the reconcile path re-measures and refreshes it). The shared block-by-line search
  (`lastIndexAtOrBefore` in `block-map.ts`) became a binary search, so the caret/hover/overlay/scroll
  block lookups are no longer linear scans. On the source-editor side, `naturalLineTops` now subtracts
  the spacer height above each anchor via prefix sums built in one pass over the spacer set, replacing
  the former per-anchor rescan of the whole decoration set (O(anchors × spacers) per reconcile). No
  observable editor behavior changes — the same lines/blocks are reported, just without the per-frame
  layout thrashing on long documents.
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
- An edit no longer triggers a full Markdig render plus a `preview.html` IPC round-trip on the hot
  `OnEditorChanged` path (`HostController.Session.cs`): the `#preview` pane those messages fed has been
  permanently hidden (`display: none`) with no consumer since PoC-12 made Split's right pane the editable
  WYSIWYG, so the render was pure overhead paid on every debounced keystroke. `RenderAndSend` stays
  (`internal`, matching `RunDiskAutosave`) as the ready entry point for a future on-demand consumer
  (diff/comments); the version-ordering guard (`PreviewCoordinator.ShouldRender`) that also protects
  `_text` itself against an out-of-order edit frame is unchanged. `webview/src/review/preview.ts`'s
  `Preview.apply`/`indexBlocks` (and `index.ts`'s `previewHtml` handler) stay wired to receive it; the
  unused scroll/highlight/geometry scaffold on `Preview` (`scrollToSourceLine`, `topVisibleSourceLine`,
  `setScrollTop`, `scrollTopValue`, `highlightSourceLine`, `highlightHoverLine`, `blockGeometry`,
  `contentWidth`) — which duplicated what the live editors already do for the visible panes — was removed.
- The webview bundle's origin is now verified by content, not by file timestamps. `npm run bundle`
  writes a deterministic content manifest (`wwwroot/webview.manifest.json`, schema-versioned, no
  absolute paths) that fingerprints every input (`webview/src/**`, `index.html`, `styles.css`,
  `package.json`, `package-lock.json`, the TypeScript configs, and the esbuild parameters) and each
  served output (`webview.js`, `index.html`, `styles.css`), written atomically only after esbuild and
  the html/css copy both succeed. The `BundleWebview` MSBuild target now decides up-to-date with a
  cheap content check (`scripts/verify-bundle.mjs`, Node built-ins only) instead of an
  `Inputs`/`Outputs` timestamp comparison, so a working-copy switch, a timestamp-preserving restore, or
  a copy that kept a source's mtime can no longer pass a stale bundle off as fresh — while a genuinely
  unchanged bundle still skips the rebuild. At startup the host re-verifies the manifest and refuses to
  load a missing, partial, corrupt, or older-schema bundle (and, on a dev run with the source tree
  present, a bundle that no longer matches the current inputs — overridable only via the explicit
  `SPECDESK_WEBVIEW_ALLOW_STALE` opt-in); the startup log records the input and output fingerprints so
  it is unambiguous which UI actually ran. `SkipWebview` (used by the node-less CI .NET jobs) is now
  blocked on publish builds, so a shipped app can never carry an unverified leftover bundle.

### Removed
- Dead release scaffolding: `.gitignore`'s `release-notes.md` entry and `cliff.toml`'s header comment
  referenced a `.github/workflows/release.yml` that never existed in this repo; `Directory.Build.props`
  carried a template comment block (and its now-unreachable conditional `CA1707` `NoWarn`) for a
  `__ProjectName__`/`scripts/init.ps1` project-stamping script that was never added. `release.cmd` and
  the build are unaffected — neither reads `cliff.toml`/`release-notes.md` nor relies on the removed
  `NoWarn`.

### Fixed

- Completing an older repository operation no longer replaces a repository, folder, or file opened later.
- Starting Edit now stays bound to the exact specification and working line until the checked-out file is reloaded, preventing concurrent updates, navigation, or window close from creating hidden drafts or publishing stale state.
- Repository updates now reject insecure or local transports masquerading as GitHub before any connection, credential callback, or working-line change.
- Document saves, saved versions, autosave, image paste, navigation, and window close now use the exact in-memory content version, preventing a queued older snapshot from overwriting newer edits or another working line.
- Saving, saving a version, and pasting an image now stay bound to the exact open specification and working line, so concurrent line changes or local removals cannot write files, assets, or versions into another line.
- Repository actions now close an active specification safely when an external checkout or replacement invalidates their final state, preventing stale text from being saved into another working line.
- Working-line switches, updates, and removals now stop stale editor text from being saved after a late post-checkout inspection or recovery failure.
- Failed local-copy removal now closes the affected specification and reports the preserved recovery folder when the original path cannot be restored, preventing autosave from writing into a replacement folder.
- Forgetting a missing or repointed local-copy registration now leaves its open folder and pending document edits active because no files were removed.
- Removing a repository registration now waits for its current local action to finish, so completed updates, line changes, and local removals settle the active document safely.
- Closing SpecDesk now waits for repository copying, updates, line changes, sharing, and local removal to finish before ending the process.
- Forgetting a repository while its local copy is still being prepared now keeps that operation isolated until cleanup finishes, preventing another copy or window close from interrupting it.
- Closing SpecDesk now settles the final editor input and writes a pending local draft before the window is allowed to close.
- A failed Discard now restores editing and autosave only after the exact draft working line is verified; if restoration changes files and then fails, the document closes so stale draft text cannot overwrite the published version.
- Opening another specification now locks both editor panes until that exact request succeeds or fails, preventing edits from crossing document identities.
- Opening a specification now waits for active local repository work to finish, preventing files from being read while their working line is changing.
- Removing a local repository copy now stops safely when it owns linked working copies and explains any unfinished or unshared work they contain.

- Opening another document now saves pending editor input before navigation and retires old edit timers before hydration.

- Repository transitions now preserve the newest pending Split-pane edit, lock both editors while files may change, and flush edits before Get updates.
- Repository transitions now stay locked until the exact branch, removal, or Get request finishes, ignoring unrelated document and workspace updates.
- Refresh all now remains visibly in progress until its exact background batch completes, ignoring unrelated repository state updates.
- Refresh all now discards fetched status when a local copy is replaced or re-registered before the result can be shown.
- Get updates now reloads the open specification after a successful fast-forward, preventing a stale editor from overwriting received changes.
- Repository copy creation now rejects a destination claimed by another repository during cloning instead of registering or opening the wrong local copy.
- Newly created local copies are verified again before opening, so a path repointed during cloning is left untouched and never registered under the wrong repository.
- Missing or invalid local-copy records no longer reserve a reusable name; forgetting one removes only SpecDesk state and never touches unrelated files.
- Opening an existing local copy now verifies that its GitHub source matches the selected repository instead of opening a different working tree under the wrong name.
- Refreshing or switching a local copy now verifies its GitHub source on the same open repository before contacting a remote, reading its state, or changing local work.
- Getting or sharing versions now captures and validates the exact fetch and effective push destinations before using credentials or starting network work, so concurrent remote-configuration changes cannot redirect an in-flight operation; switching still verifies the expected source and current working line before saving editor text.
- Local working lines without an upstream, or whose upstream comparison is unavailable, now show unshared versions and retain that warning before local deletion.
- Refreshing, switching, getting, sharing, or removing a working line now rechecks the exact current line before reloading documents or publishing status, so an external checkout cannot label another line as the completed result.
- Removing a local copy now verifies its GitHub source on the same repository handle and re-verifies the quarantined tree after moving it, leaving path replacements untouched.
- Removing a working line now uses an atomic expected-version delete and refuses lines checked out in another worktree, preserving concurrent external Git updates.
- Removing local repository work now verifies the current working line before saving pending editor text, preventing a stale action from writing that text into another line.
- Removing an open local copy now closes its document without opening a replacement folder that appears at the deleted path.
- Removing the current local working line now immediately records the line actually checked out, while detached local copies continue to appear normally.
- Local branch removal now uses the repository's current checkout instead of cached workspace state before protecting and reloading the active document.
- Clone to folder now refuses an occupied requested name and offers the registered existing copy instead of silently creating a suffixed folder.
- Repository working lines now show Delete only for local, non-default branches that the action can actually remove.
- Switching or removing repository work now flushes the editor debounce first, preserving the last keystrokes before the transition.
- Disconnecting GitHub now prevents delayed Git authentication callbacks from releasing the retired account token.
- Local-copy and working-line favorites now retain their repository identity and reopen the exact copy or line.
- Switching or removing the active working line now blocks stale autosave frames and reloads the document from the selected line; removing its local copy closes the document on Start.
- Working-line changes now refuse to overwrite ignored local files that become tracked on the destination, and local-copy deletion confirmations include nested ignored files.
- Refresh all now stops using a captured GitHub token immediately when the author disconnects or changes accounts.
- Context-specific dock modes now remain hidden outside their applicable pull request, branch, repository,
  or Markdown-file context instead of leaking into every right-panel rail.
- Detached, remote-only, and outside-repository documents now apply their active context correctly when
  nullable native fields are omitted, keeping Versions available for every repository file.
- Closing a repository copy menu now removes it from hit testing, so it no longer blocks repository rows.
- Opening a registered repository or repository favorite now reveals the Files panel before its remote tree
  arrives.
- Repository descriptions and managed destinations now wrap below the entry row instead of squeezing the
  repository field to an unreadable width.
- Disconnecting GitHub now forms a hard account boundary for review lists, document comments, review
  publishing, and Copilot streams, so cancellation-ignoring late work cannot publish private results or
  continue with another account-bound action.
- Disconnecting or changing GitHub accounts now clears the previous account's Copilot conversation, draft,
  attachments, and review comments before the new account can use them.
- Disconnecting or changing GitHub accounts now also clears private repository lookup details and pending
  copy confirmations before another account can see or act on them.
- Disconnecting GitHub now clears the running app's authorization even when its encrypted token file is
  temporarily locked or protected, records that choice durably for the next launch, and a failed managed
  repository copy keeps the entered owner/name ready to retry.
- Cancelling GitHub authorization now prevents a late token save from reconnecting the current or next app
  session after the sign-in prompt has closed.
- Closing SpecDesk during a review update now cancels the repository push before waiting for account cleanup,
  avoiding a shutdown stall while the network operation unwinds.
- Repository copies now preserve every pre-existing destination and become visible only after a complete
  copy has been moved atomically out of private staging storage.
- Disconnecting GitHub now cancels every account-bound repository request before removing the saved
  authorization, so late private clone, metadata, description, browse, and file results cannot reach the UI.
- Copilot replies are now isolated by turn, so signing out and reconnecting cannot mix a late reply into
  the new conversation or leave the composer waiting forever.
- Split view now keeps the LAST row of a table and the LAST item of a list level with their rendered
  counterparts even when content above the container has accumulated more source-pane padding than any
  of the container's own rows call for. Height-sync pads with a single document-wide running maximum, so
  such a container used to get no spacer at all: its rows all sat below their rendered targets (where
  additive padding cannot lift them — the accepted direction), but the container's tail then drifted
  visibly against the Formatted pane inside one viewport, ending shorter than its rendered counterpart.
  Each table's/list's final row now carries a container-tail floor — at least the padding at the
  container's first row plus the container's own internal growth — so a spacer lands right above the
  last row/item and the container ends in step in both panes; intermediate rows keep the documented
  drift, a tail the running maximum already aligns exactly is left untouched, and the source-taller
  direction (loose lists) still adds nothing. Covered per-plan in unit tests, through the shipped bundle
  by a delivery gate, and on real rendered geometry by a real-Chromium E2E
  (`webview/src/sync/height-sync.ts`, `webview/src/editors/sync-anchors.ts`,
  `webview/src/editors/formatted.ts`, `e2e/webview-mock/split-geometry.e2e.ts`).
- Split view's height-sync spacers now render on a fresh app start (and on any document load/switch)
  without the author having to manually switch to Code and back. Root cause: `doc.loaded` fed the raw
  on-disk text — routinely CRLF on a Windows checkout (`core.autocrlf=true`) — into the source editor and
  the formatted pane independently; CodeMirror always normalizes its own document to LF-only, so the two
  panes' texts permanently disagreed from the moment of load, and height-sync's pane-consistency gate
  correctly (if unhelpfully) refused to pad against that mismatch with no way back, since a silent load
  never fires either pane's `onChange` (the gate's only other recovery trigger). The load path now
  normalizes line endings once before handing the SAME text to both panes; `HeightSync` also gained a
  bounded, condition-driven one-shot settle retry as defense in depth for any future silent-setText path
  that leaves the panes transiently gated with no `onChange` to recover from
  (`webview/src/index.ts`, `webview/src/sync/height-sync.ts`).
- The startup working-copy currency guard now recognises the repository main working copy correctly when
  the checkout sits under a symlinked path (for example a symlinked temp or home directory): it resolves
  symlinks on both sides of the path comparison instead of trusting the raw spelling, so it no longer
  mistakes the main copy for an exempt isolated worktree and skips the stale-build refusal.
- Split view no longer misaligns the very top of the document. The first rendered block (e.g. the title
  heading) sat about 24 px above its source line — while every mid-document block lined up — because
  height-sync reproduced the formatted pane's structural top padding as a leading spacer in the source
  editor, even though the scroll coupling already scrolls that same padding off to bring the block flush.
  The lead is now measured against the formatted pane's content box rather than its scroll origin, so the
  pane inset is counted once and the top-of-document block sits level with its source line like every other
  anchor (`webview/src/sync/height-sync.ts`).
- Split scroll sync no longer judders when the author switches panes mid-scroll. Each scroll arms a 120 ms
  scroll-settle debounce that re-snaps the sibling to the pane's final momentum position; a trackpad/momentum
  scroll of one pane could still have that settle pending when the author grabbed the OTHER pane, and the
  late settle then re-declared its own pane active and yanked the pane being scrolled back to the previous
  pane's line (so "scroll Formatted" did not reliably keep Formatted's first visible line matching Code's). A
  scroll-settle now stands down when its pane is no longer the active one — the pane the author actually
  finished scrolling is the active one, so its own settle still runs (`webview/src/sync/sync-coordinator.ts`).
- The Split scroll-sync delivery gate is now deterministic. Its scroll helper drove the real editors but did
  not drain the 120 ms scroll-settle debounce between simulated scrolls, so a pending settle leaked into the
  next step and raced its coupling — the gate passed or failed by wall-clock luck, which made a green run
  meaningless. Each simulated scroll now fully settles (as a human scrolling one pane and pausing does)
  before the next, and the harness documents that its rigged jsdom geometry (tall synthetic Formatted vs a
  uniform 14 px Code line) proves WIRING, not real-engine spacer count / sub-pixel alignment — those still
  need a manual GUI pass (`webview/tests/split-delivery/`).
- The app now runs the reviewed-and-published code instead of drifting behind it on an older local build.
  The orchestrator's local coordination state under `.work/` (task queue, journal, checkpoints, review log)
  is no longer part of the versioned tree, so the main working copy can be brought up to the published `main`
  without its stale tracked queue and checkpoints either blocking the update or overwriting the live ones. A
  new `scripts/restore-main-worktree.ps1` performs this safely and repeatably: it backs up `.work/`, refuses
  to proceed when there are un-committed changes outside `.work/`, fast-forwards the working copy to `main`,
  detaches any previously tracked `.work/*` without losing their content, and verifies every restored file
  against a checksum manifest — so a published Split-sync fix (or any later change) is what actually executes
  rather than an older predecessor.
- Split view scrolling is now smooth with no forced-layout stutter or redundant scroll writes: one geometry
  reconcile per frame is frame-atomic — a single read phase (one Formatted DOM measure, one CodeMirror tops
  read) feeds an immutable geometry snapshot from which both scroll maps and the editor spacer plan are
  computed, then at most one passive `scrollTop` write follows, with no layout re-read after the write. The
  former `reconcileHeights()` measured the Formatted DOM twice and re-read CodeMirror tops right after
  writing spacers (the read→write→read that reintroduced forced layout), then unconditionally re-coupled the
  panes even when nothing changed. Now height-sync reports whether the pass actually changed the editor's
  decorations/scroll geometry, the coordinator adopts the one snapshot to rebuild both maps without a second
  measure, and it leaves the passive pane alone when it is already within a pixel of its target — so a scroll
  over an unchanged layout re-measures nothing, and a settled repeat reconcile makes zero DOM/scroll writes.
  All the invalidation sources (edit/mirror, width/wrap, image decode, font load, diff overlay, mode
  visibility, and CodeMirror's async re-measure) now funnel through one generation-aware coalescing scheduler
  (`webview/src/sync/reconcile-scheduler.ts`): a burst before the next frame collapses into one reconcile
  against the newest generation, and a spacer pass that triggers a follow-up CodeMirror measure converges to
  a fixed point over successive frames instead of looping within one. The active pane the author is reading
  stays visually still while the passive pane catches up within a frame, and a scroll clamped at a document
  boundary no longer ping-pongs.
- Split view scroll synchronization is now bidirectional with no jump when the leading pane changes.
  `SplitSync` is the single owner of both panes' scroll and of which pane is active (the last pane
  genuinely scrolled, focused, or edited); a coupling write only ever moves the passive pane, and both
  the read and the write now go through the two panes' shared line↔px map so they are exact inverses of
  one geometry. Because the round-trip of one unchanging layout is the identity, grabbing the pane that
  was following (intercepting the active pane) couples the sibling straight back to where it already
  sits — the source editor and the formatted pane no longer drift apart or jump when the author switches
  which pane leads. The momentum/trackpad settle re-snap is now symmetric for both panes, a scroll
  clamped at a document boundary writes one stable best-effort position without ping-pong, and a view
  mode switch keeps the fractional reading position of the pane the author was actually reading rather
  than unconditionally the source editor's.
- Split view no longer accumulates vertical misalignment where the Code pane runs ahead of the Formatted
  pane and the two later line back up. Height synchronization now pads the Code pane by the minimal
  cumulative amount — the running maximum of each anchor's required shift — instead of re-adding every
  local positive gap difference, so a region where the source is intrinsically taller no longer locks a
  transient lead in as permanent drift: once the Formatted pane catches back up, the following anchors
  realign with no leftover spacer. Where alignment is genuinely unreachable (the source is taller and
  height cannot be removed) the residual stays monotonic and never negative, and padding is computed in
  fractional pixels with a single rounding step so subpixel reflow no longer flickers spacers.
- Split view now aligns tall tables and long lists row-by-row and item-by-item: the Formatted pane's
  scroll and height synchronization anchor every rendered table row and list item (nested items included)
  on its own source line, instead of treating a whole table or list as one block and guessing interior
  rows by height, so scrolling through a large table or list stays lined up with the source.
- Split view no longer jitters the Formatted pane after its scroll is echoed into the Code pane and settles.
- Split view now precisely re-snaps the Formatted pane through the scroll coordinator after momentum
  scrolling stops in the Code pane.
- Split scroll synchronization timing is no longer disrupted by system clock adjustments.
- Split panes now stay aligned after window resize, wrap changes, and formatted content resizes by re-aligning from the focused or last-scrolled pane.
- Split reflow alignment no longer treats coordinator-written echo scrolls as the user's active pane.
- The formatting toolbar's buttons now honestly reflect what a click will do in both panels. Previously:
  a command PM's schema can't apply in the current context (`setBlockType(heading/code_block)` inside a
  table cell, `wrapIn(blockquote)` there too — a table cell's content is inline-only, no block children)
  silently did nothing when clicked; a partially-marked selection's bold/italic/strikethrough button
  looked pressed (`rangeHasMark` — the mark present SOMEWHERE in the selection) even though clicking it
  would make the selection MORE marked, not less; and the Code/Split panel's buttons never reflected the
  caret's context at all (`aria-pressed` stayed `false` unconditionally, on the stated grounds that "the
  source editor has no inline-mark notion"). Now: the formatted pane's inapplicable commands disable
  their buttons (a dry-run applicability query against ProseMirror's own commands, no dispatch); the
  pressed state reflects the mark covering the WHOLE selection, matching `toggleMark`'s (now explicit)
  `removeWhenPresent: false` add/widen semantics; and the Code/Split panel reads its own pressed state
  from the caret's position in the lang-markdown (GFM) syntax tree (bold/italic/strikethrough/H1/H2/
  bullet/ordered/quote/fenced-code), so both panels report the same honest state. The source editor's
  `markdown()` extension is now configured with the GFM-extended base language (`markdownLanguage`) it
  was missing, so `~~strikethrough~~` parses to a real syntax node there too (previously plain CommonMark
  only, silently excluding GFM constructs from the live editor's own syntax tree).
- The formatting toolbar now produces the same Markdown regardless of which panel (Code or Formatted)
  was focused when a button was pressed — the two editors implement toolbar commands independently
  (line-based text transforms in `webview/src/editors/md-format.ts` vs. structural ProseMirror commands
  in `webview/src/editors/pm-commands.ts`), and several had drifted: toggling bullet/numbered list on a
  line already carrying the OTHER list marker used to prefix the new marker onto the raw line instead of
  converting it (`1. x` → `- 1. x`); toggling heading on a list/quote line used to put the `#` before the
  container marker instead of nesting inside it (`- item` → `# - item` instead of `- # item`); toggling
  code on a heading line fenced the raw `# ` syntax instead of the heading's bare text; toggling quote
  off a quoted list item in the Formatted view lifted the nested list out instead of the quote itself
  (`> - a` → `> a` instead of `- a`); and toggling heading over a selection spanning several
  soft-wrapped lines of one paragraph turned every physical line into its own heading instead of the
  one heading the paragraph represents.
  ships every removed block's full text over IPC) for a pathologically large document, once `AstDiff.diff`
  (`src/SpecDesk.Diff/AstDiff.fs`) has fallen back to its flat, coarse Removed+Added listing above
  `maxNodePairs` base×head node pairs. `DiffWire.toWireDetailed` (used by `DiffProjection.Build` instead of
  `toWire`) now detects the fallback alongside the diff it already computes and, when it fired, sends a
  compact `{ removedCount, addedCount }` `DiffOverflowPayload` on `diff.result` INSTEAD of building/shipping
  the flat entries — `entries` is empty in that case. `ReviewController.applyResult` (`webview/src/review/
  review.ts`) washes nothing and raises a new, distinct "too many changes to show" notice (`#review-
  overflow-bar`) instead of expanding the (empty) entries or the ordinary "no changes" notice. The
  non-overflowing path (`toWire`, `AstDiff.diff`/`diffText`, `expandDiffMarks`) is unchanged.
- Split view no longer occasionally yanks the passive pane's scroll to the very start of the document
  while the active pane is being scrolled. `MarkdownEditor.topVisibleLineExact()` probed
  `posAtCoords` at a point inside the line-number gutter (`scrollDOM`'s rect) rather than the content
  area, and, when `posAtCoords` missed (mid-measure, e.g. during a layout rebuild), returned `0`
  instead of skipping the frame — both cases could report "top of document" mid-scroll and the
  formatted pane would sync to it. The probe now uses `contentDOM`'s left edge, and a miss now
  returns the last successfully resolved line instead of `0`.
- The review overlay now highlights only the changed row/item of a table or list, instead of washing
  the whole container, when that table/list is the document's very first block and is preceded by
  leading blank lines or link-reference-definitions. `expandDiffMarks` (`webview/src/review/
  diff-marks.ts`) keyed `childLineStarts` by md-blocks' `block.lineStart`, which for the first block
  is pulled back to `0` when the block has such leading "head" content, while the lookup used
  Markdig's `entry.lineStart`, the real content line — the mismatched keys made the lookup miss and
  fall back to whole-container highlighting. The keying now uses the block's real content-token start
  (`contentLineStart`) so the keys agree and per-row/per-item highlighting is preserved.
- Loading a document (`doc.loaded`) now resets both panes' scroll position to the start of the document.
  Previously only the content was re-hydrated; each pane kept whatever `scrollTop` the PREVIOUS document
  had left it at — an arbitrary depth for a shorter old document, the browser's own clamp for a longer
  one, and the two panes generally disagreeing with each other. `MarkdownEditor`/`FormattedEditor` gained
  a `scrollToTop()` used under `scrollSync.suppress()` so the reset doesn't itself drive a cross-pane sync.
- The Split view's source editor no longer shows a large empty hatched band above its first line. Height-
  sync pads the source editor so each source block lines up with its rendered block, and the first block's
  lead reproduces the leading space above the first rendered block. That leading space included the first
  block's own typographic top margin (a heading's `1.6em`, ~45px), which — with nothing above the first
  block to separate from — was pushed into the source pane as a tall hatched service band while the first
  source line failed to sit level with the rendered heading. The first rendered block now hugs the top of
  its pane's content area (`.sd-doc > :first-child { margin-top: 0 }` in the shared rendered stylesheet),
  which lifts the rendered document uniformly and leaves the inter-block gaps unchanged, so the lead
  shrinks to just the pane's structural inset and the first source line lines up with the rendered heading.
  (The lead is a stable fixed point — CodeMirror folds the leading spacer into the first line's block, so
  the source's natural first-line top is invariant to the lead and it never oscillates.)
- Loading a document (`doc.loaded`) no longer triggers a redundant `editor.changed` round-trip. The
  source editor's hydration used a non-silent `setText`, so ~120ms later its debounced `onChange` fired
  and sent the just-loaded text straight back to the host as if it had been edited — bumping the host's
  `docVersion` and running a full re-render on every document load, and only avoiding a false "Unsaved
  changes" notice by the accident of the load also resetting the lifecycle to Published (the disk-autosave
  arm gates on the editing state). `webview/src/editors/editor.ts`'s `setText` now takes the marker-keep/
  -drop decision (`sameDocument`) as an axis independent of the change-notification suppression (`silent`)
  — previously the same flag drove both — so `doc.loaded` can hydrate silently while still dropping any
  image-insert marker left over from the previous document, instead of restoring it at a now-meaningless
  clamped position.
- The formatting toolbar's fence toggle (`md-format.ts` — the code-block button) no longer corrupts the
  document when the selection sits inside an existing fenced code block. It used to recognize "already a
  fence" only by checking whether the SELECTION's own first/last lines started with ` ``` `, so selecting
  interior lines (never touching the fence's own delimiter lines) was invisible to that check and got
  wrapped in a nested fence — the inner ` ``` ` prematurely closed the outer one and broke everything
  below. Detection is now the enclosing `FencedCode` syntax node (parsed the same way the inline-marker
  toggle already detects its enclosing wrapper), so a selection anywhere inside a fence — including a
  ` ~~~ ` fence or one with an info string (` ```js `) — unwraps it. When the selection is not fully inside
  a single fence (e.g. it spans a whole fence plus a following paragraph) it still wraps, but the new
  fence's marker is always at least one backtick longer than any backtick run already in the content, so
  it can never be prematurely closed by an embedded fence either.
- The Split view's source editor no longer keeps an inflated spacer before a block that sat below the
  viewport (e.g. a `### A code block` after a table). Height-sync reads each source block's top from
  CodeMirror, but a not-yet-measured region below the viewport — especially a wrapped line, estimated as
  a single row — reports a top that is too small, so the computed gap looked short and the spacer grew
  too tall. The stale spacer used to persist until the next edit or window resize, because the update
  listener deliberately swallowed CodeMirror's transaction-less "I finished measuring" `geometryChanged`
  to avoid an apply→measure→apply flicker loop. That loop is now broken at its source instead —
  `HeightSync.reconcile` only re-dispatches spacers when the recomputed set actually differs from the one
  already applied (natural tops are spacer-invariant, so a settled geometry recomputes the identical set
  and stops) — so the re-measure is allowed through and the spacer self-corrects to the right height with
  no user action and no flicker.
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
- "Show changes" (`webview/src/review/review.ts`) no longer asks the host to diff a stale head:
  clicking it while an editor still has an unsent edit pending (its own 120ms debounce hasn't fired
  yet) used to diff the last-reported text while the local head had already moved on, silently
  offsetting the resulting fills/anchors/child ordinals (worst in Split with a diverging, not-yet-
  mirrored pane). The compare request is now deferred — polled on a bounded 20ms/15-attempt window —
  until every pane reports no pending edit, then fired; unaffected by an ordinary click (fires
  immediately, same as before).
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
- The Formatted pane's row/item highlight (`formatted.ts` `nodeRangeForLine`) no longer clamps a child
  ordinal into a container node whose child count disagrees with md-blocks' `childLineStarts`. It used
  to `Math.min` the computed index into range, which for a mismatched count could point at the wrong
  row/item instead of the one the source line actually falls in; on a count mismatch it now washes the
  whole container, matching how a per-child diff already falls back to a whole-block wash natively
  (`DiffWire.fs`). md-blocks and the ProseMirror schema share one tokenizer config and agree on child
  counts for any real document today, so this is a defense-in-depth guard, pinned by a new
  cross-language ordinal fixture (`webview/tests/contract/container-ordinals.json`,
  `tests/SpecDesk.Diff.Tests/ContainerOrdinalContractTests.fs`,
  `webview/tests/contract/container-ordinals.test.ts`) covering a nested list inside an item, loose/tight
  lists, a table with an empty header row, and a multi-paragraph list item.
- Split view's height-synced scroll (`height-sync.ts`/`editor.ts`) no longer lets the visible text jump
  while typing: whenever the editor spacers above the current viewport change weight (e.g. a below-
  viewport block's estimated height gets corrected once CodeMirror finishes measuring it), `scrollTop`
  is now nudged by that exact delta in the same dispatch, so the content already at the viewport top
  stays put. The new `computeScrollCompensation` (pure, unit-tested) computes the delta from the
  previous vs. next spacer set and the source line currently at the viewport top
  (`MarkdownEditor.topVisibleLine()`).
- Split view's height-synced scroll no longer occasionally pads the source editor against the OTHER
  pane's mid-write document. `HeightSync.reconcile()` is driven unconditionally from several paths
  (a formatted-pane content resize, a window resize, an editor wrap toggle, and every settled edit), so
  it could fire inside the 120ms window between an edit and its cross-pane mirror landing, applying the
  formatted pane's block anchors against a source editor whose text had already moved on (or vice
  versa) — producing duplicated/misplaced spacers and negative gaps. `reconcile()` now defers (applies
  nothing, leaving whatever spacers were already there) whenever either pane has a pending unmirrored
  edit or the two panes' texts simply disagree; the deferred reconcile is naturally retried once the
  mirror lands, because the pane that was mid-write calls `reconcileHeights()` again unconditionally as
  soon as its own debounce settles. Separately, `MarkdownEditor.naturalLineTops`/`setSpacers` no longer
  silently clamp a source line outside the CURRENT document to the last line (which could still plant a
  spacer on the wrong line if a stale anchor slipped past the gate) — an out-of-range line/spacer is now
  refused outright, with a diagnostic through the editor's own `onDebug` callback (mirroring
  `HeightSync`'s existing one).
- Split view's live cross-pane mirror no longer disrupts the passive pane while the author types in the
  other one: a source-pane edit kept resetting the WYSIWYG pane's undo history, caret and selection (its
  `FormattedEditor.setText` rebuilt the whole ProseMirror document from a fresh parse every tick), and a
  formatted-pane edit collapsed the source editor's caret/selection to the change boundary (its
  `MarkdownEditor.setText` replaced the entire document in one change, which also made image-marker
  position mapping degenerate — the reason for the whole-document restore-markers workaround). Both
  directions now mirror through the smallest change that reconciles the two panes (a new
  `mirror-patch.ts` computes a common-prefix/suffix character diff for CodeMirror and a common-leading/
  trailing block count for ProseMirror): CodeMirror applies a single changed-span transaction (caret,
  selection, scroll anchor and image markers all remap naturally, no restore workaround), and the
  formatted editor keeps every unchanged top-level block's existing node and re-parses only the changed
  middle span as one transaction (its undo history, caret and selection survive, and the per-tick
  whole-document re-parse is gone). A document using a link reference definition — the one CommonMark
  construct whose meaning crosses block boundaries — falls back to a full in-context rebuild so its
  links can't misresolve.
- The Formatted pane's inline word-diff for a changed table row / list item (sub-mark) now compares the
  WHOLE row/item — every cell/paragraph joined with the same separator the native side flattens it with
  (`DiffWire.fs`: table cells via `" | "`, list-item blocks via `" "`) — against the wire's already
  row/item-flattened `baseText`, instead of just the row/item's FIRST cell/paragraph. Comparing only the
  first cell/paragraph against a base that covers the whole row/item raised `changeRatio` on every
  multi-cell row or multi-paragraph item (an untouched second cell/paragraph read as wholesale deleted
  text), almost always falling back to a whole-row/item wash instead of the intended inline highlight, or
  in a degenerate case producing spurious highlights. `formatted.ts` gained `flattenRowOrItem`, which joins
  a table row's/list item's children the same way and maps a synthetic-text offset back to its real
  document position for the word-diff decorations; a plain top-level paragraph/heading (not a row/item)
  is unaffected. The Code pane, which already diffs the row/item's actual raw source span, needed no change.

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

- Completing an older repository operation no longer replaces a repository, folder, or file opened later.
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
