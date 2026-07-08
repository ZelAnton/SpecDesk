/**
 * The formatted editor's toolbar commands, extracted from the editor view so they can be exercised
 * against a bare {@link EditorState} (no DOM / EditorView). Each is a pure ProseMirror command over
 * the shared pm-markdown schema; {@link commandFor} maps a toolbar {@link FormatCommand} to one, and
 * {@link activeFormats} reports which commands are active at the current selection (for the toolbar's
 * pressed-button state). FormattedEditor.format() / activeFormats() are thin delegators to these.
 */

import { setBlockType, toggleMark, wrapIn } from "prosemirror-commands";
import type { MarkType, NodeType, ResolvedPos } from "prosemirror-model";
import { liftListItem, wrapInList } from "prosemirror-schema-list";
import type { Command, EditorState } from "prosemirror-state";
import { liftTarget } from "prosemirror-transform";
import { assertNever } from "../util/assert.js";
import {
  FORMAT_REGISTRY,
  type FormatCommand,
  type FormatKind,
  formatDef,
} from "./format-registry.js";
import { schema } from "./pm-markdown.js";

/** Resolve a required node type from the shared schema (these are always present — strict indexing
 *  otherwise types `schema.nodes[name]` as possibly-undefined). */
function nodeType(name: string): NodeType {
  const type = schema.nodes[name];
  if (type === undefined) {
    throw new Error(`SpecDesk: schema is missing the '${name}' node`);
  }
  return type;
}

/** Resolve a required mark type from the shared schema (see {@link nodeType}). */
function markType(name: string): MarkType {
  const type = schema.marks[name];
  if (type === undefined) {
    throw new Error(`SpecDesk: schema is missing the '${name}' mark`);
  }
  return type;
}

/** Whether any ancestor of the position is of the given node type. */
function inNodeType($pos: ResolvedPos, type: NodeType): boolean {
  for (let depth = $pos.depth; depth > 0; depth--) {
    if ($pos.node(depth).type === type) {
      return true;
    }
  }
  return false;
}

/** Toggle a textblock type at the selection: set it, or revert to a paragraph if already active. */
function toggleBlock(type: NodeType, attrs: Record<string, unknown>): Command {
  return (state, dispatch) => {
    const active = state.selection.$from.parent.hasMarkup(type, attrs);
    const target = active ? setBlockType(nodeType("paragraph")) : setBlockType(type, attrs);
    return target(state, dispatch);
  };
}

/**
 * Toggle a list. Already inside one of this type → lift out of it. Inside a list of the OTHER type
 * → convert it in place (rather than nesting a new list, which is what wrapInList would do). Not in
 * a list → wrap the selection in one.
 */
function toggleList(type: NodeType): Command {
  return (state, dispatch) => {
    const { $from } = state.selection;
    if (inNodeType($from, type)) {
      return liftListItem(nodeType("list_item"))(state, dispatch);
    }
    const bullet = nodeType("bullet_list");
    const ordered = nodeType("ordered_list");
    for (let depth = $from.depth; depth > 0; depth--) {
      const node = $from.node(depth);
      if (node.type === bullet || node.type === ordered) {
        // Preserve `tight` so a tight list doesn't become loose (blank lines between items) on
        // conversion; the new type fills its own remaining attrs (e.g. ordered's `order`).
        dispatch?.(state.tr.setNodeMarkup($from.before(depth), type, { tight: node.attrs.tight }));
        return true;
      }
    }
    return wrapInList(type)(state, dispatch);
  };
}

/**
 * Toggle a blockquote around the selection. Already inside one → lift the enclosing BLOCKQUOTE
 * specifically back out (not the generic `lift` command from prosemirror-commands, which lifts
 * whichever ancestor is closest to liftable — for a selection inside a list nested in a blockquote,
 * that closest ancestor is the LIST, so generic `lift` would strip the list and leave the quote
 * (`- a` inside `> ` becomes `> a`), the opposite of what toggling quote off should do). Targeting the
 * blockquote's own child range via `$from.blockRange($to, pred)` guarantees exactly the quote wrapper
 * comes off, leaving any nested list/heading/etc. untouched — mirroring the Code tract's line-prefix
 * toggle, which only ever touches the outermost `> ` marker.
 */
function toggleQuote(): Command {
  return (state, dispatch) => {
    const quote = nodeType("blockquote");
    if (inNodeType(state.selection.$from, quote)) {
      const { $from, $to } = state.selection;
      const range = $from.blockRange($to, (node) => node.type === quote);
      const target = range !== null ? liftTarget(range) : null;
      if (range === null || target == null) {
        return false;
      }
      dispatch?.(state.tr.lift(range, target).scrollIntoView());
      return true;
    }
    return wrapIn(quote)(state, dispatch);
  };
}

/**
 * Map a toolbar command to a ProseMirror command (toggles where it makes sense). The commands are
 * unguarded by design: the read-only / editability check lives in the caller (FormattedEditor.format),
 * so any other consumer — e.g. a future keymap binding — must apply its own gate before dispatching.
 */
export function commandFor(command: FormatCommand): Command {
  const { kind } = formatDef(command);
  switch (kind.type) {
    case "inline":
      return toggleMark(markType(kind.mark));
    case "heading":
      return toggleBlock(nodeType("heading"), { level: kind.level });
    case "fence":
      return toggleBlock(nodeType("code_block"), {});
    case "list":
      return toggleList(nodeType(kind.ordered ? "ordered_list" : "bullet_list"));
    case "quote":
      return toggleQuote();
    default:
      return assertNever(kind);
  }
}

/** Whether `type` is active at the selection: the stored/cursor marks for an empty selection, else
 *  present across the whole selected range. */
function markActive(state: EditorState, type: MarkType): boolean {
  const sel = state.selection;
  return sel.empty
    ? (state.storedMarks ?? sel.$head.marks()).some((m) => m.type === type)
    : state.doc.rangeHasMark(sel.from, sel.to, type);
}

/**
 * Whether the given format {@link FormatKind} is active at the selection — the per-kind reader
 * {@link activeFormats} maps over the registry. Inline marks read the selection's marks; a heading/fence
 * is the immediate textblock parent (only the registry's own heading levels light up — H3–H6 match no
 * entry); a list/quote is any enclosing ancestor, so a bullet nested in a blockquote lights up both.
 * `default: assertNever(kind)` keeps it exhaustive against FormatKind.
 */
function isActive(state: EditorState, kind: FormatKind): boolean {
  const { $head } = state.selection;
  switch (kind.type) {
    case "inline":
      return markActive(state, markType(kind.mark));
    case "heading":
      return $head.parent.type === nodeType("heading") && $head.parent.attrs.level === kind.level;
    case "fence":
      return $head.parent.type === nodeType("code_block");
    case "list":
      return inNodeType($head, nodeType(kind.ordered ? "ordered_list" : "bullet_list"));
    case "quote":
      return inNodeType($head, nodeType("blockquote"));
    default:
      return assertNever(kind);
  }
}

/** The toolbar commands currently active at the given selection (for the pressed-button state),
 *  derived by reading each registry command's kind against the selection. */
export function activeFormats(state: EditorState): Set<FormatCommand> {
  const result = new Set<FormatCommand>();
  for (const { id, kind } of FORMAT_REGISTRY) {
    if (isActive(state, kind)) {
      result.add(id);
    }
  }
  return result;
}
