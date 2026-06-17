# SpecDesk — Documentation

> **Working title.** `SpecDesk` is a placeholder name; rename before any registry/namespace work.

SpecDesk is a Windows desktop application that lets non-technical authors (managers) edit
Markdown specifications stored in GitHub **without ever touching git, branches, or pull
requests directly**. It wraps a source + live-preview Markdown editor, automated git/GitHub
operations, inline review comments, a rendered (semantic) PR diff, automated image handling,
and an embedded AI agent — all behind plain-language UI.

## How these documents are organized

| File | What it covers |
|------|----------------|
| [01-concept.md](01-concept.md) | Vision, the problems being solved, target users, design principles, non-goals. |
| [02-architecture.md](02-architecture.md) | Stack, process model, module layout (C#/F#), native↔webview split, data flow. |
| [03-roadmap.md](03-roadmap.md) | Phased build plan; every phase ships usable value. |
| [04-git-workflow.md](04-git-workflow.md) | The manager-friendly git/GitHub workflow and its vocabulary mapping. |
| [05-live-preview.md](05-live-preview.md) | Markdown pipeline, AST model, line mapping, scroll-sync. |
| [06-images.md](06-images.md) | Drag-and-drop, paste, auto-save, folder + naming rules. |
| [07-review-experience.md](07-review-experience.md) | Inline comments (local + GitHub sync), the rendered semantic diff, and comparing against in-flight PRs. |
| [08-ai-agent.md](08-ai-agent.md) | Microsoft Agent Framework integration, tools, safety rules. |
| [09-ipc-protocol.md](09-ipc-protocol.md) | The full native↔webview message protocol reference. |
| [10-repo-config.md](10-repo-config.md) | `.spectool.toml` — per-repository configuration reference. |

## The one idea behind everything

> The **native side (C#/F#) is the brain and owns all logic and all UI chrome.**
> The **webview is a thin, "dumb but pretty" surface**: it hosts the CodeMirror source
> editor and renders HTML that the native side computes. TypeScript stays minimal.

This keeps almost all meaningful code in C#/F# (the strongest stack here), reserves the
webview for the one thing that genuinely needs the DOM (the editor), and gives the AI agent
a typed, native surface to act through.

## Plain-language vocabulary (the most important table in this repo)

Authors never see git terms. Internally everything is still real git/GitHub.

| What the author sees | What actually happens |
|----------------------|-----------------------|
| Open a spec, **Edit** | fetch latest, create a working branch from the published version |
| **Saved** (automatic, continuous) | working copy written to disk — **no commit** |
| **Save a version** (+ a short note) | a commit, with the note as its message — the author's explicit choice |
| **Send for review** | push branch + open a pull request |
| **In review** | PR is open, awaiting reviewers |
| **Changes requested** | a reviewer requested changes on the PR |
| inline **comment** | PR review comment |
| **Update review** (after saving a version while in review) | push the new versions to the same PR |
| **Approved** | PR approved |
| **Publish** | merge the PR |
| **Sync** (background) | fetch / prune |
| "Someone else changed this too" | merge/rebase conflict, surfaced without git markers |

## Target environment

- Windows-only for v1. Shell is **Photino** (system WebView2 under the hood) rather than a
  heavy UI framework, chosen partly to keep a cross-platform escape hatch open.
- Authors come from editing `.doc` files in Office 365. The workflow is tuned to feel as
  close to that as the GitHub reality allows — continuous autosave to disk, an explicit
  "save a version" (like Office Version History), a single "send for review" gate,
  plain-language status, no merge-conflict markers.
