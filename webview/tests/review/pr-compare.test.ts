// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { PrCompare, type PrCompareDeps } from "../../src/review/pr-compare.js";
import type { PrComparePayload, PrForFilePayload } from "../../src/wire/protocol.js";

// Minimal markup mirroring the compare surface's ids (all start hidden, as in index.html).
function setupDom(): void {
  document.body.innerHTML = `
    <div id="pr-compare-affordance" hidden>
      <span id="pr-compare-affordance-text"></span>
      <button id="pr-compare-open"></button>
    </div>
    <div id="pr-compare-panel" hidden>
      <div id="pr-compare-head">
        <span id="pr-compare-title"></span>
        <button id="pr-compare-close"></button>
      </div>
      <ul id="pr-compare-list"></ul>
      <div id="pr-compare-controls" hidden>
        <fieldset id="pr-compare-base">
          <legend>Compare against</legend>
          <button data-base="workingCopy" aria-pressed="true">Working copy</button>
          <button data-base="main" aria-pressed="false">Published</button>
        </fieldset>
        <fieldset id="pr-compare-mode">
          <legend>View as</legend>
          <button data-mode="rendered" aria-pressed="true">Formatted</button>
          <button data-mode="raw" aria-pressed="false">Source</button>
        </fieldset>
      </div>
      <span id="pr-compare-status"></span>
      <div id="pr-compare-view"></div>
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

function elements(): Omit<PrCompareDeps, "requestForFile" | "requestCompare" | "onOpenLink"> {
  return {
    affordance: el("pr-compare-affordance"),
    affordanceText: el("pr-compare-affordance-text"),
    openBtn: document.querySelector<HTMLButtonElement>("#pr-compare-open"),
    panel: el("pr-compare-panel"),
    list: el("pr-compare-list"),
    controls: el("pr-compare-controls"),
    status: el("pr-compare-status"),
    view: el("pr-compare-view"),
    closeBtn: document.querySelector<HTMLButtonElement>("#pr-compare-close"),
    baseButtons: [
      ...document.querySelectorAll<HTMLButtonElement>("#pr-compare-controls [data-base]"),
    ],
    modeButtons: [
      ...document.querySelectorAll<HTMLButtonElement>("#pr-compare-controls [data-mode]"),
    ],
  };
}

const oneReview: PrForFilePayload = {
  path: "specs/billing.md",
  items: [
    {
      number: 51,
      title: "Tighten the refund wording",
      url: "https://github.com/o/r/pull/51",
      repo: "o/r",
    },
  ],
};

describe("PrCompare", () => {
  beforeEach(setupDom);

  it("reveals the affordance only when open reviews touch the file", async () => {
    const controller = new PrCompare({
      ...elements(),
      requestForFile: () => Promise.resolve(oneReview),
      requestCompare: () => Promise.reject(new Error("not used")),
      onOpenLink: vi.fn(),
    });

    await controller.refresh("specs/billing.md");
    expect(el("pr-compare-affordance").hidden).toBe(false);
    expect(el("pr-compare-affordance-text").textContent).toContain("1 other review");

    // A file with no in-flight reviews hides it again.
    // (A fresh controller keeps the test focused; refresh is idempotent for the same instance too.)
    const empty = new PrCompare({
      ...elements(),
      requestForFile: () => Promise.resolve({ path: "x", items: [] }),
      requestCompare: () => Promise.reject(new Error("not used")),
      onOpenLink: vi.fn(),
    });
    await empty.refresh("x");
    expect(el("pr-compare-affordance").hidden).toBe(true);
  });

  it("picks a review and requests a comparison, injecting the returned HTML", async () => {
    const requestCompare = vi.fn(
      (): Promise<PrComparePayload> =>
        Promise.resolve({
          html: '<p data-diff="added">New wording.</p>',
          mode: "rendered",
          base: "workingCopy",
        }),
    );
    const controller = new PrCompare({
      ...elements(),
      requestForFile: () => Promise.resolve(oneReview),
      requestCompare,
      onOpenLink: vi.fn(),
    });

    await controller.refresh("specs/billing.md");
    el("pr-compare-open").click();
    expect(el("pr-compare-panel").hidden).toBe(false);

    // Pick the one review.
    document.querySelector<HTMLButtonElement>(".pr-compare-pick")?.click();
    await flush();

    expect(requestCompare).toHaveBeenCalledWith({
      prNumber: 51,
      base: "workingCopy",
      mode: "rendered",
    });
    expect(el("pr-compare-view").innerHTML).toContain('data-diff="added"');
    expect(el("pr-compare-controls").hidden).toBe(false);
  });

  it("re-requests when the base or mode toggle changes", async () => {
    const requestCompare = vi.fn(
      (request): Promise<PrComparePayload> =>
        Promise.resolve({ html: "<p>x</p>", mode: request.mode, base: request.base }),
    );
    const controller = new PrCompare({
      ...elements(),
      requestForFile: () => Promise.resolve(oneReview),
      requestCompare,
      onOpenLink: vi.fn(),
    });

    await controller.refresh("specs/billing.md");
    el("pr-compare-open").click();
    document.querySelector<HTMLButtonElement>(".pr-compare-pick")?.click();
    await flush();

    // Switch base → main, then mode → raw.
    document.querySelector<HTMLButtonElement>('[data-base="main"]')?.click();
    document.querySelector<HTMLButtonElement>('[data-mode="raw"]')?.click();
    await flush();

    expect(requestCompare).toHaveBeenLastCalledWith({ prNumber: 51, base: "main", mode: "raw" });
    // The active toggles reflect the choice.
    expect(document.querySelector('[data-base="main"]')?.getAttribute("aria-pressed")).toBe("true");
    expect(document.querySelector('[data-mode="raw"]')?.getAttribute("aria-pressed")).toBe("true");
  });

  it("shows the host's plain reason when a comparison fails", async () => {
    const controller = new PrCompare({
      ...elements(),
      requestForFile: () => Promise.resolve(oneReview),
      requestCompare: () =>
        Promise.resolve({
          html: "",
          mode: "rendered",
          base: "workingCopy",
          error: "This review no longer changes this file.",
        }),
      onOpenLink: vi.fn(),
    });

    await controller.refresh("specs/billing.md");
    el("pr-compare-open").click();
    document.querySelector<HTMLButtonElement>(".pr-compare-pick")?.click();
    await flush();

    expect(el("pr-compare-status").textContent).toContain("no longer changes this file");
    expect(el("pr-compare-view").innerHTML).toBe("");
  });

  it("clear() hides the whole surface", async () => {
    const controller = new PrCompare({
      ...elements(),
      requestForFile: () => Promise.resolve(oneReview),
      requestCompare: () => Promise.reject(new Error("not used")),
      onOpenLink: vi.fn(),
    });
    await controller.refresh("specs/billing.md");
    controller.clear();
    expect(el("pr-compare-affordance").hidden).toBe(true);
    expect(el("pr-compare-panel").hidden).toBe(true);
  });
});
