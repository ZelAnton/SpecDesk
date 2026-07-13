// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import type { PrListPayload } from "../../src/wire/protocol.js";
import { PullRequestsPanel } from "../../src/workspace/tools/pull-requests-panel.js";

describe("PullRequestsPanel", () => {
  it("renders authored and involved open requests with pull-request-specific states", async () => {
    const payload: PrListPayload = {
      items: [
        {
          number: 1,
          title: "Mine",
          url: "https://github.com/o/r/pull/1",
          repo: "o/r",
          role: "author",
          status: "inReview",
          label: "In review",
        },
        {
          number: 2,
          title: "Joined",
          url: "https://github.com/o/other/pull/2",
          repo: "o/other",
          role: "reviewer",
          status: "approved",
          label: "Approved",
        },
      ],
    };
    const host = document.createElement("div");
    const request = vi.fn().mockResolvedValue(payload);
    const panel = new PullRequestsPanel({ request, openUrl: vi.fn() });
    panel.mount(host);
    expect(panel.id).toBe("pullRequests");
    expect(panel.label).toBe("Pull Requests");
    expect(host.textContent).toContain("Connect a GitHub account");

    panel.setSignedIn(true);
    panel.onShow();
    await vi.waitFor(() => expect(host.querySelectorAll(".remote-review-row")).toHaveLength(2));
    expect(host.querySelector(".remote-review-items")?.getAttribute("aria-label")).toBe(
      "Open pull requests involving you",
    );
    expect(host.textContent).toContain("Mine");
    expect(host.textContent).toContain("Joined");
  });

  it("states that the active queue has no open pull requests when empty", async () => {
    const host = document.createElement("div");
    const panel = new PullRequestsPanel({
      request: vi.fn().mockResolvedValue({ items: [] }),
      openUrl: vi.fn(),
    });
    panel.mount(host);
    panel.setSignedIn(true);
    panel.onShow();
    await vi.waitFor(() => expect(host.textContent).toContain("no open pull requests"));
    expect(host.querySelector(".remote-review-list")?.getAttribute("data-state")).toBe("empty");
  });
});
