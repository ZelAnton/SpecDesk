import type { Node as PmNode } from "prosemirror-model";
import { describe, expect, it } from "vitest";
import { parser, resolveImageSrc, serializer } from "../../src/editors/pm-markdown.js";

function parse(md: string): PmNode {
  const doc = parser.parse(md);
  if (doc === null) {
    throw new Error("parse returned null");
  }
  return doc;
}

// S-14: markdown-it's table plugin records each column's alignment (from the header separator row) as
// a `style="text-align:…"` attribute on every `th`/`td` in that column; the schema previously had
// nowhere to keep it, so parsing then re-serializing a table always emitted a plain, unaligned `---`.
describe("table column alignment", () => {
  it("round-trips left/right/center/none column alignment", () => {
    const md = "| A | B | C | D |\n| :--- | ---: | :---: | --- |\n| 1 | 2 | 3 | 4 |\n";
    // The serializer leaves no trailing newline of its own for the doc's last block (block-splice is
    // what re-attaches the original trailing blank-line gap elsewhere).
    expect(serializer.serialize(parse(md))).toBe(md.trimEnd());
  });

  it("assigns the same alignment to a header cell and its column's body cells", () => {
    const md = "| A | B |\n| ---: | --- |\n| 1 | 2 |\n";
    const table = parse(md).child(0);
    const headerRow = table.child(0);
    const bodyRow = table.child(1);
    expect(headerRow.child(0).attrs.align).toBe("right");
    expect(bodyRow.child(0).attrs.align).toBe("right");
    expect(headerRow.child(1).attrs.align).toBeNull();
    expect(bodyRow.child(1).attrs.align).toBeNull();
  });
});

// resolveImageSrc mirrors the native renderer's rewriteImageUrl (SpecDesk.Markdown/Renderer.fs): the
// formatted view must produce the same app://repo/… URLs the preview does, or images won't load.
describe("resolveImageSrc", () => {
  it("resolves a relative link against the document directory", () => {
    expect(resolveImageSrc("specs/api", "images/diagram.png")).toBe(
      "app://repo/specs/api/images/diagram.png",
    );
  });

  it("resolves a relative link at the repo root (empty docDir)", () => {
    expect(resolveImageSrc("", "images/diagram.png")).toBe("app://repo/images/diagram.png");
  });

  it("collapses . and .. segments", () => {
    expect(resolveImageSrc("specs/api", "../assets/./logo.png")).toBe(
      "app://repo/specs/assets/logo.png",
    );
  });

  it("leaves absolute URLs untouched", () => {
    expect(resolveImageSrc("specs", "https://example.com/x.png")).toBe("https://example.com/x.png");
    expect(resolveImageSrc("specs", "data:image/png;base64,AAAA")).toBe(
      "data:image/png;base64,AAAA",
    );
  });

  it("leaves root-anchored, anchor, and empty URLs untouched", () => {
    expect(resolveImageSrc("specs", "/abs/x.png")).toBe("/abs/x.png");
    expect(resolveImageSrc("specs", "#frag")).toBe("#frag");
    expect(resolveImageSrc("specs", "")).toBe("");
  });
});
