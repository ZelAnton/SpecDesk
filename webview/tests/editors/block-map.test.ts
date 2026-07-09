import type { Node as PmNode } from "prosemirror-model";
import { describe, expect, it } from "vitest";
import {
  BlockMap,
  type BlockMapEntry,
  lastIndexAtOrBefore,
  startOfChild,
} from "../../src/editors/block-map.js";
import { type MdBlock, splitTopLevelBlocks } from "../../src/editors/md-blocks.js";
import { parser } from "../../src/editors/pm-markdown.js";

// Build a real (doc, blocks) pair the way FormattedEditor does — the parser and the splitter share the
// tokenizer config, so their top-level counts agree for any real document, which is exactly the
// non-diverged invariant the map relies on. Layout-free: the map holds positions, never DOM.
function mapOf(md: string): { map: BlockMap; doc: PmNode; blocks: MdBlock[] } {
  const parsed = parser.parse(md);
  if (parsed === null) {
    throw new Error("parser returned null");
  }
  const blocks = splitTopLevelBlocks(md);
  return { map: BlockMap.build(parsed, blocks), doc: parsed, blocks };
}

const RICH = `# Title

Intro paragraph.

- one
- two
- three

| A | B |
| - | - |
| 1 | 2 |

Trailing paragraph.
`;

describe("lastIndexAtOrBefore", () => {
  it("returns the last index whose value is <= line", () => {
    expect(lastIndexAtOrBefore([0, 2, 6], 3)).toBe(1);
    expect(lastIndexAtOrBefore([0, 2, 6], 6)).toBe(2);
    expect(lastIndexAtOrBefore([0, 2, 6], 100)).toBe(2);
  });

  it("returns the exact index on an exact start match", () => {
    expect(lastIndexAtOrBefore([0, 2, 6], 0)).toBe(0);
    expect(lastIndexAtOrBefore([0, 2, 6], 2)).toBe(1);
  });

  it("returns -1 when the line precedes every start", () => {
    expect(lastIndexAtOrBefore([3, 5], 0)).toBe(-1);
    expect(lastIndexAtOrBefore([], 4)).toBe(-1);
  });
});

describe("startOfChild", () => {
  it("accumulates prior child sizes and accepts childCount (position past the last block)", () => {
    const { doc } = mapOf("# H\n\npara\n");
    expect(startOfChild(doc, 0)).toBe(0);
    expect(startOfChild(doc, 1)).toBe(doc.child(0).nodeSize);
    expect(startOfChild(doc, doc.childCount)).toBe(doc.content.size);
  });
});

describe("BlockMap.build", () => {
  it("pairs each source block with its ProseMirror node 1:1, with matching positions", () => {
    const { map, doc, blocks } = mapOf(RICH);
    expect(map.divergence).toBeNull();
    expect(map.entries).toHaveLength(blocks.length);
    expect(map.entries).toHaveLength(doc.childCount);
    let pos = 0;
    map.entries.forEach((entry: BlockMapEntry, i: number) => {
      expect(entry.index).toBe(i);
      expect(entry.block).toBe(blocks[i]);
      expect(entry.node).toBe(doc.child(i));
      expect(entry.from).toBe(pos);
      expect(entry.to).toBe(pos + doc.child(i).nodeSize);
      pos += doc.child(i).nodeSize;
    });
  });

  it("detects a top-level count divergence and exposes no entries (fallback, not mispairing)", () => {
    // A doc of 3 top-level nodes against a 2-block split — the markdown-it/ProseMirror divergence the
    // map exists to catch. It must NOT pair blocks[i] with doc.child(i) for the overlapping prefix.
    const { doc } = mapOf("# H\n\npara\n\n- a\n"); // 3 top-level nodes
    const blocks = splitTopLevelBlocks("# H\n\npara\n"); // 2 blocks
    const map = BlockMap.build(doc, blocks);
    expect(map.divergence).toEqual({ blockCount: 2, nodeCount: 3 });
    expect(map.entries).toHaveLength(0);
    expect(map.isEmpty).toBe(true);
    // Every accessor degrades to a safe empty/null result on divergence.
    expect(map.entryAt(0)).toBeNull();
    expect(map.entryForLine(0)).toBeNull();
    expect(map.entryForScroll(0)).toBeNull();
    expect(map.nodeRange(0, true)).toBeNull();
    expect(map.nodeRange(0, false)).toBeNull();
  });
});

describe("BlockMap.entryForLine (caret/hover/overlay)", () => {
  it("resolves the block whose range contains the line", () => {
    // Blocks: heading (line 0), paragraph (line 2), bullet list (lines 4-6).
    const { map } = mapOf("# H\n\npara\n\n- a\n- b\n- c\n");
    expect(map.entryForLine(0)?.index).toBe(0);
    expect(map.entryForLine(2)?.index).toBe(1);
    expect(map.entryForLine(5)?.index).toBe(2);
  });

  it("folds a line before the first block onto block 0", () => {
    // Leading blank lines ride with the heading block (S-13), so line 0/1 map to block 0.
    const { map } = mapOf("\n\n# H\n\npara\n");
    expect(map.entryForLine(0)?.index).toBe(0);
    expect(map.entryForLine(1)?.index).toBe(0);
  });

  it("clears (returns null) for a line past the last block's end", () => {
    const { map } = mapOf("# H\n\npara\n"); // last line index is 3
    expect(map.entryForLine(4)).toBeNull();
    expect(map.entryForLine(99)).toBeNull();
  });

  it("returns null for a null line or an empty document map", () => {
    const { map } = mapOf("# H\n\npara\n");
    expect(map.entryForLine(null)).toBeNull();
  });
});

describe("BlockMap.entryForScroll", () => {
  it("lands on the nearest block at or before the line, without clearing past the end", () => {
    const { map } = mapOf("# H\n\npara\n\n- a\n- b\n");
    expect(map.entryForScroll(0)?.index).toBe(0);
    expect(map.entryForScroll(2)?.index).toBe(1);
    // A line past the document end does NOT clear (unlike entryForLine) — it clamps to the last block.
    expect(map.entryForScroll(999)?.index).toBe(map.entries.length - 1);
  });

  it("folds a line before everything onto block 0", () => {
    const { map } = mapOf("\n\n# H\n\npara\n");
    expect(map.entryForScroll(0)?.index).toBe(0);
  });
});

describe("BlockMap.nodeRange", () => {
  it("returns the whole top-level block when narrow is false", () => {
    const { map } = mapOf("para\n\n- a\n- b\n");
    const listEntry = map.entryForLine(2);
    expect(listEntry?.index).toBe(1);
    expect(map.nodeRange(2, false)).toEqual([listEntry?.from, listEntry?.to]);
  });

  it("narrows to the row/item the line falls in inside a list", () => {
    const { map, doc } = mapOf("- one\n- two\n- three\n");
    const listEntry = map.entryAt(0);
    const list = doc.child(0);
    // Item 1 ("two") starts at from+1 (into the list) + item0.nodeSize.
    const item0Size = list.child(0).nodeSize;
    const expectedFrom = (listEntry?.from ?? 0) + 1 + item0Size;
    expect(map.nodeRange(1, true)).toEqual([expectedFrom, expectedFrom + list.child(1).nodeSize]);
  });

  it("narrows to the right table row (separator line has no row of its own)", () => {
    // Rows at lines 0 (header) and 2 (data); line 1 is the separator (no rendered row).
    const { map, doc } = mapOf("| A | B |\n| - | - |\n| 1 | 2 |\n");
    const table = doc.child(0);
    // Line 2 (the data row) must resolve to the SECOND table_row node, not the header.
    const from0 = 1; // into the table
    const row1From = from0 + table.child(0).nodeSize;
    expect(map.nodeRange(2, true)).toEqual([row1From, row1From + table.child(1).nodeSize]);
  });

  it("falls back to the whole block for narrow on a non-container block", () => {
    const { map } = mapOf("# H\n\npara\n");
    const entry = map.entryForLine(0);
    // A heading has no childLineStarts — narrow resolves to the whole heading, same as narrow: false.
    expect(map.nodeRange(0, true)).toEqual([entry?.from, entry?.to]);
  });
});
