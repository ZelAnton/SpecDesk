/**
 * Block-splice Markdown serialization — PoC-12's answer to round-trip fidelity. Serializing the whole
 * ProseMirror document reflows every hard-wrapped paragraph and rewrites list markers (see
 * docs/ROADMAP.md "Decisions to lock"); instead this re-emits only the top-level blocks the author
 * actually changed and keeps every untouched block **verbatim** from the original source. A no-op edit
 * is byte-identical; a single edit's diff is local to its block.
 */

import type { Node as PmNode } from "prosemirror-model";
import { log } from "../util/log.js";
import { trace } from "../util/trace.js";
import { type MdBlock, splitTopLevelBlocks } from "./md-blocks.js";
import { parser, schema, serializer } from "./pm-markdown.js";

// A document that persistently falls back to whole-document serialize would emit one warning per
// debounced getText() (roughly one per keystroke). Dedup the live Serilog line to one per distinct
// fallback signature — the trace ring still records every occurrence; only the log line is bounded.
let lastFallbackSignature = "";

function warnFallback(message: string, data: Record<string, unknown>): void {
  const signature = `${data.reason}:${data.blocks}:${data.originalNodes}:${data.editedNodes}`;
  if (signature !== lastFallbackSignature) {
    lastFallbackSignature = signature;
    log.warn(message, data);
  }
}

/**
 * The two derivations of an `original` document the splice needs: its ProseMirror doc (for the node↔block
 * 1:1 fidelity check and the per-block equality test) AND its source-block split (for the verbatim
 * slices, and for the fallback's non-node-content preservation). Both come from the SAME `original`
 * string, so they are computed — and cached — together as one pair.
 */
interface ParsedOriginal {
  doc: PmNode | null;
  blocks: MdBlock[];
}

/**
 * Most-recent `(original → {doc, blocks})` memo. `serializeWithSplice` runs on every getText(), which the
 * Split cross-pane mirror calls on each debounce tick just to compare — yet `original` only changes on a
 * setText (a load or a mirror), never between edits. Without this, each call re-derived the SAME source
 * twice: a ProseMirror parse plus a markdown-it tokenize (splitTopLevelBlocks), the tokenize a third time
 * inside the fallback's nonNodeLines. Keyed on the exact `original` string, so it is self-invalidating —
 * a changed `original` misses and re-derives, so a stale doc/blocks pair can never be returned.
 */
let originalCache: { original: string; parsed: ParsedOriginal } | null = null;

/** The `original` document's `{doc, blocks}`, from the memo when the string is unchanged, else freshly
 *  derived (and memoized). The two derivations always correspond to the exact `original` passed in. */
function parseOriginal(original: string): ParsedOriginal {
  if (originalCache !== null && originalCache.original === original) {
    return originalCache.parsed;
  }
  const parsed: ParsedOriginal = {
    doc: parser.parse(original),
    blocks: splitTopLevelBlocks(original),
  };
  originalCache = { original, parsed };
  return parsed;
}

/** Serialize a single top-level block node to Markdown (no surrounding blank lines). */
function serializeBlock(node: PmNode): string {
  return serializer.serialize(schema.node("doc", null, [node]));
}

/**
 * The block's source AFTER its node content — kept verbatim when the block is re-serialized. This is
 * the trailing blank-line gap PLUS any non-node source the parser dropped (link reference definitions
 * fold into a block's slice but have no ProseMirror node, so re-serializing the node alone would lose
 * them). `contentLineEnd` is the node's source-map end; everything from there to the block end is
 * preserved. Falls back to scanning the trailing blank run when `contentLineEnd` is absent — which only
 * happens when the document has no top-level token at all, a case the whole-document fallback above
 * already takes (`blocks.length` is 1 but `childCount` is 0, so they never match) — kept here only as a
 * defensive fallback, never actually exercised on that path.
 */
function tailLines(lines: string[], block: MdBlock): string[] {
  if (block.contentLineEnd !== undefined) {
    return lines.slice(block.contentLineEnd, block.lineEnd + 1);
  }
  let start = block.lineEnd + 1;
  while (start - 1 >= block.lineStart && (lines[start - 1] ?? "").trim() === "") {
    start--;
  }
  return lines.slice(start, block.lineEnd + 1);
}

/**
 * The block's source BEFORE its node content — kept verbatim when the block is re-serialized.
 * Symmetric to {@link tailLines}: only ever non-empty for the document's first block, when it has
 * leading head content (blank lines, and reference definitions the parser consumed with no node — see
 * {@link MdBlock.contentLineStart}). Re-serializing the node alone would otherwise silently drop that
 * head content instead of just leaving it untouched.
 */
function headLines(lines: string[], block: MdBlock): string[] {
  if (block.contentLineStart === undefined) {
    return [];
  }
  return lines.slice(block.lineStart, block.contentLineStart);
}

/**
 * Every source line of `original` that belongs to no ProseMirror node at all — the same "non-node"
 * content {@link headLines}/{@link tailLines} preserve per block (blank runs and, crucially, link
 * reference definitions, which markdown-it resolves into its reference map with no emitted token) —
 * but computed directly from each block's own node span rather than by walking blocks pairwise, so it
 * still finds that content even when the document has no real top-level token at all (a lone reference
 * definition and nothing else): in that case `contentLineStart`/`contentLineEnd` are both unset, so the
 * whole block's span counts as "covered by nothing" and every one of its lines is returned. Takes the
 * caller's already-computed block split (from {@link parseOriginal}) rather than re-splitting `original`.
 */
function nonNodeLines(original: string, blocks: MdBlock[]): string[] {
  const lines = original.split("\n");
  const covered = new Array<boolean>(lines.length).fill(false);
  for (const block of blocks) {
    const start = block.contentLineStart ?? block.lineStart;
    const end = block.contentLineEnd ?? start;
    for (let line = start; line < end; line++) {
      covered[line] = true;
    }
  }
  return lines.filter((_, i) => !covered[i]);
}

/**
 * Safety net for the whole-document fallback below: `serializer.serialize(edited)` walks only
 * `edited`'s NODES, so a link reference definition — which has no node at all — vanishes silently the
 * moment an unrelated block is added or removed elsewhere in the same edit (the trivial repro: press
 * Enter in the WYSIWYG view to start a new paragraph in any document that has one). Re-append whatever
 * non-node content `original` had, verbatim, as a trailing section, so it survives the fallback
 * (repositioned to the end of the file) instead of disappearing outright. A no-op — byte-for-byte the
 * plain serialize — when there is nothing to preserve, which keeps every existing whole-document-fallback
 * fixture (none of which use reference definitions) unaffected.
 */
function withPreservedNonNodeContent(
  original: string,
  serialized: string,
  blocks: MdBlock[],
): string {
  const preserved = nonNodeLines(original, blocks).filter((line) => line.trim().length > 0);
  if (preserved.length === 0) {
    return serialized;
  }
  const body = serialized.endsWith("\n") ? serialized : `${serialized}\n`;
  return `${body}\n${preserved.join("\n")}\n`;
}

/**
 * Produce Markdown for `edited` (the current formatted-view document) that differs from `original`
 * only where the author changed a top-level block. `edited` MUST come from parsing `original` with
 * the shared {@link parser} and editing the result, so node↔block order is preserved.
 *
 * Falls back to a whole-document serialize (correct, but reflows untouched blocks) when the source
 * blocks and parsed nodes don't line up 1:1, or when a top-level block was added/removed — an
 * LCS-based alignment for add/remove is a follow-up. Content with no ProseMirror node at all (link
 * reference definitions) is still preserved on that fallback path, just repositioned to the end — see
 * {@link withPreservedNonNodeContent}.
 */
export function serializeWithSplice(original: string, edited: PmNode): string {
  // One derivation of `original` (memoized): both the ProseMirror doc and the source-block split, so the
  // per-getText() double-parse of the same source is gone (see parseOriginal).
  const { doc: originalDoc, blocks } = parseOriginal(original);
  if (originalDoc === null) {
    // The original source didn't parse to a ProseMirror doc — fall back to a whole-document serialize
    // (which reflows). High-signal: also stream it to the log so it lands in the Serilog file live.
    const fallback: Record<string, unknown> = {
      reason: "null-parse",
      blocks: blocks.length,
      originalNodes: null,
      editedNodes: edited.childCount,
    };
    trace("splice", "splice.fallback", fallback);
    warnFallback(
      "block-splice fell back to whole-document serialize (original did not parse)",
      fallback,
    );
    return withPreservedNonNodeContent(original, serializer.serialize(edited), blocks);
  }
  if (originalDoc.childCount !== blocks.length || edited.childCount !== originalDoc.childCount) {
    // Blocks and parsed nodes don't line up 1:1 (or a top-level block was added/removed) — the splice
    // can't align them, so fall back to a whole-document serialize (reflows). Stream to the log live.
    const fallback: Record<string, unknown> = {
      reason: "count-mismatch",
      blocks: blocks.length,
      originalNodes: originalDoc.childCount,
      editedNodes: edited.childCount,
    };
    trace("splice", "splice.fallback", fallback);
    warnFallback(
      "block-splice fell back to whole-document serialize (block/node count mismatch)",
      fallback,
    );
    return withPreservedNonNodeContent(original, serializer.serialize(edited), blocks);
  }

  const lines = original.split("\n");
  const out: string[] = [];
  let changedBlocks = 0;
  for (let i = 0; i < blocks.length; i++) {
    const block = blocks[i];
    if (block === undefined) {
      continue;
    }
    if (edited.child(i).eq(originalDoc.child(i))) {
      // Untouched block → keep its exact source (including hard wraps and list markers).
      out.push(...lines.slice(block.lineStart, block.lineEnd + 1));
    } else {
      changedBlocks++;
      // Changed block → re-attach any original head content verbatim (leading blank lines / reference
      // definitions before the first block's own node, see headLines), re-serialize just this block,
      // then re-attach its original tail verbatim (blank-line gap + any non-node source such as link
      // reference definitions).
      out.push(...headLines(lines, block));
      out.push(...serializeBlock(edited.child(i)).split("\n"));
      out.push(...tailLines(lines, block));
    }
  }
  trace("splice", "splice.ok", { blocks: blocks.length, changedBlocks });
  return out.join("\n");
}
