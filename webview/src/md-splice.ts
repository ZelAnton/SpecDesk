/**
 * Block-splice Markdown serialization — PoC-12's answer to round-trip fidelity. Serializing the whole
 * ProseMirror document reflows every hard-wrapped paragraph and rewrites list markers (see
 * docs/ROADMAP.md "Decisions to lock"); instead this re-emits only the top-level blocks the author
 * actually changed and keeps every untouched block **verbatim** from the original source. A no-op edit
 * is byte-identical; a single edit's diff is local to its block.
 */

import type { Node as PmNode } from "prosemirror-model";
import { type MdBlock, splitTopLevelBlocks } from "./md-blocks.js";
import { parser, schema, serializer } from "./pm-markdown.js";

/** Serialize a single top-level block node to Markdown (no surrounding blank lines). */
function serializeBlock(node: PmNode): string {
  return serializer.serialize(schema.node("doc", null, [node]));
}

/** The trailing blank lines of a block's source slice (the gap before the next block). */
function gapLines(lines: string[], block: MdBlock): string[] {
  let start = block.lineEnd + 1;
  while (start - 1 >= block.lineStart && (lines[start - 1] ?? "").trim() === "") {
    start--;
  }
  return lines.slice(start, block.lineEnd + 1);
}

/**
 * Produce Markdown for `edited` (the current formatted-view document) that differs from `original`
 * only where the author changed a top-level block. `edited` MUST come from parsing `original` with
 * the shared {@link parser} and editing the result, so node↔block order is preserved.
 *
 * Falls back to a whole-document serialize (correct, but reflows untouched blocks) when the source
 * blocks and parsed nodes don't line up 1:1, or when a top-level block was added/removed — an
 * LCS-based alignment for add/remove is a follow-up.
 */
export function serializeWithSplice(original: string, edited: PmNode): string {
  const originalDoc = parser.parse(original);
  if (originalDoc === null) {
    return serializer.serialize(edited);
  }
  const blocks = splitTopLevelBlocks(original);
  if (originalDoc.childCount !== blocks.length || edited.childCount !== originalDoc.childCount) {
    return serializer.serialize(edited);
  }

  const lines = original.split("\n");
  const out: string[] = [];
  for (let i = 0; i < blocks.length; i++) {
    const block = blocks[i];
    if (block === undefined) {
      continue;
    }
    if (edited.child(i).eq(originalDoc.child(i))) {
      // Untouched block → keep its exact source (including hard wraps and list markers).
      out.push(...lines.slice(block.lineStart, block.lineEnd + 1));
    } else {
      // Changed block → re-serialize just this block, then re-attach its original trailing gap.
      out.push(...serializeBlock(edited.child(i)).split("\n"));
      out.push(...gapLines(lines, block));
    }
  }
  return out.join("\n");
}
