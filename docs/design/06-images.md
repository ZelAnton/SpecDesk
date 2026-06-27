# 06 — Image Handling

Requirement: insert images by **drag-and-drop** or **copy-paste**, with the file
**automatically saved to a defined folder** and **named by a defined rule**, then linked in
the document — all without the author thinking about file paths or naming conventions.

## Capture paths

| Source | What arrives | Has original name? |
|--------|--------------|--------------------|
| Drag-and-drop a file onto the editor | file bytes + original filename | yes |
| Paste a copied image file | file bytes + original filename | usually |
| Paste a screenshot / copied bitmap | raw image bytes from clipboard | no — must generate |
| Drag-and-drop multiple files | several of the above | mixed |

The webview captures the drop/paste event, reads bytes, and sends `image.paste {base64,
originalName?, mime}` per image to the native side. All folder/name/processing logic is
native (F# rule engine).

## Pipeline

```
webview: drop/paste → image.paste {base64, originalName?, mime}
		│
		▼  native (SpecDesk.Core image-rule engine, F#)
	1. decode bytes; sniff real format (don't trust extension/mime)
	2. optional processing: re-encode to preferred format, strip metadata, downscale
	3. resolve target FOLDER  from .spectool.toml [images].folder (token expansion)
	4. resolve target NAME    from .spectool.toml [images].naming (token expansion)
	5. enforce constraints (case, length, allowed extensions); de-duplicate name
	6. write file into the repo working tree
	7. git stage the new file
	8. compute the relative link from the document to the file
		│
		▼
	native → webview: image.inserted {markdown, cursorHint}
	webview: insert  ![alt](relative/path)  at the cursor
	preview: resolves it via app://
```

## Folder rule

`[images].folder` is a path relative to the repo root, with token expansion. Examples:

| Pattern | Result for `docs/specs/billing.md` |
|---------|-----------------------------------|
| `images` | `images/` |
| `images/{docSlug}` | `images/billing/` |
| `{docDir}/images` | `docs/specs/images/` |
| `assets/{date:yyyy}/{docSlug}` | `assets/2026/billing/` |

Tokens: `{docDir}` (document's directory), `{docSlug}` (document filename without extension,
slugified), `{date:FORMAT}` (.NET date format).

## Naming rule

`[images].naming` is the filename **without extension**, with token expansion; the extension
comes from the (possibly re-encoded) format.

| Token | Meaning |
|-------|---------|
| `{docSlug}` | slug of the host document |
| `{date:FORMAT}` | capture date, .NET format string |
| `{seq}` | per-document sequence number (next free, zero-padded) |
| `{hash8}` | first 8 hex chars of the content hash (stable, dedups identical images) |
| `{slug:DESC}` | optional author-provided description, slugified (see below) |
| `{originalName}` | slugified original filename stem (drag-drop only) |

Default recommendation:

```toml
[images]
folder    = "images/{docSlug}"
naming    = "{docSlug}-{date:yyyyMMdd}-{seq}-{hash8}"
```

This yields stable, sortable, collision-resistant names like
`billing-20260614-003-9f3a1c0b.png`, with no spaces and no manual decisions.

### Optional description prompt

If `naming` contains `{slug:DESC}`, the app shows a tiny inline prompt ("describe this
image") after paste; the author types a few words, slugified into the name. If the token is
absent, no prompt — fully automatic. Screenshots (no original name) work either way because
`{seq}`/`{hash8}` always produce a valid name.

## Constraints enforcement

```toml
[images]
allowed         = ["png", "jpg", "jpeg", "gif", "webp", "svg"]
preferred       = "png"      # screenshots / bitmaps normalized to this
case            = "kebab"    # kebab | snake | lower
max-name-length = 80
```

- A name is slugified to the chosen case, spaces/diacritics removed, truncated to length
  (preserving the `{hash8}`/`{seq}` suffix so uniqueness survives truncation).
- If the source format is not in `allowed`, it is re-encoded to `preferred` (or rejected with
  a clear message if re-encoding is impossible, e.g. SVG when not allowed).

## Processing (optional, configurable)

```toml
[images]
max-width      = 2000        # downscale wider images (keeps repos lean)
reencode-paste = true        # always normalize clipboard bitmaps to `preferred`
```

EXIF/GPS stripping is automatic (not a toggle): re-encoding always drops metadata — see below.

Processing runs natively via SkiaSharp (MIT-licensed, cross-platform). Rationale: pasted
screenshots are often huge PNGs with metadata; normalizing keeps spec repos small and avoids
leaking metadata. A decode → re-encode always drops EXIF/XMP/ICC, so re-encoded output is
metadata-free; SVG and GIF pass through verbatim (Skia decodes but cannot encode GIF, so a GIF
target keeps the original bytes — preserving any animation).

## De-duplication

`{hash8}` is computed from the **processed** bytes. If a file with the same content hash
already exists in the target folder, reuse it instead of writing a duplicate — paste the link
to the existing file. This prevents the same screenshot being committed five times.

## Staging & links

- The new file is `git add`-ed immediately so it travels with the next saved version (commit).
- The inserted Markdown link is **relative to the document**, computed natively, so it renders
  correctly both in the app (via `app://`) and on GitHub.
- On document **rename/move**, the app can offer to fix image links and move the
  `{docSlug}`-scoped folder accordingly (tie-in with [04-git-workflow.md](04-git-workflow.md)
  rename handling).

## Failure handling

- Unsupported/corrupt image → clear toast, nothing written, nothing staged.
- Folder rule resolves outside the repo → rejected (never write outside the working tree).
- Name collision after all tokens → append a numeric disambiguator before giving up.
