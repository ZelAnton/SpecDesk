# SpecDesk — UI Design Brief (for Claude Design)

> **Purpose of this document.** This is a hand-off brief for a visual/UX design pass. It describes
> what SpecDesk is, who it's for, the full planned feature set, the phased build plan, and an
> inventory of the **current** UI plus the **future** UI surfaces that still need to be designed.
> It is meant to be read **alongside screenshots of the current build**.
>
> **What I'm asking for (two deliverables):**
> 1. **A polished, cohesive UI design** for SpecDesk that honours the constraints below and
>    anticipates the planned features (so the visual language scales from today's editor to the full
>    review/AI workflow without a redesign).
> 2. **A Design Guide** — a reusable design system (tokens, components, states, patterns,
>    accessibility) we will follow as we implement each phase. Treat this as the source of truth we
>    code against.

---

## 1. The product in one paragraph

**SpecDesk** is a **Windows desktop application** that lets **non-technical authors (managers)** edit
**Markdown specifications stored in GitHub** — *without ever seeing git, branches, or pull requests*.
It wraps a Markdown editor (usable in three modes: source, split, and a formatted/WYSIWYG view),
automated git/GitHub operations behind plain-language actions, inline review comments, a rendered
(semantic) diff for review, automated image handling, and an embedded AI assistant. The whole thing
should feel as close to "editing a document in Office" as the GitHub reality allows: continuous
autosave, an explicit "save a version", a single "send for review" gate, plain-language status, and
**no merge-conflict markers or git jargon ever**.

## 2. Who the users are

- **Primary: managers / domain authors.** They write and revise specs. They come from editing
  `.doc`/`.docx` in Office 365. They are **not** developers and must never be asked to think in git
  terms. Low tolerance for jargon, modal complexity, or anything that feels "technical".
- **Secondary: reviewers.** Often more technical, but still inside SpecDesk's plain-language world.
  They read changes, leave comments, request changes, approve.
- **Tone to design for:** calm, trustworthy, document-centric, "office software" familiar — not a
  developer tool, not a flashy consumer app. Confidence and clarity over cleverness.

## 3. Design principles (these shape the visuals)

1. **No git vocabulary, ever.** Authors see *Edit, Saved, Save a version, Send for review, In
   review, Changes requested, Approved, Publish*. They never see *branch, commit, push, PR, merge,
   rebase, conflict markers*. (Full mapping in §6.) The **one** deliberate exception is the author's
   plain-language **version note** (which is technically a commit message, but presented as "describe
   your changes").
2. **Document-first.** The document is the hero. Chrome (toolbar, panels) is quiet and recedes; the
   content area dominates. Most of the time the user is reading or writing prose.
3. **Markdown is the single source of truth.** The formatted/WYSIWYG view is an *editable projection*
   of Markdown — edits flow back to clean Markdown. Visually, the rendered view should look like a
   well-typeset document, not like a web page.
4. **Calm, low-stress lifecycle.** State changes (draft → version saved → in review → approved →
   published) must be legible at a glance and never alarming. Errors are phrased in plain language,
   never as stack traces.
5. **Office-familiar, not developer-IDE.** Favour patterns a Word/Docs user recognises (a clear
   toolbar, version history, comments in a margin, "review" mode) over IDE patterns.
6. **Progressive disclosure.** Advanced/technical capability (e.g. comparing against other in-flight
   versions, raw-source diff toggle) is available but never in the user's face by default.

## 4. Platform & technical constraints (important for feasibility)

- **Desktop app, Windows-first.** Shell is **Photino** hosting the system **WebView2 (Chromium)**.
  The entire UI is **HTML/CSS/TypeScript inside one webview** — so anything web-renderable is fair
  game, but it must run **offline, locally, in a single resizable window** (no server, no CDN at
  runtime; assets are bundled). Design for desktop window sizes (target ≥ 1024×700, comfortably up to
  large monitors). **No mobile / touch layouts** needed for v1.
- **The editor is CodeMirror 6** (source/split modes) — it is themeable via CSS but has its own DOM
  structure (lines, gutters, scroller). The **formatted/WYSIWYG view** will use a rich-text engine
  (ProseMirror-family, TBD). Both must share one visual language.
- **The rendered preview is plain HTML produced natively** (a Markdown→HTML pipeline) and **styled
  only by our CSS** — the design of "rendered document" = a CSS stylesheet over standard HTML
  elements (h1–h6, p, ul/ol, blockquote, table, pre/code, img, hr, links). This is the single most
  reused visual surface (it appears in preview, formatted mode, diff, and comparison).
- **Cross-platform escape hatch exists** (future Linux/macOS via WebKit), so avoid Windows-only CSS
  hacks where a portable equivalent exists.
- **Light mode is the baseline; a dark mode is strongly desired** in the Design Guide (define both
  token sets). Respect system font rendering; a chosen typeface must be bundled (no runtime web
  fonts) or fall back gracefully to `system-ui`.
- **Accessibility:** keyboard-navigable, visible focus states, adequate contrast (WCAG AA),
  `aria-pressed`/labels on toggles. Authors may be non-expert; legibility matters.

## 5. Information architecture (the surfaces)

A single window, vertically: **Toolbar** (top) → optional **inline prompt bars** → **content area**
(one, two, or three panes depending on view mode) → (future) **side panels** (comments, AI chat,
review/PR info) and **dialogs** (conflict reconciliation, settings). A persistent **status** area
communicates lifecycle state. The design should define how side panels and the content area share
space (docked vs. overlay, resizable splitters).

## 6. Plain-language vocabulary (the most important table — never leak the right column to users)

| What the author sees | What actually happens (internal, hidden) |
|----------------------|------------------------------------------|
| Open a spec, **Edit** | fetch latest, create a working branch from the published version |
| **Saved** (automatic, continuous) | working copy written to disk — *no commit* |
| **Save a version** (+ a short note) | a commit, with the note as its message |
| **Send for review** | push branch + open a pull request |
| **In review** | PR is open, awaiting reviewers |
| **Changes requested** | a reviewer requested changes |
| inline **comment** | PR review comment |
| **Update review** | push newly saved versions to the same PR |
| **Approved** | PR approved |
| **Publish** | merge the PR |
| **Sync** (background) | fetch / prune |
| "Someone else changed this too" | merge/rebase conflict — surfaced *without* git markers |

## 7. Planned functionality (the full scope to design toward)

Grouped by capability. Not all exists yet (see §8 for status).

- **Authoring**
  - Markdown editor with **three view modes**: **Code** (source), **Split** (source + rendered), and
    **Formatted** (rendered). Formatted is read-only today; will become **WYSIWYG** (type directly
    into the rendered document; edits serialize back to clean Markdown).
  - Live rendered **preview** with bidirectional, pixel-aligned scroll-sync.
  - A future **formatting toolbar** (bold/italic/headings/lists/links/quote/table) for the
    formatted/WYSIWYG mode.
  - **Images**: drag-and-drop or paste → auto-filed into the repo with a tidy name → relative link
    inserted; resolved in preview.
- **Versioning & lifecycle (plain-language)**
  - **Edit** (start a draft, named), continuous autosave to disk, explicit **Save a version**
    (with an editable, plain-language note), **Discard**.
  - Status surface: *Read-only / Editing / Unsaved changes / Version saved* and later *In review /
    Changes requested / Approved / Published*.
- **Review & collaboration**
  - **Send for review** / **Update review** → opens & updates a pull request; reviewer assignment.
  - A list of relevant reviews/PRs (mine, assigned to me, by link).
  - **Rendered semantic diff**: review a change as a *structural, typeset* diff (heading level change,
    moved paragraph) — not raw line noise — with a **toggle to raw source diff**. Must render in
    **both** the source and formatted views.
  - **In-flight comparison**: while editing, see other open versions (PRs) touching the same file and
    compare any of them against *my working copy* or *the published version (main)*, rendered or raw.
  - **Inline comments**: comment on the document in-app, in **both** source and formatted views;
    synced two-way with GitHub PR comments; threads, replies, resolve; "not yet on GitHub" state for
    comments outside the diff.
  - **Conflict reconciliation**: a gentle "Someone else changed this too" dialog (Keep mine / Keep
    theirs / Combine / Ask for help) — **never** raw conflict markers.
  - **Publish** (merge), when permitted.
- **AI assistant**
  - A chat panel (streaming) that drafts version notes / review descriptions and answers questions
    about the document. **Every mutating action is gated** behind an explicit confirm.
- **Settings**
  - Connected repositories, reviewers, image rules, AI provider, light/dark — presented plainly.

## 8. Phased implementation plan (current vs. future — so the design can stage)

We build as thin vertical slices ("PoCs"). **The design should make today's surfaces beautiful and
define the visual language for the future ones**, but does not need pixel-final mocks of phases that
are far out — clear patterns + a few key screens are enough.

**Done / in place today**
- Native↔webview shell, local asset serving.
- **Editor + live preview + scroll-sync** (pixel-aligned height-sync).
- **Images** (paste/drop → filed + linked).
- **Local versioning lifecycle**: Edit → autosave → Save a version → Discard, all plain-language.
- **Three view modes** (Code / Split / Formatted; Formatted currently read-only).
- (Plumbing) structured logging + an "Export log…" action.

**Next (designed soon, built next)**
- **Send for review / Update review** + review/PR list and richer status (In review / Changes
  requested / Approved).
- **Rendered semantic diff** (+ raw toggle), in both views.
- **In-flight PR comparison** (vs working copy / vs main).

**Later (define the pattern now, refine later)**
- **Inline comments** (in both views, GitHub-synced).
- **AI assistant** chat panel with confirmation gates.
- **Conflict reconciliation** dialog & **Publish**.
- **WYSIWYG editing** in the formatted view + **formatting toolbar**.
- **Settings** surfaces.

## 9. Current interface — inventory (what the screenshots show)

The current UI is intentionally utilitarian (functional, not yet designed). Today it consists of:

- **Top toolbar** (single row, light background, bottom border), left-aligned buttons:
  - `Open…`, `Edit`, `Save version` (shown only while a draft is active), `Discard` (draft only),
    `Save`, `Wrap: on/off` (toggle), a **segmented view-mode control** `Code | Split | Formatted`
    (active segment filled), `Export log…`.
  - A right-aligned **status text** (e.g. the document path, or `Editing` / `Unsaved changes` /
    `Version saved`, or an error/notice message). Currently plain grey text, ellipsised.
- **Inline prompt bars** (appear directly under the toolbar when triggered; currently tinted strips):
  - **"Name new draft"** bar (light blue): a text field prefilled with a suggested draft name +
    `Start editing` / `Cancel`. Appears on **Edit** (or when the author starts typing in a read-only
    doc).
  - **"Please describe changes"** bar (light yellow): the version-note editor — single-line by
    default with a `⌄` expander to a multi-line textarea + `Save` / `Cancel`. Appears on **Save
    version**. This is the *only* place a "commit message" surfaces, in plain words.
- **Content area** (fills the rest), one/two panes by mode:
  - **Editor pane** (CodeMirror): monospace source, soft-wrap toggle, a faint **caret-line
    highlight** and a fainter **mouse-hover line highlight**. In split mode it shows synthetic
    **spacer rows** (a faint diagonal hatch) inserted to vertically align source blocks with the
    rendered side — these read as "service padding", not content.
  - **Preview pane** (rendered HTML): headings, paragraphs, lists, tables (bordered), code blocks
    (light grey, rounded), images (max-width constrained). The block matching the editor caret is
    highlighted (currently a yellow wash); the block under the mouse is faintly highlighted.
- **Current ad-hoc visual values** (to be *replaced* by the Design Guide, listed so you can map the
  screenshots): system-ui font; text `#1a1a1a`; toolbar `#fafafa` on `#ddd` borders; status `#666`;
  draft bar `#e7f0ff`; version bar / active block `#fff7d6`; hover block `#f2f2f2`; code block
  `#f3f3f3`; table borders `#ccc`. These are placeholders — please define a real palette.

**Lifecycle states the current UI moves through** (each needs a clear visual treatment):
`No document` → `Read-only (published)` → `Naming a draft` → `Editing` → `Unsaved changes` →
`Version saved` → *(future)* `In review` → `Changes requested` → `Approved` → `Published`.

## 10. Future interface — surfaces that still need designing

Please design these as part of the cohesive system (patterns + key screens):

1. **Review / PR panel** — current review status, reviewers, a list of relevant reviews; the
   `Send for review` / `Update review` actions and the title/description editor.
2. **Rendered semantic diff view** — side-by-side and unified; structural changes (added / removed /
   changed / moved) typeset like the document; a **rendered ↔ raw source** toggle; works inside both
   the source and formatted views.
3. **In-flight comparison** — a picker of other open versions touching this file, with a base
   selector (*vs my working copy* / *vs published*), reusing the diff view.
4. **Inline comments** — comment markers/gutter in the editor and overlays in the formatted view;
   comment threads (author, time, replies, resolve); a "synced / not yet on GitHub" indicator;
   how comments coexist with the document margin.
5. **AI assistant panel** — a docked, streaming chat; message types; a clear **confirmation gate**
   pattern for any action the assistant proposes (preview the change → author edits → confirm).
6. **Conflict reconciliation dialog** — the "Someone else changed this too" flow with options
   *Keep mine / Keep theirs / Combine / Ask for help*, shown as understandable document differences,
   **never** as conflict markers.
7. **Publish** — the gated "merge" action and its confirmation/success state.
8. **Formatting toolbar** — for WYSIWYG/formatted mode (bold, italic, heading, list, link, quote,
   table, image).
9. **Settings** — repositories, reviewers, image rules, AI provider, theme.
10. **Cross-cutting**: toasts/notifications, empty states (no document / no reviews), loading &
    background-sync indicators, error/notice presentation (plain language), onboarding/first-run,
    the app icon & window chrome treatment.

## 11. What the Design Guide should contain

A practical, implementable design system we can build against:

- **Foundations / tokens:** colour palette (light **and** dark), semantic colour roles
  (surface, border, text, accent, success/info/warning/danger, plus lifecycle-state colours),
  typography scale (UI font + the rendered-document type scale for h1–h6/body/code), spacing scale,
  radii, elevation/shadow, motion (durations/easing for the calm feel).
- **Rendered-document stylesheet** — the typeset look for Markdown HTML (headings, paragraphs, lists,
  tables, blockquotes, code blocks, inline code, links, images, hr). This is the product's core
  reading surface; make it genuinely beautiful and readable.
- **Editor theme** — a CodeMirror 6 colour theme consistent with the above (syntax, gutter,
  active-line, selection, the height-sync spacer treatment).
- **Components:** buttons (primary/secondary/ghost, toggle/segmented), inputs & textareas, the inline
  prompt bars, status/lifecycle badges, toasts, dialogs/modals, side-panel chrome, tabs/toggles,
  comment thread, diff chrome (add/remove/move markers), chat bubbles + confirm-gate.
- **States & patterns:** the lifecycle-state visual language; focus/hover/active/disabled;
  loading/skeleton; empty/error; light/dark switching.
- **Layout patterns:** toolbar, pane splitters, docked vs. overlay panels, responsive behaviour
  across desktop window sizes.
- **Accessibility notes:** contrast, focus order, keyboard shortcuts, ARIA expectations.
- **Iconography:** a coherent set (or a recommended icon library) for toolbar/actions.

## 12. Deliverables checklist

- [ ] A visual concept / mockups for the **current** screens (editor in all three modes, the two
      inline bars, the lifecycle states).
- [ ] Mockups (or clear patterns) for the **priority future** surfaces: review panel, rendered diff,
      comparison, inline comments, AI panel.
- [ ] The **Design Guide** (§11) — tokens, the rendered-document stylesheet, editor theme,
      component library, states, layout, a11y — in a form we can implement in CSS/TS.
- [ ] **Light and dark** token sets.

## 13. Non-goals / out of scope (for this design pass)

- Mobile / touch / responsive-to-phone layouts.
- A marketing site or app-store assets.
- Branding/naming work — **"SpecDesk" is a working title** and may change; keep the wordmark
  swappable, don't over-invest in a final logo.
- Real-time multi-cursor co-editing (not a product feature).
- Anything that requires exposing git mechanics to the author.

---

*Companion reading (deeper detail, optional):* the concept and architecture live in
`docs/design/01-concept.md` and `docs/design/02-architecture.md`; the live-preview/editor model in
`docs/design/05-live-preview.md`; the review experience in `docs/design/07-review-experience.md`; the
phased plan in `docs/ROADMAP.md`.
