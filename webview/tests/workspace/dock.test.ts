// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { Dock } from "../../src/workspace/dock.js";
import { DOCK_SIZE_BOUNDS, type DockEdge, type DockState } from "../../src/workspace/dock-state.js";
import type { PanelTool } from "../../src/workspace/panel-tool.js";

function tool(id: string, label: string): PanelTool {
  return {
    id,
    label,
    icon: `<svg data-icon="${id}"></svg>`,
    mount(body: HTMLElement): void {
      const content = document.createElement("div");
      content.className = `content-${id}`;
      content.textContent = id;
      body.appendChild(content);
    },
  };
}

interface HarnessOptions {
  edge?: DockEdge;
  tools?: PanelTool[];
  initial?: DockState;
}

function harness(options: HarnessOptions = {}) {
  document.body.innerHTML = `<div id="host"><div id="dock"></div></div>`;
  const dockEl = document.querySelector<HTMLElement>("#dock");
  if (dockEl === null) {
    throw new Error("no #dock");
  }
  const onChange = vi.fn();
  const tools = options.tools ?? [tool("a", "Alpha"), tool("b", "Bravo")];
  const initial = options.initial ?? { open: false, size: 260, mode: "a" };
  const dock = new Dock(dockEl, options.edge ?? "left", tools, initial, { onChange });
  const splitter = document.querySelector<HTMLElement>(".dock-splitter");
  if (splitter === null) {
    throw new Error("no splitter");
  }
  return { dockEl, onChange, dock, splitter };
}

function pointer(
  type: string,
  props: { clientX?: number; clientY?: number; button?: number; pointerId?: number },
): Event {
  const event = new Event(type, { bubbles: true, cancelable: true });
  Object.assign(event, { clientX: 0, clientY: 0, button: 0, pointerId: 1, ...props });
  return event;
}

function key(el: HTMLElement, k: string): void {
  el.dispatchEvent(new KeyboardEvent("keydown", { key: k, bubbles: true, cancelable: true }));
}

describe("Dock chrome", () => {
  it("builds an icon rail per tool, a header title, and a body container per tool", () => {
    const { dockEl } = harness();
    expect(dockEl.querySelector(".dock-mode-list")?.getAttribute("aria-orientation")).toBe(
      "vertical",
    );
    const buttons = dockEl.querySelectorAll<HTMLButtonElement>(".dock-rail-btn");
    expect(buttons).toHaveLength(2);
    // Icon-only buttons: the label is the accessible name (aria-label), and each renders its icon svg.
    expect(Array.from(buttons).map((b) => b.getAttribute("aria-label"))).toEqual([
      "Alpha",
      "Bravo",
    ]);
    expect(buttons[0]?.querySelector("svg")).not.toBeNull();
    // The header title shows the ACTIVE mode's label.
    expect(dockEl.querySelector(".dock-title")?.textContent).toBe("Alpha");
    expect(dockEl.querySelector(".dock-collapse")).not.toBeNull();
    expect(dockEl.querySelectorAll(".dock-tool")).toHaveLength(2);
    expect(dockEl.querySelector(".content-a")).not.toBeNull();
  });

  it("inserts the splitter as a sibling on the centre side of the dock", () => {
    const left = harness({ edge: "left" });
    // Left rail: splitter directly after the dock.
    expect(left.dockEl.nextElementSibling).toBe(left.splitter);
    expect(left.splitter.classList.contains("dock-splitter-left")).toBe(true);

    const right = harness({ edge: "right" });
    // Right rail: splitter directly before the dock.
    expect(right.dockEl.previousElementSibling).toBe(right.splitter);
    expect(right.splitter.getAttribute("aria-orientation")).toBe("vertical");

    const bottom = harness({ edge: "bottom" });
    expect(bottom.splitter.getAttribute("aria-orientation")).toBe("horizontal");
  });

  it("renders a single-tool dock with a rail so its panel can be collapsed and reopened", () => {
    const { dockEl } = harness({
      tools: [tool("only", "Solo")],
      initial: { open: true, size: 260, mode: "only" },
    });
    expect(dockEl.querySelectorAll(".dock-rail-btn")).toHaveLength(1);
    expect(dockEl.querySelector(".dock-title")?.textContent).toBe("Solo");
    expect(dockEl.hidden).toBe(false);
  });

  it("can hide a rail-less dock completely until an external control opens it", () => {
    document.body.innerHTML = `<div id="host"><div id="dock"></div></div>`;
    const dockEl = document.querySelector<HTMLElement>("#dock");
    if (dockEl === null) throw new Error("no #dock");
    const focusAfterClose = vi.fn();
    const dock = new Dock(
      dockEl,
      "bottom",
      [tool("log", "Log")],
      { open: false, size: 200, mode: "log" },
      { onChange: vi.fn() },
      { showRail: false, hideWhenClosed: true, focusAfterClose },
    );
    expect(dockEl.hidden).toBe(true);
    expect(dockEl.querySelector(".dock-rail")).toBeNull();
    dock.setOpen(true);
    expect(dockEl.hidden).toBe(false);
    dockEl.querySelector<HTMLButtonElement>(".dock-collapse")?.focus();
    dock.setOpen(false);
    expect(dockEl.hidden).toBe(true);
    expect(focusAfterClose).toHaveBeenCalledOnce();
  });

  it("adds a separate toggle action after the mode radiogroup", () => {
    const { dockEl, dock } = harness({ edge: "right" });
    const activate = vi.fn();
    const action = dock.addRailAction("bottom-panel", "Bottom panel", "<svg></svg>", activate);
    expect(action).not.toBeNull();
    expect(dockEl.querySelector(".dock-mode-list")?.getAttribute("role")).toBe("radiogroup");
    expect(action?.parentElement?.classList.contains("dock-rail")).toBe(true);
    expect(action?.getAttribute("role")).toBeNull();
    action?.click();
    expect(activate).toHaveBeenCalledOnce();
  });
});

describe("Dock open/collapse", () => {
  it("notifies the active tool when expansion or a mode switch changes its visibility", () => {
    const a = { ...tool("a", "Alpha"), onShow: vi.fn(), onHide: vi.fn() };
    const b = { ...tool("b", "Bravo"), onShow: vi.fn(), onHide: vi.fn() };
    const { dock } = harness({ tools: [a, b] });
    dock.setOpen(true);
    expect(a.onShow).toHaveBeenCalledTimes(1);
    dock.setMode("b");
    expect(a.onHide).toHaveBeenCalledTimes(1);
    expect(b.onShow).toHaveBeenCalledTimes(1);
    dock.setOpen(false);
    expect(b.onHide).toHaveBeenCalledTimes(1);
  });

  it("applies the initial collapsed state as a visible rail with a hidden splitter", () => {
    const { dockEl, splitter } = harness({
      initial: { open: false, size: 260, mode: "a" },
    });
    expect(dockEl.hidden).toBe(false);
    expect(dockEl.classList.contains("dock--collapsed")).toBe(true);
    expect(splitter.hidden).toBe(true);
    expect(dockEl.querySelector('[data-tool="a"]')?.getAttribute("aria-expanded")).toBe("false");
  });

  it("applies the initial open state and clamps the size", () => {
    const { dockEl, splitter } = harness({
      edge: "left",
      initial: { open: true, size: 99999, mode: "a" },
    });
    expect(dockEl.hidden).toBe(false);
    expect(splitter.hidden).toBe(false);
    expect(dockEl.querySelector('[data-tool="a"]')?.getAttribute("aria-expanded")).toBe("true");
    expect(dockEl.style.width).toBe(`${DOCK_SIZE_BOUNDS.left.max}px`);
  });

  it("toggle opens then collapses, persisting each change and updating the rail + splitter", () => {
    const { dockEl, onChange, splitter, dock } = harness();
    dock.toggle();
    expect(dock.open).toBe(true);
    expect(dockEl.hidden).toBe(false);
    expect(splitter.hidden).toBe(false);
    expect(dockEl.querySelector('[data-tool="a"]')?.getAttribute("aria-expanded")).toBe("true");
    dock.toggle();
    expect(dock.open).toBe(false);
    expect(dockEl.classList.contains("dock--collapsed")).toBe(true);
    expect(splitter.hidden).toBe(true);
    expect(onChange).toHaveBeenCalledTimes(2);
  });

  it("the active rail icon and the in-panel collapse button drive open/close", () => {
    const { dockEl, dock } = harness();
    const active = dockEl.querySelector<HTMLButtonElement>('[data-tool="a"]');
    active?.click();
    expect(dock.open).toBe(true);
    const collapse = dockEl.querySelector<HTMLButtonElement>(".dock-collapse");
    collapse?.click();
    expect(dock.open).toBe(false);
    expect(dockEl.classList.contains("dock--collapsed")).toBe(true);
  });

  it("a tool-less dock cannot be opened", () => {
    const { dock, onChange } = harness({ tools: [], initial: { open: true, size: 260, mode: "" } });
    expect(dock.open).toBe(false);
    dock.setOpen(true);
    expect(dock.open).toBe(false);
    expect(onChange).not.toHaveBeenCalled();
  });
});

describe("Dock mode switching", () => {
  it("clicking a mode shows that tool, updates the header title, and persists", () => {
    const { dockEl, onChange } = harness();
    const [aBody, bBody] = Array.from(dockEl.querySelectorAll<HTMLElement>(".dock-tool"));
    expect(aBody?.hidden).toBe(false);
    expect(bBody?.hidden).toBe(true);

    const bButton = dockEl.querySelectorAll<HTMLButtonElement>(".dock-rail-btn")[1];
    bButton?.click();

    expect(aBody?.hidden).toBe(true);
    expect(bBody?.hidden).toBe(false);
    expect(bButton?.getAttribute("aria-checked")).toBe("true");
    expect(dockEl.querySelector(".dock-title")?.textContent).toBe("Bravo");
    expect(onChange).toHaveBeenCalledTimes(1);
  });

  it("clicking the active mode toggles the panel, while an inactive mode selects and opens it", () => {
    const { dockEl, dock, onChange } = harness({
      initial: { open: true, size: 260, mode: "a" },
    });
    const [aButton, bButton] = dockEl.querySelectorAll<HTMLButtonElement>(".dock-rail-btn");
    aButton?.click();
    expect(dock.open).toBe(false);
    expect(aButton?.getAttribute("aria-expanded")).toBe("false");
    bButton?.click();
    expect(dock.open).toBe(true);
    expect(bButton?.getAttribute("aria-checked")).toBe("true");
    expect(bButton?.getAttribute("aria-expanded")).toBe("true");
    expect(onChange).toHaveBeenCalledTimes(2);
  });

  it("keeps aria-expanded on the current mode when a caller reveals another tool", () => {
    const { dockEl, dock } = harness({
      initial: { open: true, size: 260, mode: "a" },
    });
    dock.setMode("b");
    const [aButton, bButton] = dockEl.querySelectorAll<HTMLButtonElement>(".dock-rail-btn");
    expect(aButton?.getAttribute("aria-expanded")).toBe("false");
    expect(bButton?.getAttribute("aria-expanded")).toBe("true");
  });

  it("setMode ignores an unknown id without persisting and keeps the rail in sync", () => {
    const { dockEl, onChange, dock } = harness();
    dock.setMode("nope");
    const [aBody] = Array.from(dockEl.querySelectorAll<HTMLElement>(".dock-tool"));
    expect(aBody?.hidden).toBe(false);
    const aButton = dockEl.querySelector<HTMLButtonElement>(".dock-rail-btn");
    expect(aButton?.getAttribute("aria-checked")).toBe("true");
    expect(dockEl.querySelector(".dock-title")?.textContent).toBe("Alpha");
    expect(onChange).not.toHaveBeenCalled();
  });

  it("hides context-inapplicable modes, falls back, and restores the preferred mode", () => {
    const { dockEl, onChange, dock } = harness({
      tools: [
        tool("assistant", "Assistant"),
        tool("comments", "Comments"),
        tool("history", "History"),
      ],
      initial: { open: true, size: 320, mode: "comments" },
    });
    dock.setAvailableTools(new Set(["assistant", "history"]));
    const buttons = Array.from(dockEl.querySelectorAll<HTMLButtonElement>(".dock-rail-btn"));
    expect(buttons.map((button) => [button.getAttribute("aria-label"), button.hidden])).toEqual([
      ["Assistant", false],
      ["Comments", true],
      ["History", false],
    ]);
    expect(dockEl.querySelector(".dock-title")?.textContent).toBe("Assistant");
    expect(dock.state().mode).toBe("comments");
    expect(onChange).not.toHaveBeenCalled();

    dock.setAvailableTools(new Set(["assistant", "comments", "history"]));
    expect(dockEl.querySelector(".dock-title")?.textContent).toBe("Comments");
    expect(buttons[1]?.getAttribute("aria-checked")).toBe("true");
  });

  it("arrow-key navigation skips modes hidden by context", () => {
    const { dockEl, dock } = harness({
      tools: [
        tool("assistant", "Assistant"),
        tool("comments", "Comments"),
        tool("history", "History"),
      ],
      initial: { open: true, size: 320, mode: "assistant" },
    });
    dock.setAvailableTools(new Set(["assistant", "history"]));
    const buttons = dockEl.querySelectorAll<HTMLButtonElement>(".dock-rail-btn");
    const assistantButton = buttons[0];
    if (assistantButton === undefined) throw new Error("Assistant mode missing");
    assistantButton.focus();
    key(assistantButton, "ArrowDown");
    expect(document.activeElement).toBe(buttons[2]);
    expect(buttons[2]?.getAttribute("aria-checked")).toBe("true");
  });

  it("moves focus out of a tool body before context hides that tool", () => {
    const comments = tool("comments", "Comments");
    comments.mount = (body) => {
      const input = document.createElement("input");
      input.setAttribute("aria-label", "Comment text");
      body.appendChild(input);
    };
    const { dockEl, dock } = harness({
      tools: [tool("assistant", "Assistant"), comments],
      initial: { open: true, size: 320, mode: "comments" },
    });
    const input = dockEl.querySelector<HTMLInputElement>('[aria-label="Comment text"]');
    input?.focus();
    expect(document.activeElement).toBe(input);

    dock.setAvailableTools(new Set(["assistant"]));

    expect(document.activeElement).toBe(
      dockEl.querySelector<HTMLButtonElement>('[data-tool="assistant"]'),
    );
  });
});

describe("Dock resize", () => {
  it("keyboard resize grows/shrinks per edge, clamps, and persists only on a real change", () => {
    // Left rail: ArrowRight grows, ArrowLeft shrinks.
    const left = harness({ edge: "left", initial: { open: true, size: 260, mode: "a" } });
    key(left.splitter, "ArrowRight");
    expect(left.dockEl.style.width).toBe("276px");
    key(left.splitter, "ArrowLeft");
    expect(left.dockEl.style.width).toBe("260px");
    expect(left.onChange).toHaveBeenCalledTimes(2);

    // Right rail: the directions mirror (ArrowLeft grows).
    const right = harness({ edge: "right", initial: { open: true, size: 300, mode: "a" } });
    key(right.splitter, "ArrowLeft");
    expect(right.dockEl.style.width).toBe("316px");

    // At the min bound a shrink is a no-op (nothing changes, nothing persisted).
    const clamped = harness({
      edge: "left",
      initial: { open: true, size: DOCK_SIZE_BOUNDS.left.min, mode: "a" },
    });
    key(clamped.splitter, "ArrowLeft");
    expect(clamped.dockEl.style.width).toBe(`${DOCK_SIZE_BOUNDS.left.min}px`);
    expect(clamped.onChange).not.toHaveBeenCalled();
  });

  it("bottom edge resizes by height with ArrowUp growing", () => {
    const bottom = harness({ edge: "bottom", initial: { open: true, size: 200, mode: "a" } });
    key(bottom.splitter, "ArrowUp");
    expect(bottom.dockEl.style.height).toBe("216px");
    key(bottom.splitter, "ArrowDown");
    expect(bottom.dockEl.style.height).toBe("200px");
  });

  it("announces the collapsed bottom toolbar as horizontal and the expanded rail as vertical", () => {
    const { dockEl, dock } = harness({
      edge: "bottom",
      initial: { open: false, size: 200, mode: "a" },
    });
    const rail = dockEl.querySelector(".dock-mode-list");
    expect(rail?.getAttribute("aria-orientation")).toBe("horizontal");
    dock.toggle();
    expect(rail?.getAttribute("aria-orientation")).toBe("vertical");
  });

  it("a pointer drag resizes live and persists once on release (per edge)", () => {
    // The drag tracks the pointer on `window` (it moves off the 1px splitter immediately), so the move/up
    // events are dispatched there — not on the splitter.
    const left = harness({ edge: "left", initial: { open: true, size: 260, mode: "a" } });
    left.splitter.dispatchEvent(pointer("pointerdown", { clientX: 300 }));
    window.dispatchEvent(pointer("pointermove", { clientX: 340 })); // +40 → grow to 300
    expect(left.dockEl.style.width).toBe("300px");
    expect(left.onChange).not.toHaveBeenCalled(); // live drag doesn't persist
    window.dispatchEvent(pointer("pointerup", { clientX: 340 }));
    expect(left.onChange).toHaveBeenCalledTimes(1); // persisted once on release
    expect(document.body.style.userSelect).toBe(""); // selection restored

    // Right rail grows as the pointer moves LEFT (toward its edge).
    const right = harness({ edge: "right", initial: { open: true, size: 300, mode: "a" } });
    right.splitter.dispatchEvent(pointer("pointerdown", { clientX: 300 }));
    window.dispatchEvent(pointer("pointermove", { clientX: 260 })); // −40 delta → grow to 340
    window.dispatchEvent(pointer("pointerup", { clientX: 260 }));
    expect(right.dockEl.style.width).toBe("340px");

    // Bottom dock grows as the pointer moves UP.
    const bottom = harness({ edge: "bottom", initial: { open: true, size: 200, mode: "a" } });
    bottom.splitter.dispatchEvent(pointer("pointerdown", { clientY: 300 }));
    window.dispatchEvent(pointer("pointermove", { clientY: 260 })); // −40 delta → grow to 240
    window.dispatchEvent(pointer("pointerup", { clientY: 260 }));
    expect(bottom.dockEl.style.height).toBe("240px");
  });

  it("a zero-movement splitter click doesn't persist", () => {
    const { splitter, onChange } = harness({
      edge: "left",
      initial: { open: true, size: 260, mode: "a" },
    });
    splitter.dispatchEvent(pointer("pointerdown", { clientX: 300 }));
    window.dispatchEvent(pointer("pointerup", { clientX: 300 }));
    expect(onChange).not.toHaveBeenCalled();
    expect(document.body.style.userSelect).toBe("");
  });

  it("ignores a second (overlapping) pointer and restores text selection on the real drag's end", () => {
    const { dockEl, splitter } = harness({
      edge: "left",
      initial: { open: true, size: 260, mode: "a" },
    });
    splitter.dispatchEvent(pointer("pointerdown", { clientX: 300, pointerId: 1 }));
    // A second finger: must be ignored (no re-entered drag), and its stray move must not perturb the size.
    splitter.dispatchEvent(pointer("pointerdown", { clientX: 300, pointerId: 2 }));
    window.dispatchEvent(pointer("pointermove", { clientX: 500, pointerId: 2 }));
    expect(dockEl.style.width).toBe("260px"); // pointer 2's move filtered out
    window.dispatchEvent(pointer("pointermove", { clientX: 340, pointerId: 1 }));
    expect(dockEl.style.width).toBe("300px"); // pointer 1 (the real drag) resizes
    // The real drag ending restores selection to the true pre-drag value, not a nested "none".
    window.dispatchEvent(pointer("pointerup", { clientX: 340, pointerId: 1 }));
    expect(document.body.style.userSelect).toBe("");
  });

  it("ref-counts text-selection suppression across concurrent drags on different docks", () => {
    document.body.innerHTML = `<div id="row"><div id="leftd"></div><div id="rightd"></div></div>`;
    const leftEl = document.querySelector<HTMLElement>("#leftd");
    const rightEl = document.querySelector<HTMLElement>("#rightd");
    if (leftEl === null || rightEl === null) {
      throw new Error("no dock host");
    }
    const tools = [tool("a", "Alpha"), tool("b", "Bravo")];
    const noop = { onChange: () => {} };
    // Constructed for their side effect: each Dock wires its splitter into the DOM (the drag targets below).
    new Dock(leftEl, "left", tools, { open: true, size: 260, mode: "a" }, noop);
    new Dock(rightEl, "right", tools, { open: true, size: 300, mode: "a" }, noop);
    const leftSplitter = leftEl.nextElementSibling as HTMLElement;
    const rightSplitter = rightEl.previousElementSibling as HTMLElement;

    // Two fingers grab two different splitters; the shared body userSelect is suppressed once.
    leftSplitter.dispatchEvent(pointer("pointerdown", { clientX: 300, pointerId: 1 }));
    rightSplitter.dispatchEvent(pointer("pointerdown", { clientX: 400, pointerId: 2 }));
    expect(document.body.style.userSelect).toBe("none");
    // Lifting the first finger must NOT restore yet (the second drag is still live).
    window.dispatchEvent(pointer("pointerup", { clientX: 300, pointerId: 1 }));
    expect(document.body.style.userSelect).toBe("none");
    // Only the last drag to finish restores the original value — no stuck "none".
    window.dispatchEvent(pointer("pointerup", { clientX: 400, pointerId: 2 }));
    expect(document.body.style.userSelect).toBe("");
  });
});

describe("Dock accessibility", () => {
  it("exposes the separator's size and bounds via aria, updating valuenow on resize", () => {
    const { splitter } = harness({ edge: "left", initial: { open: true, size: 260, mode: "a" } });
    expect(splitter.getAttribute("role")).toBe("separator");
    expect(splitter.getAttribute("aria-valuemin")).toBe(String(DOCK_SIZE_BOUNDS.left.min));
    expect(splitter.getAttribute("aria-valuemax")).toBe(String(DOCK_SIZE_BOUNDS.left.max));
    expect(splitter.getAttribute("aria-valuenow")).toBe("260");
    key(splitter, "ArrowRight");
    expect(splitter.getAttribute("aria-valuenow")).toBe("276");
  });

  it("gives the chrome edge-distinct accessible names", () => {
    const { dockEl, splitter } = harness({
      edge: "left",
      initial: { open: true, size: 260, mode: "a" },
    });
    expect(splitter.getAttribute("aria-label")).toBe("Resize left panel");
    expect(dockEl.querySelector(".dock-collapse")?.getAttribute("aria-label")).toBe(
      "Collapse left panel",
    );
    expect(dockEl.querySelector(".dock-mode-list")?.getAttribute("aria-label")).toBe(
      "left panel mode",
    );
  });

  it("moves focus to the active rail icon when collapsing from the in-dock control", () => {
    const { dockEl } = harness({
      edge: "left",
      initial: { open: true, size: 260, mode: "a" },
    });
    const collapse = dockEl.querySelector<HTMLButtonElement>(".dock-collapse");
    collapse?.focus();
    expect(document.activeElement).toBe(collapse);
    collapse?.click();
    // Focus landed on the persistent rail icon, not <body> (the collapse button is now hidden).
    expect(document.activeElement).toBe(dockEl.querySelector<HTMLButtonElement>('[data-tool="a"]'));
  });
});
