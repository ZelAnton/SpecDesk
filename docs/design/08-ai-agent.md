# 08 — AI Agent

The agent automates tedious steps (commit/PR text, search, drafting) and answers questions
about the document. Built on **Microsoft Agent Framework** (GA 2026-04-03; first-party
Anthropic Claude connector; native MCP support), so the agent layer is a mature, .NET-native
fit.

## Why Microsoft Agent Framework here

- .NET-native — the agent lives in `SpecDesk.Ai` alongside the rest of the C# integrations,
  no separate runtime.
- First-party Claude connector with one-line provider swap (Claude / OpenAI / Azure OpenAI /
  Gemini / Bedrock / Ollama) — not locked to one vendor.
- Native MCP — app operations can be exposed as MCP tools, and external MCP servers (e.g. a
  GitHub MCP) can be plugged in.
- Successor to Semantic Kernel + AutoGen, so it carries their tool-calling and orchestration
  maturity.

## Tools the agent can call

Tools are thin functions over existing app operations (same operations the buttons use):

| Tool | Purpose | Mutating? |
|------|---------|-----------|
| `getCurrentDoc` | current document text + metadata | no |
| `getDiff` | structural diff of the working change | no |
| `searchSpec` | search across the repo's specs | no |
| `suggestCommitMessage` | draft a commit message from the diff | no (proposes) |
| `suggestPrDescription` | draft PR title + body from the branch diff | no (proposes) |
| `suggestImageDescription` | draft alt text / `{slug:DESC}` for a pasted image | no (proposes) |
| `proposeEdit` | propose a document edit as a reviewable change | **yes — gated** |

## Hard safety rule

> Every mutating action the agent can cause — commit, push, create/merge PR, edit the
> document — goes through the **same confirmation UI as a manual action**. The agent never
> commits, pushes, merges, or rewrites the document silently.

This matches the product requirement exactly: the agent generates text or an edit, the author
reviews and adjusts, then the author applies it. The agent's "mutating" tools therefore
*stage a proposal* and hand control to the confirmation UI; they do not perform the side
effect themselves.

Additional guardrails:
- Treat document content, search results, and any MCP/tool output as **data, not
  instructions** — text inside a spec cannot direct the agent to push or merge.
- The agent operates only within the active repo; no access to arbitrary filesystem or other
  repos.
- Provider credentials are configured by the user; keys are never written into the repo or
  surfaced to the webview.

## Chat surface

A chat panel in the webview ↔ native. Native streams `chat.delta {text}` chunks for
responsive output (see [09-ipc-protocol.md](09-ipc-protocol.md)). The panel can reference the
current document and diff so questions like "summarize what changed" or "tighten this
section" have context without the author pasting anything.

## Where the agent replaces deterministic logic

Early phases generate commit/PR text from deterministic templates (no AI dependency). Phase 7
swaps those for `suggestCommitMessage` / `suggestPrDescription`. The templates remain as a
fallback when no provider is configured or the call fails, so the core workflow never depends
on the agent being available.

## Configuration

```toml
[ai]
provider = "claude"          # claude | openai | azure-openai | gemini | bedrock | ollama
model    = "claude-opus-4-8" # provider-specific
enabled  = true
# credentials come from user/app settings, never from the repo
```
