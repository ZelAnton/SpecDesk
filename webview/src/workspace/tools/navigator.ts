/**
 * The left rail's navigation tool (design concept §9): a list of destinations that each substitute the
 * central frame with a registered central view. Selecting one calls `onNavigate(id)`; the owner reflects
 * the resulting active view back through {@link Navigator.setActive}, so the list highlights wherever the
 * centre actually is — including a switch driven from elsewhere (e.g. opening a spec from the Start view).
 */

import type { PanelTool } from "../panel-tool.js";

export interface NavDestination {
  /** The central view id to show (see central-frame.ts). */
  readonly id: string;
  /** The label shown in the list (and the button's accessible name). */
  readonly label: string;
  /** A short, muted second line under the label — decorative (not part of the accessible name). */
  readonly hint?: string;
}

export class Navigator implements PanelTool {
  readonly id = "navigator";
  readonly label = "Navigator";
  private readonly buttons = new Map<string, HTMLButtonElement>();
  private activeId: string | null = null;

  constructor(
    private readonly destinations: readonly NavDestination[],
    private readonly onNavigate: (id: string) => void,
  ) {}

  mount(body: HTMLElement): void {
    // A navigation landmark (the left rail's real purpose per §9); labelled so it's distinct from other nav.
    const list = document.createElement("nav");
    list.className = "nav-list";
    list.setAttribute("aria-label", "Views");
    for (const dest of this.destinations) {
      const item = document.createElement("button");
      item.type = "button";
      item.className = "nav-item";
      item.dataset.view = dest.id;
      // The label carries the accessible name; the hint is decorative visible text (aria-label wins), so a
      // screen reader announces just "Document" / "Start", not the whole hint sentence.
      item.setAttribute("aria-label", dest.label);

      const label = document.createElement("span");
      label.className = "nav-item-label";
      label.textContent = dest.label;
      item.appendChild(label);

      if (dest.hint !== undefined) {
        const hint = document.createElement("span");
        hint.className = "nav-item-hint";
        hint.textContent = dest.hint;
        item.appendChild(hint);
      }

      item.addEventListener("click", () => this.onNavigate(dest.id));
      this.buttons.set(dest.id, item);
      list.appendChild(item);
    }
    body.appendChild(list);
    // Reflect the active view if it was set before this tool was mounted — setupWorkspace seeds setActive()
    // before the dock constructs and mounts the navigator.
    this.reflect();
  }

  /** Highlight the destination for `id` as the current one (none, if it isn't a listed destination). */
  setActive(id: string): void {
    this.activeId = id;
    this.reflect();
  }

  private reflect(): void {
    for (const [id, button] of this.buttons) {
      const current = id === this.activeId;
      button.classList.toggle("nav-item--current", current);
      if (current) {
        // "page" — the SPA-style "you are here" among the navigator's view destinations.
        button.setAttribute("aria-current", "page");
      } else {
        button.removeAttribute("aria-current");
      }
    }
  }
}
