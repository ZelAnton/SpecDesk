// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import type { DocumentActivityPayload } from "../../src/wire/protocol.js";
import { DocumentActivityPanel } from "../../src/workspace/tools/document-activity.js";

const payload: DocumentActivityPayload = {
  document: "billing.md",
  versions: [{ id: "a", note: "Clarify refunds", author: "Alex", when: "1970-01-01T00:00:00Z" }],
  historyState: "loaded",
  comments: [],
  commentsState: "loaded",
  history: [
    {
      id: "a",
      label: "Document updated",
      note: "Clarify refunds",
      author: "Alex",
      when: "1970-01-01T00:00:00Z",
    },
  ],
};

async function mount(
  kind: "versions" | "comments" | "history",
  value: DocumentActivityPayload = payload,
) {
  const request = vi.fn<() => Promise<DocumentActivityPayload>>().mockResolvedValue(value);
  const panel = new DocumentActivityPanel(kind, kind, request);
  const body = document.createElement("div");
  panel.mount(body);
  await vi.waitFor(() => expect(request).toHaveBeenCalledOnce());
  await vi.waitFor(() => expect(body.querySelector(".document-activity")).not.toBeNull());
  return { panel, body, request };
}

describe("DocumentActivityPanel", () => {
  it("renders saved versions from the host payload", async () => {
    const { body } = await mount("versions");
    expect(body.textContent).toContain("billing.md");
    expect(body.textContent).toContain("Clarify refunds");
  });

  it("renders an honest empty comment state", async () => {
    const { body } = await mount("comments");
    expect(body.textContent).toContain("No comments on this document");
  });

  it("renders real inline comments supplied by the host", async () => {
    const { body } = await mount("comments", {
      ...payload,
      comments: [
        {
          id: "1",
          author: "reviewer",
          body: "Please clarify this sentence",
          when: "2026-07-13T00:00:00Z",
        },
      ],
    });
    expect(body.textContent).toContain("Please clarify this sentence");
    expect(body.textContent).toContain("reviewer");
  });

  it.each([
    ["notConnected", "Connect to GitHub to load review comments."],
    ["unavailable", "Could not load comments. Try again."],
  ] as const)("renders the host's %s comments state instead of a false empty state", async (state, message) => {
    const { body } = await mount("comments", {
      ...payload,
      commentsState: state,
      commentsMessage: message,
    });
    expect(body.textContent).toContain(message);
    expect(body.textContent).not.toContain("No comments on this document");
  });

  it.each([
    "versions",
    "history",
  ] as const)("renders an unsupported plain-file state in the %s panel without calling it a failure", async (kind) => {
    const { body } = await mount(kind, {
      ...payload,
      versions: [],
      history: [],
      historyState: "notVersioned",
      historyMessage: "Saved versions are available for repository documents.",
    });
    expect(body.textContent).toContain("Saved versions are available for repository documents");
    expect(body.textContent).not.toContain("Could not load");
    expect(body.textContent).not.toContain("No saved");
  });

  it("renders change semantics separately from the version note", async () => {
    const { body } = await mount("history");
    expect(body.querySelector("strong")?.textContent).toBe("Document updated");
    expect(body.querySelector("span")?.textContent).toContain("Clarify refunds");
  });

  it.each([
    "versions",
    "history",
  ] as const)("renders a history failure in the %s panel instead of a false empty state", async (kind) => {
    const { body } = await mount(kind, {
      ...payload,
      versions: [],
      history: [],
      historyState: "unavailable",
      historyMessage: "Could not load saved history. Try again.",
    });
    expect(body.textContent).toContain("Could not load saved history");
    expect(body.textContent).not.toContain("No saved");
  });

  it("drops a late response after a newer refresh", async () => {
    let resolveOld!: (value: DocumentActivityPayload) => void;
    const old = new Promise<DocumentActivityPayload>((resolve) => {
      resolveOld = resolve;
    });
    const fresh = { ...payload, document: "new.md" };
    const request = vi
      .fn<() => Promise<DocumentActivityPayload>>()
      .mockReturnValueOnce(old)
      .mockResolvedValueOnce(fresh);
    const panel = new DocumentActivityPanel("versions", "Versions", request);
    const body = document.createElement("div");
    panel.mount(body);
    await panel.refresh();
    resolveOld(payload);
    await old;
    await Promise.resolve();
    expect(body.textContent).toContain("new.md");
    expect(body.textContent).not.toContain("billing.md");
  });

  it("reloads local activity after an account-bound clear", async () => {
    const { panel, body, request } = await mount("versions");

    panel.clear();
    expect(body.textContent).toBe("");
    await panel.refresh();

    expect(request).toHaveBeenCalledTimes(2);
    expect(body.textContent).toContain("Clarify refunds");
  });
  it("keeps a PR-specific history notice without requesting the previous document", () => {
    const request = vi.fn<() => Promise<DocumentActivityPayload>>().mockResolvedValue(payload);
    const panel = new DocumentActivityPanel("history", "History", request);
    panel.showMessage("Review history is shown in the review document.");
    const body = document.createElement("div");

    panel.mount(body);

    expect(request).not.toHaveBeenCalled();
    expect(body.textContent).toContain("Review history is shown in the review document");
  });
  it("clears private comments and rejects a late old-account response", async () => {
    let resolveOld!: (value: DocumentActivityPayload) => void;
    const old = new Promise<DocumentActivityPayload>((resolve) => {
      resolveOld = resolve;
    });
    const request = vi.fn<() => Promise<DocumentActivityPayload>>().mockReturnValue(old);
    const panel = new DocumentActivityPanel("comments", "Comments", request);
    const body = document.createElement("div");
    panel.mount(body);
    panel.clear();
    resolveOld({
      ...payload,
      comments: [
        {
          id: "private",
          author: "reviewer",
          body: "old account private comment",
          when: "2026-07-13T00:00:00Z",
        },
      ],
    });
    await old;
    await Promise.resolve();

    expect(body.textContent).toBe("");
    expect(body.textContent).not.toContain("old account private comment");
  });
});
