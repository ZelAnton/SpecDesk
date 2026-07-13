# SpecDesk

Author and review GitHub-hosted Markdown specs from the desktop — automated git, rendered
diffs, inline comments, and AI assist. SpecDesk lets non-technical authors edit Markdown specs
stored in GitHub without ever touching git, branches, or pull requests directly.

The assistant's right-panel composer supports multi-line prompts: press Enter for a new line and
Ctrl+Enter (Cmd+Enter on macOS) to send.
Use **Attach** beside the composer to include the open file, current folder, or a registered repository
as context for the next message; attachments can be removed before sending.
The right panel also exposes the selected document's saved versions, comments, and change history.

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
  SpecDesk.Ai/             # C#  — Microsoft Agent Framework (PoC-8)
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

The left, right, and bottom mode rails stay available around the document. Choose a mode icon to open its
panel; choose the active icon again to collapse it. Collapsed side rails remain vertical, and the collapsed
bottom rail becomes a horizontal toolbar. Panel size, active mode, and expanded state are saved locally.
On the Start screen, `Open Repository` reveals the Repositories panel, where repositories are registered and
opened; the Start screen itself does not ask for a repository address.
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
standard authorization page in the system browser and resumes the action after authorization.
The main toolbar also lets you connect or disconnect explicitly. While connected, the bottom status bar
shows the GitHub username and organizations visible to the authorization; new authorizations request
`read:org` in addition to repository and profile access.

Registered repositories are persisted with the default branch reported by GitHub. The Repositories panel
groups any number of managed local copies beneath each repository and shows only non-default branches under
each copy. Repository entry suggests the connected user's personal and organization repositories, displays
each choice as `owner/repository`, and matches text against the repository name without requiring the owner.
You can also enter any public `owner/repository` directly when it is not in the suggestions.
The **Clone…** menu either creates a copy in SpecDesk-managed storage or lets you choose a parent folder;
the exact managed destination is shown before Clone is enabled, and an occupied same-name destination is
never reused. Both clone choices require a Yes/No confirmation; selecting **Do not show this confirmation
again** with Yes persists that preference for future clones. **Copy locally** creates another managed copy.

Selecting the repository itself browses its files directly from GitHub, so a local copy is optional. Online
files open as read-only previews; select **Copy locally** before editing. Local repository trees show all files
(large and binary files are listed but rejected with a plain preview message).

Use the star beside a registered repository, folder, or file to keep it in **Favorites**. Favorites remember
the exact online branch and path, so reopening an online folder restores that branch's tree even after an app
restart. Removing a registered repository also removes its repository, folder, and file favorites.
