/**
 * Word-level (token) diff of two strings, in HEAD coordinates — the basis for the inline change
 * highlighting inside a "changed" paragraph/heading (PoC-6). The structural block diff is native; this
 * is a presentation refinement computed where the rendered text and its positions live (the webview).
 *
 * Tokens alternate word / whitespace runs so the offsets reconstruct the text exactly: the `equal` and
 * `add` ops, concatenated, ARE the head string, and `del` ops are zero-width insertion points in it.
 */

/** One run of a word diff. `add`/`equal` carry a head [start, end) range; `del` is a zero-width point. */
export interface WordOp {
  type: "equal" | "add" | "del";
  /** Head-text character offset where the run starts (for `del`, the deletion point). */
  start: number;
  /** Head-text character offset where the run ends (`start` for `del`). */
  end: number;
  /** The deleted base text (for `del`); "" otherwise. */
  text: string;
}

export interface WordDiff {
  ops: WordOp[];
  /** Fraction of content that changed: (added + deleted chars) / (base + head length), in [0, 1]. */
  changeRatio: number;
}

/** Above this changed fraction, inline word highlighting becomes confetti — both panes fall back to a
 *  whole-block/line wash (the user's "too significant" case). */
export const INLINE_DIFF_MAX_RATIO = 0.5;

/** Split into alternating word (`\S+`) / whitespace (`\s+`) tokens; concatenating them rebuilds `s`. */
function tokenize(s: string): string[] {
  return s.match(/\s+|\S+/g) ?? [];
}

/**
 * Word-level diff of base→head via a token LCS. Returns the head-coordinate ops plus a change ratio the
 * caller can threshold (a near-total rewrite is better shown as a whole-block wash than word confetti).
 */
export function wordDiff(base: string, head: string): WordDiff {
  const a = tokenize(base);
  const b = tokenize(head);
  const m = a.length;
  const n = b.length;

  // LCS length table over token equality, as a flat (m+1)·(n+1) array (strict-index friendly).
  const width = n + 1;
  const lcs = new Array<number>((m + 1) * width).fill(0);
  const at = (i: number, j: number): number => lcs[i * width + j] ?? 0;
  for (let i = m - 1; i >= 0; i--) {
    for (let j = n - 1; j >= 0; j--) {
      lcs[i * width + j] =
        a[i] === b[j] ? at(i + 1, j + 1) + 1 : Math.max(at(i + 1, j), at(i, j + 1));
    }
  }

  const ops: WordOp[] = [];
  let i = 0;
  let j = 0;
  let headPos = 0;
  let addedChars = 0;
  let deletedChars = 0;
  let pendingDel = "";

  // Deletions accrue until the next kept/added head token, then attach at the current head position.
  const flushDel = (): void => {
    if (pendingDel.length > 0) {
      ops.push({ type: "del", start: headPos, end: headPos, text: pendingDel });
      deletedChars += pendingDel.length;
      pendingDel = "";
    }
  };

  while (i < m && j < n) {
    const ta = a[i] ?? "";
    const tb = b[j] ?? "";
    if (ta === tb) {
      flushDel();
      ops.push({ type: "equal", start: headPos, end: headPos + tb.length, text: "" });
      headPos += tb.length;
      i++;
      j++;
    } else if (at(i + 1, j) >= at(i, j + 1)) {
      pendingDel += ta;
      i++;
    } else {
      flushDel();
      ops.push({ type: "add", start: headPos, end: headPos + tb.length, text: "" });
      addedChars += tb.length;
      headPos += tb.length;
      j++;
    }
  }
  while (i < m) {
    pendingDel += a[i] ?? "";
    i++;
  }
  flushDel();
  while (j < n) {
    const tb = b[j] ?? "";
    ops.push({ type: "add", start: headPos, end: headPos + tb.length, text: "" });
    addedChars += tb.length;
    headPos += tb.length;
    j++;
  }
  flushDel();

  const total = base.length + head.length;
  const changeRatio = total === 0 ? 0 : (addedChars + deletedChars) / total;
  return { ops, changeRatio };
}
