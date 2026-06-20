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

import MarkdownIt from "markdown-it";
import {
  schema as baseSchema,
  defaultMarkdownParser,
  defaultMarkdownSerializer,
  MarkdownParser,
  MarkdownSerializer,
  type MarkdownSerializerState,
} from "prosemirror-markdown";
import { type Node as PmNode, Schema } from "prosemirror-model";

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
      attrs: { header: { default: false } },
      isolating: true,
      parseDOM: [
        { tag: "td", attrs: { header: false } },
        { tag: "th", attrs: { header: true } },
      ],
      toDOM: (node) => [node.attrs.header ? "th" : "td", 0],
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
const tokenizer = new MarkdownIt("commonmark", { html: false })
  .enable("table")
  .enable("strikethrough");

export const parser = new MarkdownParser(schema, tokenizer, {
  ...defaultMarkdownParser.tokens,
  table: { block: "table" },
  thead: { ignore: true },
  tbody: { ignore: true },
  tr: { block: "table_row" },
  th: { block: "table_cell", getAttrs: () => ({ header: true }) },
  td: { block: "table_cell", getAttrs: () => ({ header: false }) },
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
          state.write(`| ${cells.map(() => "---").join(" | ")} |`);
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
