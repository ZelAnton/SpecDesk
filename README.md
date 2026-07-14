# SpecDesk

Author and review GitHub-hosted Markdown specs from the desktop — automated git, rendered
diffs, inline comments, and AI assist. SpecDesk lets non-technical authors edit Markdown specs
stored in GitHub without ever touching git, branches, or pull requests directly.

Connect your GitHub account to use GitHub Copilot in the assistant's right panel. The OAuth token stays
inside the native host, and the chat runs without filesystem, command, or repository tools. The composer
supports multi-line prompts: press Enter for a new line and Ctrl+Enter (Cmd+Enter on macOS) to send.
The large composer keeps its actions in one footer and shows the live GitHub connection state. Use **+**
to include the open file, current folder, or a registered repository as context for the next message;
attachments can be removed before sending.
In Split view, the line or formatted block under the pointer is mirrored into both panes with a sand
highlight; the caret remains a separate blue highlight.
The right panel also exposes the selected document's saved versions, comments, and history.
Pending input is saved before switching specifications or closing the window; if a close-time write fails, SpecDesk stays open and explains the problem. Discard temporarily locks both editing views while returning to the published version. A safely restored draft becomes editable again with autosave resumed; if its working line cannot be verified after a failure, SpecDesk closes the document instead of risking a write to the published version.
Starting Edit keeps the exact specification locked until its editable working line is ready and reloaded. Concurrent navigation, repository updates, or window close wait or are rejected; if the working line changes but the reload fails, SpecDesk closes the document instead of exposing stale text. Reopening the current working line preserves its unfinished files, and any working-line change or discard stops before overwriting an untracked or ignored local file.
Assistant is the first mode on that panel's toolbar so chat stays in a consistent position.
Its mode icons follow what is active: review comments require a review, history a repository branch,
outline a Markdown file, and versions any file inside a repository.

> **Working title.** `SpecDesk` is a placeholder name; rename before any registry/namespace work.

## Documentation

- **[docs/ROADMAP.md](docs/ROADMAP.md)** — the PoC-driven execution plan we work by.
- **[docs/design/](docs/design/README.md)** — concept, architecture, and feature design docs.

## Repository layout

Multi-language monorepo: a .NET solution (C# + F#) plus a TypeScript webview bundle.

```
SpecDesk.slnx              # .NET solution — all C#/F# projects
src/
  SpecDesk.Contracts/      # C#  — IPC message DTOs
  SpecDesk.Core/           # F#  — domain, lifecycle state machine, image rules
  SpecDesk.Markdown/       # F#  — Markdig wrapper, AST DU, HTML render
  SpecDesk.Diff/           # F#  — semantic (AST) diff
  SpecDesk.Git/            # C#  — LibGit2Sharp wrapper
  SpecDesk.GitHub/         # C#  — GitHub OAuth device-flow auth (BCL HttpClient)
  SpecDesk.Ai/             # C#  — GitHub Copilot chat integration (PoC-8)
  SpecDesk.Host/           # C#  — Photino bootstrap, IPC router (the exe)
tests/
  SpecDesk.Core.Tests/     # F#
  SpecDesk.Markdown.Tests/ # F#
  SpecDesk.Diff.Tests/     # F#
webview/                   # TS  — CodeMirror editor, preview, IPC client (esbuild)
```

See [CONTRIBUTING.md](CONTRIBUTING.md) for build/test/lint commands and contributor conventions.

## Quick start

```sh
dotnet build SpecDesk.slnx          # build the .NET side
dotnet test SpecDesk.slnx           # run F# tests
cd webview && npm install && npm run build   # build the webview bundle
```

Requires .NET SDK 10 and Node 24.

## Workspace panels

On Windows, the global toolbar is also the window title bar: drag any empty area to move the window,
double-click it to maximize or restore, and use the accessible minimize, maximize/restore, and close
buttons at the far right. The window remains freely resizable. The close button, Alt+F4, and taskbar/system close all settle pending input before the window closes; a failed draft write keeps SpecDesk open.
SpecDesk opens on Start with every optional panel collapsed, keeping the first screen focused on choosing
the next specification. Reopening a panel restores its last mode and size.
The Markdown controls sit on the same calm grey header surface as the side-panel headings, keeping editing
actions visually separate from the document itself.
The formatting toolbar remains visible whenever a specification is open (disabled until editing starts)
and covers headings, lists, emphasis, inline and block code, quotes, links, starter tables, image references,
and dividers.
The global toolbar shows repository, version, path, and search only while a document is being edited;
Start and Notifications retain just the SpecDesk identity and global account/notification actions.
The left, right, and bottom mode rails stay available around the document. Choose a mode icon to open its
panel; choose the active icon again to collapse it. Collapsed side rails remain vertical, and the collapsed
bottom rail becomes a horizontal toolbar. Panel size, active mode, and expanded state are saved locally.
The bottom panel stops before the right mode rail, so chat and document tools remain reachable at every
panel height. The dark status bar uses the same high-contrast treatment as the side rails.
On the Start screen, `Open Repository` reveals the Repositories panel, where repositories are registered and
opened; the Start screen itself does not ask for a repository address. Favorite repositories, folders, and
specs appear beside recent work for one-click return.
Choosing **Open Repository** also places keyboard focus in repository search, ready to type.
The left-panel Review mode shows open review requests assigned to the connected account, including requests
for GitHub teams whose membership is visible to SpecDesk.
Pull Requests shows the connected user's active work: open requests they authored or otherwise participated
in. Closed and merged requests are intentionally excluded from this working list.

The notification icon switches the main workspace to a Notifications list. The current list is a
placeholder for future review-request and mention events.

## GitHub access

SpecDesk uses GitHub's device authorization flow and stores the resulting token with Windows DPAPI. The
public client id of SpecDesk's registered GitHub OAuth App is built in, so no account configuration is needed
before connecting. Development and test builds can override it with `SPECDESK_GITHUB_CLIENT_ID`. No client
secret is used or stored. When a disconnected user adds or opens a repository, SpecDesk opens GitHub's
standard authorization page in the system browser and resumes the action after authorization. GitHub's
documented device-flow response supplies only the standard verification page and a separate one-time code;
it does not provide a supported prefilled-code URL. SpecDesk therefore starts copying the code to the
clipboard before opening the browser and keeps it visible if clipboard access is unavailable. Removing the
manual code step entirely would require a separate browser authorization-code flow with PKCE and a verified
callback into the desktop app.
The main toolbar also lets you connect or disconnect explicitly. While connected, the bottom status bar
shows the GitHub username and organizations visible to the authorization; new authorizations request
`read:org` in addition to repository and profile access.

Registered repositories are persisted with the default branch reported by GitHub. The Repositories panel
groups any number of named local copies beneath each repository and shows every known working line. Search
suggests personal and organization repositories as owner/repository and matches by repository name alone;
any public owner/repository can also be entered directly.

The main **Clone…** action creates a named copy in SpecDesk-managed storage. Only the arrow opens the menu
for **Clone to folder…**. The repository description and exact destination are shown before confirmation.
The same GitHub repository can have several independent local copies; an occupied local name produces a
warning and offers to open the existing copy. Both clone choices require Yes/No confirmation, with an
optional **Do not show again** choice.

Selecting the online repository browses its files directly from GitHub. Online files are read-only until a
local copy is created. Selecting a local working line first flushes any pending editor input, protects
unfinished files in a named safety copy, refuses to overwrite ignored files that become tracked on the
destination, verifies that the copy still belongs to the selected GitHub repository, switches lines,
restores remembered work, reloads an open spec, and opens the copy folder.

Only local, non-default working lines show Delete. Before removing a local copy or local branch, SpecDesk
explains unfinished edits, unshared versions, protected work, ignored files, and known conflicts; the
confirmation is bound to that exact state. A local copy that owns linked working copies cannot be removed:
SpecDesk lists those copies and their unfinished edits, unshared versions, protected snapshots, or conflicts,
and asks you to close and remove them first so their shared repository data remains intact. Removing the top-level repository only unregisters it from
SpecDesk, removing a copy only deletes its local folder, and removing a working line only deletes a local
branch. Immediately before inspecting or removing local work, SpecDesk verifies that the folder still belongs
to the selected GitHub repository and is still on the working line shown in the UI, before pending editor text
is written; a changed line or replaced folder is left untouched. Once a local copy has been removed, its open
document is closed without reopening any replacement folder that appears at the old path. SpecDesk never
deletes a GitHub repository, remote branch, or any other remote resource. If final local verification fails
after the folder was moved and its original path cannot be restored, SpecDesk removes the unavailable
registration, closes the affected document, and reports the exact recovery folder where the files were kept.

Each local copy and working line separately shows unfinished edits, unshared saved versions, incoming
updates, protected work, and known conflicts. **Refresh all** checks every registered copy and continues
past unavailable or mismatched copies without contacting their remotes; it remains visibly in progress
until that exact batch finishes. Disconnecting or changing GitHub accounts cancels the batch and every delayed Git
credential callback before the previous token can be released again.

**Get updates** verifies the local copy still belongs to the selected GitHub repository and remains on the
working line shown in the UI before it flushes pending editor input, then performs only a clean fast-forward
while both editor panes are locked; it refuses to overwrite local or divergent work. Switching a working line
follows the same save-after-verification rule. If checkout or update changes local files but a later recovery
or inspection step fails, SpecDesk closes the open specification instead of saving stale editor text into the
newly selected working line.
**Share changes** performs the identity check before using the transient GitHub credential, publishes only
the selected current working line, never force-pushes, and asks the author to get newer GitHub versions first
when needed. SpecDesk verifies the source again before showing any switch, get, or share result.

Repository cards keep the online source as the top-level choice and named local copies beneath it. Stars can
keep a GitHub repository, exact local copy, branch, folder, or file in **Favorites**. Reopening a favorite
retains its exact identity; choosing a repository favorite opens Repositories and highlights that source.
Removing a registered repository removes only its SpecDesk registration and related favorites; local folders and branches, plus every GitHub resource, remain untouched.
The left rail stays intentionally small: **Navigator** (including Favorites and History), **Repositories**,
**Folders**, and **PRs**. The PRs view keeps requests needing your review beside work you created or joined.
