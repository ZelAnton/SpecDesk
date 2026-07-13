/**
 * The Start central view (design concept §10.7 "empty / first-run"): a calm, centered open-a-spec screen.
 * It is one of the views the left-rail navigator switches the central frame to. The author can open a single
 * file or a whole folder (its Markdown tree then fills the left-rail file navigator), pick one of their
 * recent items straight from here (the left-rail Recent panel holds the full history), or reveal the
 * Repositories panel where GitHub repositories are registered and opened.
 */

import type { WorkspaceItem } from "../../wire/protocol.js";
import { icon } from "../icons.js";

export interface HomeActions {
  /** Open a single spec file (the host shows a file picker). */
  onOpenFile(): void;
  /** Open a folder as the workspace (the host shows a folder picker); its tree fills the file navigator. */
  onOpenFolder(): void;
  /** Open a recent item (a folder → `folder.open`, a file → `doc.open` — the owner resolves which, the
   *  same open logic the left-rail Recent/Favorites panels use). */
  onOpenItem(item: WorkspaceItem): void;
  /** Reveal the left-rail Repositories panel. */
  onOpenRepositories(): void;
}

/** The handle {@link buildHomeView} returns so the owner can feed the recent list as `workspace.state` arrives. */
export interface HomeView {
  /** Replace the Start screen's recent list with up to a few most-recent items; empty restores the hint. */
  setRecents(items: readonly WorkspaceItem[]): void;
}

/** How many recent items the Start screen lists — a short shortcut set; the left-rail Recent panel shows all. */
const HOME_RECENTS_LIMIT = 6;

export function buildHomeView(host: HTMLElement, actions: HomeActions): HomeView {
  const screen = document.createElement("div");
  screen.className = "home-screen";

  const title = document.createElement("h1");
  title.className = "home-title";
  title.textContent = "SpecDesk";

  const prompt = document.createElement("p");
  prompt.className = "home-prompt";
  prompt.textContent = "Open a spec to start editing, or a folder to browse.";

  const actionsRow = document.createElement("div");
  actionsRow.className = "home-actions";

  const openFile = document.createElement("button");
  openFile.type = "button";
  openFile.className = "home-open";
  openFile.textContent = "Open a file";
  openFile.addEventListener("click", actions.onOpenFile);

  const openFolder = document.createElement("button");
  openFolder.type = "button";
  openFolder.className = "home-open home-open--secondary";
  openFolder.textContent = "Open a folder";
  openFolder.addEventListener("click", actions.onOpenFolder);

  const openRepositories = document.createElement("button");
  openRepositories.type = "button";
  openRepositories.className = "home-open home-open--secondary";
  openRepositories.textContent = "Open Repository";
  openRepositories.addEventListener("click", actions.onOpenRepositories);

  actionsRow.append(openFile, openFolder, openRepositories);

  const recents = document.createElement("div");
  recents.className = "home-recents";
  const recentsLabel = document.createElement("p");
  recentsLabel.className = "home-recents-label";
  recentsLabel.textContent = "Recent";
  const recentsEmpty = document.createElement("p");
  recentsEmpty.className = "home-recents-empty";
  recentsEmpty.textContent = "Your recent specs will appear here.";
  const recentsList = document.createElement("ul");
  recentsList.className = "home-recents-list";
  recents.append(recentsLabel, recentsEmpty, recentsList);

  screen.append(title, prompt, actionsRow, recents);
  host.appendChild(screen);

  const render = (items: readonly WorkspaceItem[]): void => {
    const shown = items.slice(0, HOME_RECENTS_LIMIT);
    recentsEmpty.hidden = shown.length > 0;
    recentsList.hidden = shown.length === 0;
    recentsList.replaceChildren();
    for (const item of shown) {
      const li = document.createElement("li");
      const button = document.createElement("button");
      button.type = "button";
      button.className = "home-recent";
      button.title = item.path;

      const affordance = document.createElement("span");
      affordance.className = "home-recent-icon";
      affordance.setAttribute("aria-hidden", "true");
      // Trusted in-repo markup (workspace/icons.ts) — never interpolated with host input.
      affordance.innerHTML = icon(item.isFolder ? "files" : "file");

      const label = document.createElement("span");
      label.className = "home-recent-label";
      label.textContent = item.label;

      button.append(affordance, label);
      button.addEventListener("click", () => actions.onOpenItem(item));
      li.appendChild(button);
      recentsList.appendChild(li);
    }
  };
  // Start empty (the hint shows) until the host's first `workspace.state` feeds real recents.
  render([]);

  return { setRecents: render };
}
