/**
 * The pane-independent "overlay plan" for the review/compare diff (PoC-6 / T-078). It turns the flat,
 * line-based {@link DiffMark}s into an ordered list of rendering-agnostic instructions — which
 * blocks/lines to wash, which changed blocks to word-diff inline, and where (and with what text) to plant
 * a removed-block marker — so the Code (CodeMirror) and Formatted (ProseMirror) panes share ONE anchoring
 * policy and ONE removed-text policy instead of each re-deriving them. Those had already drifted: the two
 * panes anchored a deleted block differently (the Code pane clamped a raw line by line count; the
 * Formatted pane scanned for the first block starting at/after the anchor line), and the whole-block
 * marker leaked raw Markdown into the WYSIWYG pane. Each pane is now a thin adapter that maps these
 * instructions onto its own decorations. No editor/DOM imports — pure and unit-tested directly.
 */

import { assertNever } from "../util/assert.js";
import { diffLabel } from "./diff-decoration.js";
import type { DiffMark } from "./diff-marks.js";

/**
 * Where a removed-block marker anchors, resolved ONCE here so both panes plant it in the same place. A
 * deletion has no head content of its own, so the marker stands between the surrounding head content:
 *
 *  - `end`   — the deletion followed all head content: after the last block.
 *  - `block` — a removed TOP-LEVEL block: before head top-level block `blockIndex` (whose 0-based source
 *              start line is `line`, for the line-addressed Code pane). The single anchoring policy is
 *              "the first head top-level block whose source starts at or after the wire anchor line" —
 *              robust to where trailing blank lines land (a block's END can differ between the native AST
 *              that set the anchor and a pane's own splitter; a block's START does not).
 *  - `child` — a removed ROW/ITEM inside a changed container: before the row/item at 0-based source
 *              `line`, so the marker sits between the surrounding rows/items rather than at the container
 *              edge. A deletion after the last row/item carries the line just past the container; the pane
 *              then plants the marker at the document end (nothing at/after that line to sit before).
 */
export type RemovedAnchor =
  | { at: "end" }
  | { at: "block"; blockIndex: number; line: number }
  | { at: "child"; line: number };

/** Wash a whole block/line range by change kind — an added or moved block/row. (A changed block yields
 *  an {@link InlineInstruction} instead; its whole-block wash is the pane's own fallback when the inline
 *  word-diff bows out, since the two panes fall back differently — the Code pane always keeps the wash as
 *  its block-level signal, the Formatted pane washes only when inline doesn't apply.) */
export interface FillInstruction {
  type: "fill";
  kind: "added" | "moved";
  sub: boolean;
  lineStart: number;
  lineEnd: number;
}

/** Refine a changed block/row with an inline word-diff (highlight the changed words) rather than a wash.
 *  Carries BOTH bases so either pane word-diffs in its own coordinate space: `baseText` (flattened) for
 *  the Formatted pane against the rendered text, `baseSource` (raw) for the Code pane against the source
 *  (null only when the wire carries no source to diff against — the Code pane keeps just a line wash then). */
export interface InlineInstruction {
  type: "inline";
  sub: boolean;
  lineStart: number;
  lineEnd: number;
  baseText: string;
  baseSource: string | null;
}

/** Plant a stand-in marker for a removed block (or row/item), absent from the head. `label` is the fully
 *  composed marker text under the single removed-text policy (see {@link removedMarkerText}); `anchor` is
 *  the single resolved placement (see {@link RemovedAnchor}). */
export interface RemovedInstruction {
  type: "removed";
  sub: boolean;
  label: string;
  anchor: RemovedAnchor;
}

/** One instruction of the pane-independent overlay plan. */
export type OverlayInstruction = FillInstruction | InlineInstruction | RemovedInstruction;

// A leading block-level Markdown marker on the first previewed line — an ATX heading (`#`…), a blockquote
// (`>`), or an unordered (`-`/`*`/`+`) or ordered (`1.`/`1)`) list marker, after optional indentation.
// Stripped from a WHOLE-BLOCK removed marker's preview (see removedMarkerText) so the deletion reads as
// plain language in BOTH panes.
const LEADING_BLOCK_MARKER = /^[ \t]*(?:#{1,6}[ \t]+|>[ \t]?|[-*+][ \t]+|\d{1,9}[.)][ \t]+)/;

/** Strip the leading block-level Markdown markers from one line, repeatedly (so a nested `> - x` reduces
 *  to `x`), capped so a pathological line cannot loop. */
function stripLeadingBlockMarkers(line: string): string {
  let out = line;
  for (let i = 0; i < 8 && LEADING_BLOCK_MARKER.test(out); i++) {
    out = out.replace(LEADING_BLOCK_MARKER, "");
  }
  return out;
}

/**
 * The single, documented removed-marker text policy. A deletion is a plain-language stand-in ("Deleted by
 * you — <preview>") that previews the deleted content's first line and counts the rest.
 *
 * The preview is FLATTENED plain text, uniformly for a whole block and a row/item and identically in both
 * panes — so the marker never leaks Markdown syntax, which would be doubly wrong in the WYSIWYG pane whose
 * whole point is that the author never sees markup. A row/item's text already arrives flattened from
 * native (`ChildDiffPayload.removedText`); a whole block arrives as its raw base source
 * (`DiffEntryPayload.removedText`, a source slice), so the leading block-level markers are stripped from
 * its previewed line here. Only the leading BLOCK markers of the previewed line are stripped — inline
 * emphasis/link syntax is left as-is (over-eager inline stripping risks mangling a one-line preview, and a
 * stray `*` reads far milder than a leading `## ` or `- `).
 */
export function removedMarkerText(removedText: string, sub: boolean): string {
  const lines = removedText.split("\n");
  const firstRaw = lines[0] ?? "";
  const first = (sub ? firstRaw : stripLeadingBlockMarkers(firstRaw)).trim();
  const preview = first || "(empty block)";
  const suffix = lines.length > 1 ? ` (… ${lines.length} lines)` : "";
  return `${diffLabel("removed")} — ${preview}${suffix}`;
}

/** Resolve a removed mark's placement under the single anchoring policy (see {@link RemovedAnchor}). */
function resolveRemovedAnchor(
  anchorLine: number,
  sub: boolean,
  blockLineStarts: number[],
): RemovedAnchor {
  // A removed ROW/ITEM anchors at its following row/item's source line (the pane resolves the exact node);
  // the anchor line is already that child line, or the line just past the container for a deletion after
  // the last child (the pane then falls back to the document end).
  if (sub) {
    return { at: "child", line: anchorLine };
  }
  // A removed TOP-LEVEL block anchors before the first head block starting at/after the anchor line;
  // none → after all head content.
  for (let i = 0; i < blockLineStarts.length; i++) {
    const start = blockLineStarts[i];
    if (start !== undefined && start >= anchorLine) {
      return { at: "block", blockIndex: i, line: start };
    }
  }
  return { at: "end" };
}

/**
 * Build the pane-independent overlay plan from the expanded diff marks, in mark order (so a pane's
 * decorations layer in the same order it would have produced them).
 *
 * `blockLineStarts` is the 0-based source start line of each head TOP-LEVEL block, in document order —
 * used only to resolve a removed top-level block's anchor. Both panes derive it from the SAME head
 * document (via {@link splitTopLevelBlocks}), so the resolved anchor is identical between them.
 */
export function buildOverlayPlan(
  marks: DiffMark[],
  blockLineStarts: number[],
): OverlayInstruction[] {
  const plan: OverlayInstruction[] = [];
  for (const mark of marks) {
    switch (mark.kind) {
      case "added":
      case "moved":
        plan.push({
          type: "fill",
          kind: mark.kind,
          sub: mark.sub,
          lineStart: mark.lineStart,
          lineEnd: mark.lineEnd,
        });
        break;
      case "changed":
        plan.push({
          type: "inline",
          sub: mark.sub,
          lineStart: mark.lineStart,
          lineEnd: mark.lineEnd,
          baseText: mark.baseText,
          baseSource: mark.baseSource,
        });
        break;
      case "removed":
        plan.push({
          type: "removed",
          sub: mark.sub,
          label: removedMarkerText(mark.removedText, mark.sub),
          anchor: resolveRemovedAnchor(mark.anchorLine, mark.sub, blockLineStarts),
        });
        break;
      default:
        assertNever(mark);
    }
  }
  return plan;
}
