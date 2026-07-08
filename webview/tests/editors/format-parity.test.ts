import { EditorState, TextSelection } from "prosemirror-state";
import { describe, expect, it } from "vitest";
import { type FormatCommand, formatMarkdown } from "../../src/editors/md-format.js";
import { commandFor } from "../../src/editors/pm-commands.js";
import { parser, schema, serializer } from "../../src/editors/pm-markdown.js";

/**
 * T-090: the Code (source) and Formatted (ProseMirror) toolbar tracts are implemented independently —
 * line-based text transforms in md-format.ts versus structural ProseMirror commands in pm-commands.ts —
 * so the same toolbar button used to emit different Markdown depending on which panel had focus. These
 * pairs run the SAME logical command over equivalent selections through each tract and assert they
 * serialize to identical Markdown, pinning the canonical semantics both now share (see the T-090
 * regression describes in md-format.test.ts and pm-commands.test.ts for the per-tract bug fixes).
 */

/** The Code tract's result: apply the edit and return the whole resulting document. */
function codeResult(doc: string, command: FormatCommand, from: number, to: number): string {
  const edit = formatMarkdown(doc, from, to, command);
  return (doc.slice(0, edit.from) + edit.insert + doc.slice(edit.to)).trim();
}

/** The Formatted tract's result: run the PM command over a selection, then serialize back to Markdown. */
function pmResult(markdown: string, command: FormatCommand, from: number, to: number): string {
  const state = EditorState.create({ doc: parser.parse(markdown), schema });
  const selected = state.apply(state.tr.setSelection(TextSelection.create(state.doc, from, to)));
  let next = selected;
  commandFor(command)(selected, (tr) => {
    next = selected.apply(tr);
  });
  return serializer.serialize(next.doc).trim();
}

describe("format-parity — Code and Formatted tracts agree on the resulting Markdown (T-090)", () => {
  it("bullet on an ordered-list line converts it, both tracts identical", () => {
    const expected = "- hello";
    expect(codeResult("1. hello", "bullet", 0, 8)).toBe(expected);
    expect(pmResult("1. hello\n", "bullet", 3, 3)).toBe(expected);
  });

  it("ordered on a bulleted-list line converts it, both tracts identical", () => {
    const expected = "1. hello";
    expect(codeResult("- hello", "ordered", 0, 7)).toBe(expected);
    expect(pmResult("- hello\n", "ordered", 3, 3)).toBe(expected);
  });

  it("h1 on a bullet list line nests the heading after the marker, both tracts identical", () => {
    const expected = "- # hello";
    expect(codeResult("- hello", "h1", 0, 7)).toBe(expected);
    expect(pmResult("- hello\n", "h1", 3, 3)).toBe(expected);
  });

  it("h1 on a quoted line nests the heading after the quote marker, both tracts identical", () => {
    const expected = "> # Title";
    expect(codeResult("> Title", "h1", 0, 7)).toBe(expected);
    expect(pmResult("> Title\n", "h1", 3, 3)).toBe(expected);
  });

  it("h1 on a quoted list item nests after both containers (quote outermost), both tracts identical", () => {
    const expected = "> - # hello";
    expect(codeResult("> - hello", "h1", 0, 9)).toBe(expected);
    expect(pmResult("> - hello\n", "h1", 5, 5)).toBe(expected);
  });

  it("code fences a heading line's bare text (no '#'), both tracts identical", () => {
    const expected = "```\nTitle\n```";
    expect(codeResult("# Title", "code", 0, 7)).toBe(expected);
    expect(pmResult("# Title\n", "code", 1, 6)).toBe(expected);
  });

  it("quote toggled off a quoted list item strips only the quote, both tracts identical", () => {
    const expected = "- a";
    expect(codeResult("> - a", "quote", 0, 5)).toBe(expected);
    expect(pmResult("> - a\n", "quote", 5, 5)).toBe(expected);
  });

  it("h1 over a soft-wrapped multi-line paragraph produces one heading, both tracts identical", () => {
    const expected = "# line one line two";
    expect(codeResult("line one\nline two", "h1", 0, 17)).toBe(expected);
    expect(pmResult("line one\nline two\n", "h1", 1, 18)).toBe(expected);
  });
});
