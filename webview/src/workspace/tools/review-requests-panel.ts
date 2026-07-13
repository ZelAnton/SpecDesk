import type { PrListItemPayload, PrListPayload } from "../../wire/protocol.js";
import { icon } from "../icons.js";
import type { PanelTool } from "../panel-tool.js";

export interface ReviewRequestsCallbacks {
  request(): Promise<PrListPayload>;
  openUrl(url: string): void;
}

/** The left-rail Review mode: open review requests assigned directly or through a known team. */
export class ReviewRequestsPanel implements PanelTool {
  readonly id = "reviews";
  readonly label = "Review";
  readonly icon = icon("review");

  private root: HTMLElement | null = null;
  private status: HTMLElement | null = null;
  private list: HTMLElement | null = null;
  private active = false;
  private signedIn = false;
  private generation = 0;

  constructor(private readonly callbacks: ReviewRequestsCallbacks) {}

  mount(body: HTMLElement): void {
    const root = document.createElement("div");
    root.className = "remote-review-list";

    const toolbar = document.createElement("div");
    toolbar.className = "remote-review-toolbar";
    const refresh = document.createElement("button");
    refresh.type = "button";
    refresh.className = "remote-review-refresh";
    refresh.textContent = "Refresh";
    refresh.addEventListener("click", () => void this.refresh());
    toolbar.appendChild(refresh);

    const status = document.createElement("p");
    status.className = "remote-review-status";
    status.setAttribute("role", "status");

    const list = document.createElement("ul");
    list.className = "remote-review-items";
    list.setAttribute("aria-label", "Reviews waiting for you");

    root.append(toolbar, status, list);
    body.appendChild(root);
    this.root = root;
    this.status = status;
    this.list = list;
    this.showAuthState();
  }

  onShow(): void {
    this.active = true;
    if (this.signedIn) {
      void this.refresh();
    } else {
      this.showAuthState();
    }
  }

  onHide(): void {
    this.active = false;
    this.generation++;
  }

  setSignedIn(signedIn: boolean): void {
    this.signedIn = signedIn;
    this.generation++;
    if (!signedIn) {
      this.showAuthState();
    } else if (this.active) {
      void this.refresh();
    }
  }

  async refresh(): Promise<void> {
    if (!this.signedIn) {
      this.showAuthState();
      return;
    }
    const request = ++this.generation;
    this.setState("loading", "Loading review requests…");
    this.list?.replaceChildren();
    try {
      const payload = await this.callbacks.request();
      if (request !== this.generation || !this.active) {
        return;
      }
      this.render(payload);
    } catch {
      if (request === this.generation && this.active) {
        this.setState("error", "Couldn't load review requests. Try again.");
      }
    }
  }

  private showAuthState(): void {
    this.list?.replaceChildren();
    this.setState("auth", "Connect a GitHub account to see review requests.");
  }

  private render(payload: PrListPayload): void {
    this.list?.replaceChildren();
    if (payload.error !== undefined) {
      this.setState("error", payload.error);
      return;
    }
    const items = payload.items.filter((item) => item.role === "reviewer");
    if (items.length === 0) {
      this.setState("empty", "No open reviews are waiting for you.");
      return;
    }
    this.setState("ready", "");
    for (const item of items) {
      this.list?.appendChild(this.row(item));
    }
  }

  private row(item: PrListItemPayload): HTMLLIElement {
    const row = document.createElement("li");
    row.className = "remote-review-row";
    const open = document.createElement("button");
    open.type = "button";
    open.className = "remote-review-open";
    open.addEventListener("click", () => this.callbacks.openUrl(item.url));
    const title = document.createElement("span");
    title.className = "remote-review-title";
    title.textContent = item.title || `Review #${item.number}`;
    const meta = document.createElement("span");
    meta.className = "remote-review-meta";
    meta.textContent = item.repo;
    const state = document.createElement("span");
    state.className = "remote-review-state";
    state.dataset.state = item.status;
    state.textContent = item.label;
    open.append(title, meta, state);
    row.appendChild(open);
    return row;
  }

  private setState(state: string, message: string): void {
    if (this.root !== null) {
      this.root.dataset.state = state;
    }
    if (this.status !== null) {
      this.status.textContent = message;
      this.status.hidden = message.length === 0;
    }
  }
}
