/**
 * Property-based round-trip tests for block-splice (T-084), on top of the example-based fixtures in
 * `md-splice.test.ts`. Pins the two invariants PoC-12's acceptance bar depends on across a fast-check
 * generated space of Markdown structures (headings, lists, tables, code, quotes, hard breaks — see
 * md-arbitraries.ts) rather than only the hand-written `RICH` fixture:
 *
 *  1. A no-op edit (`parse` → `serializeWithSplice` with the SAME doc) returns the source byte-for-byte.
 *  2. Editing exactly one top-level block never changes a single byte outside that block's own source
 *     line range.
 */

import * as fc from "fast-check";
import type { Node as PmNode } from "prosemirror-model";
import { describe, expect, it } from "vitest";
import { type MdBlock, splitTopLevelBlocks } from "../../../src/editors/md-blocks.js";
import { serializeWithSplice } from "../../../src/editors/md-splice.js";
import { parser, schema } from "../../../src/editors/pm-markdown.js";
import { markdownDocumentArb, PROPERTY_SEED, replacementTextArb } from "./md-arbitraries.js";

const NUM_RUNS = 300;

/** Every generated document's parse and source-block split must agree on the top-level count — this
 *  is what lets serializeWithSplice take the per-block path instead of falling back to a whole-document
 *  reflow. Both come from the SAME shared tokenizer config (md-config.ts), so this holds by
 *  construction for any well-formed document; kept as an explicit `fc.pre` guard rather than assumed,
 *  so a genuine divergence shows up as an (informative) low-coverage fast-check report instead of a
 *  silently-vacuous property. */
function parsedWithMatchingBlocks(md: string): { doc: PmNode; blocks: MdBlock[] } | null {
  const doc = parser.parse(md);
  if (doc === null) {
    return null;
  }
  const blocks = splitTopLevelBlocks(md);
  if (doc.childCount !== blocks.length) {
    return null;
  }
  return { doc, blocks };
}

describe("block-splice property: no-op round-trip is byte-identical", () => {
  it("parse -> serializeWithSplice with no edit returns the exact source bytes", () => {
    fc.assert(
      fc.property(markdownDocumentArb, (md) => {
        const parsed = parsedWithMatchingBlocks(md);
        fc.pre(parsed !== null);
        const { doc } = parsed as { doc: PmNode; blocks: MdBlock[] };
        expect(serializeWithSplice(md, doc)).toBe(md);
      }),
      { seed: PROPERTY_SEED, numRuns: NUM_RUNS },
    );
  });
});

describe("block-splice property: a single block edit is local to that block", () => {
  it("editing exactly one top-level block leaves every byte outside its line range untouched", () => {
    fc.assert(
      fc.property(
        markdownDocumentArb,
        fc.nat(),
        replacementTextArb,
        (md, indexSeed, replacementText) => {
          const parsed = parsedWithMatchingBlocks(md);
          fc.pre(parsed !== null);
          const { doc, blocks } = parsed as { doc: PmNode; blocks: MdBlock[] };
          fc.pre(doc.childCount > 0);

          const index = indexSeed % doc.childCount;
          const block = blocks[index];
          if (block === undefined) {
            throw new Error("index within childCount must resolve to a block");
          }

          const replacement = schema.node("paragraph", null, [schema.text(replacementText)]);
          fc.pre(!doc.child(index).eq(replacement));

          const children: PmNode[] = [];
          doc.forEach((child) => {
            children.push(child);
          });
          children[index] = replacement;
          const edited = schema.node("doc", null, children);

          const out = serializeWithSplice(md, edited);

          expect(out).toContain(replacementText);

          const originalLines = md.split("\n");
          const prefix = originalLines.slice(0, block.lineStart).join("\n");
          const suffix = originalLines.slice(block.lineEnd + 1).join("\n");

          if (block.lineStart > 0) {
            expect(out.startsWith(`${prefix}\n`)).toBe(true);
          }
          if (block.lineEnd + 1 < originalLines.length) {
            expect(out.endsWith(`\n${suffix}`)).toBe(true);
          }
        },
      ),
      { seed: PROPERTY_SEED, numRuns: NUM_RUNS },
    );
  });
});
