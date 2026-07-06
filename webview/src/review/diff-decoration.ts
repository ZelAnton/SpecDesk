/**
 * Pure helpers shared by the two editor panes' review/compare overlays (PoC-6). The Code (CodeMirror)
 * and Formatted (ProseMirror) panes build their own decorations, but the *logic* — the change-kind
 * label, the removed-block marker text, and the inline word-diff application (thresholds + iteration) —
 * is identical and lives here so a fix lands in both panes at once. No editor/DOM imports.
 */

import { INLINE_DIFF_MAX_RATIO, wordDiff } from "./word-diff.js";

/** The change-annotation label for a diff kind (a {@link DiffKind}, or any string — an unrecognized kind
 *  degrades to a generic label rather than throwing). The local "Show changes" diffs the working copy
 *  against the last saved version, so every change is the current author's — hence "by you". Multi-author
 *  wording ("Updated by Max, Phil and Petr") awaits the review-against-others flow (git blame / a PR base). */
export function diffLabel(kind: string): string {
  switch (kind) {
    case "added":
      return "Added by you";
    case "changed":
      return "Updated by you";
    case "moved":
      return "Moved by you";
    case "removed":
      return "Deleted by you";
    default:
      return "Changed by you";
  }
}

/** The text for a removed-block marker (a stand-in for content absent from the head): the "Deleted by
 *  you" label, then a preview of the first removed line and a count when it spanned several. */
export function removedMarkerLabel(text: string): string {
  const lines = text.split("\n");
  const first = (lines[0] ?? "").trim();
  const preview = first || "(empty block)";
  return lines.length > 1
    ? `${diffLabel("removed")} — ${preview} (… ${lines.length} lines)`
    : `${diffLabel("removed")} — ${preview}`;
}

// Above this length the O(tokens²) word-LCS would stall on a pathological block — wash it whole instead.
const WORD_DIFF_MAX_CHARS = 4000;

/**
 * Word-diff `base`→`head` and report the changes through callbacks, in HEAD text offsets. Returns
 * `false` (and reports nothing) when it should fall back to a whole-block/line wash: the block is too
 * large, too much changed (over the ratio), or nothing word-level differs (e.g. a markup-only edit).
 * Each pane wraps the offsets into its own decorations (CodeMirror marks / ProseMirror inline + widget).
 */
export function applyWordDiff(
  base: string,
  head: string,
  onAdd: (start: number, end: number) => void,
  onRemove: (at: number, text: string) => void,
): boolean {
  if (head.length > WORD_DIFF_MAX_CHARS || base.length > WORD_DIFF_MAX_CHARS) {
    return false;
  }
  const diff = wordDiff(base, head);
  if (diff.changeRatio > INLINE_DIFF_MAX_RATIO || !diff.ops.some((op) => op.type !== "equal")) {
    return false;
  }
  for (const op of diff.ops) {
    if (op.type === "add") {
      onAdd(op.start, op.end);
    } else if (op.type === "del") {
      onRemove(op.start, op.text);
    }
  }
  return true;
}
