// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { LifecycleChrome, type LifecycleChromeDeps } from "../src/lifecycle-chrome.js";

function harness() {
  document.body.innerHTML = `
    <button id="open"></button>
    <button id="edit"></button>
    <button id="save-version"></button>
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
    onDiscard: vi.fn(),
    onSave: vi.fn(),
  };
  const deps: LifecycleChromeDeps = {
    openBtn: byId("open"),
    editBtn: byId("edit"),
    saveVersionBtn: byId("save-version"),
    discardBtn: byId("discard"),
    saveBtn: byId("save"),
    formatBar: document.querySelector<HTMLElement>("#format-bar"),
    setPaneEditable,
    ...callbacks,
  };
  return { chrome: new LifecycleChrome(deps), deps, setPaneEditable, callbacks };
}

const hidden = (id: string) => document.querySelector<HTMLElement>(`#${id}`)?.hidden;

describe("LifecycleChrome.setEditing", () => {
  it("while editing: panes editable, format bar + draft actions shown, Edit hidden", () => {
    const { chrome, setPaneEditable } = harness();
    chrome.setEditing(true);
    expect(setPaneEditable).toHaveBeenLastCalledWith(true);
    expect(hidden("format-bar")).toBe(false);
    expect(hidden("edit")).toBe(true);
    expect(hidden("save-version")).toBe(false);
    expect(hidden("discard")).toBe(false);
  });

  it("while read-only: panes locked, format bar + draft actions hidden, Edit shown", () => {
    const { chrome, setPaneEditable } = harness();
    chrome.setEditing(false);
    expect(setPaneEditable).toHaveBeenLastCalledWith(false);
    expect(hidden("format-bar")).toBe(true);
    expect(hidden("edit")).toBe(false);
    expect(hidden("save-version")).toBe(true);
    expect(hidden("discard")).toBe(true);
  });
});

describe("LifecycleChrome button wiring", () => {
  it("routes each action button click to its callback", () => {
    const { callbacks } = harness();
    document.querySelector<HTMLButtonElement>("#open")?.click();
    document.querySelector<HTMLButtonElement>("#edit")?.click();
    document.querySelector<HTMLButtonElement>("#save-version")?.click();
    document.querySelector<HTMLButtonElement>("#discard")?.click();
    document.querySelector<HTMLButtonElement>("#save")?.click();
    expect(callbacks.onOpen).toHaveBeenCalledTimes(1);
    expect(callbacks.onEdit).toHaveBeenCalledTimes(1);
    expect(callbacks.onSaveVersion).toHaveBeenCalledTimes(1);
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
      discardBtn: null,
      saveBtn: null,
      formatBar: null,
      setPaneEditable,
      onOpen: vi.fn(),
      onEdit: vi.fn(),
      onSaveVersion: vi.fn(),
      onDiscard: vi.fn(),
      onSave: vi.fn(),
    });
    expect(() => chrome.setEditing(true)).not.toThrow();
    expect(setPaneEditable).toHaveBeenCalledWith(true);
  });
});
