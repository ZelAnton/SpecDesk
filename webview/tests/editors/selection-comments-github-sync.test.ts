import { describe, expect, it } from "vitest";
import {
  type GithubReviewSyncSnapshot,
  githubSyncStatus,
  type SelectionComment,
  SelectionCommentSession,
  type SelectionCommentStorage,
  type StoredSelectionComment,
  selectionDocumentKey,
  sourceSelection,
} from "../../src/editors/selection-comments.js";

class MemoryStorage implements SelectionCommentStorage {
  readonly values = new Map<string, readonly StoredSelectionComment[]>();

  async load(principal: string, document: string): Promise<readonly StoredSelectionComment[]> {
    return structuredClone(this.values.get(`${principal}\0${document}`) ?? []);
  }

  async save(
    principal: string,
    document: string,
    comments: readonly StoredSelectionComment[],
  ): Promise<void> {
    this.values.set(`${principal}\0${document}`, structuredClone(comments));
  }
}

const MARKDOWN = "line one\nline two\nline three\n";
const DOCUMENT_KEY = selectionDocumentKey("D:/repo/spec.md", "octo/repo", "main");

async function sessionOn(
  markdown = MARKDOWN,
  storage = new MemoryStorage(),
): Promise<SelectionCommentSession> {
  const session = new SelectionCommentSession(storage);
  await session.setPrincipal("octo");
  await session.setDocument(DOCUMENT_KEY, markdown);
  return session;
}

/** Add a local thread anchored to the line containing `needle`. */
function addOn(session: SelectionCommentSession, markdown: string, needle: string, body: string) {
  const from = markdown.indexOf(needle);
  const selection = sourceSelection(markdown, from, from + needle.length);
  if (selection === null) throw new Error(`no selection for ${needle}`);
  const comment = session.add(selection, body);
  if (comment === null) throw new Error("add returned null");
  return comment;
}

function snapshot(overrides: Partial<GithubReviewSyncSnapshot> = {}): GithubReviewSyncSnapshot {
  return {
    documentKey: DOCUMENT_KEY,
    number: 42,
    headCommitId: "headsha",
    path: "spec.md",
    commentableLines: [2],
    comments: [],
    ...overrides,
  };
}

const baseComment: SelectionComment = {
  fromLine: 1,
  toLine: 1,
  anchorLine: 1,
  anchorKind: "line",
  fromOffset: 9,
  toOffset: 17,
  anchorOffset: 17,
  quote: "line two",
  id: "selection-comment-1",
  body: "note",
  createdAt: "2026-07-16T00:00:00.000Z",
  author: { principalId: "github:octo", displayName: "octo" },
  replies: [],
  anchorState: "attached",
};

describe("githubSyncStatus (per-thread projection state)", () => {
  it("is undefined without an open pull request", () => {
    expect(githubSyncStatus(baseComment, null, new Set())).toBeUndefined();
    expect(githubSyncStatus(baseComment, snapshot({ number: 0 }), new Set([2]))).toBeUndefined();
  });

  it("is synced once the thread carries a GitHub id", () => {
    expect(githubSyncStatus({ ...baseComment, githubId: 5 }, snapshot(), new Set([9]))).toBe(
      "synced",
    );
  });

  it("is publishable when the anchor line is inside a diff hunk, else local-only", () => {
    // toLine 1 → head line 2.
    expect(githubSyncStatus(baseComment, snapshot(), new Set([2]))).toBe("publishable");
    expect(githubSyncStatus(baseComment, snapshot(), new Set([9]))).toBe("local-only");
  });

  it("never treats a detached thread as publishable", () => {
    expect(
      githubSyncStatus({ ...baseComment, anchorState: "detached" }, snapshot(), new Set([2])),
    ).toBe("local-only");
  });
});

describe("SelectionCommentSession GitHub sync projection", () => {
  it("decorates a local thread on a hunk line as publishable and derives its publish request", async () => {
    const session = await sessionOn();
    const comment = addOn(session, MARKDOWN, "line two", "clarify this");
    // "line two" is source line 1, so its head line is 2 — inside the commentable set.
    session.applyGithubSync(snapshot({ commentableLines: [comment.toLine + 1] }));

    const view = session.view();
    expect(view.comments[0]?.githubSync).toBe("publishable");

    const request = session.publishRequestFor(comment.id);
    expect(request).toMatchObject({
      documentKey: DOCUMENT_KEY,
      number: 42,
      commitId: "headsha",
      line: comment.toLine + 1,
      side: "RIGHT",
      body: "clarify this",
      localId: comment.id,
    });
  });

  it("labels an out-of-hunk thread local-only and refuses to publish it", async () => {
    const session = await sessionOn();
    const comment = addOn(session, MARKDOWN, "line two", "note");
    session.applyGithubSync(snapshot({ commentableLines: [999] }));

    expect(session.view().comments[0]?.githubSync).toBe("local-only");
    expect(session.publishRequestFor(comment.id)).toBeNull();
  });

  it("projects a GitHub review comment as a read-only inline thread anchored to its head line", async () => {
    const session = await sessionOn();
    session.applyGithubSync(
      snapshot({
        comments: [
          {
            id: 7,
            line: 3,
            side: "RIGHT",
            commitId: "headsha",
            inReplyToId: 0,
            author: "sam",
            body: "from github",
            when: "2026-07-16T00:00:00.000Z",
          },
        ],
      }),
    );

    const projected = session.view().comments.find((item) => item.origin === "github");
    expect(projected).toBeDefined();
    expect(projected?.body).toBe("from github");
    expect(projected?.githubId).toBe(7);
    // line 3 → 0-based source line 2 ("line three").
    expect(projected?.anchorLine).toBe(2);
  });

  it("drops the LEFT (base) side comments that don't map onto head content", async () => {
    const session = await sessionOn();
    session.applyGithubSync(
      snapshot({
        comments: [
          {
            id: 8,
            line: 2,
            side: "LEFT",
            commitId: "headsha",
            inReplyToId: 0,
            author: "sam",
            body: "base side",
            when: "2026-07-16T00:00:00.000Z",
          },
        ],
      }),
    );
    expect(session.view().comments.some((item) => item.origin === "github")).toBe(false);
  });

  it("ignores a projection meant for a different document (a raced navigation)", async () => {
    const session = await sessionOn();
    const applied = session.applyGithubSync(snapshot({ documentKey: "some/other/doc" }));
    expect(applied).toBe(false);
    expect(session.view().comments.some((item) => item.origin === "github")).toBe(false);
  });

  it("stamps a posted thread with its GitHub id, hides its projected duplicate, and persists it", async () => {
    const storage = new MemoryStorage();
    const session = await sessionOn(MARKDOWN, storage);
    const comment = addOn(session, MARKDOWN, "line two", "please clarify");
    // The same thread also comes back from GitHub (id 7); once the local thread owns 7 it must not double up.
    session.applyGithubSync(
      snapshot({
        commentableLines: [comment.toLine + 1],
        comments: [
          {
            id: 7,
            line: comment.toLine + 1,
            side: "RIGHT",
            commitId: "headsha",
            inReplyToId: 0,
            author: "octo",
            body: "please clarify",
            when: "2026-07-16T00:00:00.000Z",
          },
        ],
      }),
    );
    // Before marking: the local thread (no id yet) plus the projected GitHub copy (id 7) both show.
    expect(
      session.view().comments.filter((item) => item.origin === "github" && item.githubId === 7),
    ).toHaveLength(1);
    expect(
      session.view().comments.find((item) => item.id === comment.id)?.githubId,
    ).toBeUndefined();

    session.markGithubId(comment.id, 7);
    // After marking: the projected duplicate is dropped; only the (now synced) local thread carries id 7.
    const afterMark = session.view().comments.filter((item) => item.githubId === 7);
    expect(afterMark).toHaveLength(1);
    expect(afterMark[0]?.origin).toBeUndefined();
    expect(afterMark[0]?.githubSync).toBe("synced");

    await session.flushPersistence();
    const reloaded = await sessionOn(MARKDOWN, storage);
    const restored = reloaded.view().comments.find((item) => item.id === comment.id);
    expect(restored?.githubId).toBe(7);
  });

  it("re-anchors a projected GitHub thread when the document is edited above it", async () => {
    const session = await sessionOn();
    session.applyGithubSync(
      snapshot({
        comments: [
          {
            id: 9,
            line: 3,
            side: "RIGHT",
            commitId: "headsha",
            inReplyToId: 0,
            author: "sam",
            body: "on line three",
            when: "2026-07-16T00:00:00.000Z",
          },
        ],
      }),
    );
    expect(session.view().comments.find((c) => c.origin === "github")?.anchorLine).toBe(2);

    // Insert a new first line — the projected thread should follow its text down one line.
    session.reanchor(`inserted top\n${MARKDOWN}`);
    expect(session.view().comments.find((c) => c.origin === "github")?.anchorLine).toBe(3);
  });
});
