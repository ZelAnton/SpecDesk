# 03 — Roadmap

Phases are ordered so each one ships something usable and does not block on the next. Earlier
phases deliver value with no GitHub dependency at all.

## Phase 0 — Skeleton

- Photino window hosting WebView2; esbuild pipeline for the `webview/` bundle.
- IPC router with the typed envelope (see [09-ipc-protocol.md](09-ipc-protocol.md)) and a
  round-trip echo test.
- `app://` custom scheme handler serving a chosen working folder.

**Ships:** an empty shell that proves the native↔webview contract.

## Phase 1 — Editor + live preview (no git)

- CodeMirror 6 source editor in the webview.
- Markdig parse → HTML + `lineMap`; preview panel renders injected HTML.
- Scroll-sync driven by `lineMap`.
- Local file open / save (plain filesystem, no git yet).

**Ships:** a working local Markdown editor with live preview and scroll-sync. Already useful.

## Phase 2 — Images

- Drag-and-drop and clipboard paste capture in the editor.
- Image-rule engine: target folder + generated name per `.spectool.toml`; write file; insert
  link; preview resolves it via `app://`.
- Optional normalization (re-encode to preferred format, strip metadata, downscale).

**Ships:** the single highest-value, self-contained feature — fully usable before any git.

## Phase 3 — Git layer (local)

- Repo registration; background auto-fetch.
- Document lifecycle state machine: edit → branch (silent) → autosave to the **working copy**
  (no commit) → explicit **Save a version** (commit).
- The version note (commit message) is generated (deterministic template first; agent later)
  and editable by the author before the version is saved.
- Plain-language status: Editing / Unsaved changes / Version saved.

**Ships:** edits are versioned in git on the author's explicit "save a version", still entirely
local, with zero git vocabulary shown.

## Phase 4 — GitHub: send for review

- Octokit auth (device flow / PAT / GitHub App — decide in this phase).
- "Send for review" (offered after the first saved version): push branch + open PR; generated
  title/description (editable). "Update review" pushes later saved versions.
- PR list: documents where the user is author, reviewer, or by URL.
- Status: In review / Changes requested / Approved.

**Ships:** the full author round-trip up to an open PR, all via explicit actions.

## Phase 5 — Rendered semantic diff

- Fetch base + head versions of changed `.md` files.
- AST diff (F#) → rendered side-by-side / unified HTML.
- Toggle: raw source diff ↔ rendered diff.
- The diff must render in **both** the source and the formatted editor views (the lineMap anchors
  it either way) — see the editor-modes track below.

**Ships:** the review experience that raw GitHub cannot provide (problem 3).

## Phase 6 — In-flight PR awareness & comparison

- List the open PRs that touch the file being edited.
- Compare a chosen PR's version against either the local working copy or `main`, in both the
  rendered and raw representations — reusing the Phase 5 diff engine on different inputs.
- Promote the soft-lock "someone else is editing this" warning into this comparison entry point.

**Ships:** an author can see and understand overlapping in-flight work before colliding (problem 4).

## Phase 7 — Inline comments

- Local comment model anchored via `lineMap`.
- GitHub sync: map ranges to PR review-comment positions; pull existing comments; post new
  ones; replies; resolve.
- Comments render and anchor in **both** the source and formatted editor views; switching modes
  preserves them — see the editor-modes track below.

**Ships:** in-app commenting synchronized with GitHub (problem 2).

## Phase 8 — AI agent

- Microsoft Agent Framework agent with tools over app operations.
- Agent-generated version-note / PR text (replaces the deterministic templates).
- Chat panel with streaming.
- Strict confirmation gate on every mutating action.

**Ships:** assistant in the loop (problem 6).

## Phase 9 — Conflict handling & polish

- Rebase-on-send; friendly "someone else changed this too" reconciliation; maintainer escape
  hatch.
- New-spec creation, rename/delete as reviewable changes.
- Publish (merge) path, gated by `allow-author-publish`.

**Ships:** production-ready manager workflow.

## Editor view modes & WYSIWYG (foundational editor track)

A foundational track that extends the Phase 1 editor and parallels the GitHub track. Two steps:

- **Editor view modes** — code / split / formatted, switchable (the HedgeDoc-style shell). The
  formatted view starts as the existing read-only render. Reuses the Phase 1 pipeline + lineMap.
- **WYSIWYG editing in the formatted view** — the formatted view becomes editable; edits serialize
  straight back to Markdown (the source of truth) with a minimal, lossless round-trip. Needs a real
  editor engine (ProseMirror dual-mode is the leading candidate); the round-trip fidelity is the
  central risk and is **spiked first** on real specs. See [05-live-preview.md](05-live-preview.md)
  and the reference editors in [../../AGENTS.md](../../AGENTS.md).

**Ships:** the author can edit the rendered document directly, in any of three modes, with Markdown
kept clean. This track is a prerequisite for the *formatted-view* side of Phases 5 (diff) and 7
(comments).

## Sequencing notes

- Phases 1 and 2 give a genuinely useful local tool with no network or GitHub risk — good for
  early dogfooding with one or two managers.
- Auth (Phase 4) is the first real integration risk; spike it early even if the UI lags.
- The `lineMap` built in Phase 1 is reused by Phases 5, 6, and 7 — get it right early.
- Phase 6 (comparison) is the Phase 5 diff engine pointed at new inputs; it adds a PR-by-file
  query and a base selector, not a new algorithm.
