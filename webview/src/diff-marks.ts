/**
 * Expands a `diff.result` payload into the editors' flat, line-based {@link DiffMark}s. A changed
 * list/table carries per-child (row/item) entries; each child ordinal is resolved to its source line
 * range via the container's `childLineStarts` so the overlay highlights the individual row/item rather
 * than washing the whole container. Entries without children pass through as whole-block marks. Kept
 * pure (no editor/DOM) so it is unit-tested directly.
 */

import type { DiffMark } from "./editor.js";
import { splitTopLevelBlocks } from "./md-blocks.js";
import type { DiffEntryPayload } from "./protocol.js";

/**
 * @param entries the changed top-level blocks from `diff.result`.
 * @param text the head document the diff was computed against (same version), so child ordinals line
 *   up with its block split.
 */
export function expandDiffMarks(entries: DiffEntryPayload[], text: string): DiffMark[] {
  const hasContainers = entries.some((entry) => entry.children.length > 0);
  const childStartsByLine = new Map<number, number[]>();
  if (hasContainers) {
    for (const block of splitTopLevelBlocks(text)) {
      if (block.childLineStarts !== undefined) {
        childStartsByLine.set(block.lineStart, block.childLineStarts);
      }
    }
  }

  const marks: DiffMark[] = [];
  for (const entry of entries) {
    const starts = childStartsByLine.get(entry.lineStart);
    // No per-child diff (a plain block), or the container's children couldn't be resolved (a splitter
    // mismatch) → wash the whole block.
    if (entry.children.length === 0 || starts === undefined || starts.length === 0) {
      marks.push({
        kind: entry.kind,
        lineStart: entry.lineStart,
        lineEnd: entry.lineEnd,
        anchorLine: entry.anchorLine,
        removedText: entry.removedText,
        baseText: entry.baseText,
        baseSource: entry.baseSource,
      });
      continue;
    }
    for (const child of entry.children) {
      if (child.kind === "removed") {
        const anchor = starts[child.anchorIndex];
        marks.push({
          kind: "removed",
          lineStart: 0,
          lineEnd: 0,
          anchorLine: anchor ?? entry.lineEnd + 1,
          removedText: child.removedText,
          // A removed row/item: the Formatted pane anchors its marker at the row/item, not the container.
          sub: true,
        });
        continue;
      }
      const start = starts[child.childIndex];
      if (start === undefined) {
        continue;
      }
      // The child spans up to the next child's first line, or the container's end for the last child.
      const next = starts[child.childIndex + 1];
      marks.push({
        kind: child.kind,
        lineStart: start,
        lineEnd: next !== undefined ? next - 1 : entry.lineEnd,
        anchorLine: -1,
        removedText: "",
        sub: true,
        // A changed row/item carries its base text so the Formatted pane can word-diff it inline.
        baseText: child.baseText,
      });
    }
  }
  return marks;
}
