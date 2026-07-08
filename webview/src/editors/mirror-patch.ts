/**
 * The shared "minimal patch" primitive both directions of the Split cross-pane mirror use (index.ts
 * onEditorChange / onFormattedChange). A source-pane edit and its WYSIWYG twin describe the SAME
 * document, so mirroring one into the other need only touch the span that actually differs — not
 * rebuild the whole passive pane, which is what collapsed its caret/selection and (for the formatted
 * pane) reset its undo history and re-parsed every block per tick (T-063 / T-085, folded into T-097).
 *
 * Two granularities of the same prefix/suffix idea:
 *   - {@link computeTextPatch} — a single changed CHARACTER span, for the CodeMirror source editor
 *     (positions are UTF-16 offsets, so a text patch maps straight onto a CodeMirror change).
 *   - {@link commonEnds} — the count of unchanged leading/trailing BLOCKS, for the ProseMirror
 *     formatted editor (whose positions are tree offsets, so it splices whole top-level nodes and
 *     re-parses only the changed middle span).
 */

/** A single-range replacement: replace `[from, to)` of the OLD text with `insert` to obtain the new. */
export interface TextPatch {
  /** UTF-16 offset in the old text where the changed span begins (length of the common prefix). */
  from: number;
  /** UTF-16 offset in the old text where the changed span ends (old length minus the common suffix). */
  to: number;
  /** The new text for `[from, to)` — the changed span of the new text. */
  insert: string;
}

/**
 * The smallest single-range edit turning `oldText` into `newText`: strip the longest common prefix and
 * the longest (non-overlapping) common suffix, leaving only the middle span that differs. Returns
 * `null` when the two are identical (nothing to apply). Comparison is per UTF-16 code unit — matching
 * how both the string offsets here and CodeMirror document positions are measured — so the patch maps
 * onto a CodeMirror `changes` spec verbatim, and the caret, selection, scroll anchor and tracked
 * image-insert markers all remap naturally through it (a whole-document replace made that mapping
 * degenerate, forcing the old restore-markers workaround).
 */
export function computeTextPatch(oldText: string, newText: string): TextPatch | null {
  if (oldText === newText) {
    return null;
  }
  const oldLen = oldText.length;
  const newLen = newText.length;
  const maxPrefix = Math.min(oldLen, newLen);
  let prefix = 0;
  while (prefix < maxPrefix && oldText.charCodeAt(prefix) === newText.charCodeAt(prefix)) {
    prefix++;
  }
  // The suffix may not eat into the prefix already claimed, so it is bounded by the shorter remaining
  // tail — otherwise a repeated run (e.g. "aa" → "aaa") would double-count the overlap.
  const maxSuffix = maxPrefix - prefix;
  let suffix = 0;
  while (
    suffix < maxSuffix &&
    oldText.charCodeAt(oldLen - 1 - suffix) === newText.charCodeAt(newLen - 1 - suffix)
  ) {
    suffix++;
  }
  return {
    from: prefix,
    to: oldLen - suffix,
    insert: newText.slice(prefix, newLen - suffix),
  };
}

/** The number of leading and trailing elements common to both sequences (compared with `===`), never
 *  overlapping — the block-level analogue of {@link computeTextPatch}'s prefix/suffix. The formatted
 *  editor feeds it each pane's top-level block source slices so it can keep every unchanged block's
 *  existing ProseMirror node and re-parse only the changed middle span. */
export function commonEnds<T>(
  a: readonly T[],
  b: readonly T[],
): { prefix: number; suffix: number } {
  const max = Math.min(a.length, b.length);
  let prefix = 0;
  while (prefix < max && a[prefix] === b[prefix]) {
    prefix++;
  }
  let suffix = 0;
  while (suffix < max - prefix && a[a.length - 1 - suffix] === b[b.length - 1 - suffix]) {
    suffix++;
  }
  return { prefix, suffix };
}
