/**
 * Expands a `diff.result` payload into the editors' flat, line-based {@link DiffMark}s. A changed
 * list/table carries per-child (row/item) entries; each child ordinal is resolved to its source line
 * range via the container's `childLineStarts` so the overlay highlights the individual row/item rather
 * than washing the whole container. Entries without children pass through as whole-block marks. Kept
 * pure (no editor/DOM) so it is unit-tested directly.
 */

import { splitTopLevelBlocks } from "../editors/md-blocks.js";
import { assertNever } from "../util/assert.js";
import type { ChildDiffPayload, DiffEntryPayload } from "../wire/protocol.js";

/**
 * One changed block (or sub-block: a table row / list item) in the review overlay (PoC-6). The flat,
 * line-based form the editors render, expanded from the wire {@link DiffEntryPayload}s by
 * {@link expandDiffMarks}; defined here (with its producer) rather than in an editor module so the pure
 * diff layer never depends on CodeMirror/ProseMirror.
 *
 * Discriminated by `kind` — each case carries ONLY the fields it needs, so "a removed mark with a line
 * range" or "a changed mark with no base" is unrepresentable, and the editors gate their rendering on an
 * exhaustive match rather than a mix of `kind` / `sub` / `!== undefined` checks. `sub` is true for a
 * row/item mark inside a changed container (the Formatted pane omits the annotation pill for these — it
 * would clutter a table/list and a `<tr>` can't anchor an absolute label).
 */
export type DiffMark =
  | { kind: "added"; sub: boolean; lineStart: number; lineEnd: number }
  | { kind: "moved"; sub: boolean; lineStart: number; lineEnd: number }
  | {
      kind: "removed";
      sub: boolean;
      /** The head line the deleted block/row sat before (the overlay anchors its marker there). */
      anchorLine: number;
      /** The deleted content's base text (shown in the marker). */
      removedText: string;
    }
  | {
      kind: "changed";
      sub: boolean;
      lineStart: number;
      lineEnd: number;
      /** The base rendered/flattened text. The Formatted pane word-diffs it against the block's current
       *  text to highlight the changed words inline (or washes the whole block if too much changed). */
      baseText: string;
      /** The base raw source for the Code pane's inline word-diff, for both a whole-block changed mark and
       *  a row/item (sub) changed mark; `null` only when the wire carries no source to diff against
       *  (a container whose own children didn't resolve). The Formatted pane ignores it. */
      baseSource: string | null;
    };

/** Convert a whole-block wire entry (any kind) to its whole-block {@link DiffMark} (`sub: false`). */
function wholeBlockMark(entry: DiffEntryPayload): DiffMark {
  switch (entry.kind) {
    case "added":
    case "moved":
      return { kind: entry.kind, sub: false, lineStart: entry.lineStart, lineEnd: entry.lineEnd };
    case "removed":
      return {
        kind: "removed",
        sub: false,
        anchorLine: entry.anchorLine,
        removedText: entry.removedText,
      };
    case "changed":
      return {
        kind: "changed",
        sub: false,
        lineStart: entry.lineStart,
        lineEnd: entry.lineEnd,
        baseText: entry.baseText,
        baseSource: entry.baseSource,
      };
    default:
      return assertNever(entry);
  }
}

/** Expand one child of a changed container into a sub (row/item) {@link DiffMark}, resolving its ordinal
 *  to a source line range via the container's child line starts. */
function expandChild(
  marks: DiffMark[],
  child: ChildDiffPayload,
  starts: number[],
  containerEnd: number,
): void {
  if (child.kind === "removed") {
    const anchor = starts[child.anchorIndex];
    marks.push({
      kind: "removed",
      sub: true,
      // A removed row/item: the Formatted pane anchors its marker at the row/item, not the container.
      anchorLine: anchor ?? containerEnd + 1,
      removedText: child.removedText,
    });
    return;
  }
  const start = starts[child.childIndex];
  if (start === undefined) {
    return;
  }
  // The child spans up to the next child's first line, or the container's end for the last child.
  const next = starts[child.childIndex + 1];
  const lineEnd = next !== undefined ? next - 1 : containerEnd;
  if (child.kind === "changed") {
    // A changed row/item carries both its base text (Formatted pane word-diff) and its base source (Code
    // pane word-diff), so both panes highlight the changed row/item inline symmetrically.
    marks.push({
      kind: "changed",
      sub: true,
      lineStart: start,
      lineEnd,
      baseText: child.baseText,
      baseSource: child.baseSource,
    });
    return;
  }
  // An added / moved row/item: a whole-row wash, no inline word-diff.
  marks.push({ kind: child.kind, sub: true, lineStart: start, lineEnd });
}

/**
 * @param entries the changed top-level blocks from `diff.result`.
 * @param text the head document the diff was computed against (same version), so child ordinals line
 *   up with its block split.
 */
export function expandDiffMarks(entries: DiffEntryPayload[], text: string): DiffMark[] {
  const hasContainers = entries.some(
    (entry) => entry.kind === "changed" && entry.children.length > 0,
  );
  const childStartsByLine = new Map<number, number[]>();
  if (hasContainers) {
    for (const block of splitTopLevelBlocks(text)) {
      if (block.childLineStarts !== undefined) {
        // Key by the block's real token start (contentLineStart on the first block, when it carries
        // head content pulled back to line 0) so this matches entry.lineStart from the Markdig AST,
        // which always reports the real content start.
        childStartsByLine.set(block.contentLineStart ?? block.lineStart, block.childLineStarts);
      }
    }
  }

  const marks: DiffMark[] = [];
  for (const entry of entries) {
    // Only a changed container has per-child entries to descend into.
    if (entry.kind === "changed" && entry.children.length > 0) {
      const starts = childStartsByLine.get(entry.lineStart);
      // The container's children couldn't be resolved (a splitter mismatch) → wash the whole block.
      if (starts === undefined || starts.length === 0) {
        marks.push(wholeBlockMark(entry));
        continue;
      }
      for (const child of entry.children) {
        expandChild(marks, child, starts, entry.lineEnd);
      }
      continue;
    }
    marks.push(wholeBlockMark(entry));
  }
  return marks;
}
