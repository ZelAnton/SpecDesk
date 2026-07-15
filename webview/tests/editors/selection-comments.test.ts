import { describe, expect, it } from "vitest";
import {
  SelectionCommentSession,
  selectionDocumentKey,
  sourceSelection,
} from "../../src/editors/selection-comments.js";

describe("selected-text comment source model", () => {
  it("isolates the same document path across local branches and clones", () => {
    expect(selectionDocumentKey("D:/clones/a/docs/spec.md", undefined, "main")).not.toBe(
      selectionDocumentKey("D:/clones/a/docs/spec.md", undefined, "draft"),
    );
    expect(selectionDocumentKey("D:/clones/a/docs/spec.md", undefined, "main")).not.toBe(
      selectionDocumentKey("D:/clones/b/docs/spec.md", undefined, "main"),
    );
  });

  it("anchors a row selection after the complete table block", () => {
    const markdown = "Before\n\n| A | B |\n| - | - |\n| one | two |\n\nAfter\n";
    const from = markdown.indexOf("one");
    const to = markdown.indexOf("two") + 3;

    expect(sourceSelection(markdown, from, to)).toMatchObject({
      fromLine: 4,
      toLine: 4,
      anchorLine: 4,
      quote: "one | two",
    });
  });

  it("keeps a list-item selection on its last selected line instead of widening to the list", () => {
    const markdown = "- first selected item\n- second item\n- third item\n";
    const from = markdown.indexOf("first");
    const to = markdown.indexOf("item") + 4;

    expect(sourceSelection(markdown, from, to)).toMatchObject({
      fromLine: 0,
      toLine: 0,
      anchorLine: 0,
      quote: "first selected item",
    });
  });

  it("keeps local comments per document and reanchors an unchanged quote after edits", () => {
    const session = new SelectionCommentSession();
    const markdown = "First\n\nSelected words\n";
    session.setDocument("docs/a.md", markdown);
    const selection = sourceSelection(markdown, markdown.indexOf("Selected"), markdown.length - 1);
    expect(selection).not.toBeNull();
    if (selection === null) throw new Error("Expected a source selection");
    session.add(selection, "Local note");

    session.reanchor("New heading\n\nFirst\n\nSelected words\n");
    expect(session.all()[0]).toMatchObject({ fromLine: 4, anchorLine: 4, body: "Local note" });

    session.setDocument("docs/b.md");
    expect(session.all()).toHaveLength(0);
    session.setDocument("docs/a.md");
    expect(session.all()).toHaveLength(1);
  });

  it("maps many common short selections in bounded near-linear time", () => {
    const markdown = Array.from({ length: 20_000 }, (_, index) => `a repeated line ${index}`).join(
      "\n",
    );
    const session = new SelectionCommentSession();
    session.setDocument("large.md", markdown);
    const selection = sourceSelection(markdown, 0, 1);
    if (selection === null) throw new Error("Expected a source selection");
    for (let index = 0; index < 200; index++) {
      session.add(selection, `comment ${index}`);
    }
    const started = performance.now();
    session.reanchor(`inserted\n${markdown}`);
    const elapsed = performance.now() - started;
    expect(session.all()).toHaveLength(200);
    expect(session.all()[199]?.fromLine).toBeGreaterThan(0);
    expect(elapsed).toBeLessThan(500);
  });

  it("does not absorb text inserted exactly after the selected range", () => {
    const markdown = "selected rest\n";
    const session = new SelectionCommentSession();
    session.setDocument("boundary.md", markdown);
    const selection = sourceSelection(markdown, 0, "selected".length);
    if (selection === null) throw new Error("Expected a source selection");
    session.add(selection, "Boundary note");

    session.reanchor("selected NEW rest\n");

    expect(session.all()[0]).toMatchObject({ quote: "selected", fromOffset: 0, toOffset: 8 });
  });

  it("keeps a table comment after a row appended at the previous table boundary", () => {
    const markdown = "| A | B |\n| - | - |\n| one | two |\n\nAfter\n";
    const session = new SelectionCommentSession();
    session.setDocument("table-boundary.md", markdown);
    const selection = sourceSelection(
      markdown,
      markdown.indexOf("one"),
      markdown.indexOf("two") + 3,
    );
    if (selection === null) throw new Error("Expected table selection");
    session.add(selection, "Table note");

    session.reanchor(markdown.replace("\n\nAfter", "\n| three | four |\n\nAfter"));

    expect(session.all()[0]).toMatchObject({ anchorKind: "table", anchorLine: 3 });
  });

  it("keeps a middle-line comment through a multi-hunk Quote formatting edit", () => {
    const markdown = "one\ntarget\nthree\n";
    const session = new SelectionCommentSession();
    session.setDocument("multi-hunk.md", markdown);
    const from = markdown.indexOf("target");
    const selection = sourceSelection(markdown, from, from + "target".length);
    if (selection === null) throw new Error("Expected middle-line selection");
    session.add(selection, "Keep this line");

    session.reanchor("> one\n> target\n> three\n");

    expect(session.all()[0]).toMatchObject({
      fromLine: 1,
      toLine: 1,
      anchorLine: 1,
      quote: "target",
    });
  });

  it("uses patience anchors when an insertion and deletion keep the line count equal", () => {
    const markdown = "A\nB\nC\n";
    const session = new SelectionCommentSession();
    session.setDocument("equal-count.md", markdown);
    const selection = sourceSelection(markdown, 0, 1);
    if (selection === null) throw new Error("Expected first-line selection");
    session.add(selection, "Follow A");

    session.reanchor("X\nA\nB\n");

    expect(session.all()[0]).toMatchObject({
      fromLine: 1,
      toLine: 1,
      anchorLine: 1,
      quote: "A",
    });
  });

  it("keeps an exact selection when Bold wraps it on both sides", () => {
    const markdown = "before target after\n";
    const session = new SelectionCommentSession();
    session.setDocument("bold-wrapper.md", markdown);
    const from = markdown.indexOf("target");
    const selection = sourceSelection(markdown, from, from + "target".length);
    if (selection === null) throw new Error("Expected target selection");
    session.add(selection, "Keep target");

    session.reanchor("before **target** after\n");

    expect(session.all()[0]).toMatchObject({
      fromOffset: from + 2,
      toOffset: from + 2 + "target".length,
      quote: "target",
      anchorLine: 0,
    });
  });
});
