/**
 * Property-based invertibility test for word-diff (T-084): {@link wordDiff}'s own doc comment states
 * the contract its `WordOp` shape is built for — "the offsets reconstruct the text exactly" — so this
 * pins that as a fast-check property over arbitrary (base, head) string pairs, rather than only the
 * hand-picked examples the module's unit tests already cover.
 *
 * `head` is recoverable directly from the ops' own head-coordinate ranges (equal/add, concatenated in
 * order). `base` is recoverable too: an `equal` op's range is, by construction, the SAME text in both
 * strings (that is what made it "equal"), and a `del` op carries its own deleted base text — so walking
 * equal/del ops in order (skipping `add`, which contributes nothing to `base`) reconstructs `base`
 * byte-for-byte.
 */

import * as fc from "fast-check";
import { describe, expect, it } from "vitest";
import { wordDiff } from "../../../src/review/word-diff.js";
import { PROPERTY_SEED } from "./md-arbitraries.js";

const NUM_RUNS = 300;
// Comfortably under wordDiff's own MAX_LCS_CELLS guard even in the worst case (every character its own
// token), so every generated pair goes through the real LCS path, never the too-large fallback.
const MAX_TEXT_LENGTH = 60;

const textArb = fc.string({ maxLength: MAX_TEXT_LENGTH });

describe("word-diff property: the diff is invertible", () => {
  it("reconstructs the exact head text from the diff's equal/add ops", () => {
    fc.assert(
      fc.property(textArb, textArb, (base, head) => {
        const { ops } = wordDiff(base, head);
        const reconstructedHead = ops
          .filter((op) => op.type !== "del")
          .map((op) => head.slice(op.start, op.end))
          .join("");
        expect(reconstructedHead).toBe(head);
      }),
      { seed: PROPERTY_SEED, numRuns: NUM_RUNS },
    );
  });

  it("reconstructs the exact base text from the diff's equal/del ops", () => {
    fc.assert(
      fc.property(textArb, textArb, (base, head) => {
        const { ops } = wordDiff(base, head);
        const reconstructedBase = ops
          .map((op) => {
            if (op.type === "add") {
              return "";
            }
            return op.type === "del" ? op.text : head.slice(op.start, op.end);
          })
          .join("");
        expect(reconstructedBase).toBe(base);
      }),
      { seed: PROPERTY_SEED, numRuns: NUM_RUNS },
    );
  });
});
