/**
 * The single line↔block↔ProseMirror-node↔DOM correspondence for the formatted editor. It pairs each
 * top-level markdown-it source block ({@link MdBlock}) with the ProseMirror node the same source parsed
 * into, plus that node's `[from, to]` document positions — so every place that used to re-derive the
 * pairing by a bare `blocks[i]`/`doc.child(i)` index (block geometry, the caret/hover highlight, the
 * review overlay, and both scroll-sync directions) goes through ONE structure instead.
 *
 * Why one structure. md-blocks' split (markdown-it, see md-blocks.ts) and the ProseMirror doc
 * (pm-markdown.ts) are built from the SAME shared tokenizer config (md-config.ts), so their top-level
 * COUNTS agree by construction for any real document. When they don't — a markdown-it/ProseMirror parse
 * divergence (T-083) — a child ordinal taken from one side and read against the other silently points at
 * the WRONG block, which reads as confidently-but-incorrectly precise and quietly corrupts height-sync
 * (wrong spacers) and scroll-sync. This map DETECTS that divergence ({@link BlockMap.divergence}) and
 * exposes an EMPTY set of entries so every consumer degrades to a safe no-op (no highlight, no spacers,
 * no scroll) instead of mispairing — the same "detect and bow out rather than guess" contract
 * DiffWire.fs already applies natively.
 *
 * Layout-free and pure: it holds document POSITIONS, never DOM. A consumer that needs the rendered DOM
 * resolves it itself from an entry's `from` (`view.nodeDOM(entry.from)`) and does its own measurement,
 * so this module is unit-tested without a GUI.
 */

import type { Node as PmNode } from "prosemirror-model";
import type { MdBlock } from "./md-blocks.js";

/**
 * The index of the last element whose start line is at or before `line`, or `-1` when `line` is before
 * every element. The ONE block-by-line search: the top-level block for a source line, and — reused with a
 * container's child start lines — the table row / list item for a line, and (over cached block tops, see
 * block-geometry.ts) the block at a scroll offset. `lineStarts` is ascending (both the block split and
 * each container's child lines are emitted in document order), so this is a BINARY search — the upper
 * bound of `line` minus one — replacing the former O(n) linear scan on every caret/hover/scroll report
 * (T-072). Duplicate values resolve to the LAST matching index (the linear scan's "keep the last match"
 * semantics), and callers apply their own edge policy (clamp, first-block default, or clear) to the
 * returned index.
 */
export function lastIndexAtOrBefore(lineStarts: readonly number[], line: number): number {
  // Half-open search window [lo, hi); shrink to the first index whose value is > line — the element
  // just before it is the last one at or before `line`.
  let lo = 0;
  let hi = lineStarts.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if ((lineStarts[mid] ?? Number.POSITIVE_INFINITY) <= line) {
      lo = mid + 1;
    } else {
      hi = mid;
    }
  }
  return lo - 1;
}

/** Document position where the top-level child at `index` starts — the sum of every prior child's size.
 *  O(index); a scan visiting every block in order should accumulate inline (`pos += child.nodeSize`)
 *  rather than call this per block, which would be O(n²). Accepts `index === doc.childCount` (→ the
 *  document's content size), so it can address the position just past the last block. */
export function startOfChild(doc: PmNode, index: number): number {
  let pos = 0;
  for (let i = 0; i < index; i++) {
    pos += doc.child(i).nodeSize;
  }
  return pos;
}

/** One top-level block: its source-range side ({@link MdBlock}), the ProseMirror node the same source
 *  parsed into, and that node's `[from, to]` document positions. */
export interface BlockMapEntry {
  /** 0-based position of this block in document order (equal on both the source and PM sides). */
  readonly index: number;
  readonly block: MdBlock;
  readonly node: PmNode;
  /** Document position where `node` starts. */
  readonly from: number;
  /** `from + node.nodeSize` — the position just past `node`. */
  readonly to: number;
}

/** The observed top-level counts when the markdown-it split and the ProseMirror doc disagree — the
 *  divergence {@link BlockMap} exists to catch rather than mispair. */
export interface BlockDivergence {
  blockCount: number;
  nodeCount: number;
}

/**
 * The paired top-level blocks of one document snapshot. Build it per operation from the live doc and the
 * current source-block split ({@link BlockMap.build}); it is O(n), the same cost as the inline pairing
 * loops it replaces. When the two sides diverge its {@link entries} are empty and {@link divergence}
 * carries the mismatched counts, so every accessor returns a safe empty/null result.
 */
export class BlockMap {
  /** The paired blocks in document order — empty when the two sides diverge (see {@link divergence}). */
  readonly entries: readonly BlockMapEntry[];
  /** Non-null when the markdown-it split and the ProseMirror doc disagree on top-level count. */
  readonly divergence: BlockDivergence | null;
  // The entries' start lines, extracted once so the block-by-line search is a plain number scan.
  private readonly lineStarts: readonly number[];

  private constructor(
    entries: readonly BlockMapEntry[],
    divergence: BlockDivergence | null,
    lineStarts: readonly number[],
  ) {
    this.entries = entries;
    this.divergence = divergence;
    this.lineStarts = lineStarts;
  }

  /**
   * Pair `doc`'s top-level children with `blocks` 1:1. When their counts disagree (a parse divergence),
   * return a map with NO entries and a recorded {@link divergence} — the caller then degrades to a
   * no-op rather than reading a block against the wrong node.
   */
  static build(doc: PmNode, blocks: readonly MdBlock[]): BlockMap {
    if (doc.childCount !== blocks.length) {
      return new BlockMap([], { blockCount: blocks.length, nodeCount: doc.childCount }, []);
    }
    const entries: BlockMapEntry[] = [];
    const lineStarts: number[] = [];
    let pos = 0;
    for (let i = 0; i < doc.childCount; i++) {
      const node = doc.child(i);
      const block = blocks[i];
      if (block === undefined) {
        // Unreachable given the count guard above (i < blocks.length), but keeps the narrowing honest.
        continue;
      }
      entries.push({ index: i, block, node, from: pos, to: pos + node.nodeSize });
      lineStarts.push(block.lineStart);
      pos += node.nodeSize;
    }
    return new BlockMap(entries, null, lineStarts);
  }

  get isEmpty(): boolean {
    return this.entries.length === 0;
  }

  /** The entry at a top-level document index (e.g. a ProseMirror `$pos.index(0)`), or null when out of
   *  range or the map diverged. */
  entryAt(index: number): BlockMapEntry | null {
    return this.entries[index] ?? null;
  }

  /**
   * The entry for a caret/hover/overlay source line: the block whose source range contains `line`, else
   * the last block starting at or before it (a blank inter-block line rides with the preceding block).
   * Null when the map is empty, `line` is null, or `line` is past the last block's own `lineEnd` — a
   * stale synced line from a now-shorter document, which CLEARS the highlight rather than pinning the
   * final block (matching the source editor's activeLineField, which resets rather than clamps).
   */
  entryForLine(line: number | null): BlockMapEntry | null {
    if (line === null || this.entries.length === 0) {
      return null;
    }
    const index = lastIndexAtOrBefore(this.lineStarts, line);
    // Before the first block, fold onto block 0 (its leading blank lines/ref-defs ride with it).
    const entry = this.entries[index < 0 ? 0 : index];
    if (entry === undefined) {
      return null;
    }
    if (line > (entry.block.lineEnd ?? Number.POSITIVE_INFINITY)) {
      return null;
    }
    return entry;
  }

  /**
   * The entry to scroll to for a source line: the last block starting at or before it (block 0 when
   * `line` precedes everything). Unlike {@link entryForLine} it does NOT clear past the document end —
   * a scroll TARGET lands on the nearest block — so it is null only when the map is empty/diverged.
   */
  entryForScroll(line: number): BlockMapEntry | null {
    if (this.entries.length === 0) {
      return null;
    }
    const index = lastIndexAtOrBefore(this.lineStarts, line);
    return this.entries[index < 0 ? 0 : index] ?? null;
  }

  /**
   * `[from, to]` of the node to address for a source line: the top-level block, or — inside a table /
   * list when `narrow` is true — the row / item the line falls in (so the caret highlights one row, not
   * the whole table). Positions are against the current document, so they are always valid. Null when
   * no block resolves (see {@link entryForLine}).
   *
   * Row/item narrowing pairs the container's `childLineStarts` (markdown-it) with the PM node's children
   * by ordinal. If those child COUNTS disagree — the same parse divergence at container level — it bows
   * out to the whole container rather than clamp an ordinal into the wrong row/item (a silent wrong-row
   * guess is worse than a coarse-but-correct whole-container highlight).
   */
  nodeRange(line: number | null, narrow: boolean): [number, number] | null {
    const entry = this.entryForLine(line);
    if (entry === null || line === null) {
      return null;
    }
    if (!narrow) {
      return [entry.from, entry.to];
    }
    const childStarts = entry.block.childLineStarts;
    const node = entry.node;
    if (
      childStarts === undefined ||
      childStarts.length === 0 ||
      node.childCount === 0 ||
      childStarts.length !== node.childCount
    ) {
      return [entry.from, entry.to];
    }
    // childStarts.length === node.childCount was just verified, so this ordinal is already in range —
    // no clamp needed (a clamp here would be exactly the silent wrong-row guess this guards against).
    const childIndex = Math.max(0, lastIndexAtOrBefore(childStarts, line));
    let childPos = entry.from + 1; // step inside the container node
    for (let i = 0; i < childIndex; i++) {
      childPos += node.child(i).nodeSize;
    }
    return [childPos, childPos + node.child(childIndex).nodeSize];
  }
}
