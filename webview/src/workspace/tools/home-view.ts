/**
 * The Start central view (design concept §10.7 "empty / first-run"): a calm, centered open-a-spec screen.
 * It is one of the views the left-rail navigator switches the central frame to — the concrete second view
 * that proves the substitution architecture. `onOpen` runs the same "Open…" action as the toolbar; the
 * recents list is a placeholder until the host feeds it real data.
 */

export function buildHomeView(host: HTMLElement, onOpen: () => void): void {
  const screen = document.createElement("div");
  screen.className = "home-screen";

  const title = document.createElement("h1");
  title.className = "home-title";
  title.textContent = "SpecDesk";

  const prompt = document.createElement("p");
  prompt.className = "home-prompt";
  prompt.textContent = "Open a spec to start editing, or pick one from the left.";

  const open = document.createElement("button");
  open.type = "button";
  open.className = "home-open";
  open.textContent = "Open a spec";
  open.addEventListener("click", onOpen);

  const recents = document.createElement("div");
  recents.className = "home-recents";
  const recentsLabel = document.createElement("p");
  recentsLabel.className = "home-recents-label";
  recentsLabel.textContent = "Recent";
  const recentsEmpty = document.createElement("p");
  recentsEmpty.className = "home-recents-empty";
  recentsEmpty.textContent = "Your recent specs will appear here.";
  recents.append(recentsLabel, recentsEmpty);

  screen.append(title, prompt, open, recents);
  host.appendChild(screen);
}
