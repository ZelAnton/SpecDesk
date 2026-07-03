// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { LifecycleChrome, type LifecycleChromeDeps } from "../src/lifecycle-chrome.js";
import type { StatusState } from "../src/protocol.js";

function harness() {
  document.body.innerHTML = `
    <button id="open"></button>
    <button id="edit"></button>
    <button id="save-version"></button>
    <button id="send-for-review"></button>
    <button id="update-review"></button>
    <button id="discard"></button>
    <button id="save"></button>
    <div id="format-bar"></div>
  `;
  const byId = (id: string) => document.querySelector<HTMLButtonElement>(`#${id}`);
  const setPaneEditable = vi.fn<(editable: boolean) => void>();
  const callbacks = {
    onOpen: vi.fn(),
    onEdit: vi.fn(),
    onSaveVersion: vi.fn(),
    onSendForReview: vi.fn(),
    onUpdateReview: vi.fn(),
    onDiscard: vi.fn(),
    onSave: vi.fn(),
  };
  const deps: LifecycleChromeDeps = {
    openBtn: byId("open"),
    editBtn: byId("edit"),
    saveVersionBtn: byId("save-version"),
    sendForReviewBtn: byId("send-for-review"),
    updateReviewBtn: byId("update-review"),
    discardBtn: byId("discard"),
    saveBtn: byId("save"),
    formatBar: document.querySelector<HTMLElement>("#format-bar"),
    setPaneEditable,
    ...callbacks,
  };
  return { chrome: new LifecycleChrome(deps), deps, setPaneEditable, callbacks };
}

const hidden = (id: string) => document.querySelector<HTMLElement>(`#${id}`)?.hidden;

describe("LifecycleChrome.setLifecycle", () => {
  it("draft (GitHub available): panes editable, draft actions shown, Edit + Update review hidden", () => {
    const { chrome, setPaneEditable } = harness();
    chrome.setGitHubAvailable(true);
    chrome.setLifecycle("draft");
    expect(setPaneEditable).toHaveBeenLastCalledWith(true);
    expect(hidden("format-bar")).toBe(false);
    expect(hidden("edit")).toBe(true);
    expect(hidden("save-version")).toBe(false);
    expect(hidden("discard")).toBe(false);
    expect(hidden("send-for-review")).toBe(false);
    expect(hidden("update-review")).toBe(true);
  });

  it("published: panes locked, draft actions + both review buttons hidden, Edit shown", () => {
    const { chrome, setPaneEditable } = harness();
    chrome.setGitHubAvailable(true);
    chrome.setLifecycle("published");
    expect(setPaneEditable).toHaveBeenLastCalledWith(false);
    expect(hidden("format-bar")).toBe(true);
    expect(hidden("edit")).toBe(false);
    expect(hidden("save-version")).toBe(true);
    expect(hidden("discard")).toBe(true);
    expect(hidden("send-for-review")).toBe(true);
    expect(hidden("update-review")).toBe(true);
  });

  // Once In review (and the other post-draft states) the document is still editable and can take more
  // versions; Discard and Send for review are no longer legal moves (both stay hidden), and Update review
  // takes over — pushing the newly-saved versions to the open PR.
  it.each<StatusState>([
    "inReview",
    "changesRequested",
    "approved",
  ])("%s: editable, Save version + Update review shown, but Edit/Discard/Send for review hidden", (state) => {
    const { chrome, setPaneEditable } = harness();
    chrome.setGitHubAvailable(true);
    chrome.setLifecycle(state);
    expect(setPaneEditable).toHaveBeenLastCalledWith(true);
    expect(hidden("edit")).toBe(true);
    expect(hidden("save-version")).toBe(false);
    expect(hidden("discard")).toBe(true);
    expect(hidden("send-for-review")).toBe(true);
    expect(hidden("update-review")).toBe(false);
  });
});

describe("LifecycleChrome Send for review availability", () => {
  it("stays hidden in draft when GitHub is unavailable", () => {
    const { chrome } = harness();
    chrome.setGitHubAvailable(false);
    chrome.setLifecycle("draft");
    expect(hidden("send-for-review")).toBe(true);
  });

  it("appears/disappears when availability toggles while drafting", () => {
    const { chrome } = harness();
    chrome.setLifecycle("draft");
    expect(hidden("send-for-review")).toBe(true); // default: unavailable
    chrome.setGitHubAvailable(true);
    expect(hidden("send-for-review")).toBe(false);
    chrome.setGitHubAvailable(false);
    expect(hidden("send-for-review")).toBe(true);
  });
});

describe("LifecycleChrome Update review availability", () => {
  it("stays hidden while in review when GitHub is unavailable", () => {
    const { chrome } = harness();
    chrome.setGitHubAvailable(false);
    chrome.setLifecycle("inReview");
    expect(hidden("update-review")).toBe(true);
  });

  it("appears/disappears when availability toggles while in review", () => {
    const { chrome } = harness();
    chrome.setLifecycle("inReview");
    expect(hidden("update-review")).toBe(true); // default: unavailable
    chrome.setGitHubAvailable(true);
    expect(hidden("update-review")).toBe(false);
    chrome.setGitHubAvailable(false);
    expect(hidden("update-review")).toBe(true);
  });
});

describe("LifecycleChrome button wiring", () => {
  it("routes each action button click to its callback", () => {
    const { callbacks } = harness();
    document.querySelector<HTMLButtonElement>("#open")?.click();
    document.querySelector<HTMLButtonElement>("#edit")?.click();
    document.querySelector<HTMLButtonElement>("#save-version")?.click();
    document.querySelector<HTMLButtonElement>("#send-for-review")?.click();
    document.querySelector<HTMLButtonElement>("#update-review")?.click();
    document.querySelector<HTMLButtonElement>("#discard")?.click();
    document.querySelector<HTMLButtonElement>("#save")?.click();
    expect(callbacks.onOpen).toHaveBeenCalledTimes(1);
    expect(callbacks.onEdit).toHaveBeenCalledTimes(1);
    expect(callbacks.onSaveVersion).toHaveBeenCalledTimes(1);
    expect(callbacks.onSendForReview).toHaveBeenCalledTimes(1);
    expect(callbacks.onUpdateReview).toHaveBeenCalledTimes(1);
    expect(callbacks.onDiscard).toHaveBeenCalledTimes(1);
    expect(callbacks.onSave).toHaveBeenCalledTimes(1);
  });
});

describe("LifecycleChrome with absent elements", () => {
  it("does not throw when the buttons and format bar are missing", () => {
    const setPaneEditable = vi.fn<(editable: boolean) => void>();
    const chrome = new LifecycleChrome({
      openBtn: null,
      editBtn: null,
      saveVersionBtn: null,
      sendForReviewBtn: null,
      updateReviewBtn: null,
      discardBtn: null,
      saveBtn: null,
      formatBar: null,
      setPaneEditable,
      onOpen: vi.fn(),
      onEdit: vi.fn(),
      onSaveVersion: vi.fn(),
      onSendForReview: vi.fn(),
      onUpdateReview: vi.fn(),
      onDiscard: vi.fn(),
      onSave: vi.fn(),
    });
    expect(() => chrome.setLifecycle("draft")).not.toThrow();
    expect(() => chrome.setGitHubAvailable(true)).not.toThrow();
    expect(setPaneEditable).toHaveBeenCalledWith(true);
  });
});
