// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { FormatToolbar, type FormatToolbarDeps } from "../src/format-toolbar.js";
import type { FormatCommand } from "../src/md-format.js";
import type { ViewMode } from "../src/view-mode.js";

function harness(mode: ViewMode = "split") {
  document.body.innerHTML = `
    <button data-format="bold"></button>
    <button data-format="italic"></button>
  `;
  const buttons = Array.from(document.querySelectorAll<HTMLButtonElement>("button[data-format]"));
  const applyInSource = vi.fn<(c: FormatCommand) => void>();
  const applyInFormatted = vi.fn<(c: FormatCommand) => void>();
  let active = new Set<FormatCommand>();
  let m: ViewMode = mode;
  const deps: FormatToolbarDeps = {
    buttons,
    applyInSource,
    applyInFormatted,
    activeFormats: () => active,
    mode: () => m,
  };
  return {
    toolbar: new FormatToolbar(deps),
    applyInSource,
    applyInFormatted,
    setActive: (...cmds: FormatCommand[]) => {
      active = new Set(cmds);
    },
    setMode: (v: ViewMode) => {
      m = v;
    },
  };
}

function clickFormat(command: string): void {
  const btn = document.querySelector<HTMLButtonElement>(`button[data-format="${command}"]`);
  if (btn === null) {
    throw new Error(`no #${command} button`);
  }
  btn.click();
}

function pressed(command: string): string | null {
  return (
    document
      .querySelector<HTMLButtonElement>(`button[data-format="${command}"]`)
      ?.getAttribute("aria-pressed") ?? null
  );
}

describe("FormatToolbar", () => {
  it("Code mode routes a click to the source editor", () => {
    const h = harness("code");
    clickFormat("bold");
    expect(h.applyInSource).toHaveBeenCalledWith("bold");
    expect(h.applyInFormatted).not.toHaveBeenCalled();
  });

  it("Formatted mode routes a click to the formatted editor", () => {
    const h = harness("formatted");
    clickFormat("italic");
    expect(h.applyInFormatted).toHaveBeenCalledWith("italic");
    expect(h.applyInSource).not.toHaveBeenCalled();
  });

  it("Split routes to the source editor by default, then to whichever pane last had focus", () => {
    const h = harness("split");
    clickFormat("bold");
    expect(h.applyInSource).toHaveBeenCalledWith("bold"); // default lastFocused = editor
    h.toolbar.setFocused("formatted");
    clickFormat("bold");
    expect(h.applyInFormatted).toHaveBeenCalledWith("bold");
  });

  it("refresh marks the formatted pane's active formats pressed", () => {
    const h = harness("formatted");
    h.setActive("bold");
    h.toolbar.refresh();
    expect(pressed("bold")).toBe("true");
    expect(pressed("italic")).toBe("false");
  });

  it("refresh shows nothing pressed when the target is the source editor", () => {
    const h = harness("code");
    h.setActive("bold"); // active in the formatted pane, but the source is the target → not shown
    h.toolbar.refresh();
    expect(pressed("bold")).toBe("false");
  });

  it("running a command refreshes the pressed state", () => {
    const h = harness("formatted");
    h.setActive("bold"); // a bold toggle would become active
    clickFormat("bold");
    expect(pressed("bold")).toBe("true");
  });

  it("prevents mousedown so a click never steals editor focus", () => {
    harness("split");
    const btn = document.querySelector<HTMLButtonElement>('button[data-format="bold"]');
    const event = new MouseEvent("mousedown", { cancelable: true, bubbles: true });
    btn?.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(true);
  });
});
