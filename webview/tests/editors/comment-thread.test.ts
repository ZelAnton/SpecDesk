// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  autoGrow,
  type CommentThreadActions,
  commentThreadDOM,
} from "../../src/editors/comment-thread.js";
import type {
  SelectionComment,
  SelectionCommentDraft,
} from "../../src/editors/selection-comments.js";

const selection = {
  fromLine: 1,
  toLine: 2,
  anchorLine: 2,
  anchorKind: "line" as const,
  fromOffset: 5,
  toOffset: 20,
  anchorOffset: 20,
  quote: "selected text",
};

const comment: SelectionComment = {
  ...selection,
  id: "comment-1",
  body: "Root comment",
  createdAt: "2026-07-16T00:00:00.000Z",
  author: { principalId: "github:alice", displayName: "Alice" },
  replies: [
    {
      id: "reply-2",
      body: "Thread reply",
      createdAt: "2026-07-16T00:01:00.000Z",
      author: { principalId: "github:alice", displayName: "Alice" },
    },
  ],
};

const draft = (mode: SelectionCommentDraft["mode"]): SelectionCommentDraft => ({
  ...selection,
  documentKey: "repo\0branch\0file.md",
  surface: "code",
  mode,
  ...(mode === "create" ? {} : { commentId: comment.id }),
  initialBody: mode === "edit" ? comment.body : "",
});

describe("inline comment thread controls", () => {
  beforeEach(() => {
    vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
      callback(0);
      return 1;
    });
    vi.stubGlobal("cancelAnimationFrame", () => undefined);
  });

  afterEach(() => {
    document.body.replaceChildren();
    vi.unstubAllGlobals();
  });

  function actions(): CommentThreadActions & Record<string, ReturnType<typeof vi.fn>> {
    return {
      submit: vi.fn(),
      changeDraft: vi.fn(),
      cancel: vi.fn(),
      edit: vi.fn(),
      reply: vi.fn(),
      delete: vi.fn(),
      retry: vi.fn(),
      postToReview: vi.fn(),
    };
  }

  it("renders the creation form directly as the anchored block and submits by keyboard-safe form", () => {
    const handlers = actions();
    const root = commentThreadDOM({ draft: draft("create"), actions: handlers });
    document.body.appendChild(root);
    const textarea = root.querySelector<HTMLTextAreaElement>("textarea");
    if (textarea === null) throw new Error("Expected textarea");
    textarea.value = "A new anchored comment";
    root
      .querySelector("form")
      ?.dispatchEvent(new SubmitEvent("submit", { bubbles: true, cancelable: true }));

    expect(root.classList).toContain("selection-comment-block");
    expect(handlers.submit).toHaveBeenCalledWith("A new anchored comment");
  });

  it("grows down to its full scroll height on multiline input without an internal scrollbar", () => {
    const textarea = document.createElement("textarea");
    let scrollHeight = 48;
    Object.defineProperty(textarea, "scrollHeight", { get: () => scrollHeight });
    const stop = autoGrow(textarea);
    expect(textarea.style.height).toBe("48px");
    scrollHeight = 156;
    textarea.value = "one\ntwo\nthree\nfour";
    textarea.dispatchEvent(new InputEvent("input", { bubbles: true }));
    expect(textarea.style.height).toBe("156px");
    stop();
  });

  it("offers reply and editing for both root and owned replies", () => {
    const handlers = actions();
    const root = commentThreadDOM({ comment, actions: handlers, principalId: "github:alice" });
    document.body.appendChild(root);
    const buttons = [...root.querySelectorAll<HTMLButtonElement>("button")];
    buttons.find((button) => button.textContent === "Reply")?.click();
    buttons.find((button) => button.textContent === "Edit")?.click();
    const replyEdit = root
      .querySelector(".selection-comment-reply")
      ?.querySelector<HTMLButtonElement>("button");
    replyEdit?.click();

    expect(handlers.reply).toHaveBeenCalledWith(comment.id);
    expect(handlers.edit).toHaveBeenCalledWith(comment.id, undefined);
    expect(handlers.edit).toHaveBeenCalledWith(comment.id, comment.replies[0]?.id);
  });

  it("preserves a draft but disables every mutation control while saved comments are unavailable", () => {
    const handlers = actions();
    const root = commentThreadDOM({
      comment,
      draft: { ...draft("edit"), initialBody: "Unsaved draft text" },
      actions: handlers,
      principalId: "github:alice",
      commentsAvailable: false,
      persistence: "error",
      persistenceMessage: "Comments are unavailable until their saved snapshot loads.",
    });
    document.body.appendChild(root);

    expect(root.querySelector<HTMLTextAreaElement>("textarea")?.value).toBe("Unsaved draft text");
    const mutationButtons = [...root.querySelectorAll<HTMLButtonElement>("button")].filter(
      (button) => ["Reply", "Edit", "Delete", "Save"].includes(button.textContent ?? ""),
    );
    expect(mutationButtons.length).toBeGreaterThan(0);
    expect(mutationButtons.every((button) => button.disabled)).toBe(true);
    expect(mutationButtons.every((button) => button.title.includes("unavailable until"))).toBe(
      true,
    );
    expect(root.querySelector<HTMLButtonElement>('button[type="button"]')?.disabled).toBe(true);
    expect(
      [...root.querySelectorAll("button")].find((button) => button.textContent === "Cancel")
        ?.disabled,
    ).toBe(false);
    expect(
      [...root.querySelectorAll("button")].find((button) => button.textContent === "Retry")
        ?.disabled,
    ).toBe(false);
  });

  it("shows authors but withholds Edit and Delete from a different principal", () => {
    const handlers = actions();
    const root = commentThreadDOM({ comment, actions: handlers, principalId: "github:bob" });
    document.body.appendChild(root);
    expect(root.textContent).toContain("Alice");
    expect(root.querySelectorAll("button")).toHaveLength(1);
    expect(root.querySelector("button")?.textContent).toBe("Reply");
  });

  it("offers Post to review for a thread inside a diff hunk and forwards the click", () => {
    const handlers = actions();
    const root = commentThreadDOM({
      comment: { ...comment, githubSync: "publishable" },
      actions: handlers,
      principalId: "github:alice",
    });
    document.body.appendChild(root);

    const post = root.querySelector<HTMLButtonElement>(".selection-comment-github-post");
    expect(post).not.toBeNull();
    expect(root.dataset.githubSync).toBe("publishable");
    post?.click();
    expect(handlers.postToReview).toHaveBeenCalledWith("comment-1");
  });

  it("labels an out-of-hunk thread 'not yet on GitHub' with no post action", () => {
    const handlers = actions();
    const root = commentThreadDOM({
      comment: { ...comment, githubSync: "local-only" },
      actions: handlers,
      principalId: "github:alice",
    });
    document.body.appendChild(root);

    expect(root.querySelector(".selection-comment-github-post")).toBeNull();
    expect(root.querySelector(".selection-comment-github-sync")?.textContent).toContain(
      "Not yet on GitHub",
    );
  });

  it("renders a GitHub-projected thread read-only and from the review", () => {
    const handlers = actions();
    const root = commentThreadDOM({
      // A pulled thread: authored by someone else, carries a github id, and is flagged origin github.
      comment: {
        ...comment,
        origin: "github",
        githubId: 1001,
        author: { principalId: "github:sam", displayName: "Sam" },
        replies: [],
      },
      actions: handlers,
      principalId: "github:alice",
    });
    document.body.appendChild(root);

    expect(root.dataset.githubSync).toBe("synced");
    expect(root.querySelector(".selection-comment-github-sync")?.textContent).toContain(
      "From the review on GitHub",
    );
    // No Reply / Edit / Delete / Post affordances on a read-only projected thread.
    expect(root.querySelectorAll("button")).toHaveLength(0);
  });

  it("labels an unresolved thread as detached instead of implying a source attachment", () => {
    const handlers = actions();
    const root = commentThreadDOM({
      comment: { ...comment, anchorState: "detached" },
      actions: handlers,
      principalId: "github:alice",
    });
    document.body.appendChild(root);

    expect(root.dataset.anchorState).toBe("detached");
    expect(root.querySelector('[role="status"]')?.textContent).toContain("detached");
    expect(root.textContent).toContain("Root comment");
  });

  it("surfaces persistence failure with an operable Retry action", () => {
    const handlers = actions();
    const root = commentThreadDOM({
      comment,
      actions: handlers,
      principalId: "github:alice",
      persistence: "error",
      persistenceMessage: "Comments couldn't be saved. Retry storage.",
    });
    document.body.appendChild(root);
    const alert = root.querySelector('[role="alert"]');
    expect(alert?.textContent).toContain("couldn't be saved");
    alert?.querySelector<HTMLButtonElement>("button")?.click();
    expect(handlers.retry).toHaveBeenCalledOnce();
  });

  it("never deletes on the first action and requires the red confirmation below it", () => {
    const handlers = actions();
    const root = commentThreadDOM({ comment, actions: handlers, principalId: "github:alice" });
    document.body.appendChild(root);
    const remove = [...root.querySelectorAll<HTMLButtonElement>("button")].find(
      (button) => button.textContent === "Delete",
    );
    remove?.click();

    expect(handlers.delete).not.toHaveBeenCalled();
    const confirm = root.querySelector<HTMLButtonElement>(".destructive-confirmation-action");
    expect(confirm?.textContent).toBe("Confirm deletion");
    expect(confirm?.closest(".selection-comment-actions-wrap")).not.toBeNull();
    confirm?.click();
    expect(handlers.delete).toHaveBeenCalledWith(comment.id, undefined);
  });

  it("dismisses deletion on Escape or outside click without mutating the thread", () => {
    const handlers = actions();
    const root = commentThreadDOM({ comment, actions: handlers, principalId: "github:alice" });
    document.body.appendChild(root);
    const remove = [...root.querySelectorAll<HTMLButtonElement>("button")].find(
      (button) => button.textContent === "Delete",
    );
    remove?.click();
    root
      .querySelector<HTMLButtonElement>(".destructive-confirmation-action")
      ?.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    expect(root.querySelector(".destructive-confirmation")).toBeNull();
    expect(document.activeElement).toBe(remove);
    remove?.click();
    document.body.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true }));
    expect(root.querySelector(".destructive-confirmation")).toBeNull();
    expect(document.activeElement).toBe(remove);
    expect(handlers.delete).not.toHaveBeenCalled();
  });

  it("moves focus to the editor after confirmed root deletion", () => {
    const handlers = actions();
    const editor = document.createElement("div");
    editor.className = "cm-editor";
    const content = document.createElement("div");
    content.className = "cm-content";
    content.tabIndex = 0;
    editor.appendChild(content);
    const root = commentThreadDOM({ comment, actions: handlers, principalId: "github:alice" });
    editor.appendChild(root);
    document.body.appendChild(editor);
    const remove = root.querySelector<HTMLButtonElement>(
      ".selection-comment-message button:last-child",
    );
    vi.mocked(handlers.delete).mockImplementation(() => root.remove());
    remove?.click();
    root.querySelector<HTMLButtonElement>(".destructive-confirmation-action")?.click();
    expect(document.activeElement).toBe(content);
  });

  it("cancels an inline editor with Escape", () => {
    const handlers = actions();
    const root = commentThreadDOM({
      comment,
      draft: draft("edit"),
      actions: handlers,
      principalId: "github:alice",
    });
    document.body.appendChild(root);
    root
      .querySelector("form")
      ?.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    expect(handlers.cancel).toHaveBeenCalledOnce();
    expect(handlers.submit).not.toHaveBeenCalled();
  });
});
