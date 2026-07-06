// @vitest-environment jsdom
import { TextSelection } from "prosemirror-state";
import type { EditorView } from "prosemirror-view";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { FormattedEditor } from "../src/formatted.js";

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
      { kind: "changed", lineStart: 0, lineEnd: 0, anchorLine: -1, removedText: "" },
      { kind: "added", lineStart: 2, lineEnd: 2, anchorLine: -1, removedText: "" },
      { kind: "moved", lineStart: 4, lineEnd: 4, anchorLine: -1, removedText: "" },
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
    ed.setDiff([
      { kind: "removed", lineStart: 0, lineEnd: 0, anchorLine: 1, removedText: "gone block" },
    ]);

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
    ed.setDiff([
      { kind: "removed", lineStart: 0, lineEnd: 0, anchorLine: 0, removedText: "was first" },
    ]);
    let marker = host.querySelector(".sd-diff-removed-marker");
    const h1 = heading();
    expect(marker && h1 && follows(marker, h1)).toBe(true);

    // Deleted after all head content (anchor past the last block) → the marker follows the last block.
    ed.setDiff([
      { kind: "removed", lineStart: 0, lineEnd: 0, anchorLine: 9, removedText: "was last" },
    ]);
    marker = host.querySelector(".sd-diff-removed-marker");
    const p = kept();
    expect(marker && p && follows(p, marker)).toBe(true);
  });

  it("a sub-block mark washes the individual list item, not the whole list", () => {
    const { ed, host } = mountWithHost();
    ed.setText("- one\n- two\n- three\n"); // items at lines 0, 1, 2

    // A changed row/item mark (sub) on the second item's line.
    ed.setDiff([
      { kind: "changed", lineStart: 1, lineEnd: 1, anchorLine: -1, removedText: "", sub: true },
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
    ed.setDiff([
      {
        kind: "removed",
        lineStart: 0,
        lineEnd: 0,
        anchorLine: 1,
        removedText: "gone item",
        sub: true,
      },
    ]);

    const marker = host.querySelector(".sd-diff-removed-marker");
    const items = [...host.querySelectorAll("li")];
    const one = items.find((li) => li.textContent?.includes("one"));
    const two = items.find((li) => li.textContent?.includes("two"));
    expect(marker?.textContent).toContain("gone item");
    expect(one && marker && follows(one, marker)).toBe(true); // after the first item
    expect(marker && two && follows(marker, two)).toBe(true); // before the second item
  });

  it("highlights changed words inside a changed list item (no whole-item wash, no pill)", () => {
    const { ed, host } = mountWithHost();
    // A long second item with a single word changed, so the diff stays under the inline threshold.
    ed.setText("- one\n- buy fresh apples from the market today\n- three\n");
    ed.setDiff([
      {
        kind: "changed",
        lineStart: 1,
        lineEnd: 1,
        anchorLine: -1,
        removedText: "",
        sub: true,
        baseText: "buy fresh oranges from the market today",
      },
    ]);

    const added = [...host.querySelectorAll(".sd-diff-word-added")];
    expect(added.some((w) => w.textContent?.includes("apples"))).toBe(true);
    // No whole-item wash and no annotation pill — the inline word highlight is the signal.
    expect(host.querySelectorAll(".sd-diff-changed")).toHaveLength(0);
    expect(host.querySelectorAll(".sd-diff-inline")).toHaveLength(0);
  });

  it("highlights changed words inline inside an edited paragraph (no whole-block wash)", () => {
    const { ed, host } = mountWithHost();
    ed.setText("The quick brown fox jumps over the lazy dog today.\n");
    // Same paragraph, one word different in the base — inline word-diff should mark just that word.
    ed.setDiff([
      {
        kind: "changed",
        lineStart: 0,
        lineEnd: 0,
        anchorLine: -1,
        removedText: "",
        baseText: "The quick brown fox leaps over the lazy dog today.",
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
        lineStart: 0,
        lineEnd: 0,
        anchorLine: -1,
        removedText: "",
        baseText: "alpha beta gamma delta",
      },
    ]);

    // Most of the paragraph changed → no inline words; the whole block washes as "edited".
    expect(host.querySelectorAll(".sd-diff-word-added")).toHaveLength(0);
    expect(host.querySelectorAll(".sd-diff-changed")).toHaveLength(1);
    expect(host.querySelector(".sd-diff-changed")?.getAttribute("data-diff-label")).toBe(
      "Updated by you",
    );
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
