import { describe, expect, it } from "vitest";
import { type FormatCommand, formatMarkdown, isFormatCommand } from "../src/md-format.js";

describe("isFormatCommand (data-format DOM boundary)", () => {
  it("accepts every known command and rejects anything else", () => {
    for (const command of [
      "bold",
      "italic",
      "strike",
      "h1",
      "h2",
      "bullet",
      "ordered",
      "quote",
      "code",
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

  it("nests italic inside bold instead of stripping a bold asterisk", () => {
    // Select the inner text of **hello** and toggle italic → ***hello*** (not *hello*).
    expect(apply("**hello**", "italic", 2, 7)).toEqual({ text: "***hello***", sel: [3, 8] });
  });

  it("still unwraps a real bold selection (exact marker)", () => {
    expect(apply("**hello**", "bold", 2, 7)).toEqual({ text: "hello", sel: [0, 5] });
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
    for (const command of ["h1", "h2", "bullet", "ordered", "quote", "code"] as const) {
      it(`does not throw for ${command}`, () => {
        expect(() => formatMarkdown("\n# Title\n\npara\n", 0, 0, command)).not.toThrow();
      });

      it(`produces a valid (non-inverted) edit range for ${command}`, () => {
        const edit = formatMarkdown("\n# Title\n\npara\n", 0, 0, command);
        expect(edit.from).toBeLessThanOrEqual(edit.to);
      });
    }
  });
});
