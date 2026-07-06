/**
 * The shared ProseMirror ↔ Markdown bridge for the formatted editor (formatted.ts) and the
 * block-splice serializer (md-splice.ts). It extends prosemirror-markdown's CommonMark schema /
 * parser / serializer with **GFM tables**, so a table renders as a real table instead of collapsing
 * to raw pipe text. Both consumers use THIS parser so their top-level node counts agree with the
 * source-block split (block-splice correlates nodes ↔ source blocks 1:1).
 *
 * Tables are rendered + cell-text editable here; structural table edits (add/remove rows/columns) are
 * done in the source view for now. An untouched table is kept verbatim by block-splice, so the table
 * serializer below only runs when a table is actually edited.
 */

import {
  schema as baseSchema,
  defaultMarkdownParser,
  defaultMarkdownSerializer,
  MarkdownParser,
  MarkdownSerializer,
  type MarkdownSerializerState,
} from "prosemirror-markdown";
import { type Node as PmNode, Schema } from "prosemirror-model";
import { createTokenizer } from "./md-config.js";

/** CommonMark schema + minimal table nodes (header cells → <th>, body cells → <td>). */
export const schema = new Schema({
  nodes: baseSchema.spec.nodes
    .addToEnd("table", {
      content: "table_row+",
      group: "block",
      isolating: true,
      parseDOM: [{ tag: "table" }],
      toDOM: () => ["table", ["tbody", 0]],
    })
    .addToEnd("table_row", {
      content: "table_cell+",
      parseDOM: [{ tag: "tr" }],
      toDOM: () => ["tr", 0],
    })
    .addToEnd("table_cell", {
      content: "inline*",
      // `align` mirrors markdown-it's own column alignment (from the header separator row, e.g.
      // `:---:`) — `null` for a plain `---` column (no explicit alignment), else "left"/"right"/"center".
      // Every cell in a column carries the same value, matching markdown-it's own per-cell attrs (it
      // stamps the identical style onto every row's `td`/`th` for that column, not just the header).
      attrs: { header: { default: false }, align: { default: null } },
      isolating: true,
      parseDOM: [
        {
          tag: "td",
          getAttrs: (dom) => ({ header: false, align: alignFromStyle(dom.getAttribute("style")) }),
        },
        {
          tag: "th",
          getAttrs: (dom) => ({ header: true, align: alignFromStyle(dom.getAttribute("style")) }),
        },
      ],
      toDOM: (node) => [
        node.attrs.header ? "th" : "td",
        node.attrs.align ? { style: `text-align:${node.attrs.align}` } : {},
        0,
      ],
    }),
  // CommonMark marks (em/strong/code/link) plus GFM strikethrough, so the formatting toolbar's
  // strikethrough button has a mark to toggle. Serializes back to `~~…~~`.
  marks: baseSchema.spec.marks.addToEnd("strikethrough", {
    parseDOM: [{ tag: "s" }, { tag: "del" }, { tag: "strike" }],
    toDOM: () => ["s", 0],
  }),
});

// CommonMark + the GFM table and strikethrough rules. Used only to find structure (the serializer
// round-trips); inline rules match the CommonMark serializer so non-table content stays diff-stable.
// Built from the one shared config (md-config.ts) so its top-level block boundaries agree, by
// construction, with the source-block split (md-blocks.ts) the block-splice correlates each node
// against — see md-config.ts for why that agreement can no longer drift apart past nesting depth 20.
const tokenizer = createTokenizer();

/** Extract "left"/"right"/"center" from a `style="text-align:…"` value, else `null`. markdown-it's
 *  table plugin stamps this style onto a `th`/`td` token only for an explicitly aligned column
 *  (`:---`/`---:`/`:---:`) — a plain `---` column carries no such attribute at all. */
function alignFromStyle(style: string | null): string | null {
  return /text-align:\s*(left|right|center)/.exec(style ?? "")?.[1] ?? null;
}

export const parser = new MarkdownParser(schema, tokenizer, {
  ...defaultMarkdownParser.tokens,
  table: { block: "table" },
  thead: { ignore: true },
  tbody: { ignore: true },
  tr: { block: "table_row" },
  th: {
    block: "table_cell",
    getAttrs: (tok) => ({ header: true, align: alignFromStyle(tok.attrGet("style")) }),
  },
  td: {
    block: "table_cell",
    getAttrs: (tok) => ({ header: false, align: alignFromStyle(tok.attrGet("style")) }),
  },
  s: { mark: "strikethrough" },
});

/** One table cell's inline content as a single line of Markdown (pipes escaped). */
function cellMarkdown(cell: PmNode): string {
  const md = defaultMarkdownSerializer.serialize(
    schema.node("doc", null, [schema.node("paragraph", null, cell.content)]),
  );
  return md
    .replace(/\s*\n\s*/g, " ")
    .replace(/\|/g, "\\|")
    .trim();
}

/** The GFM separator-row marker for a column's alignment — `null`/anything else (no explicit
 *  alignment) is a plain "---"; mirrors {@link alignFromStyle}'s three explicit values so a table's
 *  alignment survives a round-trip through an edit instead of always collapsing to unaligned. */
function alignMarker(align: unknown): string {
  switch (align) {
    case "left":
      return ":---";
    case "right":
      return "---:";
    case "center":
      return ":---:";
    default:
      return "---";
  }
}

export const serializer = new MarkdownSerializer(
  {
    ...defaultMarkdownSerializer.nodes,
    // Bullet lists use "-" (common spec style) so a re-serialized list reads like hand-written Markdown.
    bullet_list: (state, node) => state.renderList(node, "  ", () => "- "),
    table: (state: MarkdownSerializerState, node: PmNode) => {
      node.forEach((row, _offset, rowIndex) => {
        // Rows are separated by a single newline; closeBlock() supplies the blank line AFTER the
        // table. The serializer must NOT leave its own trailing newline — block-splice re-attaches the
        // table's original trailing blank line, so a trailing newline here would double the gap after
        // an edited table. (Same idiom as the default code_block serializer.)
        if (rowIndex > 0) {
          state.ensureNewLine();
        }
        const cells: string[] = [];
        row.forEach((cell) => {
          cells.push(cellMarkdown(cell));
        });
        state.write(`| ${cells.join(" | ")} |`);
        if (rowIndex === 0) {
          state.ensureNewLine();
          // Every cell in a column carries the same `align` (see the table_cell attr comment) — read it
          // straight off this header row rather than needing a separate per-column lookup.
          const aligns: string[] = [];
          row.forEach((cell) => {
            aligns.push(alignMarker(cell.attrs.align));
          });
          state.write(`| ${aligns.join(" | ")} |`);
        }
      });
      state.closeBlock(node);
    },
    // Handled inside `table`; never serialized on their own.
    table_row: () => {},
    table_cell: () => {},
  },
  {
    ...defaultMarkdownSerializer.marks,
    strikethrough: { open: "~~", close: "~~", mixable: true, expelEnclosingWhitespace: true },
  },
);

// Mirror of the native renderer's `rewriteImageUrl` (SpecDesk.Markdown/Renderer.fs): a relative image
// URL is resolved against the document's directory and served via the custom `app://repo/…` scheme so
// the formatted (WYSIWYG) view can load it; absolute (scheme / root-anchored / anchor) URLs are left
// alone. This is a DISPLAY-only transform — the image node keeps its original relative `src`, so
// block-splice serialization still writes the author's path back to the Markdown.
const SCHEME_RE = /^[a-zA-Z][a-zA-Z0-9+.-]*:/;

/** Collapse `.`/`..` segments in a forward-slash relative path (mirror of native normalizeRelative). */
function normalizeRelative(path: string): string {
  const stack: string[] = [];
  for (const part of path.split("/")) {
    if (part === "" || part === ".") {
      // skip empty and current-dir segments
    } else if (part === "..") {
      stack.pop();
    } else {
      stack.push(part);
    }
  }
  return stack.join("/");
}

/** Resolve a Markdown image URL for display in the formatted view, given the doc dir (repo-relative). */
export function resolveImageSrc(docDir: string, url: string): string {
  if (url.length === 0 || SCHEME_RE.test(url) || url.startsWith("/") || url.startsWith("#")) {
    return url;
  }
  const combined = docDir === "" ? url : `${docDir.replace(/\/+$/, "")}/${url}`;
  return `app://repo/${normalizeRelative(combined)}`;
}
