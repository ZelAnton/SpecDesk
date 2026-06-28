/**
 * The formatted editor's toolbar commands, extracted from the editor view so they can be exercised
 * against a bare {@link EditorState} (no DOM / EditorView). Each is a pure ProseMirror command over
 * the shared pm-markdown schema; {@link commandFor} maps a toolbar {@link FormatCommand} to one, and
 * {@link activeFormats} reports which commands are active at the current selection (for the toolbar's
 * pressed-button state). FormattedEditor.format() / activeFormats() are thin delegators to these.
 */

import { lift, setBlockType, toggleMark, wrapIn } from "prosemirror-commands";
import type { MarkType, NodeType, ResolvedPos } from "prosemirror-model";
import { liftListItem, wrapInList } from "prosemirror-schema-list";
import type { Command, EditorState } from "prosemirror-state";
import type { FormatCommand } from "./md-format.js";
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

/** Toggle a blockquote around the selection. */
function toggleQuote(): Command {
  return (state, dispatch) =>
    inNodeType(state.selection.$from, nodeType("blockquote"))
      ? lift(state, dispatch)
      : wrapIn(nodeType("blockquote"))(state, dispatch);
}

/** Map a toolbar command to a ProseMirror command (toggles where it makes sense). */
export function commandFor(command: FormatCommand): Command {
  switch (command) {
    case "bold":
      return toggleMark(markType("strong"));
    case "italic":
      return toggleMark(markType("em"));
    case "strike":
      return toggleMark(markType("strikethrough"));
    case "h1":
      return toggleBlock(nodeType("heading"), { level: 1 });
    case "h2":
      return toggleBlock(nodeType("heading"), { level: 2 });
    case "code":
      return toggleBlock(nodeType("code_block"), {});
    case "bullet":
      return toggleList(nodeType("bullet_list"));
    case "ordered":
      return toggleList(nodeType("ordered_list"));
    default:
      return toggleQuote();
  }
}

/** The toolbar commands currently active at the given selection (for the pressed-button state). */
export function activeFormats(state: EditorState): Set<FormatCommand> {
  const sel = state.selection;
  const $head = sel.$head;
  const result = new Set<FormatCommand>();

  const markActive = (type: MarkType): boolean =>
    sel.empty
      ? (state.storedMarks ?? $head.marks()).some((m) => m.type === type)
      : state.doc.rangeHasMark(sel.from, sel.to, type);
  if (markActive(markType("strong"))) result.add("bold");
  if (markActive(markType("em"))) result.add("italic");
  if (markActive(markType("strikethrough"))) result.add("strike");

  const parent = $head.parent;
  if (parent.type === schema.nodes.heading) {
    // Only the toolbar's own levels light up; H3–H6 leave both heading buttons unpressed.
    if (parent.attrs.level === 1) {
      result.add("h1");
    } else if (parent.attrs.level === 2) {
      result.add("h2");
    }
  } else if (parent.type === schema.nodes.code_block) {
    result.add("code");
  }
  for (let depth = $head.depth; depth > 0; depth--) {
    const type = $head.node(depth).type;
    if (type === schema.nodes.bullet_list) {
      result.add("bullet");
    } else if (type === schema.nodes.ordered_list) {
      result.add("ordered");
    } else if (type === schema.nodes.blockquote) {
      result.add("quote");
    }
  }
  return result;
}
