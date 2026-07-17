import { SPELLCHECK_ENABLED, SPELLCHECK_LANG } from "../util/spellcheck.js";
import { DestructiveConfirmation } from "../workspace/destructive-confirmation.js";
import type {
  SelectionComment,
  SelectionCommentDraft,
  SelectionCommentReply,
} from "./selection-comments.js";

const commentDeletionConfirmation = new DestructiveConfirmation();

export interface CommentThreadActions {
  readonly submit: (body: string) => void;
  readonly changeDraft: (body: string) => void;
  readonly cancel: () => void;
  readonly edit: (commentId: string, replyId?: string) => void;
  readonly reply: (commentId: string) => void;
  readonly delete: (commentId: string, replyId?: string) => void;
  readonly retry: () => void;
  /** Post a local thread to the open pull request as a GitHub review comment (only offered when its anchor
   * line is inside a diff hunk — see {@link SelectionComment.githubSync}). */
  readonly postToReview: (commentId: string) => void;
}

export interface CommentThreadRender {
  readonly comment?: SelectionComment;
  readonly draft?: SelectionCommentDraft;
  readonly actions: CommentThreadActions;
  readonly focusDraft?: boolean;
  readonly principalId?: string;
  readonly commentsAvailable?: boolean;
  readonly persistence?: "saved" | "saving" | "error";
  readonly persistenceMessage?: string;
}

export const NO_COMMENT_ACTIONS: CommentThreadActions = {
  submit: () => undefined,
  changeDraft: () => undefined,
  cancel: () => undefined,
  edit: () => undefined,
  reply: () => undefined,
  delete: () => undefined,
  retry: () => undefined,
  postToReview: () => undefined,
};

/** Keep a Markdown textarea exactly tall enough for its content. Width changes are observed because line
 * wrapping changes scrollHeight even when no input event fires. */
export function autoGrow(textarea: HTMLTextAreaElement): () => void {
  let frame = 0;
  let lastWidth = -1;
  const resize = (): void => {
    cancelAnimationFrame(frame);
    frame = requestAnimationFrame(() => {
      textarea.style.height = "auto";
      textarea.style.height = `${Math.max(textarea.scrollHeight, 40)}px`;
    });
  };
  textarea.addEventListener("input", resize);
  const observer =
    typeof ResizeObserver === "undefined"
      ? null
      : new ResizeObserver((entries) => {
          const width = entries[0]?.contentRect.width ?? textarea.clientWidth;
          if (width === lastWidth) return;
          lastWidth = width;
          resize();
        });
  observer?.observe(textarea);
  resize();
  return () => {
    textarea.removeEventListener("input", resize);
    observer?.disconnect();
    cancelAnimationFrame(frame);
  };
}

function composer(
  draft: SelectionCommentDraft,
  actions: CommentThreadActions,
  focusDraft: boolean,
  commentsAvailable: boolean,
): HTMLElement {
  const form = document.createElement("form");
  form.className = "selection-comment-compose selection-comment-compose--inline";
  const label = document.createElement("label");
  label.textContent =
    draft.mode === "reply" ? "Reply" : draft.mode === "edit" ? "Edit comment" : "New comment";
  const textarea = document.createElement("textarea");
  textarea.value = draft.initialBody;
  textarea.rows = 1;
  textarea.placeholder = draft.mode === "reply" ? "Write a reply…" : "Write a comment…";
  textarea.setAttribute("aria-label", label.textContent);
  // setAttribute (not the `.spellcheck`/`.lang` IDL properties) so the attribute is present in the DOM
  // markup itself — jsdom doesn't reflect those properties onto the underlying attribute, so a property
  // assignment here would be invisible both to a jsdom test's `getAttribute` and, in principle, to any
  // consumer that reads the markup rather than the live property.
  textarea.setAttribute("spellcheck", String(SPELLCHECK_ENABLED));
  textarea.setAttribute("lang", SPELLCHECK_LANG);
  const stopGrowing = autoGrow(textarea);
  textarea.addEventListener("input", () => actions.changeDraft(textarea.value));
  const buttons = document.createElement("div");
  const submit = document.createElement("button");
  submit.type = "submit";
  submit.textContent =
    draft.mode === "reply" ? "Reply" : draft.mode === "edit" ? "Save" : "Add comment";
  submit.disabled = !commentsAvailable;
  if (!commentsAvailable) submit.title = "Comments unavailable until saved comments are loaded";
  const cancel = document.createElement("button");
  cancel.type = "button";
  cancel.textContent = "Cancel";
  cancel.addEventListener("click", actions.cancel);
  buttons.append(submit, cancel);
  form.append(label, textarea, buttons);
  form.addEventListener("submit", (event) => {
    event.preventDefault();
    if (textarea.value.trim().length > 0) actions.submit(textarea.value);
  });
  form.addEventListener("keydown", (event) => {
    if (event.key === "Escape") {
      event.preventDefault();
      actions.cancel();
    }
  });
  requestAnimationFrame(() => {
    if (!form.isConnected) {
      stopGrowing();
      return;
    }
    const removalObserver = new MutationObserver(() => {
      if (form.isConnected) return;
      stopGrowing();
      removalObserver.disconnect();
    });
    removalObserver.observe(document, { childList: true, subtree: true });
  });
  if (focusDraft) requestAnimationFrame(() => textarea.focus());
  return form;
}

function actionsRow(
  commentId: string,
  replyId: string | undefined,
  actions: CommentThreadActions,
  allowReply: boolean,
  canMutate: boolean,
  commentsAvailable: boolean,
): HTMLElement {
  const wrapper = document.createElement("div");
  wrapper.className = "selection-comment-actions-wrap";
  const row = document.createElement("div");
  row.className = "selection-comment-actions";
  if (allowReply) {
    const reply = document.createElement("button");
    reply.type = "button";
    reply.textContent = "Reply";
    reply.disabled = !commentsAvailable;
    if (!commentsAvailable) reply.title = "Comments unavailable until saved comments are loaded";
    reply.addEventListener("click", () => actions.reply(commentId));
    row.appendChild(reply);
  }
  if (!canMutate) {
    wrapper.appendChild(row);
    return wrapper;
  }
  const edit = document.createElement("button");
  edit.type = "button";
  edit.textContent = "Edit";
  edit.disabled = !commentsAvailable;
  if (!commentsAvailable) edit.title = "Comments unavailable until saved comments are loaded";
  edit.addEventListener("click", () => actions.edit(commentId, replyId));
  const remove = document.createElement("button");
  remove.type = "button";
  remove.textContent = "Delete";
  remove.disabled = !commentsAvailable;
  if (!commentsAvailable) remove.title = "Comments unavailable until saved comments are loaded";
  remove.setAttribute("aria-expanded", "false");
  remove.addEventListener("click", () => {
    const thread = remove.closest<HTMLElement>(".selection-comment-block");
    const replyFallback =
      replyId === undefined
        ? null
        : thread?.querySelector<HTMLButtonElement>(".selection-comment-message button");
    const surface = remove.closest<HTMLElement>(".cm-editor, .ProseMirror");
    commentDeletionConfirmation.open({
      trigger: remove,
      anchor: row,
      title: replyId === undefined ? "Delete this comment?" : "Delete this reply?",
      description:
        replyId === undefined
          ? "The comment and all of its replies will be removed from this specification."
          : "This reply will be removed from the comment thread.",
      onConfirm: () => actions.delete(commentId, replyId),
      focusAfterConfirm: () => {
        const replacementThread = [
          ...(surface?.querySelectorAll<HTMLElement>("[data-comment-id]") ?? []),
        ].find((candidate) => candidate.dataset.commentId === commentId);
        return (
          replacementThread?.querySelector<HTMLButtonElement>(
            ".selection-comment-message button",
          ) ??
          (replyFallback?.isConnected === true ? replyFallback : null) ??
          surface?.querySelector<HTMLElement>(".cm-content") ??
          surface
        );
      },
    });
  });
  row.append(edit, remove);
  wrapper.appendChild(row);
  return wrapper;
}

function message(
  commentId: string,
  value: SelectionComment | SelectionCommentReply,
  replyId: string | undefined,
  actions: CommentThreadActions,
  principalId: string,
  commentsAvailable: boolean,
  readOnly: boolean,
): HTMLElement {
  const item = document.createElement(replyId === undefined ? "div" : "li");
  item.className = replyId === undefined ? "selection-comment-message" : "selection-comment-reply";
  const meta = document.createElement("div");
  meta.className = "selection-comment-meta";
  const author = document.createElement("strong");
  author.textContent = value.author.displayName;
  const timestamp = document.createElement("time");
  timestamp.dateTime = value.updatedAt ?? value.createdAt;
  // A thread projected from GitHub isn't a local draft, so it reads "On GitHub" rather than "Local".
  timestamp.textContent = readOnly
    ? "On GitHub"
    : value.updatedAt === undefined
      ? "Local"
      : "Edited";
  meta.append(author, timestamp);
  const body = document.createElement("p");
  body.textContent = value.body;
  item.append(
    meta,
    body,
    actionsRow(
      commentId,
      replyId,
      actions,
      // A GitHub-projected thread is read-only in-app: no reply, edit, or delete (replies/resolve to GitHub
      // are a later stage). Local threads keep the owner-only edit/delete and the reply affordance.
      replyId === undefined && !readOnly,
      !readOnly && value.author.principalId === principalId,
      commentsAvailable,
    ),
  );
  return item;
}

/** Build the same semantic thread for CodeMirror and ProseMirror block widgets. */
export function commentThreadDOM(render: CommentThreadRender): HTMLElement {
  const element = document.createElement("aside");
  element.className = "selection-comment-block";
  element.tabIndex = -1;
  element.contentEditable = "false";
  element.setAttribute(
    "aria-label",
    render.comment === undefined && render.draft === undefined
      ? "Comment storage status"
      : "Comment thread on selected text",
  );
  element.addEventListener("pointerdown", (event) => event.stopPropagation());
  element.addEventListener("pointerup", (event) => event.stopPropagation());
  element.addEventListener("mousemove", (event) => event.stopPropagation());
  element.addEventListener("keydown", (event) => event.stopPropagation());
  const commentsAvailable = render.commentsAvailable ?? true;
  if (render.comment === undefined) {
    if (render.draft !== undefined) {
      element.appendChild(
        composer(render.draft, render.actions, render.focusDraft ?? false, commentsAvailable),
      );
    }
    if (render.persistence === "error") appendPersistenceError(element, render);
    return element;
  }
  element.dataset.commentId = render.comment.id;
  element.dataset.anchorState = render.comment.anchorState ?? "attached";
  const isGithub = render.comment.origin === "github";
  if (render.comment.githubSync !== undefined || isGithub) {
    element.dataset.githubSync = isGithub ? "synced" : render.comment.githubSync;
  }
  const principalId = render.principalId ?? "signed-out";
  if (render.comment.anchorState === "detached") {
    const detached = document.createElement("p");
    detached.className = "selection-comment-detached";
    detached.setAttribute("role", "status");
    detached.textContent =
      "The selected text changed or is ambiguous. This thread is detached until its anchor can be resolved.";
    element.appendChild(detached);
  }
  element.appendChild(
    message(
      render.comment.id,
      render.comment,
      undefined,
      render.actions,
      principalId,
      commentsAvailable,
      isGithub,
    ),
  );
  if (render.comment.replies.length > 0) {
    const replies = document.createElement("ol");
    replies.className = "selection-comment-replies";
    for (const reply of render.comment.replies) {
      replies.appendChild(
        message(
          render.comment.id,
          reply,
          reply.id,
          render.actions,
          principalId,
          commentsAvailable,
          isGithub,
        ),
      );
    }
    element.appendChild(replies);
  }
  appendGithubSyncRow(element, render.comment, render.actions, isGithub, commentsAvailable);
  if (render.draft !== undefined) {
    element.appendChild(
      composer(render.draft, render.actions, render.focusDraft ?? false, commentsAvailable),
    );
  }
  if (render.persistence === "error") appendPersistenceError(element, render);
  return element;
}

/** The plain-language line (and, when postable, the Post-to-review action) describing how a thread relates
 * to the open pull request. GitHub-projected threads read as coming from the review; a local thread inside a
 * diff hunk offers to post; one outside a hunk (or with no open PR) is labelled "not yet on GitHub" rather
 * than failing. No git vocabulary reaches the author. */
function appendGithubSyncRow(
  element: HTMLElement,
  comment: SelectionComment,
  actions: CommentThreadActions,
  isGithub: boolean,
  commentsAvailable: boolean,
): void {
  if (isGithub) {
    appendGithubSyncLabel(element, "synced", "From the review on GitHub.");
    return;
  }
  switch (comment.githubSync) {
    case "synced":
      appendGithubSyncLabel(element, "synced", "Posted to the review on GitHub.");
      return;
    case "local-only":
      appendGithubSyncLabel(
        element,
        "local-only",
        "Not yet on GitHub (this line isn't part of the review).",
      );
      return;
    case "publishable": {
      const row = document.createElement("div");
      row.className = "selection-comment-github-sync is-publishable";
      const label = document.createElement("span");
      label.className = "selection-comment-github-label";
      label.textContent = "Not yet on GitHub.";
      const post = document.createElement("button");
      post.type = "button";
      post.className = "selection-comment-github-post";
      post.textContent = "Post to review";
      post.disabled = !commentsAvailable;
      if (!commentsAvailable) post.title = "Comments unavailable until saved comments are loaded";
      post.addEventListener("click", () => actions.postToReview(comment.id));
      row.append(label, post);
      element.appendChild(row);
      return;
    }
    default:
      // No open pull request (or the sync hasn't arrived): a plain local thread, no GitHub affordance.
      return;
  }
}

function appendGithubSyncLabel(
  element: HTMLElement,
  kind: "synced" | "local-only",
  text: string,
): void {
  const row = document.createElement("p");
  row.className = `selection-comment-github-sync is-${kind}`;
  row.setAttribute("role", "status");
  row.textContent = text;
  element.appendChild(row);
}

function appendPersistenceError(element: HTMLElement, render: CommentThreadRender): void {
  const status = document.createElement("div");
  status.className = "selection-comment-persistence-error";
  status.setAttribute("role", "alert");
  const message = document.createElement("p");
  message.textContent = render.persistenceMessage ?? "Comments couldn't be saved.";
  const retry = document.createElement("button");
  retry.type = "button";
  retry.textContent = "Retry";
  retry.addEventListener("click", render.actions.retry);
  status.append(message, retry);
  element.appendChild(status);
}
