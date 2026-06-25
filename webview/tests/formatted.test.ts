// @vitest-environment jsdom
import { TextSelection } from "prosemirror-state";
import type { EditorView } from "prosemirror-view";
import { describe, expect, it } from "vitest";
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

function mount(): FormattedEditor {
  const host = document.createElement("div");
  document.body.appendChild(host);
  return new FormattedEditor(host, {
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
  ] as const) {
    it(`setText then getText is byte-identical with no edits: ${name}`, () => {
      const ed = mount();
      ed.setText(md);
      expect(ed.getText()).toBe(md);
    });
  }

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

  it("format() applies ProseMirror commands and reports the active marks/blocks", () => {
    const ed = mount();
    ed.setText("hello\n");
    ed.setEditable(true);
    const view = (ed as unknown as { view: EditorView }).view;
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
    const view = (ed as unknown as { view: EditorView }).view;
    view.dispatch(view.state.tr.setSelection(TextSelection.create(view.state.doc, 1, 3)));
    ed.format("strike");
    expect(ed.getText()).toBe("~~hi~~\n");
  });

  it("converts a bullet list to ordered in place (no nesting) and back", () => {
    const ed = mount();
    ed.setText("- one\n- two\n");
    ed.setEditable(true);
    const view = (ed as unknown as { view: EditorView }).view;
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
    const view = (ed as unknown as { view: EditorView }).view;

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
