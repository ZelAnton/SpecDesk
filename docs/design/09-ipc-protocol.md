# 09 — IPC Protocol

Photino's IPC channel carries strings. SpecDesk uses one typed JSON envelope in both
directions. C# deserializes `kind` and routes; request/response pairs match on `id`.

## Envelope

```json
{
	"kind": "editor.changed",
	"id": "f1e2-...-optional-for-request-response",
	"version": 42,
	"payload": { }
}
```

- `kind` — dotted message name, grouped by domain (`doc.*`, `diff.*`, `image.*`, `branch.*`,
  `version.*`, `github.*`, `pr.*`, …); the cross-cutting channels `ready` / `log` / `error` / `status`
  stay bare. (A remaining `action.*` row is a pre-convention placeholder for a not-yet-built action; it
  takes a domain name when implemented — as `sendForReview` / `update` did, now `doc.sendForReview` /
  `doc.updateReview`.)
- `id` — present only when a correlated reply is expected; otherwise `null`.
- `version` — monotonic counter for editor content; lets the receiver drop stale work.
- `payload` — message-specific object.

## webview → native (intents / commands)

| `kind` | payload | meaning |
|--------|---------|---------|
| `editor.changed` | `{ text, version }` | document text changed (debounced) |
| `editor.selection` | `{ from, to }` | current selection/cursor |
| `scroll.sync` | `{ side, sourceLine }` | a pane scrolled (throttled) |
| `image.paste` | `{ base64, originalName?, mime }` | image dropped/pasted |
| `doc.save` | `{}` | save the working copy to disk — **no commit** (the host also autosaves internally off `editor.changed`, with no separate message) |
| `doc.edit` | `{ branchName }` | begin a draft (fork a working branch); empty/absent name → generated |
| `doc.saveVersion` | `{ note }` | **explicit commit** of the working copy with the author's (generated, edited) version note |
| `doc.sendForReview` | `{ title, body }` | push + open PR with the author-confirmed title/description (blank title → generated; empty body honoured) |
| `doc.updateReview` | `{}` | push the newly-saved versions to the open PR |
| `review.refresh` | `{}` | re-read the open PR's review decision from GitHub (host emits a fresh `status` if it changed); fired while under review — polled (focus-gated) and on window focus |
| `action.publish` | `{}` | merge the PR (if permitted) — *not yet built (PoC-10)* |
| `github.signIn` / `github.signInCancel` / `github.signOut` | `{}` | connect / cancel-connecting / disconnect a GitHub account (device flow) |
| `doc.discard` | `{}` | abandon the draft |
| `comment.add` | `{ lineStart, lineEnd, body }` | new inline comment |
| `comment.reply` | `{ id, body }` | reply in a thread |
| `comment.resolve` | `{ id }` | resolve a thread |
| `pr.list.request` | `{}` | request the user's open reviews (author / reviewer); host replies with `pr.list`, correlated by `id` |
| `pr.forFile` | `{ path }` | request the open PRs touching a given file (comparison) |
| `pr.open` | `{ refOrUrl }` | load a PR *into the editor* for review — *not yet built*; PoC-5's My reviews panel opens a review on GitHub (from the list or a pasted URL) via `link.open` instead |
| `diff.request` | `{ base }` (`base` ∈ `"lastVersion"`, `"published"`, `"pr"`; editor `version` on the envelope) — the webview overlay picks `base`; only `"lastVersion"` (the working copy vs its last saved version, the live "show changes" overlay) is wired today, `"published"`/`"pr"` are reserved for PoC-7 | diff the working copy against the requested base — replies with structured blocks via `diff.result` |
| `pr.diff.request` | `{ path, mode }` | request the rendered/raw diff of the open PR (base↔head) |
| `pr.compare.request` | `{ prNumber, base, mode }` | compare a PR's version of the open file against a base (`base` ∈ `workingCopy`, `main`; `mode` ∈ `rendered`, `raw`) |
| `chat.send` | `{ id, text, attachments? }` (`attachments[]`: `{ kind, label, reference }`) | message to the agent; client-generated `id` correlates every streamed frame, while file/folder references are accepted only when issued by the native attachment picker, consumed once, and resolved by the host into bounded context |
| `chat.attachment.pick` | `{ kind }` (`file` or `folder`) | show an attachment-specific native picker without opening the document/workspace; host replies with `chat.attachment.picked`, correlated by `id` |
| `document.activity.request` | `{}` | request saved versions, comments, and change history for the selected document; host replies with `document.activity`, correlated by `id` |
| `templates.request` | `{}` | request the prompt-template library (personal + remote); host replies with `templates`, correlated by `id` |
| `tree.request` | `{ path? }` | request the Markdown file tree (`path` scopes it; absent → the current workspace folder, else the open document's folder). Host replies with `tree` |
| `folder.open` | `{ path? }` | open a folder as the file-navigator root (`path`), or `null`/absent → the native folder picker. A `tree` event follows |
| `doc.open` | `{ path? }` | open a spec for editing (`path`), or `null`/absent → the native open dialog |
| `workspace.request` | `{}` | request the persisted workspace state (recent items, favorites, registered repos); host replies with `workspace.state` |
| `workspace.favorite` | `{ path, favorite }` | add (`favorite` true) or remove (false) a file/folder from favorites; host re-emits `workspace.state` |
| `repo.register` | `{ url }` | register a GitHub repo from a URL/spec (`https://github.com/owner/name(.git)`, `owner/name`, or `git@github.com:owner/name(.git)`); the host parses/normalizes it and re-emits `workspace.state`, or emits `error` if it isn't a repo. A4 stores the entry only — no clone yet |
| `repo.cloneToFolder` | `{ url }` | choose a parent folder and clone the GitHub repository into a new, non-colliding child folder |
| `repo.cloneManaged` | `{ url, destinationPath }` | create a new copy at the managed path the author reviewed |
| `repo.cloneDestination.request` | `{ url, requestId }` | resolve the exact managed destination before enabling Clone |
| `repo.description.request` | `{ url, requestId }` | resolve the repository description and visibility before enabling clone actions |
| `repo.unregister` | `{ id }` | remove a registered repo by its `owner/name` id; host re-emits `workspace.state` |
| `repo.open` | `{ url }` | open a GitHub repo (`owner/name` or a GitHub URL): clone it into a managed local folder under the app data root (or reuse an existing clone) and open that folder as the workspace — a `tree` follows; an unparseable value comes back as `error`. Registers the repo too (re-emits `workspace.state`) |
| `log` / `log.export` | `{ level, message, data? }` / `{}` | forward a webview log line to the host logger / export the current rolling log file |
| `trace.dump` | `{ t0Epoch, firstSeq, entries: [{ seq, t, cat, event, data? }] }` | dump the always-on diagnostic trace ring; the host persists it as a JSON file beside the log and appends its tail (wall-clock-stamped) to the `log.export` that follows. Sent just before `log.export` when the author exports the log |

## native → webview (events)

| `kind` | payload | meaning |
|--------|---------|---------|
| `preview.html` | `{ html, lineMap, version }` | rendered preview to inject |
| `diff.result` | `{ entries }` | changed blocks of the working copy vs its last saved version, for the live "show changes" overlay (editor `version` on the envelope) |
| `pr.diff.rendered` | `{ html, mode }` | rendered/raw diff of the open PR to display |
| `pr.compare.rendered` | `{ html, mode, base }` | rendered/raw comparison of a PR's version against the chosen base |
| `version.note.suggested` | `{ note }` | editable generated version note (the commit message in plain words) |
| `pr.suggested` | `{ title, body, blocked? }` | editable generated PR text; `blocked` (a plain reason: not connected / not a GitHub repo / no saved version) means the send can't proceed — the webview shows it and does NOT open the prompt |
| `pr.list` | `{ items, error? }` | the user's open reviews (`items[]`: `{ number, title, url, repo, role, status, label }`; `label` is the host-authoritative status text) — reply to `pr.list.request`; `error` (plain reason) means the list couldn't be loaded |
| `pr.forFile` | `{ path, items }` | open PRs touching the given file |
| `comments.synced` | `{ list }` | current comment set for the doc |
| `image.inserted` | `{ markdown, cursorHint }` | link to insert at the cursor |
| `status` | `{ state, label, branch? }` | plain-language status (`state` ∈ `published`, `draft`, `inReview`, `changesRequested`, `approved` — the wire state names, pinned by `lifecycle-states.json`; `label` is the author-facing text, including transient "Unsaved changes" / "Version saved"; `branch` is diagnostic only) |
| `conflict.detected` | `{ sections }` | "someone else changed this too" data |
| `chat.delta` | `{ id, text }` | streaming agent output chunk for the identified turn |
| `chat.done` | `{ id }` | agent turn complete |
| `chat.attachment.picked` | `{ kind, label, reference }` or `null` | native-picked attachment descriptor — reply to `chat.attachment.pick`; the path stays host-owned and is usable once by `chat.send` |
| `document.activity` | `{ document?, versions, historyState, historyMessage?, comments, commentsState, commentsMessage?, history }` | selected-document activity; versions and distinct change summaries come from bounded repository history, with `notVersioned` distinguished from a load failure, while comments are the newest bounded inline GitHub review comments filtered by the exact selected repository path and distinguish verified-empty, disconnected, and unavailable states |
| `templates` | `{ personal, remote }` (each an array of `{ id, title, body }`) | the prompt-template library — reply to `templates.request` |
| `tree` | `{ root, nodes }` | the workspace folder's Markdown file tree (`root` is the folder's absolute path; each node `{ name, path, isDirectory, children }`) — reply to `folder.open` / `tree.request` |
| `workspace.state` | `{ recent, favorites, repositories }` (`recent`/`favorites`: `{ path, label, isFolder }[]` — `recent` is most-recent-first, `favorites` in the order added; `repositories`: `{ id, name, url }[]`) | the persisted workspace store — reply to `workspace.request` and re-emitted after every mutation and after opening a file/folder |
| `workspace.context` | `{ repository, repositoryRoot, branch, branchState, defaultBranch, path }` | authoritative context for the open document. Repository and relative path come from its versioning root (never the independently browsed file-tree root); `branchState` (`named` / `detached` / `unavailable`) keeps a deliberate unnamed checkout distinct from a read failure. Nullable repository fields mean the open file is not in a readable versioned repository |
| `toast` | `{ level, message }` | plain-language notice |
| `error` | `{ message }` | plain-language error (never a stack trace) |
| `github.code` | `{ userCode, verificationUri }` | the one-time device code to display while connecting a GitHub account |
| `github.account` | `{ available, signedIn, login?, message?, organizations? }` | GitHub connection state for the account affordance and status bar (`available` false → the affordance hides; `organizations` is the authorized organization-login list after it loads; `message` is a transient/failed sign-in line) |
| `github.repositories` | `{ repositories: { fullName, description? }[] }` | case-insensitively de-duplicated repositories available to the connected account for owner/name autocomplete |
| `repo.cloneDestination` | `{ url, requestId, path? }` | exact managed clone path; `requestId` lets the webview ignore a stale response |
| `repo.description` | `{ url, requestId, state, description? }` | description lookup result (`found`, `private`, `notFound`, or `error`); correlation fields let the webview ignore stale responses |
| `confirm.request` | `{ id, action, summary }` | ask the author to confirm a mutating action |

## Ordering & correctness rules

- **Staleness:** version-stamped results (`preview.html`, `diff.result`) carry the editor
  `version` they were computed from; the webview ignores any whose `version` is older than the
  latest local edit.
- **Correlation:** replies correlate to their request in one of two ways. One-shot RPCs set `id`
  and the reply echoes it (`branch.name.request` / `version.note.request` / `pr.suggested.request`
  → `*.suggested`, `pr.list.request` → `pr.list`, `image.paste` → `image.inserted`). Live-recompute requests instead correlate by `version`
  (`editor.changed` → `preview.html`,
  `diff.request` → `diff.result`): a newer edit supersedes any in-flight result. Unsolicited
  events (status, toast, chat.delta) carry neither.
- **Debounce/throttle:** `editor.changed` ~120 ms; repository-description lookup ~220 ms;
  `scroll.sync` throttled to animation frame.
- **Cancellation:** a new `editor.changed` cancels the in-flight parse/preview for the prior
  version; a newer repository-description request cancels the prior lookup and stale correlated
  responses are ignored by the webview.
- **Confirmation gate:** any agent- or button-triggered mutating action emits
  `confirm.request`; native performs the side effect only after the webview returns the
  matching confirmation. This is how the agent-safety rule in
  [08-ai-agent.md](08-ai-agent.md) is enforced at the protocol level.

## Contracts

DTOs live in `SpecDesk.Contracts` (C#) and are the single definition of payload shapes; the
webview's TS types are generated from / kept in sync with them to keep the small TS surface
honest.
