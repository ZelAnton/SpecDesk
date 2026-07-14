import { describe, expect, it } from "vitest";
import {
  type FormatCommand,
  formatMarkdown,
  isFormatCommand,
} from "../../src/editors/md-format.js";

describe("isFormatCommand (data-format DOM boundary)", () => {
  it("accepts every known command and rejects anything else", () => {
    for (const command of [
      "bold",
      "italic",
      "strike",
      "inlineCode",
      "h1",
      "h2",
      "h3",
      "bullet",
      "ordered",
      "quote",
      "code",
      "link",
      "table",
      "image",
      "rule",
    ]) {
      expect(isFormatCommand(command)).toBe(true);
    }
    expect(isFormatCommand("underline")).toBe(false);
    expect(isFormatCommand("")).toBe(false);
    expect(isFormatCommand(undefined)).toBe(false);
  });
});

/** Apply a toolbar command and return the resulting document + selection (for terse assertions). */
function apply(
  doc: string,
  command: FormatCommand,
  from: number,
  to = from,
): { text: string; sel: [number, number] } {
  const e = formatMarkdown(doc, from, to, command);
  return {
    text: doc.slice(0, e.from) + e.insert + doc.slice(e.to),
    sel: [e.selectionStart, e.selectionEnd],
  };
}

describe("formatMarkdown — inline marks", () => {
  it("wraps a selection in bold and selects the inner text", () => {
    expect(apply("hello", "bold", 0, 5)).toEqual({ text: "**hello**", sel: [2, 7] });
  });

  it("unwraps when the markers sit just outside the selection", () => {
    expect(apply("**hello**", "bold", 2, 7)).toEqual({ text: "hello", sel: [0, 5] });
  });

  it("unwraps when the selection includes the markers", () => {
    expect(apply("**hello**", "bold", 0, 9)).toEqual({ text: "hello", sel: [0, 5] });
  });

  it("inserts empty markers with the caret between them", () => {
    expect(apply("", "bold", 0)).toEqual({ text: "****", sel: [2, 2] });
  });

  it("italic uses single asterisks", () => {
    expect(apply("x", "italic", 0, 1)).toEqual({ text: "*x*", sel: [1, 2] });
  });

  it("strike uses tildes", () => {
    expect(apply("x", "strike", 0, 1)).toEqual({ text: "~~x~~", sel: [2, 3] });
  });

  it("inline code uses backticks", () => {
    expect(apply("value", "inlineCode", 0, 5)).toEqual({ text: "`value`", sel: [1, 6] });
  });

  it("inline code chooses a longer delimiter around content that contains a backtick", () => {
    expect(apply("a`b", "inlineCode", 0, 3).text).toBe("`` a`b ``");
  });

  it("nests italic inside bold instead of stripping a bold asterisk", () => {
    // Select the inner text of **hello** and toggle italic → ***hello*** (not *hello*).
    expect(apply("**hello**", "italic", 2, 7)).toEqual({ text: "***hello***", sel: [3, 8] });
  });

  it("still unwraps a real bold selection (exact marker)", () => {
    expect(apply("**hello**", "bold", 2, 7)).toEqual({ text: "hello", sel: [0, 5] });
  });
});

// T-091 regression guards: `toggleInline` used to wrap the raw selection as-is, which produced
// Markdown that does not render — a trailing/leading space defeats CommonMark's flanking rule
// (`**word **` stays literal), emphasis was allowed to straddle a paragraph break, and a partial
// selection inside an existing wrapper nested to `****foo** bar**`. The fix expels edge whitespace,
// wraps per paragraph across a blank line, and detects the enclosing wrapper on the parsed syntax tree.
describe("formatMarkdown — inline edge cases (T-091)", () => {
  describe("edge whitespace is expelled so the markers hug the text", () => {
    it("pulls a trailing space out of a bold wrap", () => {
      expect(apply("word ", "bold", 0, 5)).toEqual({ text: "**word** ", sel: [2, 6] });
    });

    it("pulls a leading space out of a bold wrap", () => {
      expect(apply(" word", "bold", 0, 5)).toEqual({ text: " **word**", sel: [3, 7] });
    });

    it("pulls spaces off both ends", () => {
      expect(apply(" word ", "bold", 0, 6)).toEqual({ text: " **word** ", sel: [3, 7] });
    });

    it("expels a space for italic too", () => {
      expect(apply("x ", "italic", 0, 2)).toEqual({ text: "*x* ", sel: [1, 2] });
    });

    it("expels a space for strike too", () => {
      expect(apply("x ", "strike", 0, 2)).toEqual({ text: "~~x~~ ", sel: [2, 3] });
    });

    it("expels a trailing newline (block boundary) from the wrap", () => {
      expect(apply("word\n", "bold", 0, 5)).toEqual({ text: "**word**\n", sel: [2, 6] });
    });

    it("keeps the markers inside the spaces mid-document", () => {
      expect(apply("a word b", "bold", 2, 7)).toEqual({ text: "a **word** b", sel: [4, 8] });
    });

    it("leaves an all-whitespace selection unchanged (no invalid `** **`)", () => {
      expect(apply("   ", "bold", 0, 3)).toEqual({ text: "   ", sel: [0, 3] });
    });
  });

  describe("a selection crossing a blank line wraps each paragraph on its own", () => {
    it("bolds both paragraphs without spanning the blank line", () => {
      expect(apply("para one\n\npara two", "bold", 0, 18).text).toBe(
        "**para one**\n\n**para two**",
      );
    });

    it("keeps the blank-line separator verbatim between the wraps", () => {
      // Selecting from mid-first-paragraph to mid-second must not emit a marker across the break.
      expect(apply("para one\n\npara two", "bold", 5, 18).text).toBe(
        "para **one**\n\n**para two**",
      );
    });

    it("does NOT split a soft line break (single newline stays one paragraph)", () => {
      expect(apply("a\nb", "bold", 0, 3)).toEqual({ text: "**a\nb**", sel: [2, 5] });
    });
  });

  describe("a partial selection inside an existing wrapper toggles it off, never nests", () => {
    it("unwraps the whole bold when only part of it is selected (no `****foo** bar**`)", () => {
      expect(apply("**foo bar**", "bold", 2, 5)).toEqual({ text: "foo bar", sel: [0, 3] });
    });

    it("unwraps regardless of which word inside the wrapper is selected", () => {
      expect(apply("**foo bar**", "bold", 6, 9)).toEqual({ text: "foo bar", sel: [4, 7] });
    });

    it("detection is by tree, not by markers touching the selection edges", () => {
      // The selection edges are nowhere near the `**`, yet the enclosing bold is still found.
      const { text } = apply("lead **foo bar baz** tail", "bold", 10, 13);
      expect(text).toBe("lead foo bar baz tail");
    });

    it("removes the inner bold of ***hello*** when bold is toggled on the inner text", () => {
      expect(apply("***hello***", "bold", 3, 8)).toEqual({ text: "*hello*", sel: [1, 6] });
    });

    it("removes the outer italic of ***hello*** when italic is toggled on the inner text", () => {
      expect(apply("***hello***", "italic", 3, 8)).toEqual({ text: "**hello**", sel: [2, 7] });
    });

    it("still nests italic inside an unrelated bold wrapper (different mark type)", () => {
      // Italic toggled inside a bold span has no enclosing *italic* node → it wraps, nesting cleanly.
      expect(apply("**foo bar**", "italic", 2, 5).text).toBe("***foo* bar**");
    });
  });
});

describe("formatMarkdown — block prefixes", () => {
  it("adds an H1 prefix", () => {
    expect(apply("Title", "h1", 0, 5).text).toBe("# Title");
  });

  it("toggles an H1 prefix off", () => {
    expect(apply("# Title", "h1", 0, 7).text).toBe("Title");
  });

  it("changes H1 to H2", () => {
    expect(apply("# Title", "h2", 0, 7).text).toBe("## Title");
  });

  it("changes H1 to H3", () => {
    expect(apply("# Title", "h3", 0, 7).text).toBe("### Title");
  });

  it("inserts link and image placeholders with the target selected", () => {
    expect(apply("guide", "link", 0, 5)).toEqual({
      text: "[guide](https://)",
      sel: [8, 16],
    });
    expect(apply("diagram", "image", 0, 7)).toEqual({
      text: "![diagram](images/image.png)",
      sel: [11, 27],
    });
  });

  it("removes an existing link instead of nesting another link", () => {
    expect(apply("[guide](https://example.com)", "link", 2, 5)).toEqual({
      text: "guide",
      sel: [0, 5],
    });
  });

  it("inserts a divider as its own block", () => {
    expect(apply("before after", "rule", 7).text).toBe("---\n\nbefore after");
  });

  it("inserts a starter table before the current block", () => {
    expect(apply("after", "table", 2).text).toBe(
      "| Column 1 | Column 2 |\n| --- | --- |\n| Value | Value |\n\nafter",
    );
  });

  it("adds bullets to each selected line", () => {
    expect(apply("a\nb", "bullet", 0, 3).text).toBe("- a\n- b");
  });

  it("removes bullets when every line already has them", () => {
    expect(apply("- a\n- b", "bullet", 0, 7).text).toBe("a\nb");
  });

  it("numbers an ordered list sequentially", () => {
    expect(apply("a\nb\nc", "ordered", 0, 5).text).toBe("1. a\n2. b\n3. c");
  });

  it("adds a blockquote prefix per line", () => {
    expect(apply("a\nb", "quote", 0, 3).text).toBe("> a\n> b");
  });

  // M-33 regression guard: toggling a line prefix over a MIXED selection (some lines already carry
  // the prefix, some don't) used to double the prefix on the already-prefixed lines instead of
  // normalizing every line to the same prefix — the "all or nothing" `has()` check only decided
  // whether to strip at all, so lines that already had a prefix got a second one prepended.
  describe("mixed-prefix selections normalize instead of doubling (M-33)", () => {
    it("ordered list: re-numbers instead of doubling an existing '1. ' prefix", () => {
      expect(apply("1. a\nb", "ordered", 0, 6).text).toBe("1. a\n2. b");
    });

    it("bullet list: normalizes instead of doubling an existing '- ' prefix", () => {
      expect(apply("- a\nb", "bullet", 0, 5).text).toBe("- a\n- b");
    });

    it("blockquote: normalizes instead of doubling an existing '> ' prefix", () => {
      expect(apply("> a\nb", "quote", 0, 5).text).toBe("> a\n> b");
    });
  });

  it("wraps a selection in a fenced code block", () => {
    expect(apply("x = 1", "code", 0, 5).text).toBe("```\nx = 1\n```");
  });

  it("unwraps an already-fenced selection", () => {
    expect(apply("```\nx = 1\n```", "code", 0, 13).text).toBe("x = 1");
  });

  // T-093 regression guards: toggleFence used to recognize "already a fence" only by checking
  // whether the SELECTION's own first/last lines started with ``` — so a selection of lines INSIDE
  // an existing fence (never touching its own delimiter lines) was invisible to that heuristic and
  // got wrapped in a nested fence, whose inner ``` prematurely closed the outer one and corrupted
  // everything after. The fix detects the enclosing FencedCode syntax node instead.
  describe("fenced code — detects an existing fence by syntax tree, not by selection edges (T-093)", () => {
    it("unwraps the whole fence when only an interior line is selected", () => {
      const doc = "```\nline one\nline two\n```";
      // Select just "line one" (an interior line, nowhere near the ``` delimiters).
      const from = doc.indexOf("line one");
      const to = from + "line one".length;
      expect(apply(doc, "code", from, to).text).toBe("line one\nline two");
    });

    it("unwraps regardless of which interior line the selection sits in", () => {
      const doc = "```\nline one\nline two\n```";
      const from = doc.indexOf("line two");
      const to = from + "line two".length;
      expect(apply(doc, "code", from, to).text).toBe("line one\nline two");
    });

    it("unwraps a caret placed inside the fence with no selection", () => {
      const doc = "```\nx = 1\n```";
      const at = doc.indexOf("x = 1");
      expect(apply(doc, "code", at, at).text).toBe("x = 1");
    });

    it("recognizes a ~~~ fence the same way", () => {
      const doc = "~~~\nline one\nline two\n~~~";
      const from = doc.indexOf("line one");
      const to = from + "line one".length;
      expect(apply(doc, "code", from, to).text).toBe("line one\nline two");
    });

    it("recognizes a fence with an info string", () => {
      const doc = "```js\nconst x = 1;\n```";
      const from = doc.indexOf("const x");
      const to = from + "const x = 1;".length;
      expect(apply(doc, "code", from, to).text).toBe("const x = 1;");
    });

    it("unwraps an empty fence", () => {
      expect(apply("```\n```", "code", 4, 4).text).toBe("");
    });

    it("does not corrupt the document below the selection when the selection spans the whole fence plus a following paragraph", () => {
      const doc = "```\nx = 1\n```\npara after";
      const { text } = apply(doc, "code", 0, doc.length);
      // Not contained by the FencedCode node → wraps instead of unwrapping. The wrap must use a
      // marker LONGER than the embedded ``` so the inner fence can't prematurely close the outer one.
      expect(text.startsWith("````\n")).toBe(true);
      expect(text.endsWith("\n````")).toBe(true);
      // The embedded fence and trailing paragraph must survive completely intact inside the wrap.
      expect(text).toContain("```\nx = 1\n```\npara after");
    });

    it("still wraps plain (non-fenced) lines in a fresh ``` fence", () => {
      expect(apply("a\nb", "code", 0, 3).text).toBe("```\na\nb\n```");
    });
  });

  it("operates on the whole line even when only part is selected", () => {
    // caret inside "Title", no selection → the whole line gets the heading prefix
    expect(apply("Title", "h1", 2, 2).text).toBe("# Title");
  });

  // S-15 regression guards: `doc.lastIndexOf("\n", from - 1)` for `from === 0` used to resolve the
  // search position -1 by clamping it to 0 — so on a document that itself STARTS with a newline (a
  // leading blank line), it wrongly matched that very first "\n" and returned `blockStart = 1`, one
  // past `blockEnd = 0` (`newlineAfter` finds the SAME leading "\n" from the other side). CodeMirror
  // then threw `RangeError: Invalid change range 1 to 0` the instant the toolbar dispatched it — for
  // every block command, not just headings. Trigger: Ctrl+Home in a document with a leading blank line,
  // then click any block-format button.
  describe("caret at document position 0 with a leading blank line (S-15)", () => {
    for (const command of ["h1", "h2", "h3", "bullet", "ordered", "quote", "code"] as const) {
      it(`does not throw for ${command}`, () => {
        expect(() => formatMarkdown("\n# Title\n\npara\n", 0, 0, command)).not.toThrow();
      });

      it(`produces a valid (non-inverted) edit range for ${command}`, () => {
        const edit = formatMarkdown("\n# Title\n\npara\n", 0, 0, command);
        expect(edit.from).toBeLessThanOrEqual(edit.to);
      });
    }
  });

  // T-090 regression guards: the Code and Formatted (ProseMirror) tracts used to disagree on the
  // Markdown a toolbar command produced because each was implemented independently — see
  // format-parity.test.ts for the paired assertions against the PM tract; these pin the Code tract's
  // own corrected behavior in isolation.
  describe("bullet/ordered conversion is bidirectional, not additive (T-090)", () => {
    it("bullet on an already-ordered line converts it instead of prefixing garbage", () => {
      // Used to give "- 1. x" (BULLET_RE didn't recognize the ordered prefix at all).
      expect(apply("1. x", "bullet", 0, 4).text).toBe("- x");
    });

    it("ordered on an already-bulleted line converts it instead of prefixing garbage", () => {
      expect(apply("- x", "ordered", 0, 3).text).toBe("1. x");
    });

    it("numbers sequentially when converting a multi-line bulleted list to ordered", () => {
      expect(apply("- a\n- b", "ordered", 0, 7).text).toBe("1. a\n2. b");
    });
  });

  describe("heading nests inside an existing list/quote container instead of jumbling marker order (T-090)", () => {
    it("h1 on a bullet list line lands after the bullet, not before it", () => {
      // Used to give "# - item" (a heading whose literal text is "- item", losing the list).
      expect(apply("- item", "h1", 0, 6).text).toBe("- # item");
    });

    it("h1 toggles back off a list item's heading, keeping the list", () => {
      expect(apply("- # item", "h1", 0, 8).text).toBe("- item");
    });

    it("h1 on an ordered list line lands after the number marker", () => {
      expect(apply("1. item", "h1", 0, 7).text).toBe("1. # item");
    });

    it("h1 on a quoted line lands after the quote marker", () => {
      expect(apply("> Title", "h1", 0, 7).text).toBe("> # Title");
    });

    it("h1 on a quoted list item lands after both containers, quote outermost", () => {
      expect(apply("> - hello", "h1", 0, 9).text).toBe("> - # hello");
    });
  });

  describe("fenced code strips a heading marker instead of fencing raw ATX syntax (T-090)", () => {
    it("code on a heading line fences just its text, not the '#'", () => {
      // Used to give "```\n# Title\n```" (the literal '#' became part of the fenced content).
      expect(apply("# Title", "code", 0, 7).text).toBe("```\nTitle\n```");
    });
  });

  describe("a multi-line selection within ONE paragraph produces a single heading (T-090)", () => {
    it("joins soft-wrapped lines into one heading instead of one per physical line", () => {
      // Used to give "# line one\n# line two" (two headings from one logical paragraph).
      expect(apply("line one\nline two", "h1", 0, 17).text).toBe("# line one line two");
    });

    it("does not join two separate ATX headings that merely sit on adjacent lines", () => {
      // "# a\n# b" is two Heading blocks, not one Paragraph — toggling h1 off both must not join them.
      expect(apply("# a\n# b", "h1", 0, 7).text).toBe("a\nb");
    });
  });
});
