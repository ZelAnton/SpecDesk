// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { Navigator } from "../../src/workspace/tools/navigator.js";

function harness() {
  const onNavigate = vi.fn<(id: string) => void>();
  const nav = new Navigator(
    [
      { id: "editor", label: "Document", hint: "The spec you're editing" },
      { id: "home", label: "Start" },
    ],
    onNavigate,
  );
  const body = document.createElement("div");
  document.body.appendChild(body);
  nav.mount(body);
  const items = () => Array.from(body.querySelectorAll<HTMLButtonElement>(".nav-item"));
  return { nav, onNavigate, body, items };
}

describe("Navigator", () => {
  it("builds a button per destination with the label as accessible name, hint decorative", () => {
    const { body, items } = harness();
    expect(items()).toHaveLength(2);
    const [doc, start] = items();
    expect(doc?.getAttribute("aria-label")).toBe("Document");
    expect(doc?.querySelector(".nav-item-label")?.textContent).toBe("Document");
    // Hint is present for Document, absent for Start; it is not part of the accessible name (aria-label).
    expect(doc?.querySelector(".nav-item-hint")?.textContent).toBe("The spec you're editing");
    expect(start?.querySelector(".nav-item-hint")).toBeNull();
    expect(body.querySelector(".nav-list")).not.toBeNull();
  });

  it("clicking a destination calls onNavigate with its view id", () => {
    const { items, onNavigate } = harness();
    items()[1]?.click();
    expect(onNavigate).toHaveBeenCalledWith("home");
  });

  it("setActive highlights the current destination (class + aria-current) and clears the rest", () => {
    const { nav, items } = harness();
    nav.setActive("home");
    const [doc, start] = items();
    expect(start?.classList.contains("nav-item--current")).toBe(true);
    expect(start?.getAttribute("aria-current")).toBe("page");
    expect(doc?.classList.contains("nav-item--current")).toBe(false);
    expect(doc?.getAttribute("aria-current")).toBeNull();

    // Moving the active view moves the highlight (a switch driven from elsewhere).
    nav.setActive("editor");
    expect(doc?.classList.contains("nav-item--current")).toBe(true);
    expect(start?.getAttribute("aria-current")).toBeNull();
  });

  it("reflects an active id chosen before the tool was mounted", () => {
    const nav = new Navigator([{ id: "editor", label: "Document" }], vi.fn());
    nav.setActive("editor");
    const body = document.createElement("div");
    nav.mount(body);
    expect(body.querySelector(".nav-item")?.getAttribute("aria-current")).toBe("page");
  });
});
