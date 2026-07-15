// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import type { PrDetailsPayload } from "../../src/wire/protocol.js";
import { CentralFrame } from "../../src/workspace/central-frame.js";
import {
  CommentDetailPanel,
  PrCommentsPanel,
  type PullRequestMutations,
  PullRequestView,
} from "../../src/workspace/tools/pull-request-experience.js";

const DETAILS: PrDetailsPayload = {
  number: 42,
  repo: "octo/spec",
  title: "Clarify refunds",
  body: "Explain the refund window.",
  url: "https://github.com/octo/spec/pull/42",
  state: "open",
  isDraft: false,
  author: "alex",
  authorAvatarUrl: "",
  baseBranch: "main",
  headBranch: "spec/refunds",
  reviewers: [{ login: "sam", avatarUrl: "", kind: "user" }],
  comments: [
    {
      id: 9,
      kind: "conversation",
      path: "",
      author: "sam",
      avatarUrl: "",
      body: "Please clarify.",
      createdAt: "2026-07-14T10:00:00Z",
      updatedAt: "2026-07-14T10:00:00Z",
      viewerDidAuthor: false,
    },
  ],
  commentsIncomplete: false,
  commitsIncomplete: false,
  commits: [
    {
      oid: "abcdef",
      shortOid: "abcdef0",
      title: "Clarify window",
      when: "2026-07-14T09:00:00Z",
      checkState: "success",
    },
  ],
};

function mutations(): PullRequestMutations {
  return {
    create: vi.fn().mockResolvedValue(undefined),
    reply: vi.fn().mockResolvedValue(undefined),
    update: vi.fn().mockResolvedValue(undefined),
    reviewers: vi.fn().mockResolvedValue(undefined),
  };
}

describe("pull request experience", () => {
  it("renders the in-app PR document with description, history, comments, CI and changes placeholder", async () => {
    const host = document.createElement("div");
    host.dataset.view = "home";
    const home = document.createElement("section");
    host.appendChild(home);
    const frame = new CentralFrame(host);
    frame.register({ id: "home", el: home });
    const comments = new PrCommentsPanel(
      mutations(),
      vi.fn(),
      vi.fn().mockResolvedValue(undefined),
    );
    const onContext = vi.fn();
    const view = new PullRequestView(
      host,
      frame,
      vi.fn().mockResolvedValue(DETAILS),
      mutations(),
      comments,
      onContext,
    );

    await view.open(
      {
        number: 42,
        repo: "octo/spec",
        title: "Clarify refunds",
        url: DETAILS.url,
        role: "author",
        status: "inReview",
        label: "In review",
      },
      frame,
    );

    expect(frame.active()).toBe("pull-request");
    expect(host.querySelector("#pull-request-view")?.getAttribute("aria-label")).toBe(
      "Change request",
    );
    expect(host.textContent).toContain("Clarify refunds");
    expect(host.textContent).toContain("Explain the refund window.");
    expect(host.textContent).toContain("Checks passed");
    expect(host.textContent).toContain("Comments");
    expect(host.textContent).toContain("Please clarify.");
    expect(host.textContent).toContain("Request review");
    expect(host.textContent).toContain("document-by-document comparison");
    expect(onContext).toHaveBeenCalledWith("octo/spec", "spec/refunds");
  });

  it("shows the native review shell while details load and preserves it after a load error", async () => {
    const host = document.createElement("div");
    const home = document.createElement("section");
    host.appendChild(home);
    const frame = new CentralFrame(host);
    frame.register({ id: "home", el: home });
    const comments = new PrCommentsPanel(
      mutations(),
      vi.fn(),
      vi.fn().mockResolvedValue(undefined),
    );
    let resolveDetails: (value: PrDetailsPayload) => void = () => {};
    const load = vi.fn(
      () => new Promise<PrDetailsPayload>((resolve) => (resolveDetails = resolve)),
    );
    const view = new PullRequestView(host, frame, load, mutations(), comments);
    const opened = view.open(
      {
        number: 42,
        repo: "octo/spec",
        title: "Clarify refunds",
        url: DETAILS.url,
        role: "author",
        status: "inReview",
        label: "In review",
      },
      frame,
    );

    expect(host.querySelector("h1")?.textContent).toBe("Clarify refunds");
    expect(host.textContent).toContain(
      "Loading the description, people, history, and conversation",
    );
    resolveDetails({ ...DETAILS, error: "GitHub details are temporarily unavailable." });
    await opened;

    expect(host.querySelector("h1")?.textContent).toBe("Clarify refunds");
    expect(host.textContent).toContain("GitHub details are temporarily unavailable.");
    expect(host.querySelector<HTMLButtonElement>(".pr-view-retry")?.textContent).toBe("Try again");
  });

  it("keeps the review document available when only comments are incomplete", async () => {
    const host = document.createElement("div");
    const home = document.createElement("section");
    host.appendChild(home);
    const frame = new CentralFrame(host);
    frame.register({ id: "home", el: home });
    const comments = new PrCommentsPanel(
      mutations(),
      vi.fn(),
      vi.fn().mockResolvedValue(undefined),
    );
    const view = new PullRequestView(
      host,
      frame,
      vi.fn().mockResolvedValue({ ...DETAILS, comments: [], commentsIncomplete: true }),
      mutations(),
      comments,
    );

    await view.open(
      {
        number: 42,
        repo: "octo/spec",
        title: DETAILS.title,
        url: DETAILS.url,
        role: "author",
        status: "inReview",
        label: "In review",
      },
      frame,
    );

    expect(host.textContent).toContain("Explain the refund window.");
    expect(host.textContent).toContain("Checks passed");
    expect(host.textContent).toContain("Comments couldn't be loaded");
  });

  it("formats a selected comment draft and opens a selected comment in the bottom detail panel", () => {
    const mutation = mutations();
    const detailHost = document.createElement("div");
    const detail = new CommentDetailPanel();
    detail.mount(detailHost);
    const panelHost = document.createElement("div");
    const panel = new PrCommentsPanel(
      mutation,
      (comment) => detail.select(comment),
      vi.fn().mockResolvedValue(undefined),
    );
    panel.mount(panelHost);
    panel.setDetails(DETAILS);
    expect(panelHost.querySelector("textarea")?.getAttribute("aria-label")).toBe(
      "New change-request comment",
    );

    const textarea = panelHost.querySelector<HTMLTextAreaElement>(".pr-comment-compose textarea");
    if (textarea === null) throw new Error("missing comment editor");
    textarea.value = "hello";
    textarea.setSelectionRange(0, 5);
    textarea.dispatchEvent(new Event("select"));
    vi.spyOn(textarea, "getBoundingClientRect").mockReturnValue({
      left: 0,
      right: 100,
      top: 0,
      bottom: 100,
      width: 100,
      height: 100,
      x: 0,
      y: 0,
      toJSON: () => ({}),
    });
    const originalClientRects = Range.prototype.getClientRects;
    Object.defineProperty(Range.prototype, "getClientRects", {
      configurable: true,
      value: () => [new DOMRect(20, 20, 40, 20)],
    });
    const toolbar = panelHost.querySelector<HTMLElement>(".selection-format-popover");
    expect(toolbar?.hidden).toBe(true);
    textarea.dispatchEvent(new MouseEvent("pointermove", { clientX: 30, clientY: 30 }));
    expect(toolbar?.hidden).toBe(false);
    const bold = panelHost.querySelector<HTMLButtonElement>(
      '.selection-format-popover button[title="Bold"]',
    );
    bold?.click();
    expect(textarea.value).toBe("**hello**");
    expect(toolbar?.hidden).toBe(true);
    Object.defineProperty(Range.prototype, "getClientRects", {
      configurable: true,
      value: originalClientRects,
    });

    panelHost.querySelector<HTMLButtonElement>(".pr-comment-open")?.click();
    expect(detailHost.textContent).toContain("Please clarify.");
    expect(detailHost.textContent).toContain("sam");
  });

  it("makes a bounded comment history explicit instead of presenting it as complete", () => {
    const panelHost = document.createElement("div");
    const panel = new PrCommentsPanel(mutations(), vi.fn(), vi.fn().mockResolvedValue(undefined));
    panel.mount(panelHost);
    panel.setDetails({ ...DETAILS, commentsIncomplete: true });

    expect(panelHost.textContent).toContain("Some comments aren't available");
    expect(panelHost.querySelector('[role="status"]')).not.toBeNull();
  });

  it("makes a bounded commit history explicit instead of presenting it as complete", async () => {
    const host = document.createElement("div");
    const home = document.createElement("section");
    host.appendChild(home);
    const frame = new CentralFrame(host);
    frame.register({ id: "home", el: home });
    const comments = new PrCommentsPanel(
      mutations(),
      vi.fn(),
      vi.fn().mockResolvedValue(undefined),
    );
    comments.mount(document.createElement("div"));
    const view = new PullRequestView(
      host,
      frame,
      vi.fn().mockResolvedValue({ ...DETAILS, commitsIncomplete: true }),
      mutations(),
      comments,
    );

    await view.open(
      {
        number: 42,
        repo: "octo/spec",
        title: "Clarify refunds",
        url: DETAILS.url,
        role: "author",
        status: "inReview",
        label: "In review",
      },
      frame,
    );

    expect(host.textContent).toContain("Some earlier saved versions aren't available");
  });

  it("renders PR prose as inert Markdown without external navigation or resource loads", async () => {
    const host = document.createElement("div");
    const home = document.createElement("section");
    host.appendChild(home);
    const frame = new CentralFrame(host);
    frame.register({ id: "home", el: home });
    const comments = new PrCommentsPanel(
      mutations(),
      vi.fn(),
      vi.fn().mockResolvedValue(undefined),
    );
    const sourceComment = DETAILS.comments.at(0);
    if (sourceComment === undefined) throw new Error("missing fixture comment");
    const view = new PullRequestView(
      host,
      frame,
      vi.fn().mockResolvedValue({
        ...DETAILS,
        body: "# Important\n\nRead **this** [guide](https://example.test). <script>bad()</script>\n\n![tracker](https://example.test/pixel.png)",
        comments: [{ ...sourceComment, body: "Use `thirty days` [here](javascript:bad())" }],
      }),
      mutations(),
      comments,
    );

    await view.open(
      {
        number: 42,
        repo: "octo/spec",
        title: DETAILS.title,
        url: DETAILS.url,
        role: "author",
        status: "inReview",
        label: "In review",
      },
      frame,
    );

    expect(host.querySelectorAll("h1")).toHaveLength(1);
    expect(host.querySelector(".pr-view-description h3")?.textContent).toBe("Important");
    expect(host.querySelector(".pr-view-description strong")?.textContent).toBe("this");
    expect(host.querySelector(".pr-view-comment code")?.textContent).toBe("thirty days");
    expect(host.querySelector(".pr-view-markdown a")).toBeNull();
    expect(host.querySelector(".pr-view-markdown img")).toBeNull();
    expect(host.querySelector("script")).toBeNull();
    expect(host.textContent).toContain("<script>bad()</script>");
  });

  it("turns a rejected details request into a retryable native error document", async () => {
    const host = document.createElement("div");
    const home = document.createElement("section");
    host.appendChild(home);
    const frame = new CentralFrame(host);
    frame.register({ id: "home", el: home });
    const comments = new PrCommentsPanel(
      mutations(),
      vi.fn(),
      vi.fn().mockResolvedValue(undefined),
    );
    const view = new PullRequestView(
      host,
      frame,
      vi.fn().mockRejectedValue(new Error("offline")),
      mutations(),
      comments,
    );

    await view.open(
      {
        number: 42,
        repo: "octo/spec",
        title: DETAILS.title,
        url: DETAILS.url,
        role: "author",
        status: "inReview",
        label: "In review",
      },
      frame,
    );

    expect(host.querySelector("h1")?.textContent).toBe(DETAILS.title);
    expect(host.querySelector('[role="alert"]')?.textContent).toContain("Couldn't load");
    expect(host.querySelector<HTMLButtonElement>(".pr-view-retry")).not.toBeNull();
  });

  it("clears rendered review and selected-comment data at an account boundary", async () => {
    const host = document.createElement("div");
    const home = document.createElement("section");
    host.appendChild(home);
    const frame = new CentralFrame(host);
    frame.register({ id: "home", el: home });
    const commentsHost = document.createElement("div");
    const detailHost = document.createElement("div");
    const detail = new CommentDetailPanel();
    detail.mount(detailHost);
    const comments = new PrCommentsPanel(
      mutations(),
      (comment) => detail.select(comment),
      vi.fn().mockResolvedValue(undefined),
    );
    comments.mount(commentsHost);
    const view = new PullRequestView(
      host,
      frame,
      vi.fn().mockResolvedValue(DETAILS),
      mutations(),
      comments,
    );
    await view.open(
      {
        number: 42,
        repo: "octo/spec",
        title: DETAILS.title,
        url: DETAILS.url,
        role: "author",
        status: "inReview",
        label: "In review",
      },
      frame,
    );
    commentsHost.querySelector<HTMLButtonElement>(".pr-comment-open")?.click();

    view.clear();
    comments.setDetails(null);
    detail.clear();

    expect(host.textContent).not.toContain(DETAILS.title);
    expect(commentsHost.textContent).not.toContain("Please clarify.");
    expect(detailHost.textContent).not.toContain("Please clarify.");
  });
});
