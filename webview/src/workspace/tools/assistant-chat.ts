/**
 * The right rail's AI assistant (design concept §10.5): a docked, streaming chat. The author types a
 * message; it goes to the host (`chat.send`) and the reply streams back as `chat.delta` chunks appended
 * to a single in-progress assistant message, finalized on `chat.done`. User messages get a subtle
 * `--surface-sunken` bubble; assistant messages are quiet (no bubble fill), per §10.5.
 *
 * A template picker (the ▤ button) lists the personal and remote prompt libraries and inserts the chosen
 * prompt into the composer — the author can edit it before sending, never auto-sent. This tool keeps no
 * IPC/Kinds knowledge: the integrator (index.ts) passes {@link AssistantChatOptions.sendMessage} and
 * {@link AssistantChatOptions.requestTemplates}, and drives streaming through {@link AssistantChat.appendDelta}
 * / {@link AssistantChat.endTurn} — mirroring how ReviewsPanel / SignInController are wired.
 */

import type { PromptTemplate, TemplatesPayload } from "../../wire/protocol.js";
import { icon } from "../icons.js";
import type { PanelTool } from "../panel-tool.js";

export interface AssistantChatOptions {
  /** Send the author's message to the host (index.ts maps this to `chat.send`). */
  sendMessage(text: string): void;
  /** Fetch the personal + remote prompt library (index.ts maps this to the `templates.request` round-trip).
   *  Resolves with an empty set on any failure — the picker just shows "no templates" then. */
  requestTemplates(): Promise<TemplatesPayload>;
}

export class AssistantChat implements PanelTool {
  readonly id = "assistant";
  readonly label = "Assistant";
  readonly icon = icon("assistant");

  private readonly options: AssistantChatOptions;

  // Assigned in mount(); every method that touches the DOM runs only after mount (a user gesture or a
  // host frame, both post-mount), so the non-null assertions below are safe.
  private log!: HTMLElement;
  private srStatus!: HTMLElement;
  private input!: HTMLTextAreaElement;
  private sendButton!: HTMLButtonElement;
  private templatesButton!: HTMLButtonElement;
  private templatesPanel!: HTMLElement;

  // The assistant message currently being streamed (its text node grows with each delta), or null between
  // turns. A single streaming turn at a time (the host single-flights; the composer is disabled meanwhile).
  private streamingText: HTMLElement | null = null;
  private templatesOpen = false;

  constructor(options: AssistantChatOptions) {
    this.options = options;
  }

  mount(body: HTMLElement): void {
    const root = document.createElement("div");
    root.className = "assistant-chat";

    // The transcript. Deliberately NOT a live region: the reply streams by mutating one message node, which a
    // screen reader in a live region would announce as noisy growing prefixes ("Spec", "SpecDesk", …). The
    // finished reply is announced once through the polite status node below instead.
    this.log = document.createElement("div");
    this.log.className = "chat-log";
    this.log.setAttribute("role", "log");
    this.log.setAttribute("aria-live", "off");
    this.log.setAttribute("aria-label", "Conversation");

    // A visually-hidden polite live region that announces the completed assistant reply once (on endTurn).
    this.srStatus = document.createElement("div");
    this.srStatus.className = "chat-sr-status";
    this.srStatus.setAttribute("role", "status");

    const intro = document.createElement("p");
    intro.className = "chat-intro";
    intro.textContent =
      "Ask about this document, or insert a prompt from the library (▤). Nothing is changed without your confirmation.";
    this.log.appendChild(intro);

    // The template picker panel, populated when opened.
    this.templatesPanel = document.createElement("div");
    this.templatesPanel.className = "chat-templates";
    this.templatesPanel.id = "chat-templates-panel";
    this.templatesPanel.hidden = true;

    // The composer: a template-picker toggle, the input, and Send.
    const composer = document.createElement("form");
    composer.className = "chat-composer";

    this.templatesButton = document.createElement("button");
    this.templatesButton.type = "button";
    this.templatesButton.className = "chat-templates-toggle";
    this.templatesButton.setAttribute("aria-label", "Insert a prompt template");
    this.templatesButton.setAttribute("aria-expanded", "false");
    this.templatesButton.setAttribute("aria-controls", "chat-templates-panel");
    this.templatesButton.title = "Insert a prompt template";
    this.templatesButton.textContent = "▤";
    this.templatesButton.addEventListener("click", () => {
      void this.toggleTemplates();
    });

    this.input = document.createElement("textarea");
    this.input.className = "chat-input";
    this.input.rows = 1;
    this.input.setAttribute("aria-label", "Message the assistant");
    this.input.placeholder = "Message the assistant…";
    // Enter sends; Shift+Enter inserts a newline (a chat convention).
    this.input.addEventListener("keydown", (event) => {
      if (event.key === "Enter" && !event.shiftKey) {
        event.preventDefault();
        this.submit();
      }
    });

    this.sendButton = document.createElement("button");
    this.sendButton.type = "submit";
    this.sendButton.className = "chat-send";
    this.sendButton.textContent = "Send";

    composer.append(this.templatesButton, this.input, this.sendButton);
    composer.addEventListener("submit", (event) => {
      event.preventDefault();
      this.submit();
    });

    root.append(this.log, this.srStatus, this.templatesPanel, composer);
    body.appendChild(root);
  }

  /** Append one streamed chunk to the in-progress assistant message (creating it on the first chunk). */
  appendDelta(text: string): void {
    if (this.streamingText === null) {
      this.streamingText = this.addMessage("assistant", "");
    }
    this.streamingText.textContent = (this.streamingText.textContent ?? "") + text;
    this.scrollToEnd();
  }

  /** Finalize the current assistant turn: announce the completed reply, then re-enable the composer. */
  endTurn(): void {
    const finalText = this.streamingText?.textContent ?? "";
    // An empty reply (the turn produced no text) leaves a stray blank message — drop it; otherwise announce
    // the completed reply once to screen readers (the transcript itself is not a live region — see mount).
    if (this.streamingText !== null && finalText === "") {
      this.streamingText.closest(".chat-msg")?.remove();
    } else if (finalText !== "") {
      this.srStatus.textContent = finalText;
    }
    this.streamingText = null;
    this.setBusy(false);
    this.input.focus();
  }

  // Send the composed message: render the user bubble, open a pending assistant message, disable the
  // composer, and hand the text to the host. Ignores a blank message or one sent while already streaming.
  private submit(): void {
    if (this.streamingText !== null) {
      return;
    }
    const text = this.input.value.trim();
    if (text === "") {
      return;
    }

    this.addMessage("user", text);
    this.input.value = "";
    this.closeTemplates();
    this.setBusy(true);
    // Open the assistant's (empty) reply now so the streamed deltas have somewhere to land and the "thinking"
    // state is visible immediately.
    this.streamingText = this.addMessage("assistant", "");
    this.scrollToEnd();
    this.options.sendMessage(text);
  }

  private addMessage(role: "user" | "assistant", text: string): HTMLElement {
    const message = document.createElement("div");
    message.className = `chat-msg chat-msg--${role}`;

    const content = document.createElement("div");
    // A user message is a subtle bubble; an assistant message is quiet text (no bubble) — §10.5.
    content.className = role === "user" ? "chat-bubble" : "chat-text";
    content.textContent = text;

    message.appendChild(content);
    this.log.appendChild(message);
    this.scrollToEnd();
    return content;
  }

  private async toggleTemplates(): Promise<void> {
    if (this.templatesOpen) {
      this.closeTemplates();
      return;
    }
    this.templatesOpen = true;
    this.templatesButton.setAttribute("aria-expanded", "true");
    this.templatesPanel.hidden = false;
    this.templatesPanel.replaceChildren(loadingRow());

    const templates = await this.options.requestTemplates();
    // The author may have closed the panel while the request was in flight — don't reopen it.
    if (!this.templatesOpen) {
      return;
    }
    this.renderTemplates(templates);
  }

  private renderTemplates(templates: TemplatesPayload): void {
    this.templatesPanel.replaceChildren();
    const groups: Array<[string, PromptTemplate[]]> = [
      ["Your templates", templates.personal],
      ["Shared templates", templates.remote],
    ];
    let any = false;
    for (const [heading, items] of groups) {
      if (items.length === 0) {
        continue;
      }
      any = true;
      const group = document.createElement("div");
      group.className = "chat-templates-group";

      const title = document.createElement("div");
      title.className = "chat-templates-heading";
      title.textContent = heading;
      group.appendChild(title);

      for (const template of items) {
        const item = document.createElement("button");
        item.type = "button";
        item.className = "chat-template-btn";
        item.textContent = template.title;
        item.title = template.body;
        item.addEventListener("click", () => this.insertTemplate(template.body));
        group.appendChild(item);
      }
      this.templatesPanel.appendChild(group);
    }

    if (!any) {
      const empty = document.createElement("p");
      empty.className = "chat-templates-empty";
      empty.textContent = "No prompt templates yet.";
      this.templatesPanel.appendChild(empty);
    }
  }

  private insertTemplate(body: string): void {
    // Insert into the composer for the author to edit — never auto-sent.
    this.input.value = body;
    this.closeTemplates();
    this.input.focus();
  }

  private closeTemplates(): void {
    this.templatesOpen = false;
    this.templatesPanel.hidden = true;
    this.templatesPanel.replaceChildren();
    this.templatesButton.setAttribute("aria-expanded", "false");
  }

  private setBusy(busy: boolean): void {
    this.sendButton.disabled = busy;
    this.templatesButton.disabled = busy;
    this.input.disabled = busy;
    this.log.setAttribute("aria-busy", String(busy));
  }

  private scrollToEnd(): void {
    this.log.scrollTop = this.log.scrollHeight;
  }
}

function loadingRow(): HTMLElement {
  const loading = document.createElement("p");
  loading.className = "chat-templates-empty";
  loading.textContent = "Loading templates…";
  return loading;
}
