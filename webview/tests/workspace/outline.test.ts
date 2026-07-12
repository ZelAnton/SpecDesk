// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import { Outline, parseOutline } from "../../src/workspace/tools/outline.js";

describe("parseOutline", () => {
  it("extracts ATX headings with level and 0-based line, skipping fenced code", () => {
    const doc = [
      "# Title", // 0
      "",
      "## Section A", // 2
      "text",
      "```", // 4 fence open
      "# not a heading", // 5 inside fence
      "```", // 6 fence close
      "### Sub ###", // 7 trailing hashes stripped
      "#notheading", // 8 no space → not a heading
      "#", // 9 empty heading
    ].join("\n");
    expect(parseOutline(doc)).toEqual([
      { level: 1, text: "Title", line: 0 },
      { level: 2, text: "Section A", line: 2 },
      { level: 3, text: "Sub", line: 7 },
      { level: 1, text: "", line: 9 },
    ]);
  });

  it("does not treat 7+ hashes as a heading, and handles a tilde fence", () => {
    const doc = ["####### too deep", "~~~", "## in fence", "~~~", "###### h6"].join("\n");
    expect(parseOutline(doc)).toEqual([{ level: 6, text: "h6", line: 4 }]);
  });

  it("returns an empty list for a heading-less document", () => {
    expect(parseOutline("just some\nprose\n")).toEqual([]);
  });

  it("allows up to 3 leading spaces and preserves mid-text hashes", () => {
    const doc = ["   ## Indented", "# C# and Issue #42"].join("\n");
    expect(parseOutline(doc)).toEqual([
      { level: 2, text: "Indented", line: 0 },
      { level: 1, text: "C# and Issue #42", line: 1 },
    ]);
  });

  it("skips a leading YAML front-matter block", () => {
    const doc = ["---", "title: Spec", "# not a heading", "---", "# Real", ""].join("\n");
    expect(parseOutline(doc)).toEqual([{ level: 1, text: "Real", line: 4 }]);
  });

  it("is CRLF-safe (trailing \\r absorbed)", () => {
    expect(parseOutline("# Title\r\n## Sub\r\n")).toEqual([
      { level: 1, text: "Title", line: 0 },
      { level: 2, text: "Sub", line: 1 },
    ]);
  });
});

describe("Outline tool", () => {
  function ready() {
    const onNavigate = vi.fn<(line: number) => void>();
    const outline = new Outline(onNavigate);
    const body = document.createElement("div");
    outline.mount(body);
    return { outline, onNavigate, body };
  }

  it("shows the empty hint until items are set", () => {
    const { body } = ready();
    expect(body.querySelector<HTMLElement>(".outline-empty")?.hidden).toBe(false);
    expect(body.querySelectorAll(".outline-item")).toHaveLength(0);
  });

  it("renders items in a nested list (programmatic hierarchy), hides the hint, navigates on click", () => {
    const { outline, onNavigate, body } = ready();
    outline.setItems([
      { level: 1, text: "A", line: 0 },
      { level: 2, text: "B", line: 4 },
    ]);
    const items = body.querySelectorAll<HTMLButtonElement>(".outline-item");
    expect(items).toHaveLength(2);
    expect(body.querySelector<HTMLElement>(".outline-empty")?.hidden).toBe(true);
    expect(items[0]?.textContent).toBe("A");
    expect(items[0]?.getAttribute("data-level")).toBe("1");
    // A (level 1) sits at the top level; B (level 2) is nested inside A's <li>.
    expect(items[0]?.closest("li")?.parentElement?.closest("li")).toBeNull();
    expect(items[1]?.closest("li")?.parentElement?.closest("li")).toBe(items[0]?.closest("li"));

    items[1]?.click();
    expect(onNavigate).toHaveBeenCalledWith(4);
  });

  it("gives each heading button a title so a truncated label is still readable, but not the untitled fallback", () => {
    const { outline, body } = ready();
    outline.setItems([
      { level: 1, text: "A long heading", line: 0 },
      { level: 2, text: "", line: 3 },
    ]);
    const items = body.querySelectorAll<HTMLButtonElement>(".outline-item");
    expect(items[0]?.title).toBe("A long heading");
    expect(items[1]?.title).toBe("");
  });

  it("does not rebuild the DOM when the heading set is unchanged", () => {
    const { outline, body } = ready();
    outline.setItems([{ level: 1, text: "A", line: 0 }]);
    const first = body.querySelector(".outline-item");
    // Same values, different array/object identities — the document text changed but the headings did not.
    outline.setItems([{ level: 1, text: "A", line: 0 }]);
    expect(body.querySelector(".outline-item")).toBe(first);
    // A real change still re-renders.
    outline.setItems([{ level: 1, text: "B", line: 0 }]);
    expect(body.querySelector(".outline-item")).not.toBe(first);
  });

  it("shows a placeholder for an empty-text heading and re-renders on setItems", () => {
    const { outline, body } = ready();
    outline.setItems([{ level: 3, text: "", line: 2 }]);
    expect(body.querySelector(".outline-item")?.textContent).toBe("(untitled)");
    outline.setItems([]);
    expect(body.querySelectorAll(".outline-item")).toHaveLength(0);
    expect(body.querySelector<HTMLElement>(".outline-empty")?.hidden).toBe(false);
  });
});
