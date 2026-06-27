# 10 — Repository Configuration (`.spectool.toml`)

Per-repo rules live in a `.spectool.toml` file committed at the repo root, so they travel with
the specs and are owned by the maintainer. Authors never edit this. Every section is optional;
sensible defaults apply.

## Full schema with defaults

```toml
# ---------------------------------------------------------------------------
# repo: base branch and which files are "specs"
# ---------------------------------------------------------------------------
[repo]
default-base = "main"
spec-globs   = ["**/*.md"]          # which files the app treats as specs
hidden-globs = ["node_modules/**", ".github/**"]   # never shown in the tree

# ---------------------------------------------------------------------------
# branch: how working branches are named (invisible to authors)
# ---------------------------------------------------------------------------
[branch]
# Tokens: {docSlug} {date:FORMAT} {user} {seq}
pattern = "spec/{docSlug}-{date:yyyyMMdd}"

# ---------------------------------------------------------------------------
# commit: how the "Save a version" note is seeded
# ---------------------------------------------------------------------------
[commit]
# Seeds the editable version note shown when the author clicks "Save a version".
# {summary} is filled by the deterministic template (or the agent later); the author
# can always edit the result before the version is committed.
template       = "{summary}"
squash-on-publish = false           # squash the branch into one commit on merge

# ---------------------------------------------------------------------------
# review: PR defaults
# ---------------------------------------------------------------------------
[review]
reviewers            = ["codeowners"]   # list of @users/@teams, or "codeowners"
allow-author-publish = false            # may the author merge their own approved PR?
draft-first          = false            # open PRs as draft until marked ready
help-contact         = "@spec-leads"    # pinged by the "Ask for help" conflict escape hatch

# ---------------------------------------------------------------------------
# markdown: keep app rendering aligned with GitHub's
# ---------------------------------------------------------------------------
[markdown]
extensions = ["tables", "task-lists", "footnotes", "definition-lists", "autolinks"]

# ---------------------------------------------------------------------------
# images: folder + naming + processing (see 06-images.md)
# ---------------------------------------------------------------------------
[images]
folder          = "images/{docSlug}"
naming          = "{docSlug}-{date:yyyyMMdd}-{seq}-{hash8}"
allowed         = ["png", "jpg", "jpeg", "gif", "webp", "svg"]
preferred       = "png"
case            = "kebab"
max-name-length = 80
max-width       = 2000
reencode-paste  = true

# ---------------------------------------------------------------------------
# ai: agent provider (credentials come from app settings, NOT this file)
# ---------------------------------------------------------------------------
[ai]
enabled  = true
provider = "claude"
model    = "claude-opus-4-8"
```

## Token reference

| Token | Available in | Meaning |
|-------|--------------|---------|
| `{docSlug}` | branch, images.folder, images.naming | host document filename (no ext), slugified |
| `{docDir}` | images.folder | host document's directory, relative to repo root |
| `{date:FORMAT}` | branch, images.* | date, .NET format string (e.g. `yyyyMMdd`) |
| `{user}` | branch | current user login, slugified |
| `{seq}` | branch, images.naming | next free sequence number, zero-padded |
| `{hash8}` | images.naming | first 8 hex of processed-content hash |
| `{slug:DESC}` | images.naming | optional author description, slugified (prompts if present) |
| `{originalName}` | images.naming | slugified original filename stem (drag-drop only) |

## Validation

- The app validates `.spectool.toml` on repo open and reports problems to the maintainer in
  plain language; invalid sections fall back to defaults rather than breaking the app.
- `images.folder` must resolve **inside** the repo working tree; paths escaping the repo are
  rejected.
- `reviewers = ["codeowners"]` reads the repo's `CODEOWNERS`; explicit `@user`/`@team` entries
  override it.

## What is deliberately NOT here

- Credentials (GitHub tokens, AI keys) — those live in per-user app settings under `%AppData%`,
  never in the repo.
- Anything authors should decide per-edit — the config is maintainer-owned policy, not
  per-document state.
