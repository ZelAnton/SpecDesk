import { describe, expect, it } from "vitest";
import type { StatusPayload, WorkspaceContextPayload } from "../../src/wire/protocol.js";
import {
  ActiveContextModel,
  EMPTY_ACTIVE_CONTEXT,
  rightToolsForContext,
} from "../../src/workspace/active-context.js";

const review: StatusPayload = {
  state: "inReview",
  label: "In review",
  branch: "spec/proposal",
};
const named: WorkspaceContextPayload = {
  repository: "acme/specs",
  repositoryRoot: "C:\\repo",
  branch: "spec/proposal",
  branchState: "named",
  defaultBranch: "main",
  path: "docs/proposal.md",
};

function tools(context: ReturnType<ActiveContextModel["current"]>): string[] {
  return [...rightToolsForContext(context)];
}

describe("active workspace context", () => {
  it("composes the same PR context for every initial event ordering", () => {
    const statusFirst = new ActiveContextModel();
    statusFirst.statusChanged(review);
    statusFirst.workspaceChanged(named);
    const fromStatusFirst = statusFirst.documentLoaded("C:\\repo\\docs\\proposal.md");

    const documentFirst = new ActiveContextModel();
    documentFirst.documentLoaded("C:\\repo\\docs\\proposal.md");
    documentFirst.workspaceChanged(named);
    const fromDocumentFirst = documentFirst.statusChanged(review);

    expect(fromStatusFirst).toEqual(fromDocumentFirst);
    expect(fromStatusFirst.pullRequest?.branch).toBe(fromStatusFirst.branch);
    expect(fromStatusFirst.branch?.name).toBe("spec/proposal");
    expect(tools(fromStatusFirst)).toEqual(["assistant", "comments", "history", "versions"]);
  });

  it.each([
    "detached",
    "unavailable",
  ] as const)("keeps repository-file tools but no branch/PR tools for %s state", (branchState) => {
    const model = new ActiveContextModel();
    model.documentLoaded("C:\\repo\\docs\\proposal.md");
    model.statusChanged(review);
    const context = model.workspaceChanged({ ...named, branch: null, branchState });
    expect(context.repository).not.toBeNull();
    expect(context.branch).toBeNull();
    expect(context.pullRequest).toBeNull();
    expect(tools(context)).toEqual(["assistant", "versions"]);
  });

  it("keeps Versions for a non-Markdown repository file on detached HEAD", () => {
    const model = new ActiveContextModel();
    model.documentLoaded("C:\\repo\\.spectool.toml");
    const context = model.workspaceChanged({
      ...named,
      branch: null,
      branchState: "detached",
      path: ".spectool.toml",
    });

    expect(context.file).toMatchObject({ type: "other", repository: context.repository });
    expect(context.branch).toBeNull();
    expect(tools(context)).toEqual(["assistant", "versions"]);
  });

  it("keeps an outside Markdown file independent from repository capabilities", () => {
    const model = new ActiveContextModel();
    model.workspaceChanged({
      repository: null,
      repositoryRoot: null,
      branch: null,
      branchState: "unavailable",
      defaultBranch: null,
      path: "outside.md",
    });
    const context = model.documentLoaded("C:\\notes\\outside.md");
    expect(context.file).toMatchObject({ type: "markdown", repository: null });
    expect(tools(context)).toEqual(["assistant"]);
  });

  it("does not apply a stale repository context to a newly loaded document", () => {
    const model = new ActiveContextModel();
    model.documentLoaded("C:\\repo\\docs\\proposal.md");
    model.workspaceChanged(named);
    const next = model.documentLoaded("C:\\notes\\other.txt");
    expect(next.repository).toBeNull();
    expect(next.pullRequest).toBeNull();
    expect(tools(next)).toEqual(["assistant"]);
  });

  it("shows document repository hints immediately, then enriches them with matching workspace data", () => {
    const model = new ActiveContextModel();
    const immediate = model.documentLoaded("C:\\repo\\docs\\proposal.md", {
      repository: "acme/specs",
      branch: "spec/proposal",
      repositoryPath: "docs/proposal.md",
    });

    expect(immediate.repository).toMatchObject({ id: "acme/specs", root: "C:\\repo" });
    expect(immediate.branch?.name).toBe("spec/proposal");
    expect(immediate.file?.path).toBe("C:\\repo\\docs\\proposal.md");

    const enriched = model.workspaceChanged(named);
    expect(enriched.repository).toMatchObject({
      id: "acme/specs",
      root: "C:\\repo",
      defaultBranch: "main",
    });
  });

  it("prefers each document branch hint over a retained matching workspace branch", () => {
    const model = new ActiveContextModel();
    const path = "C:\\repo\\docs\\proposal.md";
    model.documentLoaded(path, {
      repository: "acme/specs",
      branch: "main",
      repositoryPath: "docs/proposal.md",
    });
    model.workspaceChanged(named);

    const editing = model.documentLoaded(path, {
      repository: "acme/specs",
      branch: "draft/proposal",
      repositoryPath: "docs/proposal.md",
    });
    expect(editing.branch?.name).toBe("draft/proposal");
    expect(editing.repository).toMatchObject({
      id: "acme/specs",
      root: "C:\\repo",
      defaultBranch: "main",
    });

    const discarded = model.documentLoaded(path, {
      repository: "acme/specs",
      branch: "main",
      repositoryPath: "docs/proposal.md",
    });
    expect(discarded.branch?.name).toBe("main");

    const detached = model.workspaceChanged({ ...named, branch: null, branchState: "detached" });
    expect(detached.branch).toBeNull();
  });

  it("accepts a matching named workspace received after a hintless document", () => {
    const model = new ActiveContextModel();
    model.documentLoaded("C:\\repo\\docs\\proposal.md");

    const context = model.workspaceChanged({ ...named, branch: "spec/proposal" });
    expect(context.branch?.name).toBe("spec/proposal");
  });

  it("keeps new document hints while a late workspace frame still belongs to the old document", () => {
    const model = new ActiveContextModel();
    model.documentLoaded("C:\\repo-a\\docs\\proposal.md", {
      repository: "acme/a",
      branch: "main",
      repositoryPath: "docs/proposal.md",
    });
    model.workspaceChanged({ ...named, repository: "acme/a", repositoryRoot: "C:\\repo-a" });

    const next = model.documentLoaded("C:\\repo-b\\docs\\proposal.md", {
      repository: "acme/b",
      branch: "review/b",
      repositoryPath: "docs/proposal.md",
    });
    const afterLateOldFrame = model.workspaceChanged({
      ...named,
      repository: "acme/a",
      repositoryRoot: "C:\\repo-a",
    });

    expect(next.repository?.id).toBe("acme/b");
    expect(afterLateOldFrame.repository?.id).toBe("acme/b");
    expect(afterLateOldFrame.branch?.name).toBe("review/b");
  });

  it("does not apply a stale review status to a different active branch", () => {
    const model = new ActiveContextModel();
    model.documentLoaded("C:\\repo\\docs\\next.md");
    model.workspaceChanged({ ...named, branch: "spec/next", path: "docs/next.md" });

    const stale = model.statusChanged(review);
    expect(stale.branch?.name).toBe("spec/next");
    expect(stale.pullRequest).toBeNull();
    expect(tools(stale)).toEqual(["assistant", "history", "versions"]);

    const matched = model.statusChanged({ ...review, branch: "spec/next" });
    expect(matched.pullRequest?.branch.name).toBe("spec/next");
    expect(tools(matched)).toContain("comments");
  });

  it("rejects stale remote context when the document arrives before its context", () => {
    const model = new ActiveContextModel();
    const remoteA: WorkspaceContextPayload = {
      ...named,
      repository: "acme/specs-a",
      repositoryRoot: null,
      branch: "feature/docs",
      path: "Docs/Guide.md",
    };
    const remoteB = { ...remoteA, repository: "acme/specs-b" };

    model.documentLoaded("github://acme/specs-a/feature%2Fdocs/Docs%2FGuide.md");
    model.workspaceChanged(remoteA);
    model.statusChanged(review);

    const beforeMatchingContext = model.documentLoaded(
      "github://acme/specs-b/feature%2Fdocs/Docs%2FGuide.md",
    );
    expect(beforeMatchingContext.repository).toBeNull();
    expect(beforeMatchingContext.pullRequest).toBeNull();

    const matched = model.workspaceChanged(remoteB);
    expect(matched.repository?.id).toBe("acme/specs-b");
    expect(matched.branch?.name).toBe("feature/docs");
  });

  it("matches an encoded remote identity when context arrives before the document", () => {
    const model = new ActiveContextModel();
    const remote: WorkspaceContextPayload = {
      ...named,
      repository: "Acme/Specs-B",
      repositoryRoot: null,
      branch: "feature/docs",
      path: "Docs/Guide.md",
    };

    model.workspaceChanged(remote);
    const matched = model.documentLoaded("github://acme/specs-b/feature%2Fdocs/Docs%2FGuide.md");

    expect(matched.repository?.id).toBe("Acme/Specs-B");
    expect(matched.branch?.name).toBe("feature/docs");
    expect(matched.file?.path).toContain("feature%2Fdocs");
  });

  it("requires the remote branch and path as well as the repository", () => {
    const model = new ActiveContextModel();
    model.documentLoaded("github://acme/specs/feature%2Ftwo/Docs%2FGuide.md");

    const stale = model.workspaceChanged({
      ...named,
      repository: "acme/specs",
      repositoryRoot: null,
      branch: "feature/one",
      path: "Docs/Guide.md",
    });

    expect(stale.repository).toBeNull();
    expect(tools(stale)).toEqual(["assistant"]);
  });

  it("clears document and repository context when the active local copy is removed", () => {
    const model = new ActiveContextModel();
    model.documentLoaded("C:\\repo\\docs\\proposal.md");
    model.workspaceChanged(named);

    const cleared = model.documentCleared();

    expect(cleared).toBe(EMPTY_ACTIVE_CONTEXT);
    expect(tools(cleared)).toEqual(["assistant"]);
  });
  it("keeps an explicitly opened review authoritative across late document events", () => {
    const model = new ActiveContextModel();
    model.documentLoaded("C:\\repo\\docs\\proposal.md");
    model.workspaceChanged(named);

    const opened = model.pullRequestOpened("acme/review", "spec/review");
    expect(opened.pullRequest?.branch.name).toBe("spec/review");

    expect(model.workspaceChanged(named)).toBe(opened);
    expect(model.statusChanged({ state: "published", label: "Published" })).toBe(opened);
    expect(model.current()).toBe(opened);
  });

  it("releases an explicitly opened review when a document loads", () => {
    const model = new ActiveContextModel();
    model.pullRequestOpened("acme/review", "spec/review");

    const document = model.documentLoaded("C:\\notes\\next.md");

    expect(document.pullRequest).toBeNull();
    expect(document.file?.path).toBe("C:\\notes\\next.md");
  });

  it("uses no fabricated repository or branch while review details are loading", () => {
    const model = new ActiveContextModel();
    model.documentLoaded("C:\\repo\\docs\\proposal.md");
    model.workspaceChanged(named);

    const loading = model.pullRequestLoading();

    expect(loading).toBe(EMPTY_ACTIVE_CONTEXT);
    expect(model.workspaceChanged(named)).toBe(loading);
    expect(model.pullRequestClosed().file?.path).toBe("C:\\repo\\docs\\proposal.md");
  });

  it("has only the global assistant before a document is active", () => {
    expect(new ActiveContextModel().current()).toBe(EMPTY_ACTIVE_CONTEXT);
    expect(tools(EMPTY_ACTIVE_CONTEXT)).toEqual(["assistant"]);
  });
});
