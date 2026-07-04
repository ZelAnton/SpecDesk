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
| `diff.request` | `{}` (editor `version` on the envelope) | diff the working copy against its last saved version — replies with structured blocks via `diff.result` (the live "show changes" overlay) |
| `pr.diff.request` | `{ path, mode }` | request the rendered/raw diff of the open PR (base↔head) |
| `pr.compare.request` | `{ prNumber, base, mode }` | compare a PR's version of the open file against a base (`base` ∈ `workingCopy`, `main`; `mode` ∈ `rendered`, `raw`) |
| `chat.send` | `{ text }` | message to the agent |
| `tree.request` | `{ path? }` | request the spec file tree |
| `doc.open` | `{ path }` | open a spec for editing |

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
| `chat.delta` | `{ text }` | streaming agent output chunk |
| `chat.done` | `{ id }` | agent turn complete |
| `tree` | `{ nodes }` | spec file tree |
| `toast` | `{ level, message }` | plain-language notice |
| `error` | `{ message }` | plain-language error (never a stack trace) |
| `github.code` | `{ userCode, verificationUri }` | the one-time device code to display while connecting a GitHub account |
| `github.account` | `{ available, signedIn, login?, message? }` | GitHub connection state for the account affordance (`available` false → the affordance hides; `message` is a transient/failed sign-in line) |
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
- **Debounce/throttle:** `editor.changed` ~120 ms; `scroll.sync` throttled to animation frame.
- **Cancellation:** a new `editor.changed` cancels the in-flight parse/preview for the prior
  version.
- **Confirmation gate:** any agent- or button-triggered mutating action emits
  `confirm.request`; native performs the side effect only after the webview returns the
  matching confirmation. This is how the agent-safety rule in
  [08-ai-agent.md](08-ai-agent.md) is enforced at the protocol level.

## Contracts

DTOs live in `SpecDesk.Contracts` (C#) and are the single definition of payload shapes; the
webview's TS types are generated from / kept in sync with them to keep the small TS surface
honest.
