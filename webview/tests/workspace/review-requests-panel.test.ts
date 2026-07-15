// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import type { PrListPayload } from "../../src/wire/protocol.js";
import { ReviewRequestsPanel } from "../../src/workspace/tools/review-requests-panel.js";

const REVIEW: PrListPayload = {
  items: [
    {
      number: 7,
      title: "Payment terms",
      url: "https://github.com/octo/spec/pull/7",
      repo: "octo/spec",
      role: "reviewer",
      status: "inReview",
      label: "In review",
    },
  ],
};

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

function ready(request = vi.fn<() => Promise<PrListPayload>>()) {
  const host = document.createElement("div");
  const openReview = vi.fn();
  const panel = new ReviewRequestsPanel({ request, openReview });
  panel.mount(host);
  return { host, panel, request, openReview };
}

describe("ReviewRequestsPanel", () => {
  it("shows auth, loading, empty, error, and ready states", async () => {
    const request = vi
      .fn<() => Promise<PrListPayload>>()
      .mockResolvedValueOnce({ items: [] })
      .mockResolvedValueOnce({ items: [], error: "GitHub is unavailable." })
      .mockResolvedValueOnce(REVIEW);
    const { host, panel, openReview } = ready(request);
    expect(host.querySelector(".remote-review-list")?.getAttribute("data-state")).toBe("auth");

    panel.setSignedIn(true);
    panel.onShow();
    expect(host.querySelector(".remote-review-list")?.getAttribute("data-state")).toBe("loading");
    await vi.waitFor(() =>
      expect(host.querySelector(".remote-review-list")?.getAttribute("data-state")).toBe("empty"),
    );

    await panel.refresh();
    expect(host.querySelector(".remote-review-list")?.getAttribute("data-state")).toBe("error");
    expect(host.textContent).toContain("GitHub is unavailable.");

    await panel.refresh();
    expect(host.querySelector(".remote-review-title")?.textContent).toBe("Payment terms");
    host.querySelector<HTMLButtonElement>(".remote-review-open")?.click();
    expect(openReview).toHaveBeenCalledWith(REVIEW.items[0]);
  });

  it("ignores an older request that resolves after a newer refresh", async () => {
    const old = deferred<PrListPayload>();
    const fresh = deferred<PrListPayload>();
    const request = vi
      .fn<() => Promise<PrListPayload>>()
      .mockReturnValueOnce(old.promise)
      .mockReturnValueOnce(fresh.promise);
    const { host, panel } = ready(request);
    panel.setSignedIn(true);
    panel.onShow();
    const refresh = panel.refresh();
    fresh.resolve(REVIEW);
    await refresh;
    const first = REVIEW.items[0];
    if (first === undefined) {
      throw new Error("missing review fixture");
    }
    old.resolve({ items: [{ ...first, title: "Stale result" }] });
    await Promise.resolve();

    expect(host.textContent).toContain("Payment terms");
    expect(host.textContent).not.toContain("Stale result");
  });

  it("invalidates an in-flight request when hidden or signed out", async () => {
    const pending = deferred<PrListPayload>();
    const { host, panel } = ready(vi.fn().mockReturnValue(pending.promise));
    panel.setSignedIn(true);
    panel.onShow();
    panel.onHide();
    panel.setSignedIn(false);
    pending.resolve(REVIEW);
    await Promise.resolve();
    await Promise.resolve();

    expect(host.querySelector(".remote-review-list")?.getAttribute("data-state")).toBe("auth");
    expect(host.querySelector(".remote-review-row")).toBeNull();
  });
});
