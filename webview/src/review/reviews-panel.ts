import { setHidden, setText } from "../util/dom.js";
import type { PrListItemPayload, PrListPayload } from "../wire/protocol.js";

/** The host actions the reviews panel triggers, plus the panel's own DOM elements (each may be absent
 *  from the markup). */
export interface ReviewsPanelDeps {
  /** The panel's own elements. */
  panel: HTMLElement | null;
  list: HTMLElement | null;
  status: HTMLElement | null;
  closeBtn: HTMLButtonElement | null;
  urlInput: HTMLInputElement | null;
  urlOpenBtn: HTMLButtonElement | null;
  /** Fetch the user's open reviews from the host (a correlated request). */
  requestReviews: () => Promise<PrListPayload>;
  /** Open a review in SpecDesk's central pull-request document. */
  openReview: (item: PrListItemPayload) => void;
}

/** A GitHub pull-request web URL, e.g. https://github.com/owner/repo/pull/123. */
const PR_URL = /^https:\/\/github\.com\/([^/\s]+)\/([^/\s]+)\/pull\/([1-9]\d*)(?:[/?#].*)?$/i;

/**
 * The "My reviews" panel (PoC-5): a browse list of the open pull requests the signed-in user authored or
 * was asked to review, each opening in SpecDesk's review document, plus a field to open any review by
 * pasting its link. It owns no IPC knowledge — the integrator supplies {@link ReviewsPanelDeps}. Plain
 * language only; the author never sees git/PR vocabulary (a "pull request" is a "review").
 */
export class ReviewsPanel {
  private readonly panel: HTMLElement | null;
  private readonly list: HTMLElement | null;
  private readonly status: HTMLElement | null;
  private readonly closeBtn: HTMLButtonElement | null;
  private readonly urlInput: HTMLInputElement | null;
  private readonly urlOpenBtn: HTMLButtonElement | null;
  // True while a fetch is in flight. It serialises loads: repeat "My reviews" clicks (or a close+reopen
  // mid-flight) never fan out a second concurrent host query — the one in flight renders into the panel if
  // it's still open when it resolves.
  private loading = false;
  private requestGeneration = 0;

  constructor(private readonly deps: ReviewsPanelDeps) {
    this.panel = deps.panel;
    this.list = deps.list;
    this.status = deps.status;
    this.closeBtn = deps.closeBtn;
    this.urlInput = deps.urlInput;
    this.urlOpenBtn = deps.urlOpenBtn;

    this.closeBtn?.addEventListener("click", () => this.close());
    this.urlOpenBtn?.addEventListener("click", () => this.openByUrl());
    this.urlInput?.addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        event.preventDefault();
        this.openByUrl();
      }
    });
  }

  /** Reveal the panel and (re)load the list. Idempotent — clicking "My reviews" again just refreshes; the
   *  panel closes via its own Close button, so a natural double-click can't accidentally toggle it shut. */
  async open(): Promise<void> {
    setHidden(this.panel, false);
    // Already loading — the panel is open and the in-flight fetch will render; don't fan out a second query.
    if (this.loading) {
      return;
    }
    this.loading = true;
    const generation = ++this.requestGeneration;
    setText(this.status, "Loading your reviews…");
    if (this.list) {
      this.list.replaceChildren();
    }
    try {
      const payload = await this.deps.requestReviews();
      // Render only into a still-open panel — if the author closed it while the fetch was in flight, the
      // reply must not populate a hidden panel (and a later reopen fetches fresh once loading clears).
      if (generation === this.requestGeneration && this.panel && !this.panel.hidden) {
        this.render(payload);
      }
    } catch {
      // The host query rejected (correlation timeout, transport failure, etc.) — fall back to an error state
      // instead of leaving the panel stuck on "Loading your reviews…" forever.
      if (generation === this.requestGeneration && this.panel && !this.panel.hidden) {
        setText(this.status, "Couldn't load your reviews. Try again later.");
      }
    } finally {
      if (generation === this.requestGeneration) {
        this.loading = false;
      }
    }
  }

  close(): void {
    setHidden(this.panel, true);
  }

  clearAccountState(): void {
    this.requestGeneration++;
    this.loading = false;
    this.close();
    this.list?.replaceChildren();
    setText(this.status, "");
    if (this.urlInput) {
      this.urlInput.value = "";
    }
  }

  private render(payload: PrListPayload): void {
    if (!this.list) {
      return;
    }
    this.list.replaceChildren();
    if (payload.error !== undefined && payload.error.length > 0) {
      setText(this.status, payload.error);
      return;
    }
    if (payload.items.length === 0) {
      setText(this.status, "You have no open reviews.");
      return;
    }
    setText(this.status, "");
    for (const item of payload.items) {
      this.list.appendChild(this.rowFor(item));
    }
  }

  private rowFor(item: PrListItemPayload): HTMLElement {
    const row = document.createElement("li");
    row.className = "review-row";

    const open = document.createElement("button");
    open.type = "button";
    open.className = "review-open";
    open.addEventListener("click", () => {
      this.deps.openReview(item);
      this.close();
    });

    const title = document.createElement("span");
    title.className = "review-title";
    title.textContent = item.title.length > 0 ? item.title : `Review #${item.number}`;

    const meta = document.createElement("span");
    meta.className = "review-meta";
    const role = item.role === "author" ? "yours" : "to review";
    meta.textContent = `${item.repo} · ${role}`;

    const state = document.createElement("span");
    state.className = "review-state";
    state.dataset.state = item.status;
    state.textContent = item.label;

    open.append(title, meta, state);
    row.appendChild(open);
    return row;
  }

  private openByUrl(): void {
    const raw = this.urlInput?.value.trim() ?? "";
    const match = PR_URL.exec(raw);
    const number = Number(match?.[3]);
    if (match === null || !Number.isSafeInteger(number) || number <= 0 || number > 2_147_483_647) {
      setText(this.status, "That doesn't look like a GitHub review link.");
      return;
    }
    this.deps.openReview({
      number,
      title: `Review #${number}`,
      url: raw,
      repo: `${match[1]}/${match[2]}`,
      role: "reviewer",
      status: "inReview",
      label: "In review",
    });
    if (this.urlInput) {
      this.urlInput.value = "";
    }
    setText(this.status, "");
    this.close();
  }
}
