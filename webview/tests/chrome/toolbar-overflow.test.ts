// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { firstOverflowIndex, ToolbarOverflow } from "../../src/chrome/toolbar-overflow.js";

function rect(width: number): DOMRect {
  return {
    x: 0,
    y: 0,
    top: 0,
    right: width,
    bottom: 28,
    left: 0,
    width,
    height: 28,
    toJSON: () => ({}),
  };
}

afterEach(() => {
  vi.restoreAllMocks();
  document.body.replaceChildren();
});

describe("measured toolbar overflow", () => {
  it("moves only the trailing controls and preserves their order", () => {
    expect(firstOverflowIndex([40, 50, 60], 96, 4)).toBe(2);
    expect(firstOverflowIndex([40, 50, 60], 200, 4)).toBe(3);
    expect(firstOverflowIndex([], 0, 4)).toBe(0);
  });

  it("mirrors command state, routes clicks, closes on Escape, and returns focus", async () => {
    document.body.innerHTML = `
      <div id="toolbar">
        <button id="one" aria-label="First">One</button>
        <fieldset id="formats">
          <button id="two" aria-label="Second" aria-pressed="true">Two</button>
          <button id="three" aria-label="Third" disabled>Three</button>
        </fieldset>
      </div>`;
    const root = document.querySelector<HTMLElement>("#toolbar");
    const controls = Array.from(document.querySelectorAll<HTMLButtonElement>("#toolbar button"));
    if (root === null) throw new Error("toolbar fixture missing");
    let clientWidth = 74;
    Object.defineProperty(root, "clientWidth", {
      configurable: true,
      get: () => clientWidth,
    });
    Object.defineProperty(root, "scrollWidth", {
      configurable: true,
      get: () => {
        const commandWidth =
          controls.filter((control) => !control.classList.contains("toolbar-overflowed")).length *
          40;
        const trigger = root.querySelector<HTMLButtonElement>(".toolbar-overflow-trigger");
        return commandWidth + (trigger?.hidden === false ? 30 : 0);
      },
    });
    for (const control of controls) {
      vi.spyOn(control, "getBoundingClientRect").mockReturnValue(rect(40));
    }
    const clicked = vi.fn();
    controls[1]?.addEventListener("click", clicked);
    const overflow = new ToolbarOverflow(root, { controls, label: "More commands" });
    const trigger = root.querySelector<HTMLButtonElement>(".toolbar-overflow-trigger");
    if (trigger === null) throw new Error("overflow trigger missing");
    vi.spyOn(trigger, "getBoundingClientRect").mockReturnValue(rect(30));

    await new Promise(requestAnimationFrame);
    expect(controls[0]?.classList.contains("toolbar-overflowed")).toBe(false);
    expect(controls[1]?.classList.contains("toolbar-overflowed")).toBe(true);
    expect(controls[2]?.classList.contains("toolbar-overflowed")).toBe(true);
    trigger.click();
    const menu = root.querySelector<HTMLElement>(".toolbar-overflow-menu");
    const items = Array.from(menu?.querySelectorAll<HTMLButtonElement>("button") ?? []);
    expect(items.map((item) => item.textContent)).toEqual(["Second", "Third"]);
    expect(items[0]?.getAttribute("role")).toBe("menuitemcheckbox");
    expect(items[0]?.getAttribute("aria-checked")).toBe("true");
    expect(items[1]?.disabled).toBe(true);
    items[0]?.click();
    expect(clicked).toHaveBeenCalledOnce();
    expect(menu?.hidden).toBe(true);
    expect(document.activeElement).toBe(trigger);

    trigger.click();
    document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    expect(menu?.hidden).toBe(true);
    expect(document.activeElement).toBe(trigger);

    const fieldset = document.querySelector<HTMLFieldSetElement>("#formats");
    if (fieldset === null) throw new Error("format fieldset missing");
    fieldset.disabled = true;
    await new Promise(requestAnimationFrame);
    trigger.click();
    expect(
      Array.from(menu?.querySelectorAll<HTMLButtonElement>("button") ?? []).every(
        (item) => item.disabled,
      ),
    ).toBe(true);
    trigger.click();

    clientWidth = 240;
    window.dispatchEvent(new Event("resize"));
    await new Promise(requestAnimationFrame);
    expect(trigger.hidden).toBe(true);
    expect(controls.every((control) => !control.classList.contains("toolbar-overflowed"))).toBe(
      true,
    );
    overflow.dispose();
  });
});
