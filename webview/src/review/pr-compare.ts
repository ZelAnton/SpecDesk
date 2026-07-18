/**
 * PoC-7 Part C — in-flight PR awareness & comparison (docs/design/07-review-experience.md).
 *
 * While a document is open, this surfaces the open reviews (pull requests) that touch the current file — the
 * actionable generalization of the "someone else is editing this" soft-lock warning — and lets the author
 * compare a chosen review's proposed version of the file against a base (their working copy, or `main`), in
 * both representations (the mandatory rendered/raw toggle). The comparison HTML is produced host-side and
 * injected read-only: this surface never merges or pulls, only shows overlapping work (the v1 boundary).
 *
 * It owns no IPC knowledge — the integrator supplies {@link PrCompareDeps} (mirroring how ReviewsPanel and the
 * ReviewController reach the host only through callbacks). Plain language only; a "pull request" is a "review".
 */

import { setHidden, setText } from "../util/dom.js";
import { isOpenableHref } from "../util/links.js";
import type {
  PrCompareBase,
  PrCompareMode,
  PrComparePayload,
  PrCompareRequestPayload,
  PrForFileItemPayload,
  PrForFilePayload,
} from "../wire/protocol.js";

export interface PrCompareDeps {
  /** The entry-point affordance shown when open reviews touch the current file. */
  affordance: HTMLElement | null;
  affordanceText: HTMLElement | null;
  openBtn: HTMLButtonElement | null;
  /** The comparison panel and its parts. */
  panel: HTMLElement | null;
  list: HTMLElement | null;
  controls: HTMLElement | null;
  status: HTMLElement | null;
  view: HTMLElement | null;
  closeBtn: HTMLButtonElement | null;
  /** The base pickers (`data-base`) and mode pickers (`data-mode`), in any order. */
  baseButtons: readonly HTMLButtonElement[];
  modeButtons: readonly HTMLButtonElement[];
  /** Ask the host for the open PRs touching `path` (a correlated request). */
  requestForFile: (path: string) => Promise<PrForFilePayload>;
  /** Ask the host for a comparison (a correlated request). */
  requestCompare: (request: PrCompareRequestPayload) => Promise<PrComparePayload>;
  /** Open an http/https link (a PR web page, or a link inside the rendered comparison) in the OS browser. */
  onOpenLink: (url: string) => void;
}

export class PrCompare {
  private items: PrForFileItemPayload[] = [];
  private activeNumber: number | null = null;
  private base: PrCompareBase = "workingCopy";
  private mode: PrCompareMode = "rendered";
  // Bumped by every refresh()/clear() and every compare request, so a reply that arrives after the author
  // navigated away (or changed the selection/base/mode) is recognised as stale and dropped.
  private generation = 0;

  constructor(private readonly deps: PrCompareDeps) {
    this.deps.openBtn?.addEventListener("click", () => this.openPanel());
    this.deps.closeBtn?.addEventListener("click", () => setHidden(this.deps.panel, true));
    for (const button of this.deps.baseButtons) {
      button.addEventListener("click", () => this.pickBase(button.dataset.base as PrCompareBase));
    }
    for (const button of this.deps.modeButtons) {
      button.addEventListener("click", () => this.pickMode(button.dataset.mode as PrCompareMode));
    }
    // Links inside the rendered comparison (a PR's own links, or a review row's) must not navigate the
    // webview; hand a real web link to the host and ignore anything else (mirrors the preview's guard).
    this.deps.view?.addEventListener("click", (event) => this.onViewClick(event));
  }

  /** (Re)load the open reviews touching `path`, revealing the entry-point affordance when there are any and
   *  hiding the whole surface otherwise. Best-effort: a load failure (or none touching the file) just leaves
   *  the surface hidden — this passive awareness never interrupts the author. */
  async refresh(path: string): Promise<void> {
    const generation = ++this.generation;
    this.close();
    let payload: PrForFilePayload;
    try {
      payload = await this.deps.requestForFile(path);
    } catch {
      return; // A transport/correlation failure just leaves the surface hidden.
    }
    if (generation !== this.generation) {
      return; // A newer refresh/clear superseded this one.
    }
    this.items = payload.error === undefined ? payload.items : [];
    if (this.items.length === 0) {
      setHidden(this.deps.affordance, true);
      return;
    }
    const count = this.items.length;
    setText(
      this.deps.affordanceText,
      `${count} other ${count === 1 ? "review" : "reviews"} of this file`,
    );
    setHidden(this.deps.affordance, false);
  }

  /** Hide the whole surface and drop its state (a document change/close). */
  clear(): void {
    this.generation++;
    this.items = [];
    this.close();
    setHidden(this.deps.affordance, true);
  }

  private close(): void {
    this.activeNumber = null;
    setHidden(this.deps.panel, true);
    setHidden(this.deps.controls, true);
    if (this.deps.view) {
      this.deps.view.replaceChildren();
    }
    setText(this.deps.status, "");
  }

  private openPanel(): void {
    if (this.items.length === 0) {
      return;
    }
    setHidden(this.deps.panel, false);
    this.renderList();
    setHidden(this.deps.controls, true);
    if (this.deps.view) {
      this.deps.view.replaceChildren();
    }
    setText(this.deps.status, "Pick a review to compare with this file.");
  }

  private renderList(): void {
    if (!this.deps.list) {
      return;
    }
    this.deps.list.replaceChildren();
    for (const item of this.items) {
      const row = document.createElement("li");
      row.className = "pr-compare-row";

      const pick = document.createElement("button");
      pick.type = "button";
      pick.className = "pr-compare-pick";
      pick.setAttribute("aria-pressed", String(item.number === this.activeNumber));
      pick.addEventListener("click", () => this.pickReview(item.number));

      const title = document.createElement("span");
      title.className = "pr-compare-row-title";
      title.textContent = item.title.length > 0 ? item.title : `Review #${item.number}`;

      const meta = document.createElement("span");
      meta.className = "pr-compare-row-meta";
      meta.textContent = item.repo;

      pick.append(title, meta);
      row.appendChild(pick);
      this.deps.list.appendChild(row);
    }
  }

  private pickReview(number: number): void {
    this.activeNumber = number;
    this.renderList();
    setHidden(this.deps.controls, false);
    void this.runCompare();
  }

  private pickBase(base: PrCompareBase | undefined): void {
    if (base === undefined || base === this.base) {
      return;
    }
    this.base = base;
    this.syncPressed(this.deps.baseButtons, "base", base);
    void this.runCompare();
  }

  private pickMode(mode: PrCompareMode | undefined): void {
    if (mode === undefined || mode === this.mode) {
      return;
    }
    this.mode = mode;
    this.syncPressed(this.deps.modeButtons, "mode", mode);
    void this.runCompare();
  }

  private syncPressed(
    buttons: readonly HTMLButtonElement[],
    key: "base" | "mode",
    value: string,
  ): void {
    for (const button of buttons) {
      button.setAttribute("aria-pressed", String(button.dataset[key] === value));
    }
  }

  private async runCompare(): Promise<void> {
    const number = this.activeNumber;
    if (number === null) {
      return;
    }
    const generation = ++this.generation;
    setText(this.deps.status, "Loading the comparison…");
    if (this.deps.view) {
      this.deps.view.replaceChildren();
    }
    let payload: PrComparePayload;
    try {
      payload = await this.deps.requestCompare({
        prNumber: number,
        base: this.base,
        mode: this.mode,
      });
    } catch {
      if (generation === this.generation) {
        setText(this.deps.status, "Couldn't load that comparison. Try again.");
      }
      return;
    }
    // Drop a reply that lost its race (a newer selection/base/mode, or the document changed) OR that echoes a
    // base/mode the author has since toggled away from.
    if (
      generation !== this.generation ||
      payload.base !== this.base ||
      payload.mode !== this.mode
    ) {
      return;
    }
    if (payload.error !== undefined) {
      setText(this.deps.status, payload.error);
      return;
    }
    setText(this.deps.status, "");
    if (this.deps.view) {
      this.deps.view.innerHTML = payload.html;
    }
  }

  private onViewClick(event: MouseEvent): void {
    const target = event.target;
    if (!(target instanceof Element)) {
      return;
    }
    const anchor = target.closest("a");
    if (!anchor) {
      return;
    }
    event.preventDefault();
    const href = anchor.getAttribute("href")?.trim() ?? "";
    if (isOpenableHref(href)) {
      this.deps.onOpenLink(href);
    }
  }
}
