import MarkdownIt from "markdown-it";
import { describe, expect, it } from "vitest";
import { joinBlocks, splitTopLevelBlocks } from "../../src/editors/md-blocks.js";

// Independent of md-blocks.ts's own tokenizer instance, so this actually pins the invariant
// splitTopLevelBlocks relies on rather than restating its internals.
const referenceTokenizer = new MarkdownIt();

/** The number of real top-level tokens markdown-it itself would report for `md`. */
function topLevelTokenCount(md: string): number {
  return referenceTokenizer.parse(md, {}).filter((t) => t.level === 0 && t.map !== null).length;
}

// A representative spec: headings, a hard-wrapped paragraph, a bullet list, a blockquote, a GFM
// table, a fenced code block, an ordered list, a thematic break, and a trailing paragraph.
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

describe("splitTopLevelBlocks", () => {
  it("round-trips the rich fixture byte-for-byte", () => {
    expect(joinBlocks(splitTopLevelBlocks(RICH))).toBe(RICH);
  });

  for (const [name, md] of [
    ["empty", ""],
    ["whitespace only", "\n\n  \n"],
    ["no trailing newline", "# H\n\npara"],
    ["leading blank lines", "\n\n# H\n\npara\n"],
    ["single paragraph + newline", "para\n"],
    // S-11 regression guards: split()/join() partition on "\n" only, so a "\r" immediately preceding it
    // rides along as part of the same line string — a CRLF document must round-trip exactly as its
    // LF counterpart does, with every "\r" preserved rather than dropped or duplicated.
    ["CRLF throughout", "# H\r\n\r\npara\r\n"],
    ["CRLF with a bullet list", "# H\r\n\r\n- one\r\n- two\r\n"],
    ["CRLF, no trailing newline", "# H\r\n\r\npara"],
  ] as const) {
    it(`round-trips byte-for-byte: ${name}`, () => {
      expect(joinBlocks(splitTopLevelBlocks(md))).toBe(md);
    });
  }

  it("round-trips the rich fixture byte-for-byte with CRLF line endings", () => {
    const crlf = RICH.replaceAll("\n", "\r\n");
    expect(joinBlocks(splitTopLevelBlocks(crlf))).toBe(crlf);
  });

  it("records the same child source lines for a CRLF bullet list as for its LF counterpart", () => {
    // The child-line bookkeeping is keyed by markdown-it's own line numbers (from its internally
    // newline-normalized copy), not by anything derived from splitting on "\r\n" — CRLF input must not
    // shift or duplicate them.
    const crlf = RICH.replaceAll("\n", "\r\n");
    const blocks = splitTopLevelBlocks(crlf);
    const listBlock = blocks.find((b) => b.text.includes("- one"));
    expect(listBlock?.childLineStarts).toEqual([7, 8, 9]);
    // Includes the blank separator line before the next block (">a quote"), which after replaceAll is
    // just a lone "\r" (the original empty line's content), not "\r\n" — same shape as the LF fixture's
    // trailing blank entry, with "\r" riding along.
    expect(listBlock?.text).toBe("- one\r\n- two\r\n- three\r\n\r");
  });

  it("keeps a GFM table as a single block, separate from the list", () => {
    const blocks = splitTopLevelBlocks(RICH);
    const tableBlock = blocks.find((b) => b.text.includes("| A | B |"));
    expect(tableBlock).toBeDefined();
    expect(tableBlock?.text.includes("| 1 | 2 |")).toBe(true);
    // the table block must not swallow the neighbouring list or code block
    expect(tableBlock?.text.includes("- one")).toBe(false);
    expect(tableBlock?.text.includes("const x")).toBe(false);
  });

  it("keeps a bullet list as one block", () => {
    const blocks = splitTopLevelBlocks(RICH);
    const listBlock = blocks.find((b) => b.text.includes("- one"));
    expect(listBlock?.text.includes("- three")).toBe(true);
  });

  it("records child source lines for a table (one per row, separator excluded)", () => {
    // | A | B |  (line 14)  ← header row
    // | - | - |  (line 15)  ← separator (no rendered row)
    // | 1 | 2 |  (line 16)  ← body row
    const blocks = splitTopLevelBlocks(RICH);
    const tableBlock = blocks.find((b) => b.text.includes("| A | B |"));
    expect(tableBlock?.childLineStarts).toEqual([14, 16]);
  });

  it("records child source lines for a bullet list (one per item)", () => {
    // - one (7), - two (8), - three (9)
    const blocks = splitTopLevelBlocks(RICH);
    const listBlock = blocks.find((b) => b.text.includes("- one"));
    expect(listBlock?.childLineStarts).toEqual([7, 8, 9]);
  });

  it("records child source lines for an ordered list", () => {
    // 1. first (22), 2. second (23)
    const blocks = splitTopLevelBlocks(RICH);
    const listBlock = blocks.find((b) => b.text.includes("1. first"));
    expect(listBlock?.childLineStarts).toEqual([22, 23]);
  });

  it("leaves non-container blocks without child lines", () => {
    const blocks = splitTopLevelBlocks(RICH);
    expect(blocks.find((b) => b.text.startsWith("# Title"))?.childLineStarts).toBeUndefined();
    expect(blocks.find((b) => b.text.includes("Intro paragraph"))?.childLineStarts).toBeUndefined();
  });

  it("treats only direct items of a nested list as children", () => {
    // Two top-level items; the second has a nested sub-list whose items must NOT be counted.
    const md = "- a\n- b\n  - b1\n  - b2\n";
    const blocks = splitTopLevelBlocks(md);
    expect(blocks[0]?.childLineStarts).toEqual([0, 1]);
  });

  // T-101: the exact child source lines the semantic sync anchors (sync-anchors.ts) build on, pinned on
  // one mixed document — a table (delimiter excluded), a bullet list with a nested sub-list (direct items
  // only), and a blockquote (a single unit with no child lines).
  it("records the exact child source lines across a mixed document", () => {
    const md = `# Title

- one
- two
  - two-a
  - two-b
- three

| A | B |
| - | - |
| 1 | 2 |
| 3 | 4 |

> a quote
> more quote
`;
    const blocks = splitTopLevelBlocks(md);
    const list = blocks.find((b) => b.text.includes("- one"));
    const table = blocks.find((b) => b.text.includes("| A | B |"));
    const quote = blocks.find((b) => b.text.includes("> a quote"));
    // The bullet list's DIRECT items (lines 2, 3, 6); the nested sub-list items (4, 5) are not counted.
    expect(list?.childLineStarts).toEqual([2, 3, 6]);
    // The table's rendered rows (header 8, body 10 and 11); the delimiter row (line 9) is excluded.
    expect(table?.childLineStarts).toEqual([8, 10, 11]);
    // A blockquote is a single unit — no per-child source lines.
    expect(quote?.childLineStarts).toBeUndefined();
  });

  it("produces a contiguous partition covering every line", () => {
    const blocks = splitTopLevelBlocks(RICH);
    expect(blocks[0]?.lineStart).toBe(0);
    for (let i = 1; i < blocks.length; i++) {
      const prev = blocks[i - 1];
      const cur = blocks[i];
      if (!prev || !cur) {
        continue;
      }
      expect(cur.lineStart).toBe(prev.lineEnd + 1);
    }
    expect(blocks.at(-1)?.lineEnd).toBe(RICH.split("\n").length - 1);
  });

  // S-13 regression guards: a leading gap (blank lines, or a reference definition with no rendered
  // node) used to become its OWN synthetic block, so `blocks.length` was permanently one MORE than the
  // document's real top-level token count — desyncing every block-index-keyed consumer (the
  // block-splice's fidelity check, and formatted.ts's block↔ProseMirror-child mapping).

  for (const [name, md] of [
    ["leading blank lines", "\n\n# H\n\npara\n"],
    ["a single leading blank line", "\npara\n"],
    ["a leading reference definition", "[ref]: http://example.com\n\npara\n"],
    ["RICH with two leading blank lines prepended", `\n\n${RICH}`],
  ] as const) {
    it(`blocks.length matches the real top-level token count: ${name}`, () => {
      const blocks = splitTopLevelBlocks(md);
      expect(blocks.length).toBe(topLevelTokenCount(md));
    });
  }

  it("folds leading blank lines into the first block's head, not a separate block", () => {
    const md = "\n\n# H\n\npara\n";
    const blocks = splitTopLevelBlocks(md);
    expect(blocks.length).toBe(2);
    expect(blocks[0]?.lineStart).toBe(0);
    expect(blocks[0]?.contentLineStart).toBe(2); // "# H" is the third line (0-based index 2)
    expect(blocks[0]?.text.includes("# H")).toBe(true);
    expect(joinBlocks(blocks)).toBe(md);
  });

  it("folds a leading reference definition into the first (only) block's head", () => {
    const md = "[ref]: http://example.com\n\npara\n";
    const blocks = splitTopLevelBlocks(md);
    // The reference definition has no rendered node — the paragraph is the ONLY real top-level token —
    // so this document is exactly one block, with the ref-def riding as that block's head content.
    expect(blocks.length).toBe(1);
    expect(blocks[0]?.lineStart).toBe(0);
    expect(blocks[0]?.contentLineStart).toBe(2);
    expect(blocks[0]?.text).toBe(md);
    expect(joinBlocks(blocks)).toBe(md);
  });

  it("a document with no top-level token at all still round-trips as a single whole-document block", () => {
    // Whitespace-only: no real token anywhere, so there is nothing to fold content into — the
    // whole-document fallback (not the leading-gap fold) applies, and contentLineStart stays unset.
    const md = "\n\n  \n";
    const blocks = splitTopLevelBlocks(md);
    expect(blocks.length).toBe(1);
    expect(blocks[0]?.contentLineStart).toBeUndefined();
    expect(joinBlocks(blocks)).toBe(md);
  });
});
