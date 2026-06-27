# Borrowings catalog — `knowledge` → SpecDesk

> A reference, not a task list. When you build one of the roadmap PoCs below, consult the matching
> section here first: a working analog already exists in **Knowledge**, the author's more-mature
> sibling product — reuse its *logic and design* instead of reinventing.

## What Knowledge is, and why it's relevant

**Knowledge** (`@zelanton/knowledge`, the "ok" editor) is the **same author's** earlier, heavier Markdown
editor: an Electron + React + Tiptap/ProseMirror + Yjs/Hocuspocus + MCP monorepo
(`packages/{app,desktop,core,server,cli}` + a Next.js `docs/`). **SpecDesk is a leaner re-take of the same
product** on a different stack — Photino + WebView2, a C#/F# "brain", a framework-free vanilla-TS webview
(CodeMirror 6 + ProseMirror), **no** React and **no** CRDT. So Knowledge is **vetted prior art the author
already owns**; many SpecDesk roadmap PoCs have a tested analog there.

Source location: the Knowledge repo (a local working copy at `d:\GitHub\Personal\Temp\knowledge` at the time
of writing). Paths below are repo-relative within Knowledge.

**Provenance:** same author, so reuse is clean — but **re-implement, do not copy**. Knowledge's code style and
license boundary differ, and several files use `as`-casts that SpecDesk's strict TS-safety conventions (see
[AGENTS.md](../AGENTS.md)) forbid. Treat Knowledge's source as the *design spec* and its unit tests as the
*behavioural spec*.

## Three ways to borrow

| Mode | When | What you do |
|---|---|---|
| **Port→TS** | Pure, framework-agnostic TS logic | Re-implement in SpecDesk's vanilla-TS idiom (no `as`, `isRecord`/typed guards, `exactOptionalPropertyTypes`, Biome). Port Knowledge's unit tests as the spec. |
| **Port→F#/C#** | Logic that belongs on the native "brain" (GitHub/Octokit, git/conflict, MCP tools) | Re-implement natively; Knowledge's TS is the reference design. |
| **Adopt-as-design** | UX flows / state machines / discriminated-union models | Adopt the *shape*; implement in SpecDesk's stack. |

## Do NOT borrow (explicit non-goals)

- React `.tsx` components, Tiptap WYSIWYG, TanStack Query, Electron main/preload/IPC, shadcn/Radix — not
  portable, and against "native is the brain, webview is thin".
- **Yjs/Hocuspocus CRDT collaboration** — SpecDesk is single-user, git-based (roadmap non-goal: real-time
  co-editing).
- **Markdown REFLOW serialization** (Knowledge's `packages/core` remark round-trip) — SpecDesk's **block-splice**
  is the more minimal-diff approach; keep it. *Optional* later refinement: Knowledge stores per-node
  `sourceRaw`/`escapedChars` fidelity metadata to avoid re-escaping churn — a possible tweak to SpecDesk's
  serializer *inside* the blocks it re-emits.

---

## A. UX foundation — file tree, command palette, tabs — **Port→TS**

Pure, framework-agnostic, well-tested in Knowledge (no React/DOM/Electron imports). Pull in when SpecDesk
builds the corresponding sidebar / palette UI. **Port only when a consumer exists** (SpecDesk forbids dead
code).

**File tree** (`packages/app/src/components/`):

- `file-tree-adapter.ts` — path↔docName, tree-signature hashing, ancestor-path collection (~330 lines, pure).
- `file-tree-merge.ts` — merge a server listing with optimistic local adds, with stale-window pruning (solves
  "created locally, not on disk yet").
- `file-tree-utils.ts` — typed entry guards/transforms, hidden-file filter (the typed `FileEntry` union model).
- `file-tree-rename-validation.ts` — extension preservation on rename.
- `file-tree-okignore.ts` — gitignore-pattern escaping → maps to a `.spectool`-driven ignore feature.

**Command palette** (`packages/app/src/components/`):

- `command-palette-recents.ts` — recency / dedup / limit. Re-implement storage via the native side (not
  `localStorage`) and re-implement its `as`-based guard cleanly with `isRecord`/`isString`.
- `command-palette-semantic.ts` — pure view state-machine (show / dim / no-results / retry).
- `command-palette-tag-search.ts` → `rankTagsByQuery` ranking (starts-with > count desc > alpha).

**Generic utils** — `lib/lru-string-cache.ts`, `lib/path-utils.ts` (Win+Unix basename), `lib/doc-hash.ts`
(deep-link hash). Port only when needed.

> Tabs: no direct logic to borrow; `file-tree-adapter` path logic helps generate tab keys.

---

## B. GitHub publish + auth → **PoC-5** — **Adopt-as-design + Port→C#**

- **Auth — `packages/cli/src/auth/device-flow.ts`** uses **Octokit OAuth Device Flow** (scopes
  `repo`/`read:user`/`user:email`). This resolves SpecDesk's **open** "auth model" decision
  ([Spike-A / PoC-5](ROADMAP.md), device flow vs GitHub App): **adopt device flow** as the default (the roadmap
  already says "try device flow first"). Implement natively via Octokit + a secure OS-keyring token store.
- **Publish state machine — `packages/app/src/lib/share/publish-wizard.ts`:**
  - `sanitizeRepoName` (regex), `resolveNameCheckStatus` (discriminated union), `canSubmitPublish` (pure guard).
  - Especially **`presentPublishError` → an error→next-action discriminated union**
    (`edit-name | authorize-org | retry-push | reauth | edit-form`): the banner tells the user the *exact next
    action*. This is the model for SpecDesk's Send-for-review dialog, and the DU matches SpecDesk's own
    "no impossible states" TS-safety rule.
  - `packages/cli/src/commands/share/publish.ts` — `classifyOctokitError` (401→auth, 403+SAML→sso,
    422→name-conflict, …) and the "repo already exists → fetch + reuse" edge case → port to SpecDesk's C#
    Octokit layer.
- **Gap to fill:** SpecDesk adds the **PR step** (Knowledge only pushes to the default branch). The
  auth/validation/error scaffolding is the borrow.

---

## C. Conflict handling + publish, no markers → **PoC-10** — **Adopt-as-design + Port→F#/C#**

- **Conflict model — `packages/server/src/mcp/tools/conflicts.ts` + `resolve-conflict.ts`:** per-file
  `{ base, ours, theirs, shape, lifecycleStatus }` with a **shape discriminator**
  (`both-modified | delete-modify | modify-delete`) and a **strategy enum** (`mine`→checkout-ours,
  `theirs`→checkout-theirs, `content`→write merged bytes, `delete`→git rm), plus a guard rejecting invalid
  combos. This is exactly PoC-10's "no raw `<<<<<<<` markers ever" need — port the shape + strategy DU to F#/C#,
  and the "ours = what the user actually sees" rule.
- **Reconciliation UX — `packages/app/src/components/DiffView.tsx`:** a CodeMirror merge view with per-hunk
  accept/reject + a preview of the custom resolution. SpecDesk already has CodeMirror + an AST diff engine to
  build the plain-language "Someone else changed this too" dialog on; adopt the hunk accept/reject interaction.

---

## D. AI agent + MCP → **PoC-9** — **Adopt-as-design + Port→C#**

- **Tool anatomy — `packages/server/src/mcp/tools/*.ts`** (e.g. `index.ts`, `conflicts.ts`): each tool =
  `register(server, deps)` (dependency injection) with `inputSchema`/`outputSchema` and crucially the
  **annotations** `{ readOnlyHint, destructiveHint, idempotentHint }` for agent decision-making, plus a
  structured-plus-text output (`textResult` / `textPlusStructured`) and a logged wrapper. → model for SpecDesk's
  Microsoft-Agent-Framework tools.
- **Consent gate — `packages/app/src/lib/mcp-consent-store.ts` + `McpConsentDialogBody.tsx`:** an external store
  → payload of detected tools → user confirm → write config. → model for SpecDesk's confirmation-gate (roadmap:
  "every mutating action routes through `confirm.request`"); implement as a vanilla pub/sub store in the webview
  + a native confirm.
- Note: Knowledge's MCP tools are request/response — streaming chat (`chat.delta`/`chat.done`) is SpecDesk's own
  concern. The borrow is the tool/consent anatomy.

---

## E. (Optional) immediate small win

- `lib/document-stats.ts` — word/char count (CJK + Latin aware, strips frontmatter/code) → a SpecDesk
  status-bar word count. Independent of any PoC; only if desired.

---

## Verification of this catalog

This is a reference doc — there is no code to test. It is "correct" if each entry points at a real Knowledge
file and maps to the right SpecDesk PoC (paths confirmed during exploration; `command-palette-recents.ts` and
`publish-wizard.ts` were read in full). When a borrow is later implemented inside a PoC, that PoC's own tests
verify it.
