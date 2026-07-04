// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { PrListPayload } from "../src/protocol.js";
import { ReviewsPanel } from "../src/reviews-panel.js";

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

function flush(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
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
    const openUrl = vi.fn();
    const panel = new ReviewsPanel({
      requestReviews: () => Promise.resolve(twoReviews),
      openUrl,
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
    expect(openUrl).toHaveBeenCalledWith("https://github.com/o/r/pull/42");
  });

  it("does not fan out concurrent host queries on rapid re-opens", async () => {
    let resolveLoad: (payload: PrListPayload) => void = () => {};
    const requestReviews = vi.fn(
      () => new Promise<PrListPayload>((resolve) => (resolveLoad = resolve)),
    );
    const panel = new ReviewsPanel({ requestReviews, openUrl: vi.fn() });

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
      requestReviews: () => Promise.resolve({ items: [], error: "Couldn't load your reviews." }),
      openUrl: vi.fn(),
    });

    await panel.open();

    expect(el("reviews-status").textContent).toBe("Couldn't load your reviews.");
    expect(document.querySelectorAll("#reviews-list .review-open")).toHaveLength(0);
  });

  it("opens a valid pull-request link by URL and rejects anything else", () => {
    const openUrl = vi.fn();
    // Constructed for its side effect: it wires the url-open button's click listener.
    new ReviewsPanel({ requestReviews: () => Promise.resolve({ items: [] }), openUrl });
    const urlInput = el("reviews-url-input") as HTMLInputElement;

    urlInput.value = "https://example.com/not-a-pr";
    el("reviews-url-open").click();
    expect(openUrl).not.toHaveBeenCalled();
    expect(el("reviews-status").textContent).toContain("doesn't look like");

    urlInput.value = "https://github.com/octo/spec-repo/pull/123";
    el("reviews-url-open").click();
    expect(openUrl).toHaveBeenCalledWith("https://github.com/octo/spec-repo/pull/123");
    expect(urlInput.value).toBe("");
  });

  it("closes on the close button and invalidates an in-flight load", async () => {
    let resolveLoad: (payload: PrListPayload) => void = () => {};
    const panel = new ReviewsPanel({
      requestReviews: () => new Promise<PrListPayload>((resolve) => (resolveLoad = resolve)),
      openUrl: vi.fn(),
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
});
