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

- `kind` — dotted message name (namespace.action).
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
| `action.save` | `{}` | explicit save (also happens automatically) |
| `action.sendForReview` | `{}` | push + open PR |
| `action.update` | `{}` | push more commits to the open PR |
| `action.publish` | `{}` | merge the PR (if permitted) |
| `action.discard` | `{}` | abandon the draft |
| `comment.add` | `{ lineStart, lineEnd, body }` | new inline comment |
| `comment.reply` | `{ id, body }` | reply in a thread |
| `comment.resolve` | `{ id }` | resolve a thread |
| `pr.list` | `{}` | request PRs (author / reviewer) |
| `pr.open` | `{ refOrUrl }` | open a PR by selection or URL |
| `diff.request` | `{ path, mode }` | request rendered/raw diff |
| `chat.send` | `{ text }` | message to the agent |
| `tree.request` | `{ path? }` | request the spec file tree |
| `doc.open` | `{ path }` | open a spec for editing |

## native → webview (events)

| `kind` | payload | meaning |
|--------|---------|---------|
| `preview.html` | `{ html, lineMap, version }` | rendered preview to inject |
| `diff.rendered` | `{ html, mode }` | rendered/raw diff to display |
| `commit.suggested` | `{ message }` | editable generated commit message |
| `pr.suggested` | `{ title, body }` | editable generated PR text |
| `pr.list` | `{ items }` | PRs where the user is author/reviewer |
| `comments.synced` | `{ list }` | current comment set for the doc |
| `image.inserted` | `{ markdown, cursorHint }` | link to insert at the cursor |
| `status` | `{ state, branch?, ahead?, behind?, dirty? }` | plain-language status (`state` ∈ Draft, Saving, Saved, InReview, ChangesRequested, Approved, Published) |
| `conflict.detected` | `{ sections }` | "someone else changed this too" data |
| `chat.delta` | `{ text }` | streaming agent output chunk |
| `chat.done` | `{ id }` | agent turn complete |
| `tree` | `{ nodes }` | spec file tree |
| `toast` | `{ level, message }` | plain-language notice |
| `error` | `{ message }` | plain-language error (never a stack trace) |
| `confirm.request` | `{ id, action, summary }` | ask the author to confirm a mutating action |

## Ordering & correctness rules

- **Staleness:** every `preview.html` / `diff.rendered` carries the `version` it was computed
  from; the webview ignores any result whose `version` is older than the latest local edit.
- **Correlation:** request messages set `id`; the matching reply echoes it. Unsolicited events
  (status, toast, chat.delta) carry no `id`.
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
