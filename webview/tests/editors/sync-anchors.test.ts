import type { Node as PmNode } from "prosemirror-model";
import { describe, expect, it } from "vitest";
import { BlockMap } from "../../src/editors/block-map.js";
import { splitTopLevelBlocks } from "../../src/editors/md-blocks.js";
import { parser } from "../../src/editors/pm-markdown.js";
import { buildLeafAnchors } from "../../src/editors/sync-anchors.js";

// Build the same (doc, block-map) pair FormattedEditor does — the parser and the splitter share the
// tokenizer config, so their structure agrees for any real document. Layout-free: the projection holds
// source lines + ProseMirror POSITIONS, never DOM, so it is unit-tested here without a GUI.
function docOf(md: string): PmNode {
  const parsed = parser.parse(md);
  if (parsed === null) {
    throw new Error("parser returned null");
  }
  return parsed;
}

function mapOf(md: string): BlockMap {
  return BlockMap.build(docOf(md), splitTopLevelBlocks(md));
}

// A mixed spec: heading, paragraph, a bullet list whose second item nests a sub-list, a GFM table with a
// header + two body rows, a multi-line blockquote, and a trailing paragraph. Source lines (0-based):
//   0 # Title              2 Intro paragraph.     4 - one       5 - two
//   6   - two-a            7   - two-b            8 - three     10 | A | B |
//   11 | - | - | (delim)   12 | 1 | 2 |           13 | 3 | 4 |  15 > a quote
//   16 > more quote        18 Trailing.
const MIXED = `# Title

Intro paragraph.

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

Trailing.
`;

describe("buildLeafAnchors — the ordered semantic sync anchors", () => {
  it("anchors each rendered leaf unit at its own source line (rows and items, nested included)", () => {
    const anchors = buildLeafAnchors(mapOf(MIXED), MIXED);
    // One anchor per visual unit, in document order: heading, paragraph, three top items with the two
    // nested items interleaved after "two", header + two body rows, the whole quote, trailing paragraph.
    expect(anchors.map((a) => a.line)).toEqual([0, 2, 4, 5, 6, 7, 8, 10, 12, 13, 15, 18]);
  });

  it("pins each anchor's ProseMirror range to the node that starts at its position", () => {
    const doc = docOf(MIXED);
    const anchors = buildLeafAnchors(BlockMap.build(doc, splitTopLevelBlocks(MIXED)), MIXED);
    // The node starting at each anchor's `from` is the expected kind, and `to` is exactly one nodeSize
    // past it — a table gives table_row anchors, a list gives list_item anchors (a whole container never
    // stands in for its rows/items).
    const nodes = anchors.map((a) => doc.resolve(a.from).nodeAfter);
    expect(nodes.map((n) => n?.type.name)).toEqual([
      "heading",
      "paragraph",
      "list_item",
      "list_item",
      "list_item",
      "list_item",
      "list_item",
      "table_row",
      "table_row",
      "table_row",
      "blockquote",
      "paragraph",
    ]);
    anchors.forEach((a, i) => {
      const node = nodes[i];
      expect(node).not.toBeNull();
      expect(a.to).toBe(a.from + (node?.nodeSize ?? 0));
    });
  });

  it("gives the table delimiter row no anchor (it renders no row node)", () => {
    const lines = buildLeafAnchors(mapOf(MIXED), MIXED).map((a) => a.line);
    // The header (10) and both body rows (12, 13) are anchored; the delimiter (`| - | - |`, line 11) is
    // absent — a consumer interpolates its position between the header and the first body row.
    expect(lines).toContain(10);
    expect(lines).toContain(12);
    expect(lines).toContain(13);
    expect(lines).not.toContain(11);
  });

  it("gives a leading reference definition no anchor (it renders no node)", () => {
    // The ref-def has no rendered node, so the two paragraphs are the only units; their anchors sit at
    // the paragraphs' own content lines (2 and 4), never at the ref-def line 0.
    const md = "[ref]: http://example.com\n\nPara one.\n\nPara two.\n";
    const lines = buildLeafAnchors(mapOf(md), md).map((a) => a.line);
    expect(lines).toEqual([2, 4]);
    expect(lines).not.toContain(0);
  });

  it("keeps source line and (structural) order monotone across the document", () => {
    const lines = buildLeafAnchors(mapOf(MIXED), MIXED).map((a) => a.line);
    for (let i = 1; i < lines.length; i++) {
      expect(lines[i] ?? 0).toBeGreaterThan(lines[i - 1] ?? 0);
    }
  });

  it("anchors a multi-paragraph (loose) list item once, at the item top", () => {
    // A loose item spanning two source paragraphs is still ONE unit — one anchor at the item's first
    // line, not one per contained paragraph.
    const md = "- first para\n\n  second para\n- next\n";
    const anchors = buildLeafAnchors(mapOf(md), md);
    expect(anchors.map((a) => a.line)).toEqual([0, 3]);
  });
});

describe("buildLeafAnchors — container keys (the container-tail floor's grouping)", () => {
  it("stamps every row/item with its container's key, and top-level leaves with none", () => {
    const anchors = buildLeafAnchors(mapOf(MIXED), MIXED);
    const byLine = new Map(anchors.map((a) => [a.line, a]));
    // Top-level leaves belong to no container.
    for (const line of [0, 2, 15, 18]) {
      expect(byLine.get(line)?.containers, `top-level leaf at line ${line}`).toEqual([]);
    }
    // The three outer list items share ONE key; the table's header + body rows share ANOTHER.
    const listKeys = [4, 5, 8].map((line) => byLine.get(line)?.containers ?? []);
    expect(listKeys.every((keys) => keys.length === 1)).toBe(true);
    expect(new Set(listKeys.map((keys) => keys[0])).size).toBe(1);
    const tableKeys = [10, 12, 13].map((line) => byLine.get(line)?.containers ?? []);
    expect(tableKeys.every((keys) => keys.length === 1)).toBe(true);
    expect(new Set(tableKeys.map((keys) => keys[0])).size).toBe(1);
    expect(tableKeys[0]?.[0]).not.toBe(listKeys[0]?.[0]);
  });

  it("stacks nested sub-items under BOTH the outer list's key and their own sub-list's key", () => {
    const anchors = buildLeafAnchors(mapOf(MIXED), MIXED);
    const byLine = new Map(anchors.map((a) => [a.line, a]));
    const outerKey = byLine.get(4)?.containers[0];
    const subA = byLine.get(6)?.containers ?? [];
    const subB = byLine.get(7)?.containers ?? [];
    expect(subA.length).toBe(2);
    expect(subA[0]).toBe(outerKey);
    expect(subA[1]).not.toBe(outerKey);
    expect(subB).toEqual(subA);
  });

  it("keys a same-span nested list distinctly from its parent (`- - a`)", () => {
    // markdown-it maps BOTH lists of `- - a\n  - b` to the same [0,2] source span, so a span-based key
    // alone would merge the two container instances into one group and drop the inner tail's own floor —
    // the per-pass sequence number keeps every instance distinct.
    const md = "- - a\n  - b\n";
    const anchors = buildLeafAnchors(mapOf(md), md);
    const outerItem = anchors.find((a) => a.containers.length === 1);
    const innerItem = anchors.find((a) => a.containers.length === 2);
    expect(outerItem).toBeDefined();
    expect(innerItem).toBeDefined();
    expect(innerItem?.containers[0]).toBe(outerItem?.containers[0]);
    expect(innerItem?.containers[1]).not.toBe(innerItem?.containers[0]);
  });

  it("gives a coarsened top-level container no key of its own (a group of one has no tail)", () => {
    const pmMd = "intro\n\n| A | B |\n| - | - |\n| 1 | 2 |\n| 3 | 4 |\n\nend\n";
    const srcMd = "intro\n\n| A | B |\n| - | - |\n| 1 | 2 |\n| 3 | 4 |\n| 5 | 6 |\n\nend\n";
    const anchors = buildLeafAnchors(mapOf(pmMd), srcMd);
    expect(anchors[1]?.containers).toEqual([]);
  });

  it("keeps a coarsened NESTED sub-list inside its outer list's group", () => {
    // The coarse sub-list anchor still counts as a unit OF the outer list (it can be the outer group's
    // tail), it just adds no key of its own.
    const pmMd = "- a\n- b\n  - b1\n  - b2\n";
    const srcMd = "- a\n- b\n  - b1\n  - b2\n  - b3\n";
    const anchors = buildLeafAnchors(mapOf(pmMd), srcMd);
    const outerKey = anchors[0]?.containers[0];
    expect(outerKey).toBeDefined();
    expect(anchors[2]?.containers).toEqual(outerKey === undefined ? [] : [outerKey]);
  });
});

describe("buildLeafAnchors — divergence stays local", () => {
  it("returns no anchors when the top-level split diverged (empty block-map)", () => {
    // A 3-node doc against a 2-block split — the top-level parse divergence the block-map catches. With
    // no paired entries there is nothing to anchor, so consumers fall back to their no-op path.
    const map = BlockMap.build(docOf("# H\n\npara\n\n- a\n"), splitTopLevelBlocks("# H\n\npara\n"));
    expect(map.isEmpty).toBe(true);
    expect(buildLeafAnchors(map, "# H\n\npara\n")).toEqual([]);
  });

  it("coarsens ONLY the diverged container and keeps every other anchor working", () => {
    // Simulate a local source/PM mismatch the way the mid-edit window produces one: build the map from a
    // TWO-body-row table but project against a THREE-body-row source. Top-level counts still agree (para,
    // table, para), so the two paragraphs anchor normally; the table alone can't pair its rows by ordinal,
    // so it coarsens to a single anchor at the table's own line rather than mispairing a row.
    const pmMd = "intro\n\n| A | B |\n| - | - |\n| 1 | 2 |\n| 3 | 4 |\n\nend\n";
    const srcMd = "intro\n\n| A | B |\n| - | - |\n| 1 | 2 |\n| 3 | 4 |\n| 5 | 6 |\n\nend\n";
    const anchors = buildLeafAnchors(mapOf(pmMd), srcMd);
    // intro (line 0), the whole table coarsened to one anchor (its own line 2), end (line 9 in srcMd).
    const doc = docOf(pmMd);
    const nodes = anchors.map((a) => doc.resolve(a.from).nodeAfter?.type.name);
    expect(nodes).toEqual(["paragraph", "table", "paragraph"]);
    expect(anchors[0]?.line).toBe(0);
    expect(anchors[1]?.line).toBe(2);
  });

  it("coarsens ONLY a diverged nested sub-list, keeping the outer item anchors", () => {
    // The outer list has two items; the second nests a sub-list. Project the second item against a source
    // whose sub-list has a DIFFERENT item count, so only the nested sub-list coarsens to a single anchor
    // (its own top) instead of mispairing its items, while both outer items keep their own anchors.
    const pmMd = "- a\n- b\n  - b1\n  - b2\n";
    const srcMd = "- a\n- b\n  - b1\n  - b2\n  - b3\n";
    const anchors = buildLeafAnchors(mapOf(pmMd), srcMd);
    const doc = docOf(pmMd);
    // Outer item a (0), outer item b (1), then the nested sub-list coarsened to one anchor at its own
    // top (line 2) — the sub-list node, not a mispaired nested item.
    expect(anchors.map((a) => a.line)).toEqual([0, 1, 2]);
    expect(doc.resolve(anchors[2]?.from ?? 0).nodeAfter?.type.name).toBe("bullet_list");
  });

  it("falls back to top-level anchors if the source outline size disagrees with the block-map", () => {
    // Defensive path: a block-map of two blocks projected against a one-block source outline. Rather than
    // walk a misaligned outline it emits one anchor per top-level block (safe coarsening).
    const map = mapOf("# H\n\npara\n");
    const anchors = buildLeafAnchors(map, "# H\n");
    expect(anchors.map((a) => a.line)).toEqual([0, 2]);
  });
});
