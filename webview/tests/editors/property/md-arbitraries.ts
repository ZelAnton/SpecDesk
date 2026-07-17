/**
 * fast-check generators for property-based Markdown round-trip tests (T-084). Shared by
 * block-splice.property.test.ts and word-diff.property.test.ts — kept in its own (non `*.test.ts`)
 * module so vitest never tries to run it as a suite.
 *
 * Every generated block uses a small fixed word dictionary (plain ASCII, no Markdown-significant
 * characters) so the interesting variable is Markdown *structure* (which block types, how many, in
 * what order), not incidental character-escaping edge cases that are out of this task's scope. Blocks
 * are always joined by exactly one blank line, matching `splitTopLevelBlocks`'s own contract ("blank
 * lines after a block ride with that block").
 */

import * as fc from "fast-check";

const WORDS = [
  "alpha",
  "bravo",
  "charlie",
  "delta",
  "echo",
  "foxtrot",
  "golf",
  "hotel",
  "india",
  "juliet",
  "kilo",
  "lima",
  "mike",
  "november",
  "oscar",
  "papa",
  "quebec",
  "romeo",
  "sierra",
  "tango",
];

const word = fc.constantFrom(...WORDS);

/** A short run of dictionary words joined by single spaces — safe inline text for any block type. */
function words(minLength: number, maxLength: number): fc.Arbitrary<string> {
  return fc.array(word, { minLength, maxLength }).map((ws) => ws.join(" "));
}

/** `# Some Words` .. `###### Some Words`. */
const headingBlock: fc.Arbitrary<string> = fc
  .tuple(fc.integer({ min: 1, max: 6 }), words(1, 6))
  .map(([level, text]) => `${"#".repeat(level)} ${text}`);

/** One to three lines of prose, optionally hard-wrapped (trailing double space) between lines — the
 *  same kind of source md-splice.test.ts's `RICH` fixture exercises for "Intro paragraph that is\n
 *  hard-wrapped across two lines.". Soft/hard line breaks never end the paragraph (no blank line), so
 *  this is always exactly one top-level node. */
const paragraphBlock: fc.Arbitrary<string> = fc
  .array(words(2, 6), { minLength: 1, maxLength: 3 })
  .chain((lines) =>
    fc
      .array(fc.boolean(), { minLength: lines.length, maxLength: lines.length })
      .map((hardBreaks) =>
        lines
          .map((line, i) => (i < lines.length - 1 && hardBreaks[i] === true ? `${line}  ` : line))
          .join("\n"),
      ),
  );

/** A bullet (`-`) or ordered (`1.`) list of 2-4 items. */
const listBlock: fc.Arbitrary<string> = fc
  .tuple(fc.boolean(), fc.array(words(1, 4), { minLength: 2, maxLength: 4 }))
  .map(([ordered, items]) =>
    items.map((text, i) => (ordered ? `${i + 1}. ${text}` : `- ${text}`)).join("\n"),
  );

/** A 2-column GFM pipe table with a header row and 1-3 body rows (unaligned separator). */
const tableBlock: fc.Arbitrary<string> = fc
  .tuple(word, word, fc.array(fc.tuple(word, word), { minLength: 1, maxLength: 3 }))
  .map(([headerA, headerB, rows]) => {
    const lines = [`| ${headerA} | ${headerB} |`, "| --- | --- |"];
    for (const [a, b] of rows) {
      lines.push(`| ${a} | ${b} |`);
    }
    return lines.join("\n");
  });

/** A fenced code block, optionally tagged with a language. */
const codeBlock: fc.Arbitrary<string> = fc
  .tuple(
    fc.constantFrom("", "ts", "js", "text"),
    fc.array(words(1, 5), { minLength: 1, maxLength: 3 }),
  )
  .map(([lang, lines]) => `\`\`\`${lang}\n${lines.join("\n")}\n\`\`\``);

/** A `> `-prefixed blockquote of 1-3 lines. */
const quoteBlock: fc.Arbitrary<string> = fc
  .array(words(2, 6), { minLength: 1, maxLength: 3 })
  .map((lines) => lines.map((line) => `> ${line}`).join("\n"));

/** Any one of the block kinds this suite covers: headings, lists, tables, code, quotes, hard breaks. */
export const markdownBlockArb: fc.Arbitrary<string> = fc.oneof(
  headingBlock,
  paragraphBlock,
  listBlock,
  tableBlock,
  codeBlock,
  quoteBlock,
);

/** A full Markdown document: 1-6 blocks, each separated by exactly one blank line, with a trailing
 *  newline (matching a real on-disk file). */
export const markdownDocumentArb: fc.Arbitrary<string> = fc
  .array(markdownBlockArb, { minLength: 1, maxLength: 6 })
  .map((blocks) => `${blocks.join("\n\n")}\n`);

/** Plain replacement text for a block edit — distinct from {@link WORDS} so it can never collide with
 *  an original block's own generated content. */
export const replacementTextArb: fc.Arbitrary<string> = fc
  .array(fc.constantFrom("apple", "banana", "cherry", "damson", "elder", "fig"), {
    minLength: 1,
    maxLength: 5,
  })
  .map((ws) => `edited-${ws.join(" ")}`);

/** Fixed seed so a failing counterexample reproduces deterministically across runs/machines. */
export const PROPERTY_SEED = 20260717;
