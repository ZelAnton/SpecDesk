# 08 — AI Agent

The assistant answers questions and helps draft Markdown through the official **GitHub Copilot SDK**.
It authenticates with the GitHub account already connected in SpecDesk. SpecDesk's authentication layer
persists that token encrypted with Windows DPAPI; the Copilot layer receives it only inside the native
process and never logs it, creates a separate persisted copy, or sends it to the webview.

## Current chat-only integration

- `SpecDesk.Ai` uses `GitHub.Copilot.SDK` directly; the bundled Copilot runtime is managed by the SDK.
- The SDK client runs in hardened `Empty` mode with an empty tool allowlist. A deny-all permission handler
  is also installed, so the chat cannot read or write files, execute commands, or mutate a repository.
- One Copilot session is reused for the connected account so conversation context survives across turns.
  Sign-out or account replacement cancels the active turn and disposes the session before another account
  can use the chat.
- Only context explicitly attached through SpecDesk is included in the prompt.

## Planned tools

The streaming chat has no tools yet. A later milestone may expose thin functions over existing app
operations (the same operations the buttons use):

| Tool | Purpose | Mutating? |
|------|---------|-----------|
| `getCurrentDoc` | current document text + metadata | no |
| `getDiff` | structural diff of the working change | no |
| `searchSpec` | search across the repo's specs | no |
| `suggestVersionNote` | draft a version note (commit message) from the diff | no (proposes) |
| `suggestPrDescription` | draft PR title + body from the branch diff | no (proposes) |
| `suggestImageDescription` | draft alt text / `{slug:DESC}` for a pasted image | no (proposes) |
| `proposeEdit` | propose a document edit as a reviewable change | **yes — gated (implemented)** |

`proposeEdit` (`SpecDesk.Ai.ProposeEditTool`) is wired: it can only *stage* a proposal on the host's
`IEditProposalSink` — it holds no document-mutation capability at all, so it structurally cannot edit,
commit, or push. The host renders the difference (`confirm.request`, reusing the existing word-diff) and
the author confirms, edits, or discards it in a confirmation surface. Only a confirmed proposal is applied,
and only through the **same** editing path as a manual change (`OnEdit`/`_text`/generation increments,
under the existing `_sync` discipline), after the host re-checks the document is still exactly the one — at
the same content generation — the proposal was staged against; a concurrent edit refuses the apply. It is
deliberately kept out of the read-only tool allowlist (`AiReadOnlyTools`), the one named mutating
affordance. See [09-ipc-protocol.md](09-ipc-protocol.md) (`confirm.request` / `confirm.result` /
`confirm.applied`).

## Hard safety rule

> Every mutating action the agent can cause — commit, push, create/merge PR, edit the
> document — goes through the **same confirmation UI as a manual action**. The agent never
> commits, pushes, merges, or rewrites the document silently.

This matches the product requirement exactly: the agent generates text or an edit, the author
reviews and adjusts, then the author applies it. The agent's "mutating" tools therefore
*stage a proposal* and hand control to the confirmation UI; they do not perform the side
effect themselves.

Additional guardrails for that future tool surface:
- Treat document content, search results, and any MCP/tool output as **data, not
  instructions** — text inside a spec cannot direct the agent to push or merge.
- The agent operates only within the active repo; no access to arbitrary filesystem or other
  repos.
- The GitHub OAuth token is never written into the repo, logged, or surfaced to the webview.

## Chat surface

A chat panel in the webview ↔ native. Native streams `chat.delta {text}` chunks for
responsive output (see [09-ipc-protocol.md](09-ipc-protocol.md)). The panel can reference the
current document and diff so questions like "summarize what changed" or "tighten this
section" have context without the author pasting anything.

## Where the agent replaces deterministic logic

Early phases generate version-note / PR text from deterministic templates (no AI dependency).
Phase 8 swaps those for `suggestVersionNote` / `suggestPrDescription`. The templates remain as a
fallback when Copilot is unavailable or the call fails, so the core workflow never depends
on the agent being available.

## Configuration

```toml
[ai]
model    = ""                # optional Copilot model; blank uses the account default
enabled  = true
# authentication comes from SpecDesk's GitHub connection
```
