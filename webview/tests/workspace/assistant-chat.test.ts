// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import type { ChatAttachment, TemplatesPayload } from "../../src/wire/protocol.js";
import { AssistantChat } from "../../src/workspace/tools/assistant-chat.js";

const flush = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 0));

function harness(templates: TemplatesPayload = { personal: [], remote: [] }) {
  const sendMessage = vi.fn<(text: string, attachments: readonly ChatAttachment[]) => void>();
  const requestTemplates = vi.fn<() => Promise<TemplatesPayload>>().mockResolvedValue(templates);
  const pickAttachment = vi.fn<(kind: "file" | "folder") => Promise<ChatAttachment | null>>(
    async (kind) =>
      kind === "file"
        ? { kind, label: "roadmap.md", reference: "C:\\specs\\roadmap.md" }
        : { kind, label: "specs", reference: "C:\\specs" },
  );
  const chat = new AssistantChat({ sendMessage, requestTemplates, pickAttachment });
  const body = document.createElement("div");
  document.body.appendChild(body);
  chat.mount(body);

  const input = body.querySelector<HTMLTextAreaElement>(".chat-input");
  const sendBtn = body.querySelector<HTMLButtonElement>(".chat-send");
  const templatesBtn = body.querySelector<HTMLButtonElement>(".chat-templates-toggle");
  const messages = () => Array.from(body.querySelectorAll<HTMLElement>(".chat-msg"));
  if (!input || !sendBtn || !templatesBtn) {
    throw new Error("chat did not mount its composer");
  }
  return {
    chat,
    body,
    sendMessage,
    requestTemplates,
    pickAttachment,
    input,
    sendBtn,
    templatesBtn,
    messages,
  };
}

describe("AssistantChat", () => {
  it("mounts a transcript and a composer", () => {
    const { body, input, sendBtn } = harness();
    expect(body.querySelector(".chat-log")).not.toBeNull();
    expect(body.querySelector(".chat-composer")).not.toBeNull();
    expect(input.rows).toBe(3);
    expect(input.getAttribute("aria-describedby")).toBe("chat-input-hint");
    expect(body.querySelector("#chat-input-hint")?.textContent).toBe("Ctrl/Cmd+Enter to send");
    expect(sendBtn.getAttribute("aria-keyshortcuts")).toContain("Control+Enter");
  });

  it("sends the composed message, shows a user bubble, opens a pending reply, and disables the composer", () => {
    const { input, sendBtn, sendMessage, messages, body } = harness();
    input.value = "Summarize the changes";
    sendBtn.click();

    expect(sendMessage).toHaveBeenCalledWith("Summarize the changes", []);
    expect(input.value).toBe(""); // cleared
    expect(sendBtn.disabled).toBe(true); // busy until chat.done

    const user = body.querySelector(".chat-msg--user .chat-bubble");
    expect(user?.textContent).toBe("Summarize the changes");
    // A pending (empty) assistant message was opened for the stream.
    expect(messages()).toHaveLength(2);
    expect(body.querySelector(".chat-msg--assistant .chat-text")).not.toBeNull();
  });

  it("does not send a blank message", () => {
    const { input, sendBtn, sendMessage, messages } = harness();
    input.value = "   ";
    sendBtn.click();
    expect(sendMessage).not.toHaveBeenCalled();
    expect(messages()).toHaveLength(0);
  });

  it("plain Enter remains available for new lines; Ctrl/Cmd+Enter sends", () => {
    const { input, sendMessage } = harness();
    input.value = "hi";
    const plainEnter = new KeyboardEvent("keydown", {
      key: "Enter",
      bubbles: true,
      cancelable: true,
    });
    input.dispatchEvent(plainEnter);
    expect(sendMessage).not.toHaveBeenCalled();
    expect(plainEnter.defaultPrevented).toBe(false);

    input.dispatchEvent(
      new KeyboardEvent("keydown", { key: "Enter", ctrlKey: true, bubbles: true }),
    );
    expect(sendMessage).toHaveBeenCalledWith("hi", []);

    const second = harness();
    second.input.value = "hello";
    second.input.dispatchEvent(
      new KeyboardEvent("keydown", { key: "Enter", metaKey: true, bubbles: true }),
    );
    expect(second.sendMessage).toHaveBeenCalledWith("hello", []);
  });

  it("appends streamed deltas to the pending assistant message and re-enables on endTurn", () => {
    const { input, sendBtn, chat, body } = harness();
    input.value = "question";
    sendBtn.click();

    chat.appendDelta("Hello ");
    chat.appendDelta("world");
    expect(body.querySelector(".chat-msg--assistant .chat-text")?.textContent).toBe("Hello world");

    chat.endTurn();
    expect(sendBtn.disabled).toBe(false);
    // The finished reply is announced once through the off-screen polite status region, not by mutating
    // a live transcript delta-by-delta (which screen readers announce as noisy growing prefixes).
    expect(body.querySelector<HTMLElement>(".chat-log")?.getAttribute("aria-live")).toBe("off");
    expect(body.querySelector<HTMLElement>(".chat-sr-status")?.textContent).toBe("Hello world");
  });

  it("drops the empty assistant message when a turn ends with no output", () => {
    const { input, sendBtn, chat, messages } = harness();
    input.value = "question";
    sendBtn.click();
    expect(messages()).toHaveLength(2); // user + empty pending assistant

    chat.endTurn(); // no deltas arrived
    expect(messages()).toHaveLength(1); // the blank assistant message is removed
  });

  it("opens the template picker and inserts the chosen prompt into the composer (never auto-sends)", async () => {
    const { templatesBtn, input, sendMessage, requestTemplates, body } = harness({
      personal: [{ id: "p1", title: "Summarize", body: "Summarize the changes." }],
      remote: [{ id: "r1", title: "Style", body: "Apply the style guide." }],
    });

    templatesBtn.click();
    await flush();
    expect(requestTemplates).toHaveBeenCalledOnce();

    const items = Array.from(body.querySelectorAll<HTMLButtonElement>(".chat-template-btn"));
    expect(items.map((i) => i.textContent)).toEqual(["Summarize", "Style"]);

    // Choosing a template inserts its body into the composer and closes the picker — it does not send.
    items[1]?.click();
    expect(input.value).toBe("Apply the style guide.");
    expect(sendMessage).not.toHaveBeenCalled();
    expect(body.querySelector<HTMLElement>(".chat-templates")?.hidden).toBe(true);
  });

  it("shows an empty-state when there are no templates", async () => {
    const { templatesBtn, body } = harness({ personal: [], remote: [] });
    templatesBtn.click();
    await flush();
    expect(body.querySelector(".chat-templates-empty")?.textContent).toContain(
      "No prompt templates",
    );
  });

  it("picks a file and folder without opening them, exposes repositories, and sends structured state", async () => {
    const { chat, body, input, sendBtn, sendMessage } = harness();
    chat.setRepositories([
      { id: "octo/specs", name: "octo/specs", url: "https://github.com/octo/specs" },
    ]);

    const attach = body.querySelector<HTMLButtonElement>(".chat-attach-toggle");
    attach?.click();
    const menuItems = Array.from(body.querySelectorAll<HTMLButtonElement>(".chat-attach-item"));
    expect(menuItems.map((item) => item.textContent)).toEqual(["Files", "Folders", "octo/specs"]);
    menuItems[0]?.click();
    await flush();

    attach?.click();
    body.querySelectorAll<HTMLButtonElement>(".chat-attach-item")[1]?.click();
    await flush();
    expect(
      Array.from(body.querySelectorAll(".chat-attachment"), (item) => item.textContent),
    ).toEqual(["roadmap.md×", "specs×"]);

    input.value = "Compare these";
    sendBtn.click();
    expect(sendMessage).toHaveBeenCalledWith("Compare these", [
      { kind: "file", label: "roadmap.md", reference: "C:\\specs\\roadmap.md" },
      { kind: "folder", label: "specs", reference: "C:\\specs" },
    ]);
    expect(body.querySelector(".chat-attachments")?.hasAttribute("hidden")).toBe(true);
  });

  it("shows an honest repository empty state and removes an attached chip accessibly", async () => {
    const { body } = harness();
    const attach = body.querySelector<HTMLButtonElement>(".chat-attach-toggle");
    attach?.click();
    expect(body.querySelector(".chat-templates-empty")?.textContent).toContain("No registered");

    body.querySelector<HTMLButtonElement>(".chat-attach-item")?.click();
    await flush();
    const remove = body.querySelector<HTMLButtonElement>(".chat-attachment-remove");
    expect(remove?.getAttribute("aria-label")).toBe("Remove roadmap.md");
    remove?.click();
    expect(body.querySelectorAll(".chat-attachment")).toHaveLength(0);
  });

  it("supports Arrow/Home/End navigation and closes on Escape", () => {
    const { chat, body } = harness();
    chat.setRepositories([
      { id: "octo/specs", name: "octo/specs", url: "https://github.com/octo/specs" },
    ]);
    const attach = body.querySelector<HTMLButtonElement>(".chat-attach-toggle");
    attach?.click();
    const fileItem = body.querySelector<HTMLButtonElement>(".chat-attach-item");
    expect(document.activeElement).toBe(fileItem);
    const menu = body.querySelector<HTMLElement>(".chat-attach-menu");
    menu?.dispatchEvent(new KeyboardEvent("keydown", { key: "End", bubbles: true }));
    expect(document.activeElement?.textContent).toBe("octo/specs");
    menu?.dispatchEvent(new KeyboardEvent("keydown", { key: "Home", bubbles: true }));
    expect(document.activeElement).toBe(fileItem);
    menu?.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true }));
    expect(document.activeElement?.textContent).toBe("Folders");
    menu?.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    expect(menu?.hidden).toBe(true);
    expect(document.activeElement).toBe(attach);
  });
});
