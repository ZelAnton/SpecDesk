import { type FormatCommand, formatMarkdown } from "../../editors/md-format.js";
import type { PrCommentPayload, PrDetailsPayload, PrListItemPayload } from "../../wire/protocol.js";
import { activityStream } from "../activity-stream.js";
import type { CentralFrame } from "../central-frame.js";
import { icon } from "../icons.js";
import type { PanelTool } from "../panel-tool.js";

const MINI_FORMATS: readonly { id: FormatCommand; label: string }[] = [
  { id: "bold", label: "Bold" },
  { id: "italic", label: "Italic" },
  { id: "strike", label: "Strike" },
  { id: "inlineCode", label: "Code" },
  { id: "link", label: "Link" },
  { id: "bullet", label: "List" },
  { id: "quote", label: "Quote" },
];

function selectedTextRects(input: HTMLTextAreaElement): DOMRect[] {
  const { selectionStart, selectionEnd } = input;
  if (selectionStart === selectionEnd || document.body === null) return [];
  const inputRect = input.getBoundingClientRect();
  const computed = getComputedStyle(input);
  const mirror = document.createElement("div");
  mirror.setAttribute("aria-hidden", "true");
  Object.assign(mirror.style, {
    position: "fixed",
    visibility: "hidden",
    pointerEvents: "none",
    left: `${inputRect.left}px`,
    top: `${inputRect.top}px`,
    width: `${inputRect.width}px`,
    height: `${inputRect.height}px`,
    boxSizing: computed.boxSizing,
    overflow: "auto",
    whiteSpace: "pre-wrap",
    overflowWrap: computed.overflowWrap,
    wordBreak: computed.wordBreak,
    borderTop: computed.borderTop,
    borderRight: computed.borderRight,
    borderBottom: computed.borderBottom,
    borderLeft: computed.borderLeft,
    padding: computed.padding,
    font: computed.font,
    letterSpacing: computed.letterSpacing,
    lineHeight: computed.lineHeight,
    tabSize: computed.tabSize,
    textAlign: computed.textAlign,
    textIndent: computed.textIndent,
  });
  mirror.append(document.createTextNode(input.value.slice(0, selectionStart)));
  const selected = document.createElement("span");
  selected.textContent = input.value.slice(selectionStart, selectionEnd);
  mirror.append(selected, document.createTextNode(input.value.slice(selectionEnd) || "\u200b"));
  document.body.appendChild(mirror);
  mirror.scrollLeft = input.scrollLeft;
  mirror.scrollTop = input.scrollTop;
  const range = document.createRange();
  range.selectNodeContents(selected);
  const rects = Array.from(range.getClientRects()).filter(
    (rect) =>
      rect.right >= inputRect.left &&
      rect.left <= inputRect.right &&
      rect.bottom >= inputRect.top &&
      rect.top <= inputRect.bottom,
  );
  mirror.remove();
  return rects;
}

function formattedTextarea(
  className: string,
  label: string,
): { root: HTMLElement; input: HTMLTextAreaElement } {
  const root = document.createElement("div");
  root.className = `markdown-compose ${className}`;
  const input = document.createElement("textarea");
  input.setAttribute("aria-label", label);
  input.rows = 4;
  const toolbar = document.createElement("div");
  toolbar.className = "selection-format-popover";
  toolbar.setAttribute("role", "toolbar");
  toolbar.setAttribute("aria-label", "Format selected text");
  toolbar.hidden = true;
  const hide = (): void => {
    toolbar.hidden = true;
  };
  const showAt = (clientX: number, clientY: number): void => {
    if (input.selectionStart === input.selectionEnd) {
      hide();
      return;
    }
    toolbar.hidden = false;
    const rootRect = root.getBoundingClientRect();
    const toolbarRect = toolbar.getBoundingClientRect();
    const maximumLeft = Math.max(8, rootRect.width - toolbarRect.width - 8);
    const maximumTop = Math.max(8, rootRect.height - toolbarRect.height - 8);
    const desiredTop = clientY - rootRect.top - toolbarRect.height - 8;
    const fallbackTop = clientY - rootRect.top + 12;
    toolbar.style.left = `${Math.min(maximumLeft, Math.max(8, clientX - rootRect.left))}px`;
    toolbar.style.top = `${Math.min(maximumTop, Math.max(8, desiredTop >= 8 ? desiredTop : fallbackTop))}px`;
  };
  // A selection alone must not leave a permanent toolbar row in the composer. It appears only while
  // the pointer is over the selected editor text and is anchored beside that pointer.
  input.addEventListener("select", hide);
  input.addEventListener("keyup", hide);
  input.addEventListener("pointerup", hide);
  input.addEventListener("pointermove", (event) => {
    const overSelection = selectedTextRects(input).some(
      (rect) =>
        event.clientX >= rect.left - 2 &&
        event.clientX <= rect.right + 2 &&
        event.clientY >= rect.top - 2 &&
        event.clientY <= rect.bottom + 2,
    );
    if (overSelection) showAt(event.clientX, event.clientY);
    else hide();
  });
  input.addEventListener("pointerleave", (event) => {
    if (event.relatedTarget instanceof Node && toolbar.contains(event.relatedTarget)) return;
    hide();
  });
  toolbar.addEventListener("pointerleave", (event) => {
    if (event.relatedTarget === input) return;
    hide();
  });
  for (const format of MINI_FORMATS) {
    const button = document.createElement("button");
    button.type = "button";
    button.title = format.label;
    button.textContent = format.label.slice(0, 1);
    button.addEventListener("pointerdown", (event) => event.preventDefault());
    button.addEventListener("click", () => {
      const edit = formatMarkdown(input.value, input.selectionStart, input.selectionEnd, format.id);
      input.setRangeText(edit.insert, edit.from, edit.to, "preserve");
      input.setSelectionRange(edit.selectionStart, edit.selectionEnd);
      input.dispatchEvent(new Event("input", { bubbles: true }));
      input.focus();
      hide();
    });
    toolbar.appendChild(button);
  }
  root.append(input, toolbar);
  return { root, input };
}

export interface PullRequestMutations {
  create(repo: string, number: number, body: string): Promise<void>;
  reply(repo: string, number: number, comment: PrCommentPayload, body: string): Promise<void>;
  update(repo: string, number: number, comment: PrCommentPayload, body: string): Promise<void>;
  reviewers(repo: string, number: number, handles: readonly string[]): Promise<void>;
}

export class CommentDetailPanel implements PanelTool {
  readonly id = "comment";
  readonly label = "Comment";
  readonly icon = icon("comment");
  private body: HTMLElement | null = null;
  private selected: PrCommentPayload | null = null;

  mount(body: HTMLElement): void {
    this.body = body;
    this.render();
  }

  select(comment: PrCommentPayload): void {
    this.selected = comment;
    this.render();
  }

  clear(): void {
    this.selected = null;
    this.render();
  }

  private render(): void {
    if (this.body === null) return;
    this.body.replaceChildren();
    const root = document.createElement("article");
    root.className = "selected-pr-comment";
    if (this.selected === null) {
      root.textContent = "Select a comment to read it here.";
    } else {
      const author = document.createElement("strong");
      author.textContent = this.selected.author;
      const when = document.createElement("time");
      when.dateTime = this.selected.updatedAt;
      when.textContent = new Date(this.selected.updatedAt).toLocaleString();
      const text = document.createElement("p");
      text.textContent = this.selected.body;
      root.append(author, when, text);
    }
    this.body.appendChild(root);
  }
}

export class PrCommentsPanel implements PanelTool {
  readonly id = "comments";
  readonly label = "Comments";
  readonly icon = icon("comment");
  private body: HTMLElement | null = null;
  private details: PrDetailsPayload | null = null;

  constructor(
    private readonly mutations: PullRequestMutations,
    private readonly onSelected: (comment: PrCommentPayload) => void,
    private readonly onChanged: () => Promise<void>,
  ) {}

  mount(body: HTMLElement): void {
    this.body = body;
    this.render();
  }

  setDetails(details: PrDetailsPayload | null): void {
    this.details = details;
    this.render();
  }

  private render(): void {
    if (this.body === null) return;
    this.body.replaceChildren();
    if (this.details === null) {
      const hint = document.createElement("p");
      hint.className = "dock-placeholder-hint";
      hint.textContent = "Open a pull request to see its comments.";
      this.body.appendChild(hint);
      return;
    }
    const compose = formattedTextarea("pr-comment-compose", "New pull request comment");
    const send = document.createElement("button");
    send.type = "button";
    send.textContent = "Comment";
    send.addEventListener("click", () => void this.submitNew(compose.input, send));
    compose.root.appendChild(send);
    this.body.appendChild(compose.root);

    if (this.details.commentsIncomplete) {
      const warning = document.createElement("p");
      warning.className = "pr-comments-incomplete";
      warning.setAttribute("role", "status");
      warning.textContent =
        "Some comments are not shown. Open this review on GitHub for the complete history.";
      this.body.appendChild(warning);
    }

    const list = document.createElement("ol");
    list.className = "pr-comments-list";
    for (const comment of this.details.comments) list.appendChild(this.commentRow(comment));
    if (this.details.comments.length === 0) {
      const empty = document.createElement("li");
      empty.className = "pr-comments-empty";
      empty.textContent = "No conversation yet.";
      list.appendChild(empty);
    }
    this.body.appendChild(list);
  }

  private commentRow(comment: PrCommentPayload): HTMLLIElement {
    const row = document.createElement("li");
    row.className = "pr-comment-row";
    const open = document.createElement("button");
    open.type = "button";
    open.className = "pr-comment-open";
    open.addEventListener("click", () => this.onSelected(comment));
    const author = document.createElement("strong");
    author.textContent = comment.author;
    const text = document.createElement("span");
    text.textContent = comment.body;
    open.append(author);
    if (comment.path.length > 0) {
      const path = document.createElement("small");
      path.textContent = comment.path;
      open.appendChild(path);
    }
    open.appendChild(text);
    const actions = document.createElement("div");
    actions.className = "pr-comment-actions";
    const reply = document.createElement("button");
    reply.type = "button";
    reply.textContent = "Reply";
    reply.addEventListener("click", () => this.showEditor(row, comment, false));
    actions.appendChild(reply);
    if (comment.viewerDidAuthor) {
      const edit = document.createElement("button");
      edit.type = "button";
      edit.textContent = "Edit";
      edit.addEventListener("click", () => this.showEditor(row, comment, true));
      actions.appendChild(edit);
    }
    row.append(open, actions);
    return row;
  }

  private showEditor(row: HTMLElement, comment: PrCommentPayload, editing: boolean): void {
    row.querySelector(".pr-comment-inline-editor")?.remove();
    const compose = formattedTextarea(
      "pr-comment-inline-editor",
      editing ? "Edit comment" : "Reply",
    );
    compose.input.value = editing ? comment.body : "";
    const save = document.createElement("button");
    save.type = "button";
    save.textContent = editing ? "Save" : "Reply";
    save.addEventListener(
      "click",
      () => void this.submitInline(compose.input, save, comment, editing),
    );
    compose.root.appendChild(save);
    row.appendChild(compose.root);
    compose.input.focus();
  }

  private async submitNew(input: HTMLTextAreaElement, button: HTMLButtonElement): Promise<void> {
    const details = this.details;
    if (details === null || input.value.trim().length === 0) return;
    button.disabled = true;
    try {
      await this.mutations.create(details.repo, details.number, input.value);
      input.value = "";
      await this.onChanged();
    } catch {
      // The mutation callback already surfaced the author-facing GitHub error; keep the draft for retry.
    } finally {
      button.disabled = false;
    }
  }

  private async submitInline(
    input: HTMLTextAreaElement,
    button: HTMLButtonElement,
    comment: PrCommentPayload,
    editing: boolean,
  ): Promise<void> {
    const details = this.details;
    if (details === null || input.value.trim().length === 0) return;
    button.disabled = true;
    try {
      if (editing) await this.mutations.update(details.repo, details.number, comment, input.value);
      else await this.mutations.reply(details.repo, details.number, comment, input.value);
      await this.onChanged();
    } catch {
      // Keep the inline editor and its text intact after a failed save so the author can retry.
    } finally {
      button.disabled = false;
    }
  }
}

export class PullRequestView {
  readonly id = "pull-request";
  private readonly root: HTMLElement;
  private current: PrListItemPayload | null = null;
  private details: PrDetailsPayload | null = null;
  private requestGeneration = 0;

  constructor(
    host: HTMLElement,
    frame: CentralFrame,
    private readonly load: (repo: string, number: number) => Promise<PrDetailsPayload>,
    private readonly mutations: PullRequestMutations,
    private readonly comments: PrCommentsPanel,
    private readonly onContext?: (repository: string, branch: string) => void,
  ) {
    this.root = document.createElement("section");
    this.root.id = "pull-request-view";
    this.root.className = "pull-request-view";
    this.root.setAttribute("aria-label", "Pull request");
    host.appendChild(this.root);
    frame.register({ id: this.id, el: this.root });
  }

  async open(item: PrListItemPayload, frame: CentralFrame): Promise<void> {
    this.current = item;
    this.requestGeneration++;
    this.details = null;
    this.comments.setDetails(null);
    this.renderLoading(item);
    frame.show(this.id);
    activityStream.add("View", `Opened ${item.repo} review #${item.number}`);
    await this.refresh();
  }

  clear(): void {
    this.current = null;
    this.details = null;
    this.requestGeneration++;
    this.comments.setDetails(null);
    this.root.replaceChildren();
  }

  async refresh(): Promise<void> {
    if (this.current === null) return;
    const current = this.current;
    const generation = ++this.requestGeneration;
    const details = await this.load(current.repo, current.number);
    if (this.current !== current || generation !== this.requestGeneration) return;
    this.details = details;
    this.comments.setDetails(details.error === undefined ? details : null);
    if (details.error === undefined) this.onContext?.(details.repo, details.headBranch);
    this.render();
  }

  private renderLoading(item: PrListItemPayload): void {
    this.root.replaceChildren();
    const loading = document.createElement("p");
    loading.className = "pr-view-state";
    loading.textContent = `Loading ${item.repo} #${item.number}…`;
    this.root.appendChild(loading);
  }

  private render(): void {
    this.root.replaceChildren();
    const details = this.details;
    if (details === null || details.error !== undefined) {
      const error = document.createElement("p");
      error.className = "pr-view-state pr-view-state--error";
      error.textContent = details?.error ?? "Couldn't load this review.";
      this.root.appendChild(error);
      return;
    }
    const header = document.createElement("header");
    header.className = "pr-view-header";
    const eyebrow = document.createElement("p");
    eyebrow.className = "pr-view-eyebrow";
    eyebrow.textContent = `${details.repo} · #${details.number}`;
    const title = document.createElement("h1");
    title.textContent = details.title;
    const badges = document.createElement("div");
    badges.className = "pr-view-badges";
    const state = document.createElement("span");
    state.className = "status-badge";
    state.textContent = details.isDraft ? "Draft" : "Ready for review";
    const branch = document.createElement("span");
    branch.textContent = `${details.headBranch} → ${details.baseBranch}`;
    badges.append(state, branch);
    const author = document.createElement("p");
    author.className = "pr-view-author";
    author.textContent = `Opened by ${details.author}`;
    header.append(eyebrow, title, badges, author);

    const body = document.createElement("div");
    body.className = "pr-view-body";
    const description = document.createElement("section");
    const descTitle = document.createElement("h2");
    descTitle.textContent = "Description";
    const desc = document.createElement("p");
    desc.textContent = details.body || "No description provided.";
    description.append(descTitle, desc);

    const reviewers = document.createElement("section");
    const reviewersTitle = document.createElement("h2");
    reviewersTitle.textContent = "Reviewers";
    const reviewerList = document.createElement("div");
    reviewerList.className = "pr-reviewers";
    reviewerList.textContent =
      details.reviewers.map((item) => item.login).join(", ") || "No reviewers requested";
    const reviewerForm = document.createElement("form");
    reviewerForm.className = "pr-reviewer-form";
    const reviewerInput = document.createElement("input");
    reviewerInput.placeholder = "name or org/team";
    reviewerInput.setAttribute("aria-label", "Reviewer handles");
    const request = document.createElement("button");
    request.type = "submit";
    request.textContent = "Request review";
    reviewerForm.append(reviewerInput, request);
    reviewerForm.addEventListener("submit", (event) => {
      event.preventDefault();
      const handles = reviewerInput.value
        .split(/[ ,]+/)
        .map((item) => item.trim())
        .filter(Boolean);
      if (handles.length === 0) return;
      request.disabled = true;
      void this.mutations
        .reviewers(details.repo, details.number, handles)
        .then(() => this.refresh())
        .catch(() => {
          // The mutation callback already displayed the error; leave the entered handles available.
        })
        .finally(() => {
          request.disabled = false;
        });
    });
    reviewers.append(reviewersTitle, reviewerList, reviewerForm);

    const timeline = document.createElement("section");
    const timelineTitle = document.createElement("h2");
    timelineTitle.textContent = "History";
    const timelineList = document.createElement("ol");
    timelineList.className = "pr-timeline";
    const events = [
      ...details.commits.map((commit) => ({
        when: commit.when,
        title: commit.title,
        meta: `${commit.shortOid} · Checks: ${commit.checkState}`,
      })),
      ...details.comments.map((comment) => ({
        when: comment.createdAt,
        title: comment.body,
        meta: `Comment by ${comment.author}`,
      })),
    ].sort((a, b) => Date.parse(b.when) - Date.parse(a.when));
    for (const event of events) {
      const row = document.createElement("li");
      const text = document.createElement("strong");
      text.textContent = event.title;
      const meta = document.createElement("span");
      meta.textContent = `${event.meta} · ${new Date(event.when).toLocaleString()}`;
      row.append(text, meta);
      timelineList.appendChild(row);
    }
    timeline.append(timelineTitle);
    if (details.commitsIncomplete) {
      const warning = document.createElement("p");
      warning.className = "pr-comments-incomplete";
      warning.setAttribute("role", "status");
      warning.textContent =
        "Some earlier versions are not shown. Open this review on GitHub for the complete history.";
      timeline.appendChild(warning);
    }
    timeline.appendChild(timelineList);

    const changes = document.createElement("section");
    changes.className = "pr-changes-placeholder";
    const changesTitle = document.createElement("h2");
    changesTitle.textContent = "Changes";
    const changesText = document.createElement("p");
    changesText.textContent =
      "The file-by-file changes view will be added in the next review milestone.";
    changes.append(changesTitle, changesText);

    body.append(description, reviewers, timeline, changes);
    this.root.append(header, body);
  }
}
