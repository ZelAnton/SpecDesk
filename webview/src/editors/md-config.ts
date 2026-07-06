/**
 * The single markdown-it tokenizer configuration shared by the source-block split (md-blocks.ts) and
 * the ProseMirror parser (pm-markdown.ts). The block-splice round-trip correlates the split's top-level
 * source blocks with the parser's top-level ProseMirror nodes 1:1, so the two MUST agree on where the
 * top-level block boundaries fall. That agreement is now CONSTRUCTIVE: both tokenizers come from this
 * one factory, so they are configured identically — same preset, same enabled rules, hence the same
 * block-nesting cap — and therefore split any document the same way at any depth.
 *
 * Previously each configured its own MarkdownIt independently: md-blocks used the default preset (block-
 * nesting cap 100) while pm-markdown used commonmark + table + strikethrough (cap 20). They tokenize
 * identically at shallow depth, so the agreement held only by convention — but past nesting depth 20 the
 * two caps diverge: a structure nested deeper than 20 truncates at a different point, so the split and
 * the parse disagree on the top-level boundaries (e.g. a deeply nested list absorbs, or does not absorb,
 * the paragraph after it). That mismatch — blocks.length != childCount — silently forced
 * serializeWithSplice onto its whole-document fallback for the entire document. Building both from here
 * removes the divergence by construction, so it can no longer regress into a convention.
 */

import MarkdownIt from "markdown-it";

/**
 * A markdown-it configured for CommonMark + GFM tables + strikethrough. The block rules (which decide
 * top-level boundaries) plus the commonmark preset's block-nesting cap are what the two consumers must
 * share; strikethrough is inline-only and never shifts a top-level boundary, but is included so BOTH
 * tokenizers are the same configuration byte-for-byte rather than merely equivalent for block purposes.
 *
 * A fresh instance per call — md-blocks and pm-markdown each own theirs — built from this identical
 * recipe, so their top-level boundaries agree at any nesting depth.
 */
export function createTokenizer(): MarkdownIt {
  return new MarkdownIt("commonmark", { html: false }).enable("table").enable("strikethrough");
}
