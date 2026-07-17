# SpecDesk — User Guide

SpecDesk is a desktop editor for the Markdown specifications your team keeps on GitHub. It looks
and behaves like an ordinary document editor — open a spec, edit it, save a version, send it for
review, and see it published — without ever asking you to understand the tools developers use to
store that history. This guide walks through that everyday flow, screen by screen, plus what to do
if something doesn't look right.

If a term here doesn't match what you see on screen, the screen is right — SpecDesk changes
often. This guide is kept up to date alongside the app; see `CHANGELOG.md` for what's new.

## Contents

- [Starting SpecDesk](#starting-specdesk)
- [Opening a repository, a folder, or a single file](#opening-a-repository-a-folder-or-a-single-file)
- [Repositories: local copies and working lines](#repositories-local-copies-and-working-lines)
- [The editor: Code, Split, and Formatted](#the-editor-code-split-and-formatted)
- [Editing: from Published to a draft](#editing-from-published-to-a-draft)
- [Saving a version](#saving-a-version)
- [Seeing what changed](#seeing-what-changed)
- [Sending your draft for review](#sending-your-draft-for-review)
- [Commenting directly on the text](#commenting-directly-on-the-text)
- [The change request: description, reviewers, history, comments](#the-change-request-description-reviewers-history-comments)
- [Keeping track of reviews and change requests](#keeping-track-of-reviews-and-change-requests)
- [After approval: publishing](#after-approval-publishing)
- [The AI assistant](#the-ai-assistant)
- [Disk and favorites](#disk-and-favorites)
- [Your account, notifications, and settings](#your-account-notifications-and-settings)
- [If something goes wrong](#if-something-goes-wrong)

## Starting SpecDesk

SpecDesk opens on the **Start** screen: a title, a short prompt, and three buttons —
**Open a file**, **Open a folder**, and **Open Repository**. Below them sit two shortcut lists:

- **Favorites** — repositories, folders, and specs you've starred, so they're always one click
  away.
- **Recent** — the items you opened most recently.

Both lists stay empty (with a short hint in their place) until you've opened or starred something.
Clicking any item opens it directly; the app remembers your favorites and recent items between
runs.

## Opening a repository, a folder, or a single file

Which button you use on the Start screen depends on what you're working with:

- **Open a file** opens one Markdown document directly — useful for a quick edit of a spec you
  already have on disk, without any of the review workflow below.
- **Open a folder** opens a whole folder as your workspace; its Markdown files fill the **Disk**
  panel on the left so you can browse and open any of them.
- **Open Repository** takes you to the left-rail **Repositories** panel, where you connect SpecDesk
  to the GitHub repositories your team keeps specs in.

### Registering a repository

First, connect your GitHub account with **Sign in** in the top toolbar (or let SpecDesk prompt you
the first time you need it, for example when adding a repository while signed out). SpecDesk shows
a short one-time code, copies it for you, and opens GitHub in your browser to enter it; once you
approve it there, SpecDesk continues automatically.

In the **Repositories** panel, type an `owner/name` (for example `acme/specs`) or paste a GitHub
link into the field at the top. SpecDesk suggests repositories you have access to as you type; a
public repository outside that list still works if you type its exact name.

Registering a repository doesn't download anything by itself — it just keeps the repository handy
in the list, and lets you browse its files remotely without a local copy yet.

## Repositories: local copies and working lines

To actually edit a document, SpecDesk needs a **local copy** of the repository on your machine, and
inside it, a **working line** — the separate line of work SpecDesk creates for you automatically the
moment you start editing a document, so your changes never touch what's already published until
you're ready.

- **Creating a local copy.** Next to a registered repository, give it a **Local copy name** (a
  folder name; SpecDesk suggests one from the repository's own name) and choose **Clone…** to place
  it in SpecDesk's own managed folder, or **Clone to folder…** to pick the parent folder yourself.
  SpecDesk shows you the exact destination and asks you to confirm before it starts (with an option
  to skip that confirmation next time). You can make more than one local copy of the same
  repository — each one keeps its own name.
- **Opening a local copy** shows its working lines; **Open working line** (or **Switch and open**
  for another line) opens it in the editor.
- **Creating a working line** ahead of time is available from the icon action beside a local copy,
  or from its menu (**Create working line…**) — most of the time, though, you won't need this: Edit
  (below) creates one for you.
  Renaming is available too, from the same menu (**Rename local copy…** / **Rename working
  line…**).
- **Removing** a local copy or a working line asks you to confirm, and warns you first about any
  unfinished edits, versions you haven't sent for review yet, or work SpecDesk is holding onto
  safely — nothing is deleted on GitHub itself.
- **Status at a glance.** Each local copy and working line shows small labels when relevant: how
  many saved versions haven't been sent for review yet, unsaved edits, work SpecDesk is holding
  safely for another line, available updates, and (rarely) a change that needs your attention
  because it couldn't be applied automatically.
- **Refresh**, at the top of the panel, checks every local copy for updates from GitHub in one go;
  SpecDesk also checks periodically in the background.

Favoriting works the same way here as everywhere else in SpecDesk — the star on a repository, a
local copy, or a working line adds it to **Favorites** on the Start screen and in the left rail.

## The editor: Code, Split, and Formatted

Once a document is open, the editor toolbar offers three view modes:

- **Code** — the plain Markdown source, with line numbers and the active line highlighted.
- **Split** — source and the rendered document side by side, scrolled together.
- **Formatted** — the rendered document itself, editable directly (a formatting toolbar for bold,
  italic, headings, lists, links, quotes, code, tables, images, and dividers appears above it); your
  edits are saved back as clean Markdown.

All three share the same document and the same formatting toolbar, so you can switch freely without
losing your place. **Wrap** toggles long-line wrapping, and the search box in the top toolbar finds
text in the open document (press Enter).

## Editing: from Published to a draft

A document you haven't touched is **Published** — the version everyone on the team sees. Its
toolbar shows a single **Edit** button. Choosing it:

1. Suggests a short name for your new working line (you can change it, or keep the suggestion).
2. Starts editing: the document becomes writable, the formatting toolbar switches on, and **Edit**
   gives way to **Save version**, **Discard**, and **Send for review**.

While you type, SpecDesk saves your text to disk continuously in the background — that's what keeps
the status area reading **Unsaved changes**. This is not the same as saving a version: nothing is
recorded in the document's history until you explicitly choose to.

**Discard** abandons the draft and returns the document to Published, deleting your unpublished
edits and working line. **Save** (always available, separately from the draft actions above) writes
the file to disk immediately, without the rest of the versioning workflow — handy for documents you
opened directly with **Open a file**.

## Saving a version

**Save version** is the one deliberate step that records your progress: it opens a small prompt
labelled **Please describe changes**, pre-filled with a short suggested note, which you can edit (a
small ⌄ button, or the Down arrow key, expands it to a multi-line note for a longer explanation).
Confirming saves that note as your version. The status area then briefly reads **Version saved**,
and your **Versions** panel (see below) picks up the new entry.

You can save as many versions as you like while you keep working — nothing is shared with anyone
else until you send it for review.

## Seeing what changed

**Show changes**, in the editor toolbar, highlights the difference between your current text and
your last saved version, right inside the editor — added and removed text, with word-level changes
shown inline rather than as a delete-and-reinsert. If there's nothing to compare (no changes since
the last saved version, or an unusually large document), SpecDesk tells you why instead of leaving
the toggle looking broken.

## Sending your draft for review

**Send for review** is available as soon as you start editing, but SpecDesk needs at least one saved
version before it can actually send anything — try it too early and it tells you so instead of
opening the prompt. Once you have a saved version, choosing it opens a prompt with a **Title for
reviewers** (pre-filled from your last version note) and an optional short description — edit either
before confirming. SpecDesk then shares your draft and opens a change request for it; the status
area moves from **Draft — only you can see this** to **In review**.

While a change request is open:

- **Save version** keeps working exactly as before — it's a purely local step.
- **Update review** (replacing **Send for review**) shares your newly saved versions with the
  already-open change request.
- If a reviewer asks for changes, the status shows **Changes requested**; that stands until the
  reviewer looks again, even after you've sent an update — send your fix, then let them know so they
  can re-review.
- Once a reviewer approves it, the status shows **Approved** — until you save and share a new
  version, which puts it back **In review** for a fresh look.

**Send for review** and **Update review** need a connected GitHub account; SpecDesk simply keeps
them out of the toolbar until you connect one, rather than showing a button that couldn't do
anything.

## Commenting directly on the text

While you're editing (in Code, Split, or Formatted), select any stretch of text and a small toolbar
appears above it with formatting shortcuts and a **Comment** button — choosing it opens **New
comment** right underneath the text it's attached to, as an inline thread that stays with that spot
as you keep editing. Existing comments can be replied to, edited, or deleted (deleting always asks
you to confirm first), and each is attributed to your signed-in account. If a later edit removes the
exact text a comment was attached to, SpecDesk marks it so you can find it again rather than losing
it silently.

These comments are your own working notes on the document — they stay with your local copy and
aren't part of the shared change-request conversation described next. For a conversation the whole
team sees, use the change request's own **Comments** section, described below.

## The change request: description, reviewers, history, comments

Opening a change request — from **Change requests**, from **My reviews**, or from a link — shows it
as a single document, not a raw GitHub page:

- **Header** — its title, current state (**Draft**, **In review**, **Changes requested**,
  **Approved**, **Accepted**, or **Closed**), and the proposed version's name alongside the
  published line it will update.
- **Description** — the summary you (or whoever proposed the change) wrote when sending it for
  review.
- **People** — who proposed it, who's already reviewing, and a field to **Request review** from
  someone else by their GitHub name or team.
- **History** — every saved version that's been shared, oldest first, each with its plain-language
  check status (passed, running, needs attention, or not required).
- **Comments** — the full conversation, with a box to add your own; replies and edits are supported
  inline, and SpecDesk tells you plainly if some comments couldn't be loaded rather than pretending
  there are none.

A document-by-document comparison of the proposed change is planned for a future update; for now,
use **Show changes** in the editor while the document is open.

## Keeping track of reviews and change requests

The left rail's **Change requests** mode groups two lists:

- **Needs your review** — open reviews assigned to you directly or through a GitHub team you
  belong to.
- **Your change requests** — open change requests you proposed or are otherwise involved in.

Each row opens straight into the change-request document above. The account menu (your avatar, top
right) also has **My reviews**, a lighter-weight version of the same list with a field to open any
review by pasting its link — handy when someone sends you one directly.

## After approval: publishing

Today, once a change request is **Approved**, completing the publish — making it the version
everyone sees — is a step your team finishes on GitHub itself, usually by whoever maintains the
repository; SpecDesk doesn't yet have its own **Publish** button for this last step. The next time
SpecDesk checks the document's status, it will show **Published** again, exactly as if you'd just
opened a fresh spec — ready for you to **Edit** again whenever the next change is needed.

## The AI assistant

The right rail's **Assistant** is a chat panel connected to GitHub Copilot (it needs the same
connected GitHub account as the rest of SpecDesk). Type a question about the open document or
repository and the reply streams in as it's written. A few extras in the composer:

- **+** attaches context — an open file, a folder, or one of your registered repositories — as a
  removable chip alongside your next message.
- **▤** opens a library of ready-made prompts (your own, plus any your team shares) and inserts the
  chosen one into the message box for you to edit before sending.

Nothing the assistant proposes changes your document by itself: every suggested action shows you
what it would do first, and nothing happens until you confirm it.

## Disk and favorites

- **Disk**, on the left rail, is the folder tree of whatever you opened (a folder or a repository's
  local copy) — expand folders, open a file by clicking it, or delete one (with confirmation).
- The left-rail **Navigator** mode groups **Start** with two more sections: **Favorites**, and your
  opened-lately list (labelled **History** here; the Start screen calls the same list **Recent**). A
  click opens either straight away, and the star next to each item adds or removes it from
  favorites, everywhere it appears (Start screen, Navigator, Disk, Repositories).
- **Outline** appears once a document is open: every heading as a clickable list that scrolls the
  editor to it as you edit.
- On the right rail, **Versions** and **History** each list the open document's saved versions (with
  their notes, authors, and times) as a quiet, read-only companion to the editor. **Comments**
  appears once the document is part of an open change request, and is the very same conversation
  described above — so you can read and add comments without leaving the editor.

## Your account, notifications, and settings

Your avatar, at the top right, opens a menu with:

- **Notifications** — a dedicated screen for review requests and mentions (still filling in as more
  notification sources are added).
- **My reviews** — see [Keeping track of reviews and change requests](#keeping-track-of-reviews-and-change-requests).
- **Dark theme** — switches the whole app's appearance.
- **Export diagnostic log…** — see [If something goes wrong](#if-something-goes-wrong).
- **Help** and, once connected, **Sign out**.

## If something goes wrong

- **See what SpecDesk has been doing.** The bottom **Log** panel keeps a running, plain-language
  trail of recent actions — views you opened, GitHub requests, and their outcomes — handy for
  retracing your steps before asking for help.
- **Something failed to load** (a change request, its comments, its saved versions, a repository
  description) — SpecDesk says so in plain language, usually next to a **Try again** or **Refresh**
  action, rather than leaving a blank panel.
- **A delete would lose work** — SpecDesk always asks first, spells out exactly what would be lost
  (unfinished edits, versions not yet sent for review, work it's holding safely), and needs a
  second, explicit **Confirm deletion** before anything disappears. Choosing **Keep it** (or closing
  the prompt) cancels safely.
- **A local copy or working line shows a small "needs attention" label** — this means a change
  couldn't be applied automatically; ask a teammate familiar with the repository for help resolving
  it before continuing to edit that line.
- **Export the diagnostic log** from the account menu (**Export diagnostic log…**) when you need to
  report a problem: SpecDesk lets you choose where to save a copy of its own activity log, alongside
  a snapshot of what the on-screen interface was doing at the time — send that file along with a
  description of what you were doing when the problem happened.
