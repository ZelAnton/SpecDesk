// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { closestElement } from "../src/dom.js";

describe("closestElement (EventTarget boundary)", () => {
  it("returns the nearest matching ancestor element, or null off-element", () => {
    const root = document.createElement("div");
    root.innerHTML = '<section><a href="#x"><span id="inner">click</span></a></section>';
    const inner = root.querySelector("#inner");
    expect(inner).not.toBeNull();

    // From a descendant, find the enclosing anchor.
    const anchor = closestElement(inner, "a");
    expect(anchor?.tagName.toLowerCase()).toBe("a");
    // No matching ancestor → null.
    expect(closestElement(inner, "table")).toBeNull();
    // A non-element target (null, or a text node) → null, no throw.
    expect(closestElement(null, "a")).toBeNull();
    expect(closestElement(document.createTextNode("x"), "a")).toBeNull();
  });
});
