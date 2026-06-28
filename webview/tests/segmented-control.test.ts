// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { SegmentedControl } from "../src/segmented-control.js";

function harness() {
  document.body.innerHTML = `
    <span role="radiogroup">
      <button id="a" role="radio">A</button>
      <button id="b" role="radio">B</button>
      <button id="c" role="radio">C</button>
    </span>`;
  const byId = (id: string) => {
    const el = document.querySelector<HTMLButtonElement>(`#${id}`);
    if (el === null) {
      throw new Error(`no #${id}`);
    }
    return el;
  };
  const onSelect = vi.fn<(value: string) => void>();
  const control = new SegmentedControl(
    [
      { el: byId("a"), value: "a" },
      { el: byId("b"), value: "b" },
      { el: byId("c"), value: "c" },
    ],
    onSelect,
  );
  return { control, onSelect, byId };
}

function arrow(el: HTMLElement, key: string): void {
  el.dispatchEvent(new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true }));
}

describe("SegmentedControl.setSelected", () => {
  it("checks the chosen radio and gives it the single tab stop", () => {
    const { control, byId } = harness();
    control.setSelected("b");
    expect(byId("a").getAttribute("aria-checked")).toBe("false");
    expect(byId("b").getAttribute("aria-checked")).toBe("true");
    expect(byId("c").getAttribute("aria-checked")).toBe("false");
    expect(byId("a").tabIndex).toBe(-1);
    expect(byId("b").tabIndex).toBe(0);
    expect(byId("c").tabIndex).toBe(-1);
  });
});

describe("SegmentedControl interaction", () => {
  it("a click selects that radio", () => {
    const { onSelect, byId } = harness();
    byId("b").click();
    expect(onSelect).toHaveBeenLastCalledWith("b");
  });

  it("ArrowRight / ArrowDown move to the next radio, focus it, and select it", () => {
    const { onSelect, byId } = harness();
    arrow(byId("a"), "ArrowRight");
    expect(onSelect).toHaveBeenLastCalledWith("b");
    expect(document.activeElement).toBe(byId("b"));
    arrow(byId("b"), "ArrowDown");
    expect(onSelect).toHaveBeenLastCalledWith("c");
    expect(document.activeElement).toBe(byId("c"));
  });

  it("ArrowLeft / ArrowUp move to the previous radio, wrapping past the first", () => {
    const { onSelect, byId } = harness();
    arrow(byId("a"), "ArrowLeft"); // wraps to the last
    expect(onSelect).toHaveBeenLastCalledWith("c");
    expect(document.activeElement).toBe(byId("c"));
  });

  it("ignores non-arrow keys", () => {
    const { onSelect, byId } = harness();
    arrow(byId("a"), "Enter");
    arrow(byId("a"), " ");
    expect(onSelect).not.toHaveBeenCalled();
  });
});
