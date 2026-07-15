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

import { createTokenizer } from "./md-config.js";

/** A top-level Markdown block: its verbatim source slice and 0-based inclusive source-line range. */
export interface MdBlock {
  /** The block's source text, including any blank lines that follow it up to the next block. */
  text: string;
  lineStart: number;
  lineEnd: number;
  /** Container kind when the top-level block needs selection-specific placement semantics. */
  containerKind?: "table" | "list";
  /**
   * For a top-level **table** or **list**: the 0-based source line where each direct child (table row
   * / list item) begins, in document order. Lets the formatted view highlight the row/item under the
   * caret instead of the whole block. The indices line up 1:1 with the rendered block's children
   * (table_row / list_item nodes). Undefined for any other block.
   */
  childLineStarts?: number[];
  /**
   * The line AFTER the block's own node content (the top-level token's source-map end). Lines from
   * here to {@link lineEnd} are trailing source the block's ProseMirror node does NOT represent —
   * blank lines, and crucially **link reference definitions** (`[id]: url`), which the parser consumes
   * into its reference map with no node. The block-splice keeps that tail verbatim when a block is
   * re-serialized, so editing such a block doesn't silently drop the ref-def. Undefined only when the
   * document has no top-level token at all (empty / whitespace-only), the one case with no real node.
   */
  contentLineEnd?: number;
  /**
   * The line WHERE the block's own node content actually starts, when it differs from
   * {@link lineStart}. Only ever set on the document's first block: any lines before the first
   * top-level token (leading blank lines, and link reference definitions the parser consumes with no
   * node) ride along as that block's own "head" source — never a separate synthetic block, so
   * `blocks.length` always equals the document's real top-level token count. Symmetric to
   * {@link contentLineEnd}, which covers the same kind of content trailing a block.
   */
  contentLineStart?: number;
}

// Used ONLY to find top-level block boundaries — never to render — so its inline rules don't matter
// here; what matters is that each top-level block token carries a source-line `map`, AND that these
// boundaries agree with the ProseMirror parse (pm-markdown.ts) the block-splice correlates them 1:1
// against. Both come from the one shared config (md-config.ts) — same block rules, same block-nesting
// cap — so the agreement holds constructively at any nesting depth rather than by convention (the two
// previously diverged past depth 20, where their caps differed). The config recognises GFM tables, so a
// table stays one block instead of splitting into paragraphs.
const tokenizer = createTokenizer();

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
  const containerKindByLine = new Map<number, "table" | "list">();
  // The end of each top-level block's node content (the token's map end), keyed by its start line.
  const contentEndByLine = new Map<number, number>();
  for (let i = 0; i < tokens.length; i++) {
    const token = tokens[i];
    if (token === undefined || token.level !== 0 || token.map === null) {
      continue;
    }
    starts.push(token.map[0]);
    contentEndByLine.set(token.map[0], token.map[1]);

    const isTable = token.type === "table_open";
    const isList = token.type === "bullet_list_open" || token.type === "ordered_list_open";
    if (!isTable && !isList) {
      continue;
    }
    containerKindByLine.set(token.map[0], isTable ? "table" : "list");
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

  // Boundaries partition [0, lines.length). A doc with no block tokens at all (empty / whitespace-only)
  // gets a single whole-document block. Otherwise, any gap before the first real token's line — leading
  // blank lines, and reference definitions the parser consumes with no node — rides with that first
  // block as its own head content rather than becoming a separate synthetic block: pulling boundary 0's
  // start back to line 0 (instead of unshifting an extra boundary) keeps `blocks.length` equal to the
  // document's real top-level token count, which the block-splice's fidelity check depends on.
  const boundaries = [...new Set(starts)].sort((a, b) => a - b);
  let firstContentStart: number | undefined;
  if (boundaries.length === 0) {
    boundaries.push(0);
  } else if (boundaries[0] !== 0) {
    firstContentStart = boundaries[0];
    boundaries[0] = 0;
  }

  const blocks: MdBlock[] = [];
  for (let i = 0; i < boundaries.length; i++) {
    const start = boundaries[i] ?? 0;
    const end = boundaries[i + 1] ?? lines.length;
    // The first block's own node starts at firstContentStart (if there is head content), not at its
    // (pulled-back-to-0) slice start — contentEndByLine/childStartsByLine are keyed by the token's own
    // start line, so look those up under the node's real start rather than the block's.
    const contentKey = i === 0 && firstContentStart !== undefined ? firstContentStart : start;
    const childLineStarts = childStartsByLine.get(contentKey);
    const containerKind = containerKindByLine.get(contentKey);
    const contentLineEnd = contentEndByLine.get(contentKey);
    const contentLineStart = i === 0 ? firstContentStart : undefined;
    blocks.push({
      text: lines.slice(start, end).join("\n"),
      lineStart: start,
      lineEnd: end - 1,
      // Only set when present — exactOptionalPropertyTypes forbids an explicit `undefined`.
      ...(childLineStarts !== undefined ? { childLineStarts } : {}),
      ...(containerKind !== undefined ? { containerKind } : {}),
      ...(contentLineEnd !== undefined ? { contentLineEnd } : {}),
      ...(contentLineStart !== undefined ? { contentLineStart } : {}),
    });
  }
  return blocks;
}

/** Reconstruct the original Markdown from blocks produced by {@link splitTopLevelBlocks}. */
export function joinBlocks(blocks: MdBlock[]): string {
  return blocks.map((block) => block.text).join("\n");
}
