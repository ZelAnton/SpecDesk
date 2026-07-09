/**
 * The ordered SEMANTIC SYNC ANCHORS of the formatted pane: one anchor per rendered visual unit, paired
 * with the ProseMirror node whose DOM top measures it. This is the ONE ordered snapshot both height-sync
 * and BOTH sides of the scroll map read (formatted.ts turns it into measured boxes), so a tall table or a
 * long list aligns ROW-BY-ROW / ITEM-BY-ITEM instead of being one interpolated rectangle (T-101).
 *
 * What counts as a visual unit (the anchor contract):
 * - a heading, a paragraph (however hard-wrapped), a blockquote, a fenced/indented code block, a
 *   thematic break — ONE anchor each (a blockquote is one unit, not one per contained line);
 * - EACH GFM table row — the header row and every body row — its OWN anchor; the delimiter row (`|---|`)
 *   renders no row node, so it gets NO anchor (a consumer interpolates its position between the header
 *   and the first body row);
 * - EACH list item — tight or loose, a multi-paragraph item is still one anchor — with correctly
 *   representable NESTED items descended into and anchored in document order too.
 * Non-rendered Markdown (the table delimiter, link reference definitions, leading/inter-block blank
 * lines) produces no ProseMirror node, so it is simply absent from the list; scroll-map.ts interpolates
 * such lines MONOTONICALLY between the neighbouring real anchors.
 *
 * The spacer-insertion line height-sync needs is derived by the geometry step from anchor ORDER, never
 * from a node's own source span: nested-container source spans OVERLAP (a parent list item's span
 * contains its children's), so a span-based spacer line would land past the child anchors. Each anchor
 * carries only where its own rendered content starts/ends; the "point before the next anchor" is the
 * geometry step's business (block-geometry.ts / formatted.ts), not this projection's.
 *
 * Divergence is caught at the MINIMAL container. The markdown-it split and the ProseMirror parse come
 * from one shared config (md-config.ts), so their structure agrees for real documents; where a local
 * subtree disagrees (its child COUNTS differ), that one container coarsens to a single anchor rather
 * than pairing a source row/item against a foreign node by a clamped ordinal — every other anchor in the
 * document keeps working, the same "detect and bow out rather than guess" contract the top-level
 * {@link BlockMap} already applies (an empty map yields no anchors here too).
 *
 * Pure and layout-free: it holds source lines + ProseMirror POSITIONS, never DOM, so it is unit-tested
 * without a GUI. The source outline is rebuilt from the shared tokenizer's maps only on a geometry change
 * (an edit/resize) — never on a scroll frame, where the measured anchors are read from cache
 * (block-geometry.ts) and only searched.
 */

import type { Node as PmNode } from "prosemirror-model";
import type { BlockMap, BlockMapEntry } from "./block-map.js";
import { createTokenizer } from "./md-config.js";

/**
 * One rendered visual unit's structural anchor (no pixels yet): where its rendered content begins in
 * source, the ProseMirror node span to measure, and that node's own exclusive source end.
 */
export interface LeafAnchor {
  /** 0-based source line where this unit's rendered content begins. */
  readonly line: number;
  /**
   * 0-based EXCLUSIVE end of this unit's OWN source content (the markdown-it token's map end). For a
   * parent unit that holds nested containers (a list item with a sub-list) this still spans the nested
   * source; the geometry step clips it to the next anchor so the parent's own interpolation span
   * excludes the children that have anchors of their own.
   */
  readonly ownEnd: number;
  /** Document position where this unit's ProseMirror node starts (measure via `view.nodeDOM(from)`). */
  readonly from: number;
  /** `from + node.nodeSize` — the position just past the node. */
  readonly to: number;
}

// GFM tables + lists recognised identically to the source split and the PM parse (one shared config).
const tokenizer = createTokenizer();

// The block containers this projection descends into; everything else is a single-anchor leaf. Table
// rows have no sub-containers worth anchoring (cells aren't units); a list item can nest lists/tables.
const CONTAINER_TYPES = new Set(["table", "bullet_list", "ordered_list"]);

/**
 * A node of the source-side rendered-unit outline, mirroring the markdown-it token nesting — the shape
 * the ProseMirror descent walks in parallel to read each row/item's source line.
 */
interface SrcUnit {
  /** markdown-it token type without the `_open` suffix (e.g. "table", "bullet_list", "list_item", "tr"). */
  readonly type: string;
  /** 0-based source start line (token map[0]). */
  readonly line: number;
  /** 0-based exclusive source end line (token map[1]). */
  readonly end: number;
  /** Direct structural children in document order. */
  readonly children: SrcUnit[];
}

/**
 * Build the source-side outline: one {@link SrcUnit} per top-level markdown-it block, tables and lists
 * carrying their rows / items (recursively) as children. Pure — from the shared tokenizer's source maps.
 */
function sourceOutline(md: string): SrcUnit[] {
  const tokens = tokenizer.parse(md, {});
  const root: SrcUnit = { type: "root", line: 0, end: 0, children: [] };
  const stack: SrcUnit[] = [root];
  for (const token of tokens) {
    const top = stack[stack.length - 1];
    if (top === undefined) {
      break; // Unreachable: the root keeps the stack non-empty for the whole walk.
    }
    if (token.nesting === 1) {
      const map = token.map;
      const unit: SrcUnit = {
        type: token.type.replace(/_open$/, ""),
        line: map ? map[0] : top.line,
        end: map ? map[1] : top.end,
        children: [],
      };
      top.children.push(unit);
      stack.push(unit);
    } else if (token.nesting === -1) {
      if (stack.length > 1) {
        stack.pop();
      }
    } else if (token.type !== "inline" && token.map) {
      // A block-level leaf token (thematic break, fenced/indented code): a childless unit. Inline
      // tokens carry maps too but are never units, so they are skipped.
      top.children.push({ type: token.type, line: token.map[0], end: token.map[1], children: [] });
    }
  }
  return root.children;
}

/** The table's rendered rows in document order, flattening markdown-it's thead/tbody wrappers (which the
 *  ProseMirror table node does not have — its children are the rows directly). */
function tableRows(table: SrcUnit): SrcUnit[] {
  const rows: SrcUnit[] = [];
  for (const child of table.children) {
    if (child.type === "tr") {
      rows.push(child);
    } else if (child.type === "thead" || child.type === "tbody") {
      for (const grandchild of child.children) {
        if (grandchild.type === "tr") {
          rows.push(grandchild);
        }
      }
    }
  }
  return rows;
}

/** The list's direct items in document order (the counterpart of the PM list node's list_item children). */
function listItems(list: SrcUnit): SrcUnit[] {
  return list.children.filter((child) => child.type === "list_item");
}

/** A list item's own nested containers (a sub-list / nested table) in document order. */
function nestedContainers(item: SrcUnit): SrcUnit[] {
  return item.children.filter((child) => CONTAINER_TYPES.has(child.type));
}

/** The ProseMirror children of `node` that are themselves rendered containers, with their absolute
 *  document positions — the counterpart of {@link nestedContainers}. */
function pmContainers(node: PmNode, from: number): { node: PmNode; from: number }[] {
  const found: { node: PmNode; from: number }[] = [];
  node.forEach((child, offset) => {
    if (CONTAINER_TYPES.has(child.type.name)) {
      found.push({ node: child, from: from + 1 + offset });
    }
  });
  return found;
}

/** The single coarsened anchor for a container whose source/PM child counts diverged locally. */
function coarseAnchor(node: PmNode, from: number, src: SrcUnit): LeafAnchor {
  return { line: src.line, ownEnd: src.end, from, to: from + node.nodeSize };
}

/** The single top-level anchor for a non-container block, using the paired {@link BlockMap} entry so its
 *  line already accounts for any leading blank lines / reference definitions that ride with a first block. */
function topLevelAnchor(entry: BlockMapEntry): LeafAnchor {
  const block = entry.block;
  return {
    line: block.contentLineStart ?? block.lineStart,
    ownEnd: block.contentLineEnd ?? block.lineEnd + 1,
    from: entry.from,
    to: entry.to,
  };
}

/** Emit anchors for one container's children (table rows / list items), recursing into items. On a child
 *  COUNT mismatch the container coarsens to one anchor rather than pairing by a clamped ordinal. */
function emitContainer(node: PmNode, from: number, src: SrcUnit, out: LeafAnchor[]): void {
  const type = node.type.name;
  const children = type === "table" ? tableRows(src) : listItems(src);
  if (children.length !== node.childCount) {
    out.push(coarseAnchor(node, from, src));
    return;
  }
  const isList = type === "bullet_list" || type === "ordered_list";
  let pos = from + 1; // step inside the container node
  for (let i = 0; i < node.childCount; i++) {
    const childNode = node.child(i);
    const childSrc = children[i];
    if (childSrc !== undefined) {
      out.push({
        line: childSrc.line,
        ownEnd: childSrc.end,
        from: pos,
        to: pos + childNode.nodeSize,
      });
      if (isList) {
        emitItemNested(childNode, pos, childSrc, out);
      }
    }
    pos += childNode.nodeSize;
  }
}

/** Descend a list item into its nested containers (a sub-list / nested table), pairing them with the
 *  source item's own container children. A count mismatch coarsens LOCALLY — keep the item anchor, skip
 *  the nested ones — leaving the item as one unit rather than mispairing a nested row/item. */
function emitItemNested(
  itemNode: PmNode,
  itemFrom: number,
  itemSrc: SrcUnit,
  out: LeafAnchor[],
): void {
  const srcContainers = nestedContainers(itemSrc);
  const pm = pmContainers(itemNode, itemFrom);
  if (srcContainers.length !== pm.length) {
    return;
  }
  for (let i = 0; i < pm.length; i++) {
    const entry = pm[i];
    const srcChild = srcContainers[i];
    if (entry !== undefined && srcChild !== undefined) {
      emitContainer(entry.node, entry.from, srcChild, out);
    }
  }
}

/**
 * The ordered leaf anchors of the document behind `map`, paired against the source outline of `md`. Empty
 * when the top-level split diverged ({@link BlockMap} is empty) — the caller then reports zero blocks and
 * degrades to its no-op / first-line fallback, exactly as before this projection existed.
 */
export function buildLeafAnchors(map: BlockMap, md: string): LeafAnchor[] {
  if (map.isEmpty) {
    return [];
  }
  const entries = map.entries;
  const outline = sourceOutline(md);
  // The outline's top-level units come from the same shared tokenizer as the split, so they line up 1:1
  // with the paired blocks. If they somehow don't, fall back to one anchor per top-level block — safe
  // coarsening, never a mispaired ordinal.
  if (outline.length !== entries.length) {
    return entries.map(topLevelAnchor);
  }
  const out: LeafAnchor[] = [];
  for (let i = 0; i < entries.length; i++) {
    const entry = entries[i];
    const src = outline[i];
    if (entry === undefined || src === undefined) {
      continue;
    }
    if (CONTAINER_TYPES.has(entry.node.type.name)) {
      emitContainer(entry.node, entry.from, src, out);
    } else {
      out.push(topLevelAnchor(entry));
    }
  }
  return out;
}
