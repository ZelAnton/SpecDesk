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
): HTMLElement {
  const item = document.createElement(replyId === undefined ? "div" : "li");
  item.className = replyId === undefined ? "selection-comment-message" : "selection-comment-reply";
  const meta = document.createElement("div");
  meta.className = "selection-comment-meta";
  const author = document.createElement("strong");
  author.textContent = value.author.displayName;
  const timestamp = document.createElement("time");
  timestamp.dateTime = value.updatedAt ?? value.createdAt;
  timestamp.textContent = value.updatedAt === undefined ? "Local" : "Edited";
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
      replyId === undefined,
      value.author.principalId === principalId,
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
    ),
  );
  if (render.comment.replies.length > 0) {
    const replies = document.createElement("ol");
    replies.className = "selection-comment-replies";
    for (const reply of render.comment.replies) {
      replies.appendChild(
        message(render.comment.id, reply, reply.id, render.actions, principalId, commentsAvailable),
      );
    }
    element.appendChild(replies);
  }
  if (render.draft !== undefined) {
    element.appendChild(
      composer(render.draft, render.actions, render.focusDraft ?? false, commentsAvailable),
    );
  }
  if (render.persistence === "error") appendPersistenceError(element, render);
  return element;
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
