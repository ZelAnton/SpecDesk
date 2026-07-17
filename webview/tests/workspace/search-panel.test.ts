// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import type { SearchResultsPayload } from "../../src/wire/protocol.js";
import { SearchPanel } from "../../src/workspace/tools/search-panel.js";

function ready(request = vi.fn<(query: string) => Promise<SearchResultsPayload>>()) {
  const onOpenResult = vi.fn<(path: string, line: number) => void>();
  const panel = new SearchPanel({ request, onOpenResult });
  const body = document.createElement("div");
  panel.mount(body);
  return { panel, body, request, onOpenResult };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

function submit(body: HTMLElement, query: string): void {
  const input = body.querySelector<HTMLInputElement>(".search-panel-input");
  const form = body.querySelector<HTMLFormElement>(".search-panel-form");
  if (input === null || form === null) {
    throw new Error("search panel fixture is incomplete");
  }
  input.value = query;
  form.dispatchEvent(new Event("submit", { cancelable: true }));
}

const RESULTS: SearchResultsPayload = {
  query: "refund",
  truncated: false,
  results: [
    {
      path: "C:\\specs\\repo\\billing.md",
      line: 4,
      snippet: "The refund window is 30 days.",
    },
  ],
};

describe("SearchPanel", () => {
  it("starts idle, then shows loading/empty/ready/error states", async () => {
    const request = vi
      .fn<(query: string) => Promise<SearchResultsPayload>>()
      .mockResolvedValueOnce({ query: "nothing", truncated: false, results: [] })
      .mockResolvedValueOnce(RESULTS)
      .mockRejectedValueOnce(new Error("transport failed"));
    const { body } = ready(request);
    expect(body.querySelector(".search-panel")?.getAttribute("data-state")).toBe("idle");

    submit(body, "nothing");
    expect(body.querySelector(".search-panel")?.getAttribute("data-state")).toBe("loading");
    await vi.waitFor(() =>
      expect(body.querySelector(".search-panel")?.getAttribute("data-state")).toBe("empty"),
    );
    expect(body.textContent).toContain('No matches for "nothing"');

    submit(body, "refund");
    await vi.waitFor(() =>
      expect(body.querySelector(".search-panel")?.getAttribute("data-state")).toBe("ready"),
    );
    expect(body.querySelector(".search-panel-result-file")?.textContent).toBe("billing.md:4");
    expect(body.querySelector(".search-panel-result-snippet")?.textContent).toBe(
      "The refund window is 30 days.",
    );

    submit(body, "boom");
    await vi.waitFor(() =>
      expect(body.querySelector(".search-panel")?.getAttribute("data-state")).toBe("error"),
    );
    expect(body.textContent).toContain("Could not search the workspace");
  });

  it("clearing the query returns to idle without a request", () => {
    const { body, request } = ready();
    submit(body, "");
    expect(request).not.toHaveBeenCalled();
    expect(body.querySelector(".search-panel")?.getAttribute("data-state")).toBe("idle");
    expect(body.textContent).toContain("Enter text to search");
  });

  it("opens a result at its 0-based line (the host's 1-based line, converted)", async () => {
    const request = vi
      .fn<(query: string) => Promise<SearchResultsPayload>>()
      .mockResolvedValue(RESULTS);
    const { body, onOpenResult } = ready(request);
    submit(body, "refund");
    await vi.waitFor(() => expect(body.querySelector(".search-panel-result")).not.toBeNull());

    body.querySelector<HTMLButtonElement>(".search-panel-result")?.click();

    expect(onOpenResult).toHaveBeenCalledWith("C:\\specs\\repo\\billing.md", 3);
  });

  it("ignores an older request that resolves after a newer search", async () => {
    const old = deferred<SearchResultsPayload>();
    const fresh = deferred<SearchResultsPayload>();
    const request = vi
      .fn<(query: string) => Promise<SearchResultsPayload>>()
      .mockReturnValueOnce(old.promise)
      .mockReturnValueOnce(fresh.promise);
    const { body } = ready(request);

    submit(body, "old");
    submit(body, "fresh");
    fresh.resolve(RESULTS);
    await vi.waitFor(() =>
      expect(body.querySelector(".search-panel")?.getAttribute("data-state")).toBe("ready"),
    );
    old.resolve({ query: "old", truncated: false, results: [] });
    await Promise.resolve();

    expect(body.querySelector(".search-panel")?.getAttribute("data-state")).toBe("ready");
    expect(body.textContent).toContain("billing.md:4");
  });

  it("notes a truncated result set in the status line", async () => {
    const request = vi.fn<(query: string) => Promise<SearchResultsPayload>>().mockResolvedValue({
      ...RESULTS,
      truncated: true,
    });
    const { body } = ready(request);
    submit(body, "refund");
    await vi.waitFor(() =>
      expect(body.querySelector(".search-panel")?.getAttribute("data-state")).toBe("ready"),
    );
    expect(body.querySelector(".search-panel-status")?.textContent).toContain(
      "showing the first matches",
    );
  });

  it("focusPrimary focuses the search input", () => {
    const { body, panel } = ready();
    document.body.append(body);
    panel.focusPrimary();
    expect(document.activeElement).toBe(body.querySelector(".search-panel-input"));
    body.remove();
  });
});
