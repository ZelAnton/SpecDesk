import { EditorState, TextSelection } from "prosemirror-state";
import { describe, expect, it } from "vitest";
import type { FormatCommand } from "../../src/editors/md-format.js";
import { activeFormats, commandFor, disabledFormats } from "../../src/editors/pm-commands.js";
import { parser, schema, serializer } from "../../src/editors/pm-markdown.js";

// These exercise the extracted toolbar commands directly against a bare EditorState — no DOM, no
// EditorView. The end-to-end path (FormattedEditor.format / activeFormats) is covered separately in
// formatted.test.ts; here we pin the command logic itself, including the toggle-off and the
// list-conversion edge cases.

function stateFrom(markdown: string): EditorState {
  return EditorState.create({ doc: parser.parse(markdown), schema });
}

function select(state: EditorState, from: number, to: number = from): EditorState {
  return state.apply(state.tr.setSelection(TextSelection.create(state.doc, from, to)));
}

function run(state: EditorState, command: FormatCommand): EditorState {
  let next = state;
  commandFor(command)(state, (tr) => {
    next = state.apply(tr);
  });
  return next;
}

/** The document serialized back to Markdown, trimmed of the serializer's trailing newline. */
function md(state: EditorState): string {
  return serializer.serialize(state.doc).trim();
}

describe("commandFor — inline marks", () => {
  it("wraps the selection in the corresponding mark", () => {
    const sel = select(stateFrom("hello\n"), 1, 6);
    expect(md(run(sel, "bold"))).toBe("**hello**");
    expect(md(run(sel, "italic"))).toBe("*hello*");
    expect(md(run(sel, "strike"))).toBe("~~hello~~");
  });

  it("toggles a mark off when the selection already carries it", () => {
    const bolded = select(stateFrom("**hello**\n"), 1, 6);
    expect(md(run(bolded, "bold"))).toBe("hello");
  });
});

describe("commandFor — blocks", () => {
  it("sets a heading and toggles it back to a paragraph", () => {
    const para = select(stateFrom("hello\n"), 1, 6);
    const h1 = run(para, "h1");
    expect(md(h1)).toBe("# hello");
    // Re-running the same block command on an active block reverts it to a paragraph.
    expect(md(select(h1, 1, 6))).toBe("# hello");
    expect(md(run(select(h1, 1, 6), "h1"))).toBe("hello");
  });

  it("distinguishes h1 from h2", () => {
    const para = select(stateFrom("hello\n"), 1, 6);
    expect(md(run(para, "h2"))).toBe("## hello");
  });

  it("turns a paragraph into a code block", () => {
    const para = select(stateFrom("hello\n"), 1, 6);
    expect(md(run(para, "code"))).toBe("```\nhello\n```");
  });

  it("toggles a blockquote on and off", () => {
    const para = select(stateFrom("hello\n"), 1, 6);
    const quoted = run(para, "quote");
    expect(md(quoted)).toBe("> hello");
    expect(md(run(select(quoted, 2, 7), "quote"))).toBe("hello");
  });

  // T-090 regression guard: toggleQuote used to lift with the generic prosemirror-commands `lift`,
  // which lifts whichever ancestor is CLOSEST to liftable — for a list nested inside a blockquote,
  // that's the list itself, so toggling quote off a quoted list item used to strip the LIST and keep
  // the quote ("> - a" -> "> a") instead of stripping the quote and keeping the list. Targeting the
  // blockquote specifically (via blockRange's predicate) fixes it to match the Code tract's own
  // (always outer-marker-only) line-prefix toggle.
  it("toggling quote off a quoted list item strips only the quote, keeping the list (T-090)", () => {
    const quotedList = select(stateFrom("> - a\n"), 5);
    expect(md(run(quotedList, "quote"))).toBe("- a");
  });
});

describe("commandFor — lists", () => {
  it("wraps a paragraph in a bullet or ordered list", () => {
    const para = select(stateFrom("hello\n"), 1, 6);
    expect(md(run(para, "bullet"))).toBe("- hello");
    expect(md(run(para, "ordered"))).toBe("1. hello");
  });

  it("converts a bullet list to an ordered list in place (no nesting)", () => {
    // Cursor inside the single list item.
    const bullet = select(stateFrom("- hello\n"), 3);
    const ordered = run(bullet, "ordered");
    expect(md(ordered)).toBe("1. hello");
    // One top-level list node, not a list nested inside a list.
    expect(ordered.doc.childCount).toBe(1);
    expect(ordered.doc.child(0).type).toBe(schema.nodes.ordered_list);
  });

  it("lifts the item out of the list when toggling the same list type", () => {
    const bullet = select(stateFrom("- hello\n"), 3);
    expect(md(run(bullet, "bullet"))).toBe("hello");
  });
});

describe("activeFormats", () => {
  it("reports active inline marks", () => {
    const bolded = select(stateFrom("**hello**\n"), 1, 6);
    expect(activeFormats(bolded).has("bold")).toBe(true);
    expect(activeFormats(bolded).has("italic")).toBe(false);
  });

  it("reports h1 and h2 but leaves H3–H6 unpressed", () => {
    expect(activeFormats(select(stateFrom("# h\n"), 1)).has("h1")).toBe(true);
    expect(activeFormats(select(stateFrom("## h\n"), 1)).has("h2")).toBe(true);
    const h3 = activeFormats(select(stateFrom("### h\n"), 1));
    expect(h3.has("h1")).toBe(false);
    expect(h3.has("h2")).toBe(false);
  });

  it("reports the active block kind for code, lists and quote", () => {
    expect(activeFormats(select(stateFrom("```\nx\n```\n"), 2)).has("code")).toBe(true);
    expect(activeFormats(select(stateFrom("- x\n"), 3)).has("bullet")).toBe(true);
    expect(activeFormats(select(stateFrom("1. x\n"), 3)).has("ordered")).toBe(true);
    expect(activeFormats(select(stateFrom("> x\n"), 3)).has("quote")).toBe(true);
  });

  it("reports every ancestor block kind at a nested selection", () => {
    // A bullet item inside a blockquote lights up BOTH buttons — the point of the depth scan.
    const nested = activeFormats(select(stateFrom("> - hello\n"), 5));
    expect(nested.has("quote")).toBe(true);
    expect(nested.has("bullet")).toBe(true);
  });
});

// T-100: the pressed criterion must match what a click actually does. commandFor's inline case now uses
// `removeWhenPresent: false`, so a partially-marked selection ADDS the mark across the whole selection
// (widens) rather than stripping it everywhere — activeFormats must therefore report "pressed" only once
// the mark covers the WHOLE selection, not merely somewhere in it (the old rangeHasMark reading), so a
// click on an unpressed partially-marked selection never surprises the author by making it MORE marked.
describe("activeFormats — pressed matches the WHOLE-selection toggleMark criterion (T-100)", () => {
  it("a partially-bold selection is NOT pressed", () => {
    const partial = select(stateFrom("**he**llo\n"), 1, 6); // "hello": "he" bold, "llo" plain
    expect(activeFormats(partial).has("bold")).toBe(false);
  });

  it("a fully-bold selection IS pressed", () => {
    const full = select(stateFrom("**hello**\n"), 1, 6);
    expect(activeFormats(full).has("bold")).toBe(true);
  });

  it("clicking bold on a partially-bold selection ADDS bold across the whole selection", () => {
    const partial = select(stateFrom("**he**llo\n"), 1, 6);
    expect(md(run(partial, "bold"))).toBe("**hello**");
  });

  it("clicking bold on a fully-bold selection removes it (unchanged toggle-off behaviour)", () => {
    const full = select(stateFrom("**hello**\n"), 1, 6);
    expect(md(run(full, "bold"))).toBe("hello");
  });
});

describe("disabledFormats — commands the schema can't apply at the selection (T-100)", () => {
  // A table_cell's content model is `inline*` (no block children at all — a cell holds inline content
  // directly, with no wrapping paragraph), so none of the block-structure commands can replace/wrap it.
  const TABLE = "| a | b |\n| --- | --- |\n| x | y |\n";

  it("disables setBlockType(heading/code_block) and the list/quote toggles inside a table cell", () => {
    const cell = select(stateFrom(TABLE), 3);
    const disabled = disabledFormats(cell);
    expect(disabled.has("h1")).toBe(true);
    expect(disabled.has("h2")).toBe(true);
    expect(disabled.has("code")).toBe(true);
    expect(disabled.has("quote")).toBe(true);
    expect(disabled.has("bullet")).toBe(true);
    expect(disabled.has("ordered")).toBe(true);
  });

  it("does not disable inline marks inside a table cell (inline content is exactly what it holds)", () => {
    const cell = select(stateFrom(TABLE), 3);
    expect(disabledFormats(cell).has("bold")).toBe(false);
    expect(disabledFormats(cell).has("italic")).toBe(false);
  });

  it("disables nothing in an ordinary paragraph", () => {
    const para = select(stateFrom("hello\n"), 1, 6);
    expect(disabledFormats(para).size).toBe(0);
  });

  it("does not disable block-structure commands inside a list item (its block+ content allows nesting)", () => {
    const item = select(stateFrom("- hello\n"), 3);
    const disabled = disabledFormats(item);
    expect(disabled.has("h1")).toBe(false);
    expect(disabled.has("code")).toBe(false);
    expect(disabled.has("quote")).toBe(false);
  });
});
