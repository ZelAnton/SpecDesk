// @vitest-environment jsdom
import { undo, undoDepth } from "prosemirror-history";
import { TextSelection } from "prosemirror-state";
import type { EditorView } from "prosemirror-view";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { FormattedEditor } from "../../src/editors/formatted.js";
import type { MdBlock } from "../../src/editors/md-blocks.js";
import { log } from "../../src/util/log.js";

// Runtime check of the ProseMirror integration (which can't be verified headlessly in the app):
// constructing the view, parsing Markdown into it, and serializing back via block-splice. Layout-
// dependent helpers (scroll) aren't exercised here — jsdom has no layout — they need the GUI.

const RICH = `# Title

Intro paragraph that is
hard-wrapped across two lines.

## Section

- one
- two
- three

> a quote
> second line

| A | B |
| - | - |
| 1 | 2 |

\`\`\`ts
const x = 1;
\`\`\`

1. first
2. second

---

Trailing paragraph.
`;

function mountWithHost(): { ed: FormattedEditor; host: HTMLDivElement } {
  const host = document.createElement("div");
  document.body.appendChild(host);
  const ed = new FormattedEditor(host, {
    onChange: () => {},
    onEditAttempt: () => {},
    onScroll: () => {},
    onCursor: () => {},
    onHover: () => {},
    onContentResize: () => {},
    onFocus: () => {},
    onActiveChange: () => {},
    onOpenLink: () => {},
  });
  return { ed, host };
}

function mount(): FormattedEditor {
  return mountWithHost().ed;
}

/** True if `b` comes after `a` in document order (robust to wrapping, unlike sibling checks). */
function follows(a: Element, b: Element): boolean {
  return (a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;
}

/** The editor's ProseMirror view, for test setup (dispatching selections). Test-only internal access —
 *  the one unavoidable cast is isolated here rather than repeated at each call site. */
function viewOf(ed: FormattedEditor): EditorView {
  return (ed as unknown as { view: EditorView }).view;
}

describe("FormattedEditor (jsdom)", () => {
  it("constructs a ProseMirror view without throwing", () => {
    expect(() => mount()).not.toThrow();
  });

  for (const [name, md] of [
    ["rich fixture (table, list, code, quote, hr)", RICH],
    ["heading + paragraph", "# H\n\nA paragraph.\n"],
    ["bullet list", "- one\n- two\n- three\n"],
    ["ordered list + code", "1. a\n2. b\n\n```\ncode\n```\n"],
    // S-13 regression guards: a leading gap (blank lines / a reference definition with no rendered
    // node) used to add a synthetic block, permanently desyncing blocks.length from doc.childCount, so
    // getText() ALWAYS took the whole-document fallback here — reflowing an untouched document the
    // instant applyMode read it while leaving "Форматированный" mode (index.ts applyMode).
    ["leading blank lines", "\n\n# H\n\npara\n"],
    ["leading reference definition", "[a]: http://x\n\nSee [a] link.\n"],
  ] as const) {
    it(`setText then getText is byte-identical with no edits: ${name}`, () => {
      const ed = mount();
      ed.setText(md);
      expect(ed.getText()).toBe(md);
    });
  }

  it("maps a source line to the correct block when the document has leading blank lines (no off-by-one)", () => {
    const { ed, host } = mountWithHost();
    // Leading blank lines (0, 1) fold into the heading's own block (S-13); blocks stay 1:1 with the
    // three ProseMirror children (heading, paragraph, bullet list) instead of gaining a synthetic
    // fourth entry that would shift every later block index by one.
    ed.setText("\n\n# H\n\npara\n\n- a\n- b\n");

    ed.setActiveLine(0); // a leading blank line — folds into the heading block, not a block of its own
    let active = host.querySelectorAll(".sd-active-block");
    expect(active.length).toBe(1);
    expect(active[0]?.tagName.toLowerCase()).toBe("h1");

    ed.setActiveLine(4); // inside "para" — must NOT resolve to the list (the pre-fix off-by-one)
    active = host.querySelectorAll(".sd-active-block");
    expect(active.length).toBe(1);
    expect(active[0]?.tagName.toLowerCase()).toBe("p");
    expect(active[0]?.textContent).toContain("para");

    ed.setActiveLine(6); // "- a"
    active = host.querySelectorAll(".sd-active-block");
    expect(active.length).toBe(1);
    expect(active[0]?.tagName.toLowerCase()).toBe("li");
    expect(active[0]?.textContent).toContain("a");
    expect(active[0]?.textContent).not.toContain("b");
  });

  it("re-bases the splice baseline on each setText", () => {
    const ed = mount();
    ed.setText("# One\n\nfirst\n");
    expect(ed.getText()).toBe("# One\n\nfirst\n");
    ed.setText("# Two\n\nsecond\n");
    expect(ed.getText()).toBe("# Two\n\nsecond\n");
  });

  it("setActiveLine/setHoverLine decorate the block containing that source line", () => {
    const host = document.createElement("div");
    document.body.appendChild(host);
    const ed = new FormattedEditor(host, {
      onChange: () => {},
      onEditAttempt: () => {},
      onScroll: () => {},
      onCursor: () => {},
      onHover: () => {},
      onContentResize: () => {},
      onFocus: () => {},
      onActiveChange: () => {},
      onOpenLink: () => {},
    });
    // Blocks: heading (line 0), paragraph (line 2), bullet list (line 4).
    ed.setText("# H\n\npara\n\n- a\n- b\n");

    ed.setActiveLine(2); // a line inside the paragraph block
    const active = host.querySelectorAll(".sd-active-block");
    expect(active.length).toBe(1);
    expect(active[0]?.textContent).toContain("para");

    ed.setHoverLine(4); // a line inside the list block
    const hover = host.querySelectorAll(".sd-hover-block");
    expect(hover.length).toBe(1);
    expect(hover[0]?.textContent).toContain("a");
    // The active highlight is unchanged and distinct from the hover one.
    expect(host.querySelectorAll(".sd-active-block")[0]?.textContent).toContain("para");
  });

  it("clears (rather than pins to the last block) the active highlight once a shorter setText leaves it out of range (M-34)", () => {
    const { ed, host } = mountWithHost();
    // Blocks: heading (line 0), paragraph (line 2), bullet list (line 4).
    ed.setText("# H\n\npara\n\n- a\n- b\n");
    ed.setActiveLine(4); // "- a" — inside the last block
    expect(host.querySelectorAll(".sd-active-block")).toHaveLength(1);

    // The document shrinks (e.g. the sibling editor pane mirrors in a shorter edit) so the previously
    // synced active line no longer exists — matches the source editor's behavior (see editor.ts
    // activeLineField), which also clears rather than pins to its last line for this case.
    ed.setText("# H\n\npara\n");

    expect(host.querySelectorAll(".sd-active-block")).toHaveLength(0);
  });

  it("highlights the table row (not the whole table) for a row's source line", () => {
    const host = document.createElement("div");
    document.body.appendChild(host);
    const ed = new FormattedEditor(host, {
      onChange: () => {},
      onEditAttempt: () => {},
      onScroll: () => {},
      onCursor: () => {},
      onHover: () => {},
      onContentResize: () => {},
      onFocus: () => {},
      onActiveChange: () => {},
      onOpenLink: () => {},
    });
    // Rows at lines 0 (header), 2 (1|2), 3 (3|4); line 1 is the separator.
    ed.setText("| A | B |\n| - | - |\n| 1 | 2 |\n| 3 | 4 |\n");

    ed.setActiveLine(2);
    const active = host.querySelectorAll(".sd-active-block");
    expect(active.length).toBe(1);
    expect(active[0]?.tagName.toLowerCase()).toBe("tr");
    expect(active[0]?.textContent).toContain("1");
    expect(active[0]?.textContent).not.toContain("3");
  });

  it("highlights the list item (not the whole list) for an item's source line", () => {
    const host = document.createElement("div");
    document.body.appendChild(host);
    const ed = new FormattedEditor(host, {
      onChange: () => {},
      onEditAttempt: () => {},
      onScroll: () => {},
      onCursor: () => {},
      onHover: () => {},
      onContentResize: () => {},
      onFocus: () => {},
      onActiveChange: () => {},
      onOpenLink: () => {},
    });
    ed.setText("- one\n- two\n- three\n");

    ed.setActiveLine(1); // "- two"
    const active = host.querySelectorAll(".sd-active-block");
    expect(active.length).toBe(1);
    expect(active[0]?.tagName.toLowerCase()).toBe("li");
    expect(active[0]?.textContent).toContain("two");
    expect(active[0]?.textContent).not.toContain("three");
  });

  it("setDiff washes each changed block by its kind, and clearDiff removes them", () => {
    const { ed, host } = mountWithHost();
    // heading (line 0), paragraph "keep" (line 2), paragraph "more" (line 4).
    ed.setText("# H\n\nkeep\n\nmore\n");

    ed.setDiff([
      // A changed heading whose base equals its head text → nothing word-differs → a whole-block wash
      // (the inline word-diff bows out), which is what this test asserts the pill on.
      { kind: "changed", sub: false, lineStart: 0, lineEnd: 0, baseText: "H", baseSource: "H" },
      { kind: "added", sub: false, lineStart: 2, lineEnd: 2 },
      { kind: "moved", sub: false, lineStart: 4, lineEnd: 4 },
    ]);
    expect(host.querySelector(".sd-diff-changed")?.textContent).toContain("H");
    expect(host.querySelector(".sd-diff-added")?.textContent).toContain("keep");
    expect(host.querySelector(".sd-diff-moved")?.textContent).toContain("more");
    // Each block carries its change-annotation label (the CSS ::before pill reads it via attr()).
    expect(host.querySelector(".sd-diff-changed")?.getAttribute("data-diff-label")).toBe(
      "Updated by you",
    );
    expect(host.querySelector(".sd-diff-added")?.getAttribute("data-diff-label")).toBe(
      "Added by you",
    );
    expect(host.querySelector(".sd-diff-moved")?.getAttribute("data-diff-label")).toBe(
      "Moved by you",
    );

    ed.clearDiff();
    expect(host.querySelectorAll(".sd-diff-changed, .sd-diff-added, .sd-diff-moved")).toHaveLength(
      0,
    );
  });

  it("places a removed-block marker between the surrounding head blocks, not at an edge", () => {
    const { ed, host } = mountWithHost();
    // head: heading (line 0), paragraph "keep" (line 2). A block was removed in between, so the wire
    // anchor is line 1 (the blank inter-block line) — the case that naive block-containment got wrong.
    ed.setText("# H\n\nkeep\n");
    ed.setDiff([{ kind: "removed", sub: false, anchorLine: 1, removedText: "gone block" }]);

    const marker = host.querySelector(".sd-diff-removed-marker");
    const heading = host.querySelector("h1");
    const kept = [...host.querySelectorAll("p")].find((p) => p.textContent?.includes("keep"));
    expect(marker?.textContent).toContain("Deleted by you");
    expect(marker?.textContent).toContain("gone block");
    expect(heading && marker && follows(heading, marker)).toBe(true); // after the heading
    expect(marker && kept && follows(marker, kept)).toBe(true); // before the kept paragraph
  });

  it("anchors a leading removal at the top and a trailing removal at the end", () => {
    const { ed, host } = mountWithHost();
    ed.setText("# H\n\nkeep\n");
    const heading = () => host.querySelector("h1");
    const kept = () => [...host.querySelectorAll("p")].find((p) => p.textContent?.includes("keep"));

    // Deleted before all head content (anchor 0) → the marker precedes the heading.
    ed.setDiff([{ kind: "removed", sub: false, anchorLine: 0, removedText: "was first" }]);
    let marker = host.querySelector(".sd-diff-removed-marker");
    const h1 = heading();
    expect(marker && h1 && follows(marker, h1)).toBe(true);

    // Deleted after all head content (anchor past the last block) → the marker follows the last block.
    ed.setDiff([{ kind: "removed", sub: false, anchorLine: 9, removedText: "was last" }]);
    marker = host.querySelector(".sd-diff-removed-marker");
    const p = kept();
    expect(marker && p && follows(p, marker)).toBe(true);
  });

  it("a sub-block mark washes the individual list item, not the whole list", () => {
    const { ed, host } = mountWithHost();
    ed.setText("- one\n- two\n- three\n"); // items at lines 0, 1, 2

    // A changed row/item mark (sub) on the second item's line; its base equals the item text so nothing
    // word-differs → a whole-item wash (no inline words), which is what this test asserts.
    ed.setDiff([
      { kind: "changed", sub: true, lineStart: 1, lineEnd: 1, baseText: "two", baseSource: null },
    ]);
    const changed = host.querySelectorAll(".sd-diff-changed");
    expect(changed).toHaveLength(1);
    expect(changed[0]?.tagName.toLowerCase()).toBe("li");
    expect(changed[0]?.textContent).toContain("two");
    expect(changed[0]?.textContent).not.toContain("three");
    // A sub-block mark gets no annotation pill (only whole-block changes do).
    expect(changed[0]?.getAttribute("data-diff-label")).toBeNull();
  });

  it("places a removed list-item marker between the surrounding items, not at the container edge", () => {
    const { ed, host } = mountWithHost();
    ed.setText("- one\n- two\n- three\n"); // items at lines 0, 1, 2
    // A removed item sat just before "two" (the following head item, line 1).
    ed.setDiff([{ kind: "removed", sub: true, anchorLine: 1, removedText: "gone item" }]);

    const marker = host.querySelector(".sd-diff-removed-marker");
    const items = [...host.querySelectorAll("li")];
    const one = items.find((li) => li.textContent?.includes("one"));
    const two = items.find((li) => li.textContent?.includes("two"));
    expect(marker?.textContent).toContain("gone item");
    expect(one && marker && follows(one, marker)).toBe(true); // after the first item
    expect(marker && two && follows(marker, two)).toBe(true); // before the second item
  });

  // T-078 parity: the same single anchoring + removed-text policy the Code pane now uses (overlay-plan.ts).
  it("flattens a removed whole block's raw Markdown source in the marker (no leaked syntax)", () => {
    const { ed, host } = mountWithHost();
    ed.setText("# Keep\n\nkeep\n");
    // A whole removed block arrives as RAW source. Before T-078 the WYSIWYG marker lit up the raw
    // Markdown ("## Gone Section"); the single policy now strips the leading block marker in both panes.
    ed.setDiff([
      { kind: "removed", sub: false, anchorLine: 0, removedText: "## Gone Section\n\nbody text" },
    ]);
    const marker = host.querySelector(".sd-diff-removed-marker");
    expect(marker?.textContent).toContain("Gone Section");
    expect(marker?.textContent).not.toContain("#");
    expect(marker?.textContent).toContain("(… 3 lines)");
    expect(host.querySelectorAll(".sd-diff-removed-marker")).toHaveLength(1);
  });

  it("plants a removed row/item marker at the document end when its anchor line is past the document", () => {
    const { ed, host } = mountWithHost();
    ed.setText("- one\n- two\n");
    // A sub anchor past the document (a row/item deleted after the last one) resolves to no node, so the
    // marker falls to the document end — the same 'nothing at/after the anchor' outcome the Code pane
    // reaches by planting below the last line.
    ed.setDiff([{ kind: "removed", sub: true, anchorLine: 99, removedText: "gone item" }]);
    const marker = host.querySelector(".sd-diff-removed-marker");
    const items = [...host.querySelectorAll("li")];
    const last = items[items.length - 1];
    expect(marker?.textContent).toContain("gone item");
    expect(last && marker && follows(last, marker)).toBe(true); // after the last item
  });

  it("highlights changed words inside a changed list item (no whole-item wash, no pill)", () => {
    const { ed, host } = mountWithHost();
    // A long second item with a single word changed, so the diff stays under the inline threshold.
    ed.setText("- one\n- buy fresh apples from the market today\n- three\n");
    ed.setDiff([
      {
        kind: "changed",
        sub: true,
        lineStart: 1,
        lineEnd: 1,
        baseText: "buy fresh oranges from the market today",
        baseSource: null,
      },
    ]);

    const added = [...host.querySelectorAll(".sd-diff-word-added")];
    expect(added.some((w) => w.textContent?.includes("apples"))).toBe(true);
    // No whole-item wash and no annotation pill — the inline word highlight is the signal.
    expect(host.querySelectorAll(".sd-diff-changed")).toHaveLength(0);
    expect(host.querySelectorAll(".sd-diff-inline")).toHaveLength(0);
  });

  // T-076: a sub (row/item) inline word-diff must compare the WHOLE row/item — cells/blocks joined the
  // same way the native side flattens it (DiffWire.fs tableRowText/listItemText) — not just its first
  // cell/paragraph, or an untouched second cell/paragraph would read as almost entirely different text
  // and either raise changeRatio into a whole-item wash or spuriously mark the rest as deleted.
  it("highlights a changed word in a multi-cell table row using the whole row, not just its first cell", () => {
    const { ed, host } = mountWithHost();
    // Data row at line 2 (line 0 header, line 1 the separator, line 2 the one data row).
    ed.setText("| Name | Notes |\n| - | - |\n| Alice | buy fresh apples today |\n");
    ed.setDiff([
      {
        kind: "changed",
        sub: true,
        lineStart: 2,
        lineEnd: 2,
        // Native tableRowText joins cells with " | ": "Alice | buy fresh oranges today".
        baseText: "Alice | buy fresh oranges today",
        baseSource: null,
      },
    ]);

    const added = [...host.querySelectorAll(".sd-diff-word-added")];
    expect(added.some((w) => w.textContent?.includes("apples"))).toBe(true);
    const removed = [...host.querySelectorAll(".sd-diff-word-removed")];
    expect(removed.some((w) => w.textContent?.includes("oranges"))).toBe(true);
    // The untouched first cell ("Alice") must not be marked as changed, and the row keeps no whole-row
    // wash (the old first-cell-only comparison would either wash the row or leave "Notes" looking almost
    // entirely rewritten, since the whole-row base was compared against just the first cell's text).
    expect(host.querySelectorAll(".sd-diff-changed")).toHaveLength(0);
    expect(added.some((w) => w.textContent?.includes("Alice"))).toBe(false);
  });

  it("highlights a changed word in a multi-paragraph list item using the whole item, not just its first paragraph", () => {
    const { ed, host } = mountWithHost();
    // Item "two" (line 1) carries a second paragraph, indented to stay inside the item (line 3).
    ed.setText("- one\n- buy fresh apples today\n\n  the market closes on Friday\n- three\n");
    ed.setDiff([
      {
        kind: "changed",
        sub: true,
        lineStart: 1,
        lineEnd: 3,
        // Native listItemText joins the item's blocks with " ".
        baseText: "buy fresh apples today the market closes on Thursday",
        baseSource: null,
      },
    ]);

    const added = [...host.querySelectorAll(".sd-diff-word-added")];
    expect(added.some((w) => w.textContent?.includes("Friday"))).toBe(true);
    const removed = [...host.querySelectorAll(".sd-diff-word-removed")];
    expect(removed.some((w) => w.textContent?.includes("Thursday"))).toBe(true);
    // The untouched first paragraph must not be marked as changed, and the item keeps no whole-item wash
    // (the old first-paragraph-only comparison would have read the second paragraph as wholly deleted).
    expect(host.querySelectorAll(".sd-diff-changed")).toHaveLength(0);
    expect(added.some((w) => w.textContent?.includes("apples"))).toBe(false);
  });

  // R-01 (T-076 review): flattenRowOrItem used to gate the joiner on accumulated text LENGTH
  // (`text.length > 0`), so a leading empty cell — itself contributing zero characters — left the
  // joiner out entirely, producing "changed" instead of the native side's " | changed". Fixed to gate
  // on segment COUNT instead, so an empty leading cell still gets its joiner.
  it("highlights a changed word in a table row with a leading empty cell", () => {
    const { ed, host } = mountWithHost();
    ed.setText("| Name | Notes |\n| - | - |\n|  | buy fresh apples today |\n");
    ed.setDiff([
      {
        kind: "changed",
        sub: true,
        lineStart: 2,
        lineEnd: 2,
        // Native tableRowText joins ["", "buy fresh oranges today"] with " | ": " | buy fresh oranges today".
        baseText: " | buy fresh oranges today",
        baseSource: null,
      },
    ]);

    const added = [...host.querySelectorAll(".sd-diff-word-added")];
    expect(added.some((w) => w.textContent?.includes("apples"))).toBe(true);
    const removed = [...host.querySelectorAll(".sd-diff-word-removed")];
    expect(removed.some((w) => w.textContent?.includes("oranges"))).toBe(true);
    // The missing joiner would inflate changeRatio (comparing "buy fresh apples today" against the
    // joiner-prefixed base) and fall back to a whole-row wash instead of the inline highlight above.
    expect(host.querySelectorAll(".sd-diff-changed")).toHaveLength(0);
    // The dropped joiner (the actual R-01 bug) would also misalign the leading " | " itself, surfacing it
    // as a spurious removed run — asserting its absence is what pins the regression down to the joiner,
    // not just the ratio staying under threshold by coincidence.
    expect(removed.some((w) => w.textContent?.includes("|"))).toBe(false);
  });

  it("highlights changed words inline inside an edited paragraph (no whole-block wash)", () => {
    const { ed, host } = mountWithHost();
    ed.setText("The quick brown fox jumps over the lazy dog today.\n");
    // Same paragraph, one word different in the base — inline word-diff should mark just that word.
    ed.setDiff([
      {
        kind: "changed",
        sub: false,
        lineStart: 0,
        lineEnd: 0,
        baseText: "The quick brown fox leaps over the lazy dog today.",
        baseSource: "The quick brown fox leaps over the lazy dog today.",
      },
    ]);

    const added = [...host.querySelectorAll(".sd-diff-word-added")];
    expect(added.some((w) => w.textContent?.includes("jumps"))).toBe(true);
    const removed = [...host.querySelectorAll(".sd-diff-word-removed")];
    expect(removed.some((w) => w.textContent?.includes("leaps"))).toBe(true);
    // The whole-block "changed" wash is NOT applied — inline took over — but the pill still shows.
    expect(host.querySelectorAll(".sd-diff-changed")).toHaveLength(0);
    expect(host.querySelector(".sd-diff-inline")?.getAttribute("data-diff-label")).toBe(
      "Updated by you",
    );
  });

  it("falls back to a whole-block wash when too much of the paragraph changed", () => {
    const { ed, host } = mountWithHost();
    ed.setText("totally different wording here now\n");
    ed.setDiff([
      {
        kind: "changed",
        sub: false,
        lineStart: 0,
        lineEnd: 0,
        baseText: "alpha beta gamma delta",
        baseSource: "alpha beta gamma delta",
      },
    ]);

    // Most of the paragraph changed → no inline words; the whole block washes as "edited".
    expect(host.querySelectorAll(".sd-diff-word-added")).toHaveLength(0);
    expect(host.querySelectorAll(".sd-diff-changed")).toHaveLength(1);
    expect(host.querySelector(".sd-diff-changed")?.getAttribute("data-diff-label")).toBe(
      "Updated by you",
    );
  });

  // T-075: a whole-container (sub: false) diff mark must wash the ENTIRE table/list, not just the first
  // row/item nodeRangeForLine would otherwise narrow to — the Code pane (editor.ts) already gets this
  // right and is the parity reference these assert against.
  it("an added table container washes the whole table, not just its first row", () => {
    const { ed, host } = mountWithHost();
    // "Intro" (line 0), table (lines 2-3: header row + one data row).
    ed.setText("Intro\n\n| A | B |\n| - | - |\n| 1 | 2 |\n");
    ed.setDiff([{ kind: "added", sub: false, lineStart: 2, lineEnd: 3 }]);

    const washed = host.querySelector(".sd-diff-added");
    expect(washed?.tagName.toLowerCase()).toBe("table");
    expect(washed?.querySelectorAll("tr")).toHaveLength(2); // header row AND data row both inside
    expect(washed?.textContent).toContain("A");
    expect(washed?.textContent).toContain("1");
    expect(washed?.getAttribute("data-diff-label")).toBe("Added by you");
  });

  it("an added list container washes every item, not just the first", () => {
    const { ed, host } = mountWithHost();
    ed.setText("Intro\n\n- one\n- two\n- three\n");
    ed.setDiff([{ kind: "added", sub: false, lineStart: 2, lineEnd: 4 }]);

    const washed = host.querySelector(".sd-diff-added");
    expect(washed?.tagName.toLowerCase()).toBe("ul");
    expect(washed?.querySelectorAll("li")).toHaveLength(3);
    expect(washed?.textContent).toContain("one");
    expect(washed?.textContent).toContain("three");
    expect(washed?.getAttribute("data-diff-label")).toBe("Added by you");
  });

  it("a moved container (table or list) washes the whole container, not just its first row/item", () => {
    const { ed: tableEd, host: tableHost } = mountWithHost();
    tableEd.setText("| A | B |\n| - | - |\n| 1 | 2 |\n");
    tableEd.setDiff([{ kind: "moved", sub: false, lineStart: 0, lineEnd: 1 }]);
    const washedTable = tableHost.querySelector(".sd-diff-moved");
    expect(washedTable?.tagName.toLowerCase()).toBe("table");
    expect(washedTable?.querySelectorAll("tr")).toHaveLength(2);
    expect(washedTable?.getAttribute("data-diff-label")).toBe("Moved by you");

    const { ed: listEd, host: listHost } = mountWithHost();
    listEd.setText("- one\n- two\n- three\n");
    listEd.setDiff([{ kind: "moved", sub: false, lineStart: 0, lineEnd: 2 }]);
    const washedList = listHost.querySelector(".sd-diff-moved");
    expect(washedList?.tagName.toLowerCase()).toBe("ul");
    expect(washedList?.querySelectorAll("li")).toHaveLength(3);
    expect(washedList?.getAttribute("data-diff-label")).toBe("Moved by you");
  });

  it("a changed-container fallback (no per-child diff) washes the whole table/list, not just its first row/item", () => {
    const { ed: tableEd, host: tableHost } = mountWithHost();
    tableEd.setText("| A | B |\n| - | - |\n| 1 | 2 |\n| 3 | 4 |\n");
    // sub: false with children.length === 0 (an empty childDiff, per expandDiffMarks) — the container's
    // node text isn't pure, so pushInlineWordDiff bows out and pushDiff must wash the whole container.
    tableEd.setDiff([
      {
        kind: "changed",
        sub: false,
        lineStart: 0,
        lineEnd: 3,
        baseText: "irrelevant",
        baseSource: null,
      },
    ]);
    const washedTable = tableHost.querySelector(".sd-diff-changed");
    expect(washedTable?.tagName.toLowerCase()).toBe("table");
    expect(washedTable?.querySelectorAll("tr")).toHaveLength(3);
    expect(washedTable?.getAttribute("data-diff-label")).toBe("Updated by you");

    const { ed: listEd, host: listHost } = mountWithHost();
    listEd.setText("- one\n- two\n- three\n");
    listEd.setDiff([
      {
        kind: "changed",
        sub: false,
        lineStart: 0,
        lineEnd: 2,
        baseText: "irrelevant",
        baseSource: null,
      },
    ]);
    const washedList = listHost.querySelector(".sd-diff-changed");
    expect(washedList?.tagName.toLowerCase()).toBe("ul");
    expect(washedList?.querySelectorAll("li")).toHaveLength(3);
    expect(washedList?.getAttribute("data-diff-label")).toBe("Updated by you");
  });

  it("format() applies ProseMirror commands and reports the active marks/blocks", () => {
    const ed = mount();
    ed.setText("hello\n");
    ed.setEditable(true);
    const view = viewOf(ed);
    view.dispatch(view.state.tr.setSelection(TextSelection.create(view.state.doc, 1, 6)));

    ed.format("bold");
    expect(ed.getText()).toBe("**hello**\n");
    expect(ed.activeFormats().has("bold")).toBe(true);

    ed.format("h1");
    expect(ed.getText().startsWith("# ")).toBe(true);
    expect(ed.activeFormats().has("h1")).toBe(true);
  });

  // T-100: a table cell's content model is inline*-only, so the block-structure toolbar commands can't
  // apply there — disabledFormats() reports them so the toolbar can disable the buttons instead of the
  // click silently no-op'ing.
  it("disabledFormats() reports block-structure commands as inapplicable inside a table cell", () => {
    const ed = mount();
    ed.setText("| a | b |\n| --- | --- |\n| x | y |\n");
    ed.setEditable(true);
    const view = viewOf(ed);
    // Caret inside the first header cell's text.
    view.dispatch(view.state.tr.setSelection(TextSelection.create(view.state.doc, 3)));

    const disabled = ed.disabledFormats();
    expect(disabled.has("h1")).toBe(true);
    expect(disabled.has("code")).toBe(true);
    expect(disabled.has("quote")).toBe(true);
    expect(disabled.has("bullet")).toBe(true);
    expect(disabled.has("bold")).toBe(false);
  });

  it("disabledFormats() reports nothing inapplicable in an ordinary paragraph", () => {
    const ed = mount();
    ed.setText("hello\n");
    ed.setEditable(true);
    const view = viewOf(ed);
    view.dispatch(view.state.tr.setSelection(TextSelection.create(view.state.doc, 1, 6)));

    expect(ed.disabledFormats().size).toBe(0);
  });

  it("round-trips strikethrough and applies it via format()", () => {
    const ed = mount();
    // parse path: ~~ becomes a strikethrough mark and round-trips byte-identical (verbatim).
    ed.setText("~~struck~~ text\n");
    expect(ed.getText()).toBe("~~struck~~ text\n");

    // serialize path: toggling strike wraps the selection in tildes.
    ed.setText("hi\n");
    ed.setEditable(true);
    const view = viewOf(ed);
    view.dispatch(view.state.tr.setSelection(TextSelection.create(view.state.doc, 1, 3)));
    ed.format("strike");
    expect(ed.getText()).toBe("~~hi~~\n");
  });

  it("converts a bullet list to ordered in place (no nesting) and back", () => {
    const ed = mount();
    ed.setText("- one\n- two\n");
    ed.setEditable(true);
    const view = viewOf(ed);
    view.dispatch(view.state.tr.setSelection(TextSelection.create(view.state.doc, 3)));

    ed.format("ordered");
    expect(ed.getText()).toBe("1. one\n2. two\n");
    ed.format("bullet");
    expect(ed.getText()).toBe("- one\n- two\n");
  });

  it("format() is blocked while read-only and offers a draft instead", () => {
    const host = document.createElement("div");
    document.body.appendChild(host);
    let attempts = 0;
    const ed = new FormattedEditor(host, {
      onChange: () => {},
      onEditAttempt: () => {
        attempts += 1;
      },
      onScroll: () => {},
      onCursor: () => {},
      onHover: () => {},
      onContentResize: () => {},
      onFocus: () => {},
      onActiveChange: () => {},
      onOpenLink: () => {},
    });
    ed.setText("hello\n");
    ed.setEditable(false);
    ed.format("bold");
    expect(attempts).toBe(1);
    expect(ed.getText()).toBe("hello\n");
  });

  it("read-only blocks edits and offers a draft; a draft accepts them", () => {
    const host = document.createElement("div");
    document.body.appendChild(host);
    let attempts = 0;
    const ed = new FormattedEditor(host, {
      onChange: () => {},
      onEditAttempt: () => {
        attempts += 1;
      },
      onScroll: () => {},
      onCursor: () => {},
      onHover: () => {},
      onContentResize: () => {},
      onFocus: () => {},
      onActiveChange: () => {},
      onOpenLink: () => {},
    });
    ed.setText("# H\n\npara\n");
    const view = viewOf(ed);

    // Read-only: a document edit is blocked, the source is unchanged, and a draft is offered.
    ed.setEditable(false);
    view.dispatch(view.state.tr.insertText("X", 1));
    expect(attempts).toBe(1);
    expect(ed.getText()).toBe("# H\n\npara\n");

    // Draft: the same edit now applies and changes the serialized Markdown.
    ed.setEditable(true);
    view.dispatch(view.state.tr.insertText("X", 1));
    expect(ed.getText()).not.toBe("# H\n\npara\n");
  });
});

// T-071: when the markdown-it source-block split and the ProseMirror doc disagree on top-level count (a
// parse divergence), the shared block-map (block-map.ts) reports NO entries, so every consumer degrades
// to a safe no-op — no geometry, no highlight — rather than pairing a source block with the wrong node
// and silently corrupting height-sync/scroll-sync. The divergence is also logged once as a diagnostic.
describe("FormattedEditor block-map divergence fallback (jsdom, T-071)", () => {
  // Force the divergence by desyncing the cached source-block split from the live doc: a 0-length split
  // against a non-empty ProseMirror doc is exactly the count mismatch the block-map guards. Test-only
  // internal access, isolated to this helper (the same private-field pattern as viewOf above).
  function desyncBlocks(ed: FormattedEditor): void {
    (ed as unknown as { blocks: MdBlock[] }).blocks = [];
  }

  it("reports no geometry and clears the highlight instead of mispairing when the split diverges", () => {
    const { ed, host } = mountWithHost();
    ed.setText("# H\n\npara\n\n- a\n- b\n");
    ed.setActiveLine(0);
    expect(host.querySelectorAll(".sd-active-block")).toHaveLength(1);
    expect(ed.blockGeometry().length).toBeGreaterThan(0);

    desyncBlocks(ed);
    // No blocks reported → height-sync's zero-block path clears its spacers (vs mispaired anchors).
    expect(ed.blockGeometry()).toEqual([]);
    // Re-pushing the highlight against the diverged map resolves no node → the decoration is cleared.
    ed.setActiveLine(0);
    expect(host.querySelectorAll(".sd-active-block")).toHaveLength(0);
  });

  it("logs the divergence once per occurrence (a diagnostic, not silent) and re-arms after it clears", () => {
    const warn = vi.spyOn(log, "warn").mockImplementation(() => {});
    try {
      const { ed } = mountWithHost();
      ed.setText("# H\n\npara\n");
      expect(warn).not.toHaveBeenCalled();

      desyncBlocks(ed);
      ed.blockGeometry();
      ed.blockGeometry(); // still diverged — must NOT re-log on every per-frame map build
      expect(warn).toHaveBeenCalledTimes(1);

      // A fresh setText re-pairs the split (blocks match the doc again), re-arming the guard so a later
      // divergence is diagnosed anew rather than swallowed.
      ed.setText("# H\n\npara\n");
      desyncBlocks(ed);
      ed.blockGeometry();
      expect(warn).toHaveBeenCalledTimes(2);
    } finally {
      warn.mockRestore();
    }
  });
});

// T-072: the scroll hot path (topVisibleSourceLine / scrollToSourceLine) reads block geometry from a
// scroll-invariant cache and binary-searches it, instead of measuring every block's DOM per frame. jsdom
// has no layout, so each block's getBoundingClientRect is stubbed with a fixed absolute top that scrolls
// with the pane — reproducing the content-relative-top invariance the cache relies on — and the pane's
// own getBoundingClientRect is spied to count how many times the geometry was actually measured.
describe("FormattedEditor block-geometry cache (jsdom, T-072)", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  function rect(top: number, height: number): DOMRect {
    return {
      top,
      height,
      bottom: top + height,
      left: 0,
      right: 0,
      width: 0,
      x: 0,
      y: top,
      toJSON: () => ({}),
    } as DOMRect;
  }

  // Give each rendered block a fixed ABSOLUTE top (cumulative heights) that scrolls with the pane, so the
  // editor's content-relative top (rect.top − containerTop + scrollTop) is scroll-INVARIANT — the property
  // that lets a single measurement serve every scroll frame. Returns the pane's getBoundingClientRect spy,
  // which is called exactly once per geometry measurement (measureBlocks reads containerTop from it).
  function stubGeometry(ed: FormattedEditor, host: HTMLElement, heights: number[]) {
    const measured = vi.spyOn(host, "getBoundingClientRect").mockReturnValue(rect(0, 0));
    const blocks = [...viewOf(ed).dom.children] as HTMLElement[];
    blocks.forEach((el, i) => {
      const absTop = heights.slice(0, i).reduce((sum, h) => sum + h, 0);
      vi.spyOn(el, "getBoundingClientRect").mockImplementation(() =>
        rect(absTop - host.scrollTop, heights[i] ?? 0),
      );
    });
    return measured;
  }

  // Four single-line paragraphs at source lines 0, 2, 4, 6; each rendered block 100px tall, so the
  // content-relative tops are 0, 100, 200, 300.
  const FOUR = "a\n\nb\n\nc\n\nd\n";

  it("topVisibleSourceLine binary-searches the cached geometry for the viewport-top line", () => {
    const { ed, host } = mountWithHost();
    ed.setText(FOUR);
    stubGeometry(ed, host, [100, 100, 100, 100]);

    host.scrollTop = 0;
    expect(ed.topVisibleSourceLine()).toBe(0);
    host.scrollTop = 150;
    expect(ed.topVisibleSourceLine()).toBe(2);
    host.scrollTop = 250;
    expect(ed.topVisibleSourceLine()).toBe(4);
    host.scrollTop = 350;
    expect(ed.topVisibleSourceLine()).toBe(6);
  });

  it("reuses one measurement across scroll frames and re-measures only after an invalidation", () => {
    const { ed, host } = mountWithHost();
    ed.setText(FOUR);
    const measured = stubGeometry(ed, host, [100, 100, 100, 100]);

    host.scrollTop = 250;
    expect(ed.topVisibleSourceLine()).toBe(4);
    expect(measured).toHaveBeenCalledTimes(1); // measured once, building the cache

    // A second scroll frame reuses the cached geometry — NO forced per-block layout measure (the T-072
    // hot-path guarantee). The cached tops are scroll-invariant, so they resolve the new scrollTop too.
    host.scrollTop = 50;
    expect(ed.topVisibleSourceLine()).toBe(0);
    expect(measured).toHaveBeenCalledTimes(1);

    // An edit relays the blocks out → the cache is invalidated → the next read re-measures.
    ed.setEditable(true);
    const view = viewOf(ed);
    view.dispatch(view.state.tr.insertText("X", 1));
    ed.topVisibleSourceLine();
    expect(measured).toHaveBeenCalledTimes(2);
  });

  it("scrollToSourceLine binary-searches the cached geometry and scrolls without forcing layout", () => {
    const { ed, host } = mountWithHost();
    ed.setText(FOUR);
    const measured = stubGeometry(ed, host, [100, 100, 100, 100]);

    ed.scrollToSourceLine(4); // block at line 4, content-relative top 200
    expect(host.scrollTop).toBe(200);
    expect(measured).toHaveBeenCalledTimes(1);

    ed.scrollToSourceLine(2); // cache hit — no re-measure
    expect(host.scrollTop).toBe(100);
    expect(measured).toHaveBeenCalledTimes(1);

    ed.scrollToSourceLine(0);
    expect(host.scrollTop).toBe(0);
  });

  it("blockGeometry always re-measures (the reconcile path) and refreshes the cache", () => {
    const { ed, host } = mountWithHost();
    ed.setText(FOUR);
    const measured = stubGeometry(ed, host, [100, 100, 100, 100]);

    const geometry = ed.blockGeometry();
    expect(geometry.map((block) => block.top)).toEqual([0, 100, 200, 300]);
    expect(measured).toHaveBeenCalledTimes(1);

    // A second reconcile re-measures unconditionally (it must read the freshest post-relayout geometry).
    ed.blockGeometry();
    expect(measured).toHaveBeenCalledTimes(2);

    // …but it left the fresh boxes cached, so a following scroll frame is a cache hit (no re-measure).
    host.scrollTop = 150;
    expect(ed.topVisibleSourceLine()).toBe(2);
    expect(measured).toHaveBeenCalledTimes(2);
  });
});

describe("FormattedEditor.hasPendingChange (jsdom, T-042)", () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("is true only while an edit's debounced onChange hasn't fired yet", () => {
    const ed = mount();
    ed.setText("hello\n");
    ed.setEditable(true);
    expect(ed.hasPendingChange()).toBe(false);

    const view = viewOf(ed);
    view.dispatch(view.state.tr.insertText("X", 1));
    expect(ed.hasPendingChange()).toBe(true);
    vi.advanceTimersByTime(119);
    expect(ed.hasPendingChange()).toBe(true);
    vi.advanceTimersByTime(1);
    expect(ed.hasPendingChange()).toBe(false);
  });

  it("stays false across a setText rebuild (setText uses updateState, not a dispatched transaction)", () => {
    const ed = mount();
    ed.setText("mirrored\n");
    expect(ed.hasPendingChange()).toBe(false);
  });
});

describe("FormattedEditor.mirror — the Split cross-pane sync (jsdom, T-097)", () => {
  it("keeps the caret/selection when a mirror edits a later block", () => {
    const { ed } = mountWithHost();
    ed.setEditable(true);
    // Blocks: heading (line 0), paragraph "para one" (line 2), paragraph "para two" (line 4).
    ed.setText("# H\n\npara one\n\npara two\n");
    const view = viewOf(ed);
    // Select "para one" (positions 4..12) — the FIRST paragraph, before the block the mirror changes.
    view.dispatch(view.state.tr.setSelection(TextSelection.create(view.state.doc, 4, 12)));
    expect(view.state.selection.from).toBe(4);
    expect(view.state.selection.to).toBe(12);

    ed.mirror("# H\n\npara one\n\npara TWO\n"); // only the SECOND paragraph changes

    expect(ed.getText()).toBe("# H\n\npara one\n\npara TWO\n");
    // The selection is untouched — the change lands entirely after it. A freshState rebuild (the old
    // setText mirror) would have reset the caret to the document start on every tick.
    expect(view.state.selection.from).toBe(4);
    expect(view.state.selection.to).toBe(12);
  });

  it("re-parses only the changed block, not the whole document, per mirror tick", async () => {
    const { parser } = await import("../../src/editors/pm-markdown.js");
    const { ed } = mountWithHost();
    ed.setText("# H\n\npara one\n\npara two\n\npara three\n");

    const spy = vi.spyOn(parser, "parse");
    ed.mirror("# H\n\npara one\n\npara CHANGED\n\npara three\n"); // only the middle paragraph differs

    // Exactly one parse, and it saw ONLY the changed block's source — never the (longer) whole document.
    // This is the per-tick whole-document re-parse the task set out to eliminate.
    expect(spy).toHaveBeenCalledTimes(1);
    const parsedArg = spy.mock.calls[0]?.[0] ?? "";
    expect(parsedArg).toContain("CHANGED");
    expect(parsedArg).not.toContain("para one");
    expect(parsedArg).not.toContain("para three");
    spy.mockRestore();
  });

  it("mirrors a document with a link reference definition correctly (full-rebuild fallback)", () => {
    const { ed } = mountWithHost();
    ed.setText("[a]: http://example.com\n\nSee [a] here.\n");
    // Sanity: the reference-style link round-trips byte-identically before the mirror.
    expect(ed.getText()).toBe("[a]: http://example.com\n\nSee [a] here.\n");

    ed.mirror("[a]: http://example.com\n\nSee [a] there.\n");

    // The cross-block reference forces the full-rebuild fallback (an isolated slice parse could not
    // resolve the definition kept in another block); the content is still mirrored correctly.
    expect(ed.getText()).toBe("[a]: http://example.com\n\nSee [a] there.\n");
  });

  describe("history + silence (fake timers)", () => {
    beforeEach(() => {
      vi.useFakeTimers();
    });
    afterEach(() => {
      vi.useRealTimers();
    });

    it("keeps the undo history of an earlier in-pane edit across a source-pane mirror", () => {
      const { ed } = mountWithHost();
      ed.setEditable(true);
      ed.setText("# H\n\npara one\n");
      const view = viewOf(ed);
      const dispatch = view.dispatch.bind(view);

      // A real edit the author made IN the formatted pane — a genuine ProseMirror history entry.
      view.dispatch(view.state.tr.insertText("X", 1)); // heading "H" → "XH"
      // Flush the debounce so the block map refreshes, exactly as the pending-change guard in index.ts
      // guarantees before any real mirror is allowed through.
      vi.advanceTimersByTime(120);
      expect(ed.getText()).toBe("# XH\n\npara one\n");

      // A source-pane edit to a DIFFERENT block mirrors in (its text carries the in-pane "X" already).
      ed.mirror("# XH\n\npara two\n");
      expect(ed.getText()).toBe("# XH\n\npara two\n");

      // The history survived the mirror: a freshState rebuild would have reset undoDepth to 0 and made
      // undo a no-op. It is still non-empty, and undo still reverts a change.
      expect(undoDepth(view.state)).toBeGreaterThan(0);
      const beforeUndo = ed.getText();
      expect(undo(view.state, dispatch)).toBe(true);
      expect(ed.getText()).not.toBe(beforeUndo);
    });

    it("mirrors the content in and stays silent (no change notification round-trips out)", () => {
      let reported = "";
      const host = document.createElement("div");
      document.body.appendChild(host);
      const ed = new FormattedEditor(host, {
        onChange: (text) => {
          reported = text;
        },
        onEditAttempt: () => {},
        onScroll: () => {},
        onCursor: () => {},
        onHover: () => {},
        onContentResize: () => {},
        onFocus: () => {},
        onActiveChange: () => {},
        onOpenLink: () => {},
      });
      ed.setText("# H\n\nfirst\n\nsecond\n");

      ed.mirror("# H\n\nfirst EDITED\n\nsecond\n");

      expect(ed.getText()).toBe("# H\n\nfirst EDITED\n\nsecond\n");
      // A mirror must not re-fire as this pane's own edit — no debounced onChange ever runs.
      vi.advanceTimersByTime(500);
      expect(reported).toBe("");
    });
  });
});
