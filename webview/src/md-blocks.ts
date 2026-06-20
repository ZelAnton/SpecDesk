/**
 * Source-span block splitting — the foundation of PoC-12's block-splice round-trip. It partitions
 * Markdown into ordered top-level blocks, each carrying its exact source slice and 0-based inclusive
 * source-line range, so that concatenating the blocks reproduces the input **byte-for-byte**.
 *
 * The WYSIWYG serialize re-emits only the blocks the author actually changed and keeps every other
 * block verbatim from here, so a formatted edit yields a minimal, local Markdown diff rather than a
 * whole-document reflow (docs/design/05-live-preview.md; the spike that motivated this is recorded in
 * docs/ROADMAP.md "Decisions to lock").
 */

import MarkdownIt from "markdown-it";

/** A top-level Markdown block: its verbatim source slice and 0-based inclusive source-line range. */
export interface MdBlock {
  /** The block's source text, including any blank lines that follow it up to the next block. */
  text: string;
  lineStart: number;
  lineEnd: number;
  /**
   * For a top-level **table** or **list**: the 0-based source line where each direct child (table row
   * / list item) begins, in document order. Lets the formatted view highlight the row/item under the
   * caret instead of the whole block. The indices line up 1:1 with the rendered block's children
   * (table_row / list_item nodes). Undefined for any other block.
   */
  childLineStarts?: number[];
}

// Used ONLY to find top-level block boundaries — never to render — so its inline rules don't matter
// here; what matters is that each top-level block token carries a source-line `map`. The default
// preset recognises GFM tables, so a table stays one block instead of splitting into paragraphs.
const tokenizer = new MarkdownIt();

/**
 * Split Markdown into ordered top-level blocks. The slices form a contiguous line partition, so
 * `joinBlocks(splitTopLevelBlocks(md)) === md` for any input. Blank lines after a block ride with
 * that block; any leading blank lines ride with the first block.
 */
export function splitTopLevelBlocks(md: string): MdBlock[] {
  const lines = md.split("\n");
  const tokens = tokenizer.parse(md, {});
  const starts: number[] = [];
  // The child (row/item) source lines of each top-level table/list, keyed by the container's start.
  const childStartsByLine = new Map<number, number[]>();
  for (let i = 0; i < tokens.length; i++) {
    const token = tokens[i];
    if (token === undefined || token.level !== 0 || token.map === null) {
      continue;
    }
    starts.push(token.map[0]);

    const isTable = token.type === "table_open";
    const isList = token.type === "bullet_list_open" || token.type === "ordered_list_open";
    if (!isTable && !isList) {
      continue;
    }
    // Scan this container's tokens (everything until the next level-0 token, its close) for direct
    // children: table rows (`tr_open`, never nested in GFM) or list items one level deep (so items of
    // a nested sub-list, two levels deeper, are not mistaken for direct items).
    const childStarts: number[] = [];
    for (let j = i + 1; j < tokens.length; j++) {
      const child = tokens[j];
      if (child === undefined || child.level === 0) {
        break;
      }
      const isRow = isTable && child.type === "tr_open";
      const isItem = isList && child.type === "list_item_open" && child.level === token.level + 1;
      if ((isRow || isItem) && child.map !== null) {
        childStarts.push(child.map[0]);
      }
    }
    if (childStarts.length > 0) {
      childStartsByLine.set(token.map[0], childStarts);
    }
  }

  // Boundaries partition [0, lines.length); a leading gap (or a doc with no block tokens) folds into
  // the first slice by ensuring 0 is a boundary.
  const boundaries = [...new Set(starts)].sort((a, b) => a - b);
  if (boundaries[0] !== 0) {
    boundaries.unshift(0);
  }

  const blocks: MdBlock[] = [];
  for (let i = 0; i < boundaries.length; i++) {
    const start = boundaries[i] ?? 0;
    const end = boundaries[i + 1] ?? lines.length;
    const childLineStarts = childStartsByLine.get(start);
    blocks.push({
      text: lines.slice(start, end).join("\n"),
      lineStart: start,
      lineEnd: end - 1,
      // Only set when present — exactOptionalPropertyTypes forbids an explicit `undefined`.
      ...(childLineStarts !== undefined ? { childLineStarts } : {}),
    });
  }
  return blocks;
}

/** Reconstruct the original Markdown from blocks produced by {@link splitTopLevelBlocks}. */
export function joinBlocks(blocks: MdBlock[]): string {
  return blocks.map((block) => block.text).join("\n");
}
