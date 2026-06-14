# 07 — Review Experience (Comments + Rendered Diff)

This covers problems 2 and 3 together because both serve the review workflow and both rely on
the `lineMap` from [05-live-preview.md](05-live-preview.md).

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
