# 07 — Review Experience (Comments + Rendered Diff)

This covers problems 2 and 3 together because both serve the review workflow and both rely on
the `lineMap` from [05-live-preview.md](05-live-preview.md).

## Core requirement: review works in both representations

Because the editor has both a **source** view and a **formatted (WYSIWYG)** view
([05-live-preview.md](05-live-preview.md)), **diff highlighting and inline comments must render and
anchor in both** — the reviewer sees the same review state whichever mode they are in, and switching
modes preserves it. This is a defining requirement, not a nice-to-have: a manager reviews in the
formatted view, a developer may prefer the source/raw view, and they are looking at *the same*
comments and the same change.

The bridge is the `lineMap` (source line range ↔ rendered/editor node). Everything below — the
comment model (Part A) and the rendered diff (Part B) — is defined against source lines, then
projected into whichever view is active:
- **Source view:** CodeMirror line/range decorations (gutter comment markers, changed-line styling)
  and the raw source diff.
- **Formatted view:** overlays anchored to editor-document positions via each node's source-line
  provenance (editor decorations / `data-line` markers).

---

## Part A — Inline comments

### Local model (source of truth in-app)

```fsharp
type Comment =
	{
		Id:			Guid
		Path:		string          // spec file
		CommitSha:	string          // commit the comment is anchored to
		LineStart:	int
		LineEnd:	int
		Body:		string
		Author:		string
		GithubId:	int64 option    // set once synced to a PR review comment
		Resolved:	bool
		Replies:	Reply list
	}
```

The local store holds comments even before a PR exists (e.g. a reviewer jotting notes), and
even on lines that are not part of any diff hunk. GitHub sync is a **projection** over this
local model, not the source of truth.

### Display

A comment's `[LineStart, LineEnd]` resolves through `lineMap` to a rendered node; an overlay
marker sits beside that node in the preview (and a gutter marker in the source pane). Clicking
opens the thread.

### GitHub sync

GitHub PR review comments anchor to `(path, commit_id, line, side)` within the **diff**. So:

- **Pulling:** fetch PR review comments via Octokit; map each `(path, line)` back through the
  current `lineMap` to a rendered node; display inline. Threads/replies preserved.
- **Posting:** a new local comment is pushed as a PR review comment **only if** an open PR
  exists and the target line falls inside a diff hunk. Map `[LineStart, LineEnd]` → diff
  position → create comment; store the returned `GithubId`.
- **Hard constraint:** GitHub will not accept a comment on a line outside the diff. The app
  keeps such comments local and labels them "not yet on GitHub (line unchanged in this
  review)" rather than failing silently.
- **Resolve / reply:** mirror to GitHub when `GithubId` is set; stay local otherwise.

### Reconciliation

When the head commit changes (author pushed an update), re-anchor comments: GitHub moves
comments it can and marks others outdated; the app mirrors that, re-resolving local anchors
through the new `lineMap` and flagging any it cannot place.

---

## Part B — Rendered semantic diff

Raw GitHub shows only line-level `.md` diffs. SpecDesk adds a **structural** diff plus a
rendered view, with a toggle between them.

### Inputs

For the selected PR, fetch the **base** and **head** versions of each changed `.md` file
(contents at base/head SHA, or PR file blobs via Octokit).

### Algorithm

```
base text ─► Markdig ─► F# Document (AST)  ┐
                                           ├─► AST diff (F#) ─► annotated tree
head text ─► Markdig ─► F# Document (AST)  ┘                          │
                                                                      ▼
                                          render to HTML (side-by-side or unified),
                                          nodes tagged added / removed / changed / moved
```

The diff is over the AST DU, so it can recognize:
- a **heading level change** (`## ` → `### `) as a single structural edit, not a whole-line
  replacement,
- a **moved paragraph** as a move rather than a delete+add,
- a **changed link target or image** as an attribute change on an otherwise unchanged node,
- list reordering, table cell changes, etc.

This is the "semantic analysis for building the comparison" from the original requirement.

### Matching strategy

Use a tree-diff that first matches nodes by a stable signature (type + normalized text for
leaves; type + matched children for containers), then classifies the remainder as
added/removed/changed, and finally detects moves by matching unmatched removed↔added nodes
with high text similarity. Keep it explainable — exhaustive `match` over the DU makes each
case auditable (and testable in `SpecDesk.Diff.Tests`).

### Output modes

- **Rendered side-by-side:** two rendered columns, changed regions highlighted, scroll-locked.
- **Rendered unified:** one rendered column with inline add/remove styling.
- **Raw source diff:** the familiar line diff, for when an author/reviewer wants the literal
  text. The **toggle** between rendered and raw is mandatory — it is the core of the feature.

### Interaction with comments

Comments are overlaid on the diff view too: a reviewer reads the rendered structural diff and
comments directly on a changed node, which resolves (via `lineMap`) to the head-commit source
line for GitHub posting.

### Notes / pitfalls

- Keep the AST projection identical to the preview path so the diff renders the same way the
  document does — one Markdig configuration, shared.
- Moves vs. delete+add is a heuristic; expose it as styling ("moved here") but never block on
  perfect detection.
- Large diffs: render top-level changed blocks first, fill in unchanged context lazily, so the
  view appears progressively rather than blocking.

---

## Part C — In-flight PR awareness & comparison

Part B diffs the **two ends of one PR** (its own base vs head). Part C answers a different
question the author has *while they are editing*: **"what else is in flight on this file, and how
does it differ from what I have?"** Spec repos are shared; two people editing the same document is
common, and the author needs to see overlapping work before they collide — not after.

### What the author sees

While a document is open for editing, SpecDesk shows the **open PRs that touch the current file**
(this generalizes the soft-lock warning in [04-git-workflow.md](04-git-workflow.md) from a one-line
"someone else is editing this" into something actionable). Selecting one opens a comparison of
**that PR's proposed version** of the file against a chosen base.

### Two comparison bases (modes)

The author picks what to compare the PR's version against:

1. **vs my working copy** — the author's current local content, *including unsaved changes* (the
   working copy on disk, not necessarily the last saved version). Answers "how does their proposal
   differ from exactly what I'm looking at right now?"
2. **vs `main`** — the published baseline. Answers "what does their PR actually change about the
   current published spec?" — i.e. it shows the PR as a reviewer on `main` would see it,
   independent of the author's own in-progress edits.

These are the only two modes. Mode 1 is the collision-avoidance lens (mine vs theirs); mode 2 is
the published-truth lens (theirs vs the shared baseline).

### Both representations

Each comparison is available in **both** representations, reusing the Part B engine:

- **Rendered** structural diff (side-by-side or unified) — the default, for reading.
- **Raw source** diff — the literal `.md` line diff, via the same mandatory toggle as Part B.

So Part C is, mechanically, **Part B's diff engine pointed at a different pair of inputs**: instead
of `(PR base, PR head)` it diffs `(chosen base, PR head)` where the chosen base is the working copy
or `main`. No new diff algorithm — only new input selection and a PR-list/affected-file query.

### Inputs

- **PR list for a file:** query open PRs whose changed-file set includes the current path (Octokit;
  filter the repo's open PRs by changed files, cache per Sync). Cheap to keep fresh in the
  background.
- **PR head content:** the file's blob at the PR head SHA (as in Part B).
- **Bases:** the working copy is already in memory (the editor buffer / disk file); `main` is the
  file's blob at the base branch tip from the local fetched clone.

### Boundaries (v1)

- **Read-only.** Part C is for *seeing* overlapping work, not merging it. Pulling another PR's
  changes into the author's draft, or resolving across PRs, is conflict-handling territory
  ([04-git-workflow.md](04-git-workflow.md)) and out of scope here.
- **Open PRs only**, touching **this one file** — not a general cross-repo PR browser.
- Comparison is **per file**; a PR touching several specs is compared one file at a time (the file
  currently open).
