// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ReviewsPanel, type ReviewsPanelDeps } from "../../src/review/reviews-panel.js";
import type { PrListPayload } from "../../src/wire/protocol.js";

// Minimal markup mirroring the reviews panel's ids (all start hidden, as in index.html).
function setupDom(): void {
  document.body.innerHTML = `
    <div id="reviews-panel" hidden>
      <div id="reviews-panel-head">
        <span id="reviews-panel-title"></span>
        <button id="reviews-close"></button>
      </div>
      <div id="reviews-open-by-url">
        <input id="reviews-url-input" />
        <button id="reviews-url-open"></button>
      </div>
      <ul id="reviews-list"></ul>
      <span id="reviews-status"></span>
    </div>
  `;
}

function el(id: string): HTMLElement {
  const node = document.querySelector(`#${id}`);
  if (!(node instanceof HTMLElement)) {
    throw new Error(`#${id} missing`);
  }
  return node;
}

// instanceof narrowing (no `as`) — mirrors the pattern in dialogs.test.ts.
function input(id: string): HTMLInputElement {
  const node = document.querySelector(`#${id}`);
  if (!(node instanceof HTMLInputElement)) {
    throw new Error(`#${id} not an input`);
  }
  return node;
}

function flush(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

// The elements ReviewsPanel receives via its deps (queried from the test markup here, not by
// ReviewsPanel itself — mirrors the injection pattern in lifecycle-chrome.test.ts).
function elements(): Pick<
  ReviewsPanelDeps,
  "panel" | "list" | "status" | "closeBtn" | "urlInput" | "urlOpenBtn"
> {
  return {
    panel: el("reviews-panel"),
    list: el("reviews-list"),
    status: el("reviews-status"),
    closeBtn: document.querySelector<HTMLButtonElement>("#reviews-close"),
    urlInput: input("reviews-url-input"),
    urlOpenBtn: document.querySelector<HTMLButtonElement>("#reviews-url-open"),
  };
}

describe("ReviewsPanel", () => {
  beforeEach(setupDom);

  const twoReviews: PrListPayload = {
    items: [
      {
        number: 42,
        title: "Clarify refunds",
        url: "https://github.com/o/r/pull/42",
        repo: "o/r",
        role: "author",
        status: "changesRequested",
        label: "Changes requested",
      },
      {
        number: 7,
        title: "Payment terms",
        url: "https://github.com/o/x/pull/7",
        repo: "o/x",
        role: "reviewer",
        status: "inReview",
        label: "In review",
      },
    ],
  };

  it("toggles open, renders each review, and opens one on click", async () => {
    const openReview = vi.fn();
    const panel = new ReviewsPanel({
      ...elements(),
      requestReviews: () => Promise.resolve(twoReviews),
      openReview,
    });

    await panel.open();

    expect(el("reviews-panel").hidden).toBe(false);
    const rows = document.querySelectorAll<HTMLButtonElement>("#reviews-list .review-open");
    expect(rows).toHaveLength(2);
    const first = rows[0];
    if (!first) {
      throw new Error("expected a review row");
    }
    expect(first.textContent).toContain("Clarify refunds");
    expect(first.querySelector(".review-state")?.textContent).toBe("Changes requested");

    first.click();
    expect(openReview).toHaveBeenCalledWith(twoReviews.items[0]);
    expect(el("reviews-panel").hidden).toBe(true);
  });

  it("does not fan out concurrent host queries on rapid re-opens", async () => {
    let resolveLoad: (payload: PrListPayload) => void = () => {};
    const requestReviews = vi.fn(
      () => new Promise<PrListPayload>((resolve) => (resolveLoad = resolve)),
    );
    const panel = new ReviewsPanel({ ...elements(), requestReviews, openReview: vi.fn() });

    void panel.open();
    void panel.open();
    void panel.open();

    // Only the first click issued a request; the others saw a load already in flight.
    expect(requestReviews).toHaveBeenCalledTimes(1);

    // After it resolves (loading guard drops), a fresh open fetches again. open() calls requestReviews
    // synchronously before its first await, so the count is observable without awaiting the new promise.
    resolveLoad(twoReviews);
    await flush();
    void panel.open();
    expect(requestReviews).toHaveBeenCalledTimes(2);
  });

  it("shows the host's error and no rows when the list fails to load", async () => {
    const panel = new ReviewsPanel({
      ...elements(),
      requestReviews: () => Promise.resolve({ items: [], error: "Couldn't load your reviews." }),
      openReview: vi.fn(),
    });

    await panel.open();

    expect(el("reviews-status").textContent).toBe("Couldn't load your reviews.");
    expect(document.querySelectorAll("#reviews-list .review-open")).toHaveLength(0);
  });

  it("falls back to an error state when requestReviews rejects", async () => {
    const panel = new ReviewsPanel({
      ...elements(),
      requestReviews: () => Promise.reject(new Error("transport failure")),
      openReview: vi.fn(),
    });

    await panel.open();

    expect(el("reviews-status").textContent).toBe("Couldn't load your reviews. Try again later.");
    expect(document.querySelectorAll("#reviews-list .review-open")).toHaveLength(0);
  });

  it("opens a valid pull-request link in SpecDesk and rejects anything else", () => {
    const openReview = vi.fn();
    // Constructed for its side effect: it wires the url-open button's click listener.
    new ReviewsPanel({
      ...elements(),
      requestReviews: () => Promise.resolve({ items: [] }),
      openReview,
    });
    const urlInput = input("reviews-url-input");

    urlInput.value = "https://example.com/not-a-pr";
    el("reviews-url-open").click();
    expect(openReview).not.toHaveBeenCalled();
    expect(el("reviews-status").textContent).toContain("doesn't look like");

    urlInput.value = "https://github.com/octo/spec-repo/pull/123";
    el("reviews-url-open").click();
    expect(openReview).toHaveBeenCalledWith({
      number: 123,
      title: "Review #123",
      url: "https://github.com/octo/spec-repo/pull/123",
      repo: "octo/spec-repo",
      role: "reviewer",
      status: "inReview",
      label: "In review",
    });
    expect(urlInput.value).toBe("");
    expect(el("reviews-panel").hidden).toBe(true);
  });

  it("does not render an in-flight load after the panel is closed", async () => {
    let resolveLoad: (payload: PrListPayload) => void = () => {};
    const panel = new ReviewsPanel({
      ...elements(),
      requestReviews: () => new Promise<PrListPayload>((resolve) => (resolveLoad = resolve)),
      openReview: vi.fn(),
    });

    // Open (load in flight), then close before it resolves.
    void panel.open();
    expect(el("reviews-panel").hidden).toBe(false);
    el("reviews-close").click();
    expect(el("reviews-panel").hidden).toBe(true);

    // The late reply must NOT render into the now-hidden panel.
    resolveLoad(twoReviews);
    await flush();
    expect(document.querySelectorAll("#reviews-list .review-open")).toHaveLength(0);
  });

  it("starts a fresh load after an account change and ignores the retired account reply", async () => {
    const resolves: Array<(payload: PrListPayload) => void> = [];
    const requestReviews = vi.fn(
      () => new Promise<PrListPayload>((resolve) => resolves.push(resolve)),
    );
    const panel = new ReviewsPanel({ ...elements(), requestReviews, openReview: vi.fn() });

    void panel.open();
    expect(requestReviews).toHaveBeenCalledTimes(1);

    panel.clearAccountState();
    expect(el("reviews-panel").hidden).toBe(true);
    void panel.open();
    expect(requestReviews).toHaveBeenCalledTimes(2);

    resolves[0]?.(twoReviews);
    await flush();
    expect(document.querySelectorAll("#reviews-list .review-open")).toHaveLength(0);
    expect(el("reviews-status").textContent).toBe("Loading your reviews…");

    const nextAccountReview = twoReviews.items[1];
    if (nextAccountReview === undefined) {
      throw new Error("expected a review for the replacement account");
    }
    resolves[1]?.({ items: [nextAccountReview] });
    await flush();
    expect(document.querySelectorAll("#reviews-list .review-open")).toHaveLength(1);
    expect(el("reviews-list").textContent).toContain("Payment terms");
  });
});
