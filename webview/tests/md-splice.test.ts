import type { Node as PmNode } from "prosemirror-model";
import { describe, expect, it } from "vitest";
import { serializeWithSplice } from "../src/md-splice.js";
import { parser, schema } from "../src/pm-markdown.js";

const RICH = `# Title

Intro paragraph that is
hard-wrapped across two lines.

## Section

- one
- two
- three

> a quote
> second line

| A | B |
| - | - |
| 1 | 2 |

\`\`\`ts
const x = 1;
\`\`\`

1. first
2. second

---

Trailing paragraph.
`;

function parse(md: string): PmNode {
  const doc = parser.parse(md);
  if (doc === null) {
    throw new Error("parse returned null");
  }
  return doc;
}

/** A copy of `doc` with the top-level child at `index` replaced by `node`. */
function withChildReplaced(doc: PmNode, index: number, node: PmNode): PmNode {
  const children: PmNode[] = [];
  doc.forEach((child) => {
    children.push(child);
  });
  children[index] = node;
  return schema.node("doc", null, children);
}

function paragraph(text: string): PmNode {
  return schema.node("paragraph", null, [schema.text(text)]);
}

function bulletList(items: string[]): PmNode {
  return schema.node(
    "bullet_list",
    null,
    items.map((t) => schema.node("list_item", null, [paragraph(t)])),
  );
}

describe("serializeWithSplice", () => {
  it("no-op round-trip is byte-identical", () => {
    expect(serializeWithSplice(RICH, parse(RICH))).toBe(RICH);
  });

  it("editing one paragraph changes only that block; everything after is verbatim", () => {
    const edited = withChildReplaced(parse(RICH), 1, paragraph("Edited intro."));
    const out = serializeWithSplice(RICH, edited);

    expect(out).toContain("Edited intro.");
    expect(out).not.toContain("Intro paragraph that is");
    // every block from "## Section" onward (table, code, lists, hr, trailing) is byte-identical
    expect(out.slice(out.indexOf("## Section"))).toBe(RICH.slice(RICH.indexOf("## Section")));
    // the heading before the edited block is preserved verbatim too
    expect(out.startsWith("# Title\n")).toBe(true);
  });

  it("adding a list item keeps every other block verbatim (the acceptance edit)", () => {
    const edited = withChildReplaced(parse(RICH), 3, bulletList(["one", "two", "three", "four"]));
    const out = serializeWithSplice(RICH, edited);

    expect(out).toContain("- four");
    // the unchanged items kept their "-" marker (no *), so the list diff is just the new line
    expect(out).toContain("- one");
    expect(out).not.toContain("* one");
    // everything from the blockquote onward is byte-identical
    expect(out.slice(out.indexOf("> a quote"))).toBe(RICH.slice(RICH.indexOf("> a quote")));
  });

  it("preserves an untouched GFM table verbatim when another block is edited", () => {
    const edited = withChildReplaced(parse(RICH), 9, paragraph("New trailing."));
    const out = serializeWithSplice(RICH, edited);
    expect(out).toContain("| A | B |\n| - | - |\n| 1 | 2 |");
    expect(out).toContain("New trailing.");
  });

  it("serializes an edited table back to a pipe table", () => {
    const original = "| A | B |\n| - | - |\n| 1 | 2 |\n";
    const cell = (text: string, header: boolean): PmNode =>
      schema.node("table_cell", { header }, [schema.text(text)]);
    const edited = schema.node("doc", null, [
      schema.node("table", null, [
        schema.node("table_row", null, [cell("A", true), cell("B", true)]),
        schema.node("table_row", null, [cell("X", false), cell("2", false)]),
      ]),
    ]);
    const out = serializeWithSplice(original, edited);
    // Exactly the edited table with the original's single trailing newline — no doubled blank line
    // (the serializer leaves no trailing newline of its own; block-splice re-attaches the gap).
    expect(out).toBe("| A | B |\n| --- | --- |\n| X | 2 |\n");
  });

  it("preserves a link reference definition when its adjacent block is edited", () => {
    // The ref-def folds into the heading's source block but has no ProseMirror node; re-serializing
    // the heading must keep it verbatim rather than dropping it.
    const original = "# H\n\n[a]: http://x\n\nSee [a] link.\n";
    const heading = schema.node("heading", { level: 1 }, [schema.text("H edited")]);
    const edited = withChildReplaced(parse(original), 0, heading);
    const out = serializeWithSplice(original, edited);
    expect(out).toBe("# H edited\n\n[a]: http://x\n\nSee [a] link.\n");
  });

  it("falls back to a whole-document serialize when a block is added (count changes)", () => {
    const doc = parse(RICH);
    const children: PmNode[] = [];
    doc.forEach((c) => {
      children.push(c);
    });
    children.push(paragraph("Appended paragraph."));
    const edited = schema.node("doc", null, children);
    const out = serializeWithSplice(RICH, edited);
    expect(out).toContain("Appended paragraph.");
    expect(out.length).toBeGreaterThan(0);
  });

  // S-13 regression guards: a leading gap used to make `blocks.length` permanently one MORE than
  // `originalDoc.childCount`, so `serializeWithSplice` ALWAYS took the whole-document fallback for such
  // a document — reflowing every hard-wrapped paragraph and list marker even with no real edit.

  it("no-op round-trip is byte-identical for a document with leading blank lines (no whole-document fallback)", () => {
    const md = `\n\n${RICH}`;
    expect(serializeWithSplice(md, parse(md))).toBe(md);
  });

  it("no-op round-trip is byte-identical for a document with a leading reference definition", () => {
    const md = "[a]: http://x\n\nSee [a] link.\n";
    expect(serializeWithSplice(md, parse(md))).toBe(md);
  });

  it("editing the first block of a document with leading blank lines preserves them verbatim", () => {
    const md = "\n\n# H\n\npara\n";
    const heading = schema.node("heading", { level: 1 }, [schema.text("H edited")]);
    const edited = withChildReplaced(parse(md), 0, heading);
    const out = serializeWithSplice(md, edited);
    expect(out).toBe("\n\n# H edited\n\npara\n");
  });

  it("editing the sole block of a document with a leading reference definition preserves it verbatim", () => {
    const md = "[a]: http://x\n\nSee [a] link.\n";
    const edited = withChildReplaced(parse(md), 0, paragraph("Edited text."));
    const out = serializeWithSplice(md, edited);
    expect(out).toBe("[a]: http://x\n\nEdited text.\n");
  });
});
