import { describe, expect, it } from "vitest";
import { joinBlocks, splitTopLevelBlocks } from "../src/md-blocks.js";

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
  ] as const) {
    it(`round-trips byte-for-byte: ${name}`, () => {
      expect(joinBlocks(splitTopLevelBlocks(md))).toBe(md);
    });
  }

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
});
