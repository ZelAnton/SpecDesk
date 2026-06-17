# 01 — Concept

## Vision

A desktop application that makes editing Markdown specifications stored in GitHub feel as
approachable as editing a document in Office 365, while preserving the full git/GitHub
model underneath (real branches, commits, pull requests, reviews). Authors get a friendly
editor and a single "send for review" gate; reviewers and maintainers get genuine PRs and
diffs.

## Problems being solved

1. **Editing requires git/GitHub skill.** Today an author must clone, branch, commit, push,
   and open a PR by hand. Not everyone can. → All git/GitHub operations are automated and
   hidden behind plain-language actions.
2. **Commenting in the GitHub web UI is awkward.** → Inline comments are placed directly on
   the rendered/source document inside the app and synchronized with GitHub.
3. **PR review is only available as raw Markdown diff.** → The app compares both the raw
   source and a rendered view, using a semantic (AST-level) diff so structural changes read
   as structure, not line noise.
4. **Overlapping edits are invisible until they collide.** In a shared spec repo, several
   people may be changing the same document at once, and an author has no easy way to see
   other in-flight proposals or how they differ from their own work. → The app shows the open
   reviews (PRs) that touch the file being edited and lets the author compare any of them —
   rendered or raw — against either their own working copy or the published `main`.
5. **Inserting images is fiddly.** Spec repos have rules about where image files live and how
   they must be named. → Images are inserted by drag-and-drop or paste; the app saves the
   file to the correct folder, generates a compliant name, stages it, and inserts the link
   automatically.
6. **No assistant in the loop.** → An embedded AI agent automates the tedious bits
   (version notes / PR text, search, drafting) under explicit user confirmation.

## Target users

- **Author (manager).** Edits specs. Should never need to know what a branch is. Primary
  persona; the whole UX is tuned for them.
- **Reviewer.** Reviews proposed changes, leaves inline comments, approves or requests
  changes. May be another manager or a developer.
- **Maintainer.** Owns the repo, decides who can publish (merge). Usually a developer or
  lead. Configures the per-repo rules (`.spectool.toml`).

## Design principles

- **Hide git vocabulary; keep git mechanics.** The UI speaks Draft / In review / Approved /
  Published. The disk still holds ordinary branches and commits a developer can inspect.
- **One spec, one editing session, one review.** The default unit of work is "I am editing
  this document." That maps to one branch and one PR. Multi-file changes are a power-user
  feature, not the default.
- **Continuous autosave, explicit versioning, explicit sharing.** Three distinct levels: typing
  autosaves to disk continuously and silently (never lose work, no commit); the author then
  **saves a version** when a state is worth keeping (an explicit commit, with a short editable
  note); and **sending for review / updating** is a further deliberate button. We do *not*
  auto-commit on idle — committing is the author's decision, because only they know when a change
  is a real version, and their note is what makes the history legible.
- **Never show merge-conflict markers to an author.** Conflicts are surfaced as "someone
  else changed this too" with a simple reconciliation choice, or escalated to a maintainer.
- **Native owns logic; webview stays thin.** See [02-architecture.md](02-architecture.md).
- **The agent proposes, the human disposes.** Every mutating action the agent can take goes
  through the same confirmation UI as a manual action. No silent commits, pushes, or merges.
- **Rules live in the repo.** Image folders, naming, reviewers, base branch — all configured
  in a file committed alongside the specs, so they travel with the repo.

## Non-goals (v1)

- Not a real-time collaborative editor (no simultaneous multi-cursor editing). Collaboration
  happens through the review workflow, not live co-editing.
- Not a general Markdown notes app or wiki. It operates on files in a git repo, full stop.
- Not cross-platform yet. Windows-only; the stack choice merely keeps the door open.
- Not a replacement for developer git tooling. Developers keep using their normal tools; this
  serves the non-developer path into the same repos.
- No WYSIWYG. Authors see Markdown source with a live rendered preview beside it
  (explicit product decision — keeps diffs clean and round-tripping lossless).

## Success criteria

- An author who has never used git can open a spec, edit it, save versions with plain-language
  notes, add an image, send it for review, respond to inline comments, and get it published —
  without learning a single git term.
- Before colliding, an author can see the other open reviews touching the file they are editing
  and compare them — rendered or raw — against their own working copy or against `main`.
- A reviewer can review the change as a rendered, structural diff, not as raw `.md` lines.
- The resulting git history is clean enough that a developer reviewing the same PR sees
  sensible commits and minimal formatting noise.
