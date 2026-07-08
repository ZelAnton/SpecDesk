// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { FormatToolbar, type FormatToolbarDeps } from "../../src/chrome/format-toolbar.js";
import type { ViewMode } from "../../src/chrome/view-mode.js";
import type { FormatCommand } from "../../src/editors/md-format.js";

function harness(mode: ViewMode = "split") {
  document.body.innerHTML = `
    <button data-format="bold"></button>
    <button data-format="italic"></button>
  `;
  const buttons = Array.from(document.querySelectorAll<HTMLButtonElement>("button[data-format]"));
  const applyInSource = vi.fn<(c: FormatCommand) => void>();
  const applyInFormatted = vi.fn<(c: FormatCommand) => void>();
  let activeSource = new Set<FormatCommand>();
  let activeFormatted = new Set<FormatCommand>();
  let disabledFormatted = new Set<FormatCommand>();
  let m: ViewMode = mode;
  const deps: FormatToolbarDeps = {
    buttons,
    applyInSource,
    applyInFormatted,
    activeInSource: () => activeSource,
    activeInFormatted: () => activeFormatted,
    disabledInFormatted: () => disabledFormatted,
    mode: () => m,
  };
  return {
    toolbar: new FormatToolbar(deps),
    applyInSource,
    applyInFormatted,
    setActiveSource: (...cmds: FormatCommand[]) => {
      activeSource = new Set(cmds);
    },
    setActiveFormatted: (...cmds: FormatCommand[]) => {
      activeFormatted = new Set(cmds);
    },
    setDisabledFormatted: (...cmds: FormatCommand[]) => {
      disabledFormatted = new Set(cmds);
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

function disabled(command: string): boolean | undefined {
  return document.querySelector<HTMLButtonElement>(`button[data-format="${command}"]`)?.disabled;
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
    h.setActiveFormatted("bold");
    h.toolbar.refresh();
    expect(pressed("bold")).toBe("true");
    expect(pressed("italic")).toBe("false");
  });

  it("refresh reads the source pane's active formats when the target is the source editor", () => {
    const h = harness("code");
    h.setActiveFormatted("bold"); // active in the OTHER (formatted) pane — must not leak here
    h.setActiveSource("italic");
    h.toolbar.refresh();
    expect(pressed("bold")).toBe("false");
    expect(pressed("italic")).toBe("true");
  });

  it("running a command refreshes the pressed state", () => {
    const h = harness("formatted");
    h.setActiveFormatted("bold"); // a bold toggle would become active
    clickFormat("bold");
    expect(pressed("bold")).toBe("true");
  });

  it("refresh disables the formatted pane's inapplicable commands", () => {
    const h = harness("formatted");
    h.setDisabledFormatted("bold");
    h.toolbar.refresh();
    expect(disabled("bold")).toBe(true);
    expect(disabled("italic")).toBe(false);
  });

  it("the source target is never disabled, even if the formatted pane reports it inapplicable", () => {
    const h = harness("code");
    h.setDisabledFormatted("bold");
    h.toolbar.refresh();
    expect(disabled("bold")).toBe(false);
  });

  it("a disabled command's click is a no-op (native disabled blocks the click event)", () => {
    const h = harness("formatted");
    h.setDisabledFormatted("bold");
    h.toolbar.refresh();
    clickFormat("bold");
    expect(h.applyInFormatted).not.toHaveBeenCalled();
  });

  it("prevents mousedown so a click never steals editor focus", () => {
    harness("split");
    const btn = document.querySelector<HTMLButtonElement>('button[data-format="bold"]');
    const event = new MouseEvent("mousedown", { cancelable: true, bubbles: true });
    btn?.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(true);
  });
});
