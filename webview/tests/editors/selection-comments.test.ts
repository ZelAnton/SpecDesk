import { describe, expect, it, vi } from "vitest";
import {
  opaqueSelectionStorageKey,
  SelectionCommentSession,
  type SelectionCommentStorage,
  type StoredSelectionComment,
  selectionDocumentKey,
  sourceSelection,
} from "../../src/editors/selection-comments.js";

class MemoryStorage implements SelectionCommentStorage {
  readonly values = new Map<string, readonly StoredSelectionComment[]>();
  readonly loadCalls: Array<{ principal: string; document: string }> = [];
  readonly saveCalls: Array<{ principal: string; document: string; serialized: string }> = [];
  readonly saveStarts: Array<{ principal: string; document: string }> = [];
  readonly failSaveDocuments = new Set<string>();
  failLoad = false;
  failSave = false;
  loadGate: Promise<void> | null = null;
  saveGate: Promise<void> | null = null;

  async load(principal: string, document: string) {
    this.loadCalls.push({ principal, document });
    await this.loadGate;
    if (this.failLoad) throw new Error("load failed");
    return structuredClone(this.values.get(`${principal}\0${document}`) ?? []);
  }

  async save(principal: string, document: string, comments: readonly StoredSelectionComment[]) {
    this.saveStarts.push({ principal, document });
    await this.saveGate;
    if (this.failSave || this.failSaveDocuments.has(document)) {
      throw new Error("quota exceeded");
    }
    const serialized = JSON.stringify(comments);
    this.saveCalls.push({ principal, document, serialized });
    this.values.set(`${principal}\0${document}`, structuredClone(comments));
  }
}

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

  it("keeps local comments per document and reanchors an unchanged quote after edits", async () => {
    const session = new SelectionCommentSession(new MemoryStorage());
    const markdown = "First\n\nSelected words\n";
    session.setDocument("docs/a.md", markdown);
    const selection = sourceSelection(markdown, markdown.indexOf("Selected"), markdown.length - 1);
    expect(selection).not.toBeNull();
    if (selection === null) throw new Error("Expected a source selection");
    session.add(selection, "Local note");

    session.reanchor("New heading\n\nFirst\n\nSelected words\n");
    expect(session.all()[0]).toMatchObject({ fromLine: 4, anchorLine: 4, body: "Local note" });

    await session.flushPersistence();
    await session.setDocument("docs/b.md", "Other\n");
    expect(session.all()).toHaveLength(0);
    await session.setDocument("docs/a.md", "New heading\n\nFirst\n\nSelected words\n");
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

  it("persists a complete editable thread under the clone, branch and document identity", async () => {
    const storage = new MemoryStorage();
    const markdown = "Intro\n\nSelected paragraph\n\nEnd\n";
    const key = selectionDocumentKey("D:/clone-a/spec.md", "org/repo", "draft/one");
    const selection = sourceSelection(
      markdown,
      markdown.indexOf("Selected"),
      markdown.indexOf("paragraph") + "paragraph".length,
    );
    if (selection === null) throw new Error("Expected selection");
    const session = new SelectionCommentSession(storage);
    await session.setPrincipal("alice");
    await session.setDocument(key, markdown);
    session.begin(selection);
    expect(session.view().draft).toMatchObject({ mode: "create", anchorLine: 2 });
    expect(session.submitDraft("Root note\nwith detail")).toBe(true);
    const root = session.all()[0];
    if (root === undefined) throw new Error("Expected root comment");
    expect(session.beginReply(root.id)).toBe(true);
    expect(session.submitDraft("A reply")).toBe(true);
    const reply = session.all()[0]?.replies[0];
    if (reply === undefined) throw new Error("Expected reply");
    expect(session.beginEdit(root.id, reply.id)).toBe(true);
    expect(session.submitDraft("Edited reply")).toBe(true);
    expect(session.beginEdit(root.id)).toBe(true);
    expect(session.submitDraft("Edited root")).toBe(true);
    await session.flushPersistence();
    expect(storage.saveCalls.at(-1)?.serialized).not.toContain("D:/clone-a");
    expect(storage.saveCalls.at(-1)?.serialized).not.toContain("org/repo");

    const restored = new SelectionCommentSession(storage);
    await restored.setPrincipal("alice");
    await restored.setDocument(key, markdown);
    expect(restored.all()[0]).toMatchObject({ body: "Edited root" });
    expect(restored.all()[0]?.replies[0]).toMatchObject({ body: "Edited reply" });
    expect(restored.delete(root.id, reply.id)).toBe(true);
    expect(restored.all()[0]?.replies).toHaveLength(0);
    expect(restored.delete(root.id)).toBe(true);
    expect(restored.all()).toHaveLength(0);
    await restored.flushPersistence();
    expect(storage.values.get(`github:alice\0${key}`)).toEqual([]);
  });

  it("reanchors durable comments from bounded fingerprints when the document changed while closed", async () => {
    const storage = new MemoryStorage();
    const original = "Before\nselected text\nAfter\n";
    const selection = sourceSelection(
      original,
      original.indexOf("selected"),
      original.indexOf("text") + 4,
    );
    if (selection === null) throw new Error("Expected selection");
    const firstSession = new SelectionCommentSession(storage);
    await firstSession.setDocument("external-edit.md", original);
    firstSession.add(selection, "Durable note");
    await firstSession.flushPersistence();

    const restarted = new SelectionCommentSession(storage);
    await restarted.setDocument("external-edit.md", `Inserted\n${original}`);

    expect(restarted.all()[0]).toMatchObject({
      fromLine: 2,
      anchorLine: 2,
      quote: "selected text",
    });
    const serialized = storage.saveCalls[0]?.serialized ?? "";
    expect(serialized).not.toContain(original);
    expect(serialized).not.toContain("external-edit.md");
    expect(serialized.length).toBeLessThan(2_000);
  });

  it("uses surrounding context to restore an externally edited selected range", async () => {
    const storage = new MemoryStorage();
    const original = "Before\nselected text\nAfter\n";
    const selection = sourceSelection(
      original,
      original.indexOf("selected"),
      original.indexOf("text") + 4,
    );
    if (selection === null) throw new Error("Expected selection");
    const seed = new SelectionCommentSession(storage);
    await seed.setDocument("edited-range.md", original);
    seed.add(selection, "Follow the edited selection");
    await seed.flushPersistence();

    const changed = "Inserted\nBefore\nselected changed text\nAfter\n";
    const restored = new SelectionCommentSession(storage);
    await restored.setDocument("edited-range.md", changed);

    expect(restored.all()[0]).toMatchObject({
      anchorState: "attached",
      fromLine: 2,
      anchorLine: 2,
      quote: "selected changed text",
    });
  });

  it("detaches a thread when edited context resolves to multiple equal ranges", async () => {
    const storage = new MemoryStorage();
    const before = "B".repeat(96);
    const after = "A".repeat(96);
    const original = `${before}selected text${after}`;
    const selection = sourceSelection(
      original,
      before.length,
      before.length + "selected text".length,
    );
    if (selection === null) throw new Error("Expected selection");
    const seed = new SelectionCommentSession(storage);
    await seed.setDocument("ambiguous.md", original);
    seed.add(selection, "Do not guess");
    await seed.flushPersistence();

    const repeated = `${before}selected changed text${after}`;
    const restored = new SelectionCommentSession(storage);
    await restored.setDocument("ambiguous.md", `${repeated}\n${repeated}`);

    expect(restored.all()[0]).toMatchObject({
      anchorState: "detached",
      fromOffset: 0,
      toOffset: 0,
      quote: "",
    });
  });

  it("detaches when exact duplicate search exceeds its bounded candidate set", async () => {
    const storage = new MemoryStorage();
    const original = "a".repeat(200);
    const selection = sourceSelection(original, 0, original.length);
    if (selection === null) throw new Error("Expected selection");
    const seed = new SelectionCommentSession(storage);
    await seed.setDocument("many-exact-duplicates.md", original);
    seed.add(selection, "Bounded ambiguity");
    await seed.flushPersistence();

    const restored = new SelectionCommentSession(storage);
    await restored.setDocument("many-exact-duplicates.md", "a".repeat(6_000));

    expect(restored.all()[0]).toMatchObject({ anchorState: "detached" });
  });

  it("detaches a thread when the selected range was deleted", async () => {
    const storage = new MemoryStorage();
    const original = "Before\nselected text\nAfter\n";
    const selection = sourceSelection(
      original,
      original.indexOf("selected"),
      original.indexOf("text") + 4,
    );
    if (selection === null) throw new Error("Expected selection");
    const seed = new SelectionCommentSession(storage);
    await seed.setDocument("deleted-range.md", original);
    seed.add(selection, "Keep as unresolved");
    await seed.flushPersistence();

    const restored = new SelectionCommentSession(storage);
    await restored.setDocument("deleted-range.md", "Inserted\nBefore\nAfter\n");

    expect(restored.all()[0]).toMatchObject({ anchorState: "detached" });
  });

  it("keeps an externally edited table selection attached after the complete table", async () => {
    const storage = new MemoryStorage();
    const original = "| A | B |\n| - | - |\n| one | two |\n\nAfter\n";
    const selection = sourceSelection(
      original,
      original.indexOf("one"),
      original.indexOf("two") + 3,
    );
    if (selection === null) throw new Error("Expected table selection");
    const seed = new SelectionCommentSession(storage);
    await seed.setDocument("edited-table.md", original);
    seed.add(selection, "Follow this row");
    await seed.flushPersistence();

    const changed = original.replace("one | two", "one changed | two");
    const restored = new SelectionCommentSession(storage);
    await restored.setDocument("edited-table.md", changed);

    expect(restored.all()[0]).toMatchObject({
      anchorState: "attached",
      anchorKind: "table",
      anchorLine: 2,
      quote: "one changed | two",
    });
  });

  it("bounds fingerprint verification for a long repeated selection", async () => {
    const storage = new MemoryStorage();
    const repeated = "a".repeat(100_000);
    const markdown = repeated.repeat(20);
    const selection = sourceSelection(markdown, 0, repeated.length);
    if (selection === null) throw new Error("Expected selection");
    const seed = new SelectionCommentSession(storage);
    await seed.setDocument("long-repeat.md", markdown);
    seed.add(selection, "Long anchor");
    await seed.flushPersistence();

    const started = performance.now();
    let indexBuilds = 0;
    let verifiedCharacters = 0;
    const restored = new SelectionCommentSession(storage, {
      onDocumentIndexBuilt: () => indexBuilds++,
      onFingerprintVerified: (length) => {
        verifiedCharacters += length;
      },
    });
    await restored.setDocument("long-repeat.md", markdown);

    expect(restored.all()).toHaveLength(1);
    expect(indexBuilds).toBe(1);
    expect(verifiedCharacters).toBeLessThanOrEqual(5_000);
    expect(performance.now() - started).toBeLessThan(500);
    expect(storage.saveCalls[0]?.serialized.length).toBeLessThan(2_000);
  });

  it("builds one source index when restoring hundreds of threads", async () => {
    const storage = new MemoryStorage();
    const markdown = "Before\nselected text\nAfter\n";
    const selection = sourceSelection(
      markdown,
      markdown.indexOf("selected"),
      markdown.indexOf("text") + 4,
    );
    if (selection === null) throw new Error("Expected selection");
    const seed = new SelectionCommentSession(storage);
    await seed.setDocument("many-threads.md", markdown);
    for (let comment = 0; comment < 300; comment++) {
      seed.add(selection, `Thread ${comment}`);
    }
    await seed.flushPersistence();

    let indexBuilds = 0;
    let verifiedCharacters = 0;
    const restored = new SelectionCommentSession(storage, {
      onDocumentIndexBuilt: () => indexBuilds++,
      onFingerprintVerified: (length) => {
        verifiedCharacters += length;
      },
    });
    await restored.setDocument("many-threads.md", markdown);

    expect(restored.all()).toHaveLength(300);
    expect(indexBuilds).toBe(1);
    expect(verifiedCharacters).toBe(300);
  });

  it("dismisses stale drafts when document identity changes and does not submit them later", () => {
    const storage = new MemoryStorage();
    const session = new SelectionCommentSession(storage);
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    session.setDocument("clone-a\0main\0a.md", markdown);
    session.begin(selection);
    session.setDocument("clone-b\0main\0a.md", markdown);

    expect(session.view().draft).toBeNull();
    expect(session.submitDraft("must not cross documents")).toBe(false);
    expect(session.all()).toHaveLength(0);
  });

  it("keeps a create draft and a saved thread anchored through edits before, inside and after it", () => {
    const storage = new MemoryStorage();
    const session = new SelectionCommentSession(storage);
    const markdown = "Before\nselected words\nAfter\n";
    const from = markdown.indexOf("selected");
    const selection = sourceSelection(markdown, from, from + "selected words".length);
    if (selection === null) throw new Error("Expected selection");
    session.setDocument("reanchor-draft.md", markdown);
    session.begin(selection);
    session.reanchor("New\nBefore\nselected better words\nAfter changed\n");
    expect(session.view().draft).toMatchObject({ fromLine: 2, anchorLine: 2 });
    expect(session.submitDraft("Mapped draft")).toBe(true);
    expect(session.all()[0]).toMatchObject({ fromLine: 2, anchorLine: 2 });
  });

  it("detaches a live thread when its range is deleted and reattaches if the text returns", async () => {
    const storage = new MemoryStorage();
    const markdown = "Before\nselected text\nAfter\n";
    const selection = sourceSelection(
      markdown,
      markdown.indexOf("selected"),
      markdown.indexOf("text") + 4,
    );
    if (selection === null) throw new Error("Expected selection");
    const session = new SelectionCommentSession(storage);
    await session.setDocument("live-delete.md", markdown);
    session.add(selection, "Keep the original anchor");
    await session.flushPersistence();

    session.reanchor("Before\nAfter\n");
    expect(session.all()[0]).toMatchObject({ anchorState: "detached", quote: "" });
    await session.flushPersistence();
    expect(storage.saveCalls.at(-1)?.serialized).toContain("selected text");

    session.reanchor(markdown);
    expect(session.all()[0]).toMatchObject({
      anchorState: "attached",
      quote: "selected text",
    });
  });

  it("keeps a table draft outside the complete table when rows are added", () => {
    const session = new SelectionCommentSession(new MemoryStorage());
    const markdown = "| A | B |\n| - | - |\n| one | two |\n\nAfter\n";
    const selection = sourceSelection(
      markdown,
      markdown.indexOf("one"),
      markdown.indexOf("two") + 3,
    );
    if (selection === null) throw new Error("Expected table selection");
    session.setDocument("table-draft.md", markdown);
    session.begin(selection);
    session.reanchor(markdown.replace("\n\nAfter", "\n| three | four |\n\nAfter"));

    expect(session.view().draft).toMatchObject({ anchorKind: "table", anchorLine: 3 });
  });

  it("keeps persistence off the typing hot path and coalesces reanchor writes", async () => {
    vi.useFakeTimers();
    try {
      const storage = new MemoryStorage();
      const session = new SelectionCommentSession(storage);
      const original = "selected text\n";
      await session.setDocument("typing.md", original);
      const selection = sourceSelection(original, 0, "selected".length);
      if (selection === null) throw new Error("Expected selection");
      session.add(selection, "note");
      await session.flushPersistence();
      storage.saveCalls.length = 0;

      for (let index = 0; index < 100; index++) {
        session.reanchor(`${"x".repeat(index)}selected text\n`);
      }

      expect(storage.saveCalls).toHaveLength(0);
      await vi.advanceTimersByTimeAsync(499);
      expect(storage.saveCalls).toHaveLength(0);
      await vi.advanceTimersByTimeAsync(1);
      expect(storage.saveCalls).toHaveLength(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it("surfaces quota failures and succeeds through the retry path", async () => {
    const storage = new MemoryStorage();
    storage.failSave = true;
    const session = new SelectionCommentSession(storage);
    await session.setDocument("quota.md", "selected\n");
    const selection = sourceSelection("selected\n", 0, 8);
    if (selection === null) throw new Error("Expected selection");
    session.add(selection, "keep this in session");
    await session.flushPersistence();
    expect(session.view()).toMatchObject({ persistence: "error" });
    expect(session.view().persistenceMessage).toContain("retry");
    expect(session.all()).toHaveLength(1);

    storage.failSave = false;
    await session.retryPersistence();
    expect(session.view().persistence).toBe("saved");
    expect(storage.saveCalls).toHaveLength(1);
  });

  it("keeps a newer in-flight snapshot authoritative over a stale retry queued behind it", async () => {
    const storage = new MemoryStorage();
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    const session = new SelectionCommentSession(storage);
    await session.setDocument("monotonic-retry.md", markdown);

    storage.failSave = true;
    session.add(selection, "Older pending note");
    await session.flushPersistence();
    expect(session.view()).toMatchObject({ persistence: "error" });

    storage.failSave = false;
    let releaseSave: (() => void) | undefined;
    storage.saveGate = new Promise<void>((resolve) => {
      releaseSave = resolve;
    });
    session.add(selection, "Newer authoritative note");
    const newerSave = session.flushPersistence();
    await vi.waitFor(() => expect(storage.saveStarts).toHaveLength(2));
    const staleRetry = session.retryPersistence();
    releaseSave?.();
    await Promise.all([newerSave, staleRetry]);

    expect(storage.saveCalls).toHaveLength(1);
    expect(storage.values.get("signed-out\0monotonic-retry.md")?.map((item) => item.body)).toEqual([
      "Older pending note",
      "Newer authoritative note",
    ]);
    const restored = new SelectionCommentSession(storage);
    await restored.setDocument("monotonic-retry.md", markdown);
    expect(restored.all().map((comment) => comment.body)).toEqual([
      "Older pending note",
      "Newer authoritative note",
    ]);
  });

  it("blocks mutations after a load failure until retry restores the saved snapshot", async () => {
    const storage = new MemoryStorage();
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    const seed = new SelectionCommentSession(storage);
    await seed.setDocument("load-failure.md", markdown);
    const root = seed.add(selection, "Stored root");
    if (root === null) throw new Error("Expected comment");
    expect(seed.beginReply(root.id)).toBe(true);
    expect(seed.submitDraft("Stored reply")).toBe(true);
    await seed.flushPersistence();
    const storedBefore = structuredClone(storage.values.get("signed-out\0load-failure.md"));
    storage.saveCalls.length = 0;

    storage.failLoad = true;
    const session = new SelectionCommentSession(storage);
    expect(await session.setDocument("load-failure.md", markdown)).toBe(false);
    expect(session.view()).toMatchObject({
      commentsAvailable: false,
      persistence: "error",
    });
    expect(session.view().persistenceMessage).toContain("unavailable until");

    expect(session.add(selection, "Must not overwrite storage")).toBeNull();
    session.begin(selection);
    expect(session.view().draft).toBeNull();
    expect(session.beginEdit(root.id)).toBe(false);
    expect(session.beginReply(root.id)).toBe(false);
    expect(session.submitDraft("Must not submit")).toBe(false);
    expect(session.delete(root.id)).toBe(false);
    session.reanchor("changed selected\n");
    await session.flushPersistence();
    expect(storage.saveCalls).toHaveLength(0);
    expect(storage.values.get("signed-out\0load-failure.md")).toEqual(storedBefore);

    storage.failLoad = false;
    await session.retryPersistence();
    expect(session.view()).toMatchObject({ commentsAvailable: true, persistence: "saved" });
    expect(session.all().map((comment) => comment.body)).toEqual(["Stored root"]);
    expect(session.all()[0]?.replies.map((reply) => reply.body)).toEqual(["Stored reply"]);
    expect(session.beginReply(root.id)).toBe(true);
    expect(session.submitDraft("Reply after recovery")).toBe(true);
    await session.flushPersistence();
    expect(storage.values.get("signed-out\0load-failure.md")?.[0]?.replies).toHaveLength(2);
  });

  it("never overwrites unknown storage when navigation retires a pending-load mutation", async () => {
    const storage = new MemoryStorage();
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    const seed = new SelectionCommentSession(storage);
    await seed.setDocument("retired-load-failure.md", markdown);
    seed.add(selection, "Stored note");
    await seed.flushPersistence();
    storage.saveCalls.length = 0;

    storage.failLoad = true;
    const session = new SelectionCommentSession(storage);
    const failingLoad = session.setDocument("retired-load-failure.md", markdown);
    session.add(selection, "Pending while loading");
    expect(await failingLoad).toBe(false);
    await session.setDocument("after-load-failure.md", "other\n");
    await session.flushPersistence();

    expect(storage.saveCalls).toHaveLength(0);
    expect(
      storage.values.get("signed-out\0retired-load-failure.md")?.map((item) => item.body),
    ).toEqual(["Stored note"]);
    storage.failLoad = false;
    await session.retryPersistence();
    await session.setDocument("retired-load-failure.md", markdown);
    expect(session.all().map((comment) => comment.body)).toEqual([
      "Stored note",
      "Pending while loading",
    ]);
  });

  it("requires a fresh read before retrying an older failed snapshot into a load-failed document", async () => {
    const storage = new MemoryStorage();
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    const seed = new SelectionCommentSession(storage);
    await seed.setDocument("retry-after-load-failure.md", markdown);
    seed.add(selection, "Stored note");
    await seed.flushPersistence();

    const session = new SelectionCommentSession(storage);
    await session.setDocument("retry-after-load-failure.md", markdown);
    session.add(selection, "Pending note");
    storage.failSave = true;
    await session.setDocument("retry-load-other.md", "other\n");
    expect(session.view()).toMatchObject({ persistence: "error" });
    storage.saveCalls.length = 0;

    storage.failSave = false;
    storage.failLoad = true;
    expect(await session.setDocument("retry-after-load-failure.md", markdown)).toBe(false);
    expect(session.view().persistenceMessage).toContain("unavailable until");
    expect(session.view().persistenceMessage).toContain("1 pending comment snapshot");
    await session.retryPersistence();
    expect(storage.saveCalls).toHaveLength(0);
    expect(
      storage.values.get("signed-out\0retry-after-load-failure.md")?.map((item) => item.body),
    ).toEqual(["Stored note"]);

    storage.failLoad = false;
    await session.retryPersistence();
    expect(session.all().map((comment) => comment.body)).toEqual(["Stored note", "Pending note"]);
    expect(
      storage.values.get("signed-out\0retry-after-load-failure.md")?.map((item) => item.body),
    ).toEqual(["Stored note", "Pending note"]);
  });

  it("isolates ownership and storage across GitHub accounts and sign-out", async () => {
    const storage = new MemoryStorage();
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    const session = new SelectionCommentSession(storage);
    await session.setPrincipal("Alice");
    await session.setDocument("account.md", markdown);
    const alice = session.add(selection, "Alice note");
    if (alice === null) throw new Error("Expected Alice comment");
    await session.flushPersistence();

    await session.setPrincipal("Bob");
    expect(session.all()).toHaveLength(0);
    expect(session.beginEdit(alice.id)).toBe(false);
    const bob = session.add(selection, "Bob note");
    if (bob === null) throw new Error("Expected Bob comment");
    await session.flushPersistence();

    await session.setPrincipal("Alice");
    expect(session.all()).toHaveLength(1);
    expect(session.all()[0]).toMatchObject({
      body: "Alice note",
      author: { principalId: "github:alice", displayName: "Alice" },
    });
    expect(session.beginEdit(session.all()[0]?.id ?? "")).toBe(true);
    await session.setPrincipal(null);
    expect(session.all()).toHaveLength(0);
  });

  it("never exposes signed-out or another pending account while a GitHub login is loading", async () => {
    const storage = new MemoryStorage();
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    const session = new SelectionCommentSession(storage);
    await session.setDocument("pending-account.md", markdown);
    session.add(selection, "Signed-out note");
    await session.flushPersistence();

    await session.setPrincipal("", "account-publication-a");
    expect(session.view().principalId).toMatch(/^github-pending:/);
    expect(session.all()).toHaveLength(0);
    session.add(selection, "Pending account A note");
    await session.flushPersistence();

    await session.setPrincipal("", "account-publication-b");
    expect(session.all()).toHaveLength(0);
    await session.setPrincipal(null);
    expect(session.all()[0]?.body).toBe("Signed-out note");
  });

  it("drops a stale account load after the principal changes", async () => {
    const storage = new MemoryStorage();
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    const seed = new SelectionCommentSession(storage);
    await seed.setPrincipal("Alice");
    await seed.setDocument("stale.md", markdown);
    seed.add(selection, "Alice private note");
    await seed.flushPersistence();

    let release: (() => void) | undefined;
    storage.loadGate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const session = new SelectionCommentSession(storage);
    await session.setPrincipal("Alice");
    const aliceLoad = session.setDocument("stale.md", markdown);
    const bobLoad = session.setPrincipal("Bob");
    release?.();
    await Promise.all([aliceLoad, bobLoad]);

    expect(session.view().principalId).toBe("github:bob");
    expect(session.all()).toHaveLength(0);
  });

  it("flushes a pending bounded snapshot when navigation retires the document", async () => {
    const storage = new MemoryStorage();
    const session = new SelectionCommentSession(storage);
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    await session.setPrincipal("Alice");
    await session.setDocument("old.md", markdown);
    session.add(selection, "save before leaving");

    await session.setDocument("new.md", "new\n");

    expect(storage.values.get("github:alice\0old.md")).toHaveLength(1);
    expect(session.all()).toHaveLength(0);
  });

  it("publishes an empty loading view before a new document can render cached threads", async () => {
    const storage = new MemoryStorage();
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    const seed = new SelectionCommentSession(storage);
    await seed.setDocument("old-view.md", markdown);
    seed.add(selection, "Only in the old document");
    await seed.flushPersistence();

    const session = new SelectionCommentSession(storage);
    await session.setDocument("old-view.md", markdown);
    expect(session.all()).toHaveLength(1);
    const published: ReturnType<SelectionCommentSession["view"]>[] = [];
    session.setNotifier(() => published.push(session.view()));
    let release: (() => void) | undefined;
    storage.loadGate = new Promise<void>((resolve) => {
      release = resolve;
    });

    const loading = session.setDocument("new-view.md", "new\n");
    expect(published.at(-1)).toMatchObject({ comments: [], commentsAvailable: false });
    release?.();
    await loading;
    expect(session.all()).toHaveLength(0);
  });

  it("merges a comment created while persisted threads are still loading", async () => {
    const storage = new MemoryStorage();
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    const seed = new SelectionCommentSession(storage);
    await seed.setDocument("slow.md", markdown);
    seed.add(selection, "Persisted note");
    await seed.flushPersistence();

    let release: (() => void) | undefined;
    storage.loadGate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const session = new SelectionCommentSession(storage);
    const loading = session.setDocument("slow.md", markdown);
    session.add(selection, "Immediate local note");
    const saving = session.flushPersistence();
    release?.();
    await Promise.all([loading, saving]);

    expect(session.all().map((comment) => comment.body)).toEqual([
      "Persisted note",
      "Immediate local note",
    ]);
    expect(new Set(session.all().map((comment) => comment.id)).size).toBe(2);
    expect(storage.values.get("signed-out\0slow.md")).toHaveLength(2);
  });

  it("merges pending-load comments before an immediate document navigation", async () => {
    const storage = new MemoryStorage();
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    const seed = new SelectionCommentSession(storage);
    await seed.setDocument("leave-slow.md", markdown);
    seed.add(selection, "Persisted before navigation");
    await seed.flushPersistence();

    let release: (() => void) | undefined;
    storage.loadGate = new Promise<void>((resolve) => {
      release = resolve;
    });
    const session = new SelectionCommentSession(storage);
    void session.setDocument("leave-slow.md", markdown);
    session.add(selection, "Added while opening");
    const navigation = session.setDocument("next.md", "next\n");
    release?.();
    await navigation;

    await vi.waitFor(() => {
      expect(storage.values.get("signed-out\0leave-slow.md")).toHaveLength(2);
    });
    expect(session.all()).toHaveLength(0);
  });

  it("serializes overlapping saves so an older snapshot cannot finish last", async () => {
    let releaseFirst: (() => void) | undefined;
    let markFirstStarted: (() => void) | undefined;
    const firstStarted = new Promise<void>((resolve) => {
      markFirstStarted = resolve;
    });
    const firstGate = new Promise<void>((resolve) => {
      releaseFirst = resolve;
    });
    let saveCount = 0;
    const storage = new MemoryStorage();
    const originalSave = storage.save.bind(storage);
    storage.save = async (principal, document, comments) => {
      saveCount++;
      if (saveCount === 1) {
        markFirstStarted?.();
        await firstGate;
      }
      await originalSave(principal, document, comments);
    };
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    const session = new SelectionCommentSession(storage);
    await session.setDocument("ordered.md", markdown);
    session.add(selection, "First note");
    const firstSave = session.flushPersistence();
    await firstStarted;
    session.add(selection, "Second note");
    const secondSave = session.flushPersistence();
    releaseFirst?.();
    await Promise.all([firstSave, secondSave]);

    expect(storage.values.get("signed-out\0ordered.md")).toHaveLength(2);
    expect(storage.saveCalls.at(-1)?.serialized).toContain("Second note");
  });

  it("waits for a retired same-document save before A to B to A reloads", async () => {
    const storage = new MemoryStorage();
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    const seed = new SelectionCommentSession(storage);
    await seed.setDocument("same-key-a.md", markdown);
    seed.add(selection, "Original note");
    await seed.flushPersistence();

    storage.loadCalls.length = 0;
    storage.saveCalls.length = 0;
    storage.saveStarts.length = 0;
    const session = new SelectionCommentSession(storage);
    await session.setDocument("same-key-a.md", markdown);
    session.add(selection, "Retired context note");
    let releaseSave: (() => void) | undefined;
    storage.saveGate = new Promise<void>((resolve) => {
      releaseSave = resolve;
    });
    const leaving = session.setDocument("same-key-b.md", "other\n");
    await vi.waitFor(() => expect(storage.saveStarts).toHaveLength(1));
    const aLoadsBeforeReentry = storage.loadCalls.filter(
      (call) => call.document === "same-key-a.md",
    ).length;

    const reentry = session.setDocument("same-key-a.md", markdown);
    await Promise.resolve();
    await Promise.resolve();
    expect(storage.loadCalls.filter((call) => call.document === "same-key-a.md").length).toBe(
      aLoadsBeforeReentry,
    );

    releaseSave?.();
    await Promise.all([leaving, reentry]);
    expect(session.all().map((comment) => comment.body)).toEqual([
      "Original note",
      "Retired context note",
    ]);

    session.add(selection, "Mutation after re-entry");
    await session.flushPersistence();
    expect(storage.values.get("signed-out\0same-key-a.md")).toHaveLength(3);
    expect(storage.saveCalls.at(-1)?.serialized).toContain("Mutation after re-entry");
  });

  it("keeps a failed retired snapshot visible and lets a newer same-key save supersede it", async () => {
    const storage = new MemoryStorage();
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    const seed = new SelectionCommentSession(storage);
    await seed.setDocument("failed-reentry-a.md", markdown);
    seed.add(selection, "Persisted note");
    await seed.flushPersistence();

    const session = new SelectionCommentSession(storage);
    await session.setDocument("failed-reentry-a.md", markdown);
    session.add(selection, "Failed retired note");
    storage.failSave = true;
    const leaving = session.setDocument("failed-reentry-b.md", "other\n");
    const reentry = session.setDocument("failed-reentry-a.md", markdown);
    await Promise.all([leaving, reentry]);

    expect(session.all().map((comment) => comment.body)).toEqual([
      "Persisted note",
      "Failed retired note",
    ]);
    expect(session.view()).toMatchObject({ persistence: "error" });

    storage.failSave = false;
    session.add(selection, "Newer re-entry mutation");
    await session.flushPersistence();
    expect(session.view()).toMatchObject({ persistence: "saved" });
    expect(storage.values.get("signed-out\0failed-reentry-a.md")).toHaveLength(3);

    await session.setDocument("failed-reentry-b.md", "other\n");
    await session.setDocument("failed-reentry-a.md", markdown);
    expect(session.all()).toHaveLength(3);
    expect(session.view()).toMatchObject({ persistence: "saved" });
  });

  it("keeps failures from multiple documents globally visible and retries them independently", async () => {
    const storage = new MemoryStorage();
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    const session = new SelectionCommentSession(storage);
    await session.setDocument("failed-a.md", markdown);
    session.add(selection, "Pending A");
    storage.failSaveDocuments.add("failed-a.md");

    await session.setDocument("failed-b.md", markdown);
    expect(session.all()).toHaveLength(0);
    expect(session.view().persistenceMessage).toContain("1 comment snapshot");

    session.add(selection, "Successful B");
    await session.flushPersistence();
    expect(storage.values.get("signed-out\0failed-b.md")).toHaveLength(1);
    expect(session.view().persistenceMessage).toContain("1 comment snapshot");

    session.add(selection, "Pending B");
    storage.failSaveDocuments.add("failed-b.md");
    await session.flushPersistence();
    expect(session.view().persistenceMessage).toContain("2 comment snapshots");

    storage.failSaveDocuments.delete("failed-a.md");
    await session.retryPersistence();
    expect(storage.values.get("signed-out\0failed-a.md")).toHaveLength(1);
    expect(session.view().persistenceMessage).toContain("1 comment snapshot");
    expect(session.view()).toMatchObject({ persistence: "error" });

    storage.failSaveDocuments.delete("failed-b.md");
    await session.retryPersistence();
    expect(storage.values.get("signed-out\0failed-b.md")).toHaveLength(2);
    expect(session.view()).toMatchObject({ persistence: "saved" });
  });

  it("keeps failed snapshots queued without exposing them across GitHub accounts", async () => {
    const storage = new MemoryStorage();
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    const session = new SelectionCommentSession(storage);
    await session.setPrincipal("Alice");
    await session.setDocument("account-failure.md", markdown);
    session.add(selection, "Pending Alice note");
    storage.failSave = true;
    await session.flushPersistence();
    expect(session.view()).toMatchObject({ persistence: "error" });

    await session.setPrincipal("Bob");
    expect(session.view()).toMatchObject({ persistence: "saved" });
    expect(session.view().persistenceMessage).toBeUndefined();
    await session.retryPersistence();
    expect(storage.values.get("github:alice\0account-failure.md")).toBeUndefined();

    await session.setPrincipal("Alice");
    expect(session.view().persistenceMessage).toContain("1 comment snapshot");
    expect(session.all().map((comment) => comment.body)).toEqual(["Pending Alice note"]);
    storage.failSave = false;
    await session.retryPersistence();
    expect(storage.values.get("github:alice\0account-failure.md")).toHaveLength(1);
    expect(session.view()).toMatchObject({ persistence: "saved" });
  });

  it.each([
    "create",
    "edit",
    "reply",
    "delete",
  ] as const)("flushes an immediate %s mutation before allowing the window close ACK", async (mutation) => {
    const storage = new MemoryStorage();
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    const session = new SelectionCommentSession(storage);
    await session.setDocument(`close-${mutation}.md`, markdown);
    const root = session.add(selection, "Original note");
    if (root === null) throw new Error("Expected comment");
    await session.flushPersistence();
    storage.saveCalls.length = 0;

    if (mutation === "create") session.add(selection, "Created at close");
    if (mutation === "edit") {
      expect(session.beginEdit(root.id)).toBe(true);
      expect(session.submitDraft("Edited at close")).toBe(true);
    }
    if (mutation === "reply") {
      expect(session.beginReply(root.id)).toBe(true);
      expect(session.submitDraft("Replied at close")).toBe(true);
    }
    if (mutation === "delete") expect(session.delete(root.id)).toBe(true);

    expect(await session.flushForClose()).toBe(true);
    expect(storage.saveCalls).toHaveLength(1);
    const stored = storage.values.get(`signed-out\0close-${mutation}.md`) ?? [];
    if (mutation === "create") expect(stored.map((item) => item.body)).toHaveLength(2);
    if (mutation === "edit") expect(stored[0]?.body).toBe("Edited at close");
    if (mutation === "reply") expect(stored[0]?.replies[0]?.body).toBe("Replied at close");
    if (mutation === "delete") expect(stored).toHaveLength(0);
    expect(session.add(selection, "Too late")).toBeNull();
  });

  it("waits for a delayed comment save before completing the close flush", async () => {
    const storage = new MemoryStorage();
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    const session = new SelectionCommentSession(storage);
    await session.setDocument("close-delayed.md", markdown);
    let releaseSave: (() => void) | undefined;
    storage.saveGate = new Promise<void>((resolve) => {
      releaseSave = resolve;
    });
    session.add(selection, "Wait for this save");
    let settled = false;
    const closing = session.flushForClose().then((result) => {
      settled = true;
      return result;
    });
    await vi.waitFor(() => expect(storage.saveStarts).toHaveLength(1));
    expect(settled).toBe(false);
    releaseSave?.();
    expect(await closing).toBe(true);
    expect(storage.values.get("signed-out\0close-delayed.md")).toHaveLength(1);
  });

  it("keeps close fail-closed on save errors, then Retry allows a fresh close", async () => {
    const storage = new MemoryStorage();
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    const session = new SelectionCommentSession(storage);
    await session.setDocument("close-save-failure.md", markdown);
    storage.failSave = true;
    session.add(selection, "Do not lose this");

    expect(await session.flushForClose()).toBe(false);
    expect(session.view()).toMatchObject({ commentsAvailable: true, persistence: "error" });
    expect(session.view().persistenceMessage).toContain("before closing");
    expect(session.all().map((comment) => comment.body)).toEqual(["Do not lose this"]);

    storage.failSave = false;
    await session.retryPersistence();
    expect(session.view()).toMatchObject({ persistence: "saved" });
    expect(await session.flushForClose()).toBe(true);
  });

  it("keeps close fail-closed on load errors and succeeds after Retry", async () => {
    const storage = new MemoryStorage();
    storage.failLoad = true;
    const session = new SelectionCommentSession(storage);
    expect(await session.setDocument("close-load-failure.md", "selected\n")).toBe(false);

    expect(await session.flushForClose()).toBe(false);
    expect(session.view()).toMatchObject({ commentsAvailable: false, persistence: "error" });
    expect(session.view().persistenceMessage).toContain("unavailable until");

    storage.failLoad = false;
    await session.retryPersistence();
    expect(await session.flushForClose()).toBe(true);
  });

  it("rejects a stale close flush when the account changes during a delayed save", async () => {
    const storage = new MemoryStorage();
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    const session = new SelectionCommentSession(storage);
    await session.setPrincipal("Alice");
    await session.setDocument("close-account-transition.md", markdown);
    let releaseSave: (() => void) | undefined;
    storage.saveGate = new Promise<void>((resolve) => {
      releaseSave = resolve;
    });
    session.add(selection, "Alice pending note");
    const closing = session.flushForClose();
    await vi.waitFor(() => expect(storage.saveStarts).toHaveLength(1));
    const switching = session.setPrincipal("Bob");
    releaseSave?.();

    expect(await closing).toBe(false);
    await switching;
    expect(session.view().principalId).toBe("github:bob");
    expect(session.view().persistenceMessage).toContain("changed while");
    await session.retryPersistence();
    expect(await session.flushForClose()).toBe(true);
  });

  it("blocks close for another account's failed snapshot and retries it without exposing its thread", async () => {
    const storage = new MemoryStorage();
    const markdown = "selected\n";
    const selection = sourceSelection(markdown, 0, 8);
    if (selection === null) throw new Error("Expected selection");
    const session = new SelectionCommentSession(storage);
    await session.setPrincipal("Alice");
    await session.setDocument("close-other-account.md", markdown);
    storage.failSave = true;
    session.add(selection, "Alice private pending note");
    await session.flushPersistence();
    await session.setPrincipal("Bob");
    expect(session.all()).toHaveLength(0);

    expect(await session.flushForClose()).toBe(false);
    expect(session.view().persistenceMessage).toContain("before closing");
    expect(session.view().persistenceMessage).not.toContain("Alice");
    storage.failSave = false;
    await session.retryPersistence();
    expect(storage.values.get("github:alice\0close-other-account.md")).toHaveLength(1);
    expect(session.all()).toHaveLength(0);
    expect(await session.flushForClose()).toBe(true);
  });

  it("uses bounded opaque storage keys instead of account names or absolute paths", () => {
    const account = opaqueSelectionStorageKey("github:Alice");
    const document = opaqueSelectionStorageKey("org/repo\0main\0D:/secret/spec.md");
    expect(account).not.toContain("Alice");
    expect(document).not.toContain("secret");
    expect(account.length + document.length).toBeLessThan(60);
  });
});
