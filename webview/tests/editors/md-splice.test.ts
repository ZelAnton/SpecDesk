import type { Node as PmNode } from "prosemirror-model";
import { describe, expect, it } from "vitest";
import { splitTopLevelBlocks } from "../../src/editors/md-blocks.js";
import { serializeWithSplice } from "../../src/editors/md-splice.js";
import { parser, schema } from "../../src/editors/pm-markdown.js";

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

  it("editing one cell's text preserves the table's column alignment (S-14 acceptance case)", () => {
    // Fixing a typo in a right-/center-aligned column used to rewrite the whole table with no
    // alignment at all — pm-markdown had nowhere to carry the alignment through the schema.
    const original = "| A | B |\n| ---: | :---: |\n| 1 | 2 |\n";
    const doc = parse(original);
    const cell = (text: string, header: boolean, align: string): PmNode =>
      schema.node("table_cell", { header, align }, [schema.text(text)]);
    const editedTable = schema.node("table", null, [
      schema.node("table_row", null, [cell("A", true, "right"), cell("B", true, "center")]),
      schema.node("table_row", null, [cell("1 fixed", false, "right"), cell("2", false, "center")]),
    ]);
    const edited = withChildReplaced(doc, 0, editedTable);

    const out = serializeWithSplice(original, edited);

    expect(out).toBe("| A | B |\n| ---: | :---: |\n| 1 fixed | 2 |\n");
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

  // S-12 regression guards: the whole-document fallback above serializes only `edited`'s NODES, so a
  // link reference definition — which markdown-it resolves into its reference map with no node at all —
  // used to vanish outright the instant an unrelated block was added or removed elsewhere in the same
  // edit. The trivial repro from the finding: press Enter in the WYSIWYG view to start a new paragraph
  // in any document that has a reference definition.

  it("the whole-document fallback preserves a reference definition when a block is added", () => {
    const md = "[a]: http://x\n\nSee [a] link.\n";
    const doc = parse(md);
    const children: PmNode[] = [];
    doc.forEach((c) => {
      children.push(c);
    });
    children.push(paragraph("New paragraph."));
    const edited = schema.node("doc", null, children);

    const out = serializeWithSplice(md, edited);

    expect(out).toContain("[a]: http://x");
    expect(out).toContain("New paragraph.");
  });

  it("the whole-document fallback preserves a reference definition when a block is removed", () => {
    const md = "[a]: http://x\n\n# H\n\nSee [a] link.\n\nAnother paragraph.\n";
    const doc = parse(md);
    const children: PmNode[] = [];
    doc.forEach((c) => {
      children.push(c);
    });
    children.pop(); // drop the last paragraph — the block count no longer lines up 1:1
    const edited = schema.node("doc", null, children);

    const out = serializeWithSplice(md, edited);

    expect(out).toContain("[a]: http://x");
  });

  it("pressing Enter to start a new paragraph keeps a reference definition (S-12 acceptance case)", () => {
    const md = "[a]: http://x\n\nSee [a] link.\n";
    const doc = parse(md);
    const children: PmNode[] = [];
    doc.forEach((c) => {
      children.push(c);
    });
    children.splice(1, 0, paragraph("New line."));
    const edited = schema.node("doc", null, children);

    const out = serializeWithSplice(md, edited);

    expect(out).toContain("[a]: http://x");
    expect(out).toContain("New line.");
  });

  it("preserves a reference definition in a document with no other top-level content", () => {
    // The degenerate whole-document-fallback case: `original` has no ProseMirror node for the definition
    // to hang off of at all (the parsed doc is a single, auto-filled empty paragraph), so the fallback's
    // preservation logic must treat the WHOLE original text as "non-node" content, not just a gap
    // between two real nodes.
    const md = "[a]: http://x\n";
    const doc = parse(md);
    const children: PmNode[] = [];
    doc.forEach((c) => {
      children.push(c);
    });
    children.push(paragraph("New content."));
    const edited = schema.node("doc", null, children);

    const out = serializeWithSplice(md, edited);

    expect(out).toContain("[a]: http://x");
    expect(out).toContain("New content.");
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

  // S-11 regression guards: CodeMirror's document model normalizes every line break to "\n" (that fix is
  // host-side — HostController re-applies the file's on-disk style at every disk-write site), but this
  // module's own responsibility is narrower: an untouched block must keep its original bytes, "\r"
  // included, rather than the whole-document fallback silently dropping it.

  it("no-op round-trip is byte-identical for a CRLF document (no whole-document fallback)", () => {
    const crlf = RICH.replaceAll("\n", "\r\n");
    expect(serializeWithSplice(crlf, parse(crlf))).toBe(crlf);
  });

  it("preserves every untouched CRLF block verbatim when one block is edited", () => {
    const crlf = RICH.replaceAll("\n", "\r\n");
    const edited = withChildReplaced(parse(crlf), 1, paragraph("Edited intro."));
    const out = serializeWithSplice(crlf, edited);

    // Every block from "## Section" onward — table, code, lists, hr, trailing — is untouched and must
    // still carry its original "\r\n", not have it silently dropped.
    const fromSection = crlf.indexOf("## Section");
    expect(out.slice(out.indexOf("## Section"))).toBe(crlf.slice(fromSection));
    expect(out.startsWith("# Title\r\n")).toBe(true);
  });

  it("re-serializes a CHANGED block as LF, even inside an otherwise-CRLF document", () => {
    // Documents the one line-ending seam this module does not paper over: `serializeBlock` goes through
    // the shared ProseMirror `serializer`, which always emits "\n" — a freshly re-emitted block is LF
    // regardless of the document's surrounding style. This is fine in practice: on the actual save path
    // (HostController, S-11's real fix) EVERY block's text passes through the webview's CodeMirror model
    // before reaching disk, which normalizes to "\n" anyway, so re-applying the file's line-ending style
    // once at the very end (ApplyLineEnding) makes the changed block's own seam here moot — it is
    // re-normalized regardless. Pinned here so a future change to this behavior is a deliberate choice,
    // not a silent regression.
    const crlf = RICH.replaceAll("\n", "\r\n");
    const edited = withChildReplaced(parse(crlf), 1, paragraph("Edited intro."));
    const out = serializeWithSplice(crlf, edited);

    // "Edited intro." + the join's own "\n" (the freshly serialized node, LF) + "\r" (the original blank
    // separator line's tail content, preserved verbatim by tailLines) + "\n" (the join back to the next,
    // untouched "## Section\r\n..." block) — a visible mix of LF (the new node) and "\r" (the untouched gap).
    const editedBlock = out.slice(out.indexOf("Edited intro."), out.indexOf("## Section"));
    expect(editedBlock).toBe("Edited intro.\n\r\n");
  });
});

// T-059 regression guard: block-splice correlates the source-block split (md-blocks) with the
// ProseMirror parse (pm-markdown) 1:1, so the two must agree on where the top-level block boundaries
// fall. That agreement used to hold only by convention — md-blocks tokenized with the default preset
// (block-nesting cap 100) and pm-markdown with commonmark + table + strikethrough (cap 20), which
// tokenize identically only up to nesting depth 20. Past that, the two caps truncate a deeply nested
// structure at different points, so they disagree on the top-level boundaries. Both now derive from one
// shared config (md-config.ts), pinning the same cap for both, so the agreement is constructive.
describe("tokenizer config agreement past nesting depth 20 (T-059)", () => {
  // A bullet list nested 25 levels deep (past the shared cap), then a blank line and a paragraph. With
  // the pre-fix cap mismatch, md-blocks (cap 100) parsed the list deeply and ended it before the
  // paragraph → TWO top-level blocks (list, paragraph), while pm-markdown (cap 20) truncated the deep
  // list and absorbed the trailing paragraph into it → ONE top-level node. That blocks.length(2) !=
  // childCount(1) mismatch forced serializeWithSplice onto its whole-document fallback for the whole
  // document, reflowing every hard-wrapped paragraph and list marker on any edit.
  function deeplyNestedListThenParagraph(): string {
    let md = "";
    for (let level = 0; level < 25; level++) {
      md += `${"  ".repeat(level)}- item${level}\n`;
    }
    return `${md}\nAfter the deeply nested list.\n`;
  }

  it("the source-block split and the ProseMirror parse agree on the top-level block count", () => {
    const md = deeplyNestedListThenParagraph();
    const doc = parser.parse(md);
    expect(doc).not.toBeNull();
    // The invariant serializeWithSplice's fidelity check depends on — equal, not merely close. Before the
    // shared config this was 2 (split) vs 1 (parse); now both tokenizers truncate at the same depth.
    expect(splitTopLevelBlocks(md).length).toBe((doc as PmNode).childCount);
  });

  it("no-op round-trips a document nested deeper than 20 byte-for-byte (no whole-document fallback)", () => {
    // The observable payoff of the agreement: because the counts now line up, serializeWithSplice keeps
    // every block verbatim instead of taking the reflowing whole-document fallback the mismatch forced.
    const md = deeplyNestedListThenParagraph();
    expect(serializeWithSplice(md, parse(md))).toBe(md);
  });
});
