import { describe, expect, it } from "vitest";
import {
  parseBranchNameSuggested,
  parseDiffResult,
  parseDocDiscardCompleted,
  parseDocLoaded,
  parseDocOpenCompleted,
  parseError,
  parseImageInserted,
  parsePreview,
  parseRepoConfirmation,
  parseRepoOperationCompleted,
  parseStatus,
  parseTree,
  parseVersionNoteSuggested,
  parseWindowCloseCompleted,
  parseWindowCloseRequested,
  parseWorkspaceState,
} from "../../src/wire/decoders.js";

describe("IPC payload decoders (the native→webview JSON boundary)", () => {
  it("accepts only correlated window-close handshake payloads", () => {
    expect(parseWindowCloseRequested({ requestId: 7 })).toEqual({ requestId: 7 });
    expect(parseWindowCloseRequested({ requestId: 0 })).toBeNull();
    expect(parseWindowCloseRequested({ requestId: Number.MAX_SAFE_INTEGER + 1 })).toBeNull();
    expect(parseWindowCloseCompleted({ requestId: 7, succeeded: false })).toEqual({
      requestId: 7,
      succeeded: false,
    });
    expect(parseWindowCloseCompleted({ requestId: 7 })).toBeNull();
    expect(parseWindowCloseCompleted({ requestId: 7, succeeded: "no" })).toBeNull();
  });
  it("parseRepoConfirmation normalizes an omitted clone branch but still requires a delete branch", () => {
    const cloneConfirmation = {
      operation: "deleteClone",
      id: "acme/specs",
      clonePath: "C:\\SpecDesk\\repos\\product-specs",
      message: "Delete this local copy from this computer?",
      warnings: ["There are unfinished local edits."],
      confirmationToken: "DD42A087",
    };

    expect(parseRepoConfirmation(cloneConfirmation)).toEqual({
      ...cloneConfirmation,
      branch: null,
    });
    expect(parseRepoConfirmation({ ...cloneConfirmation, operation: "deleteBranch" })).toBeNull();
    expect(parseRepoConfirmation({ ...cloneConfirmation, branch: "review-copy" })).toBeNull();
    expect(
      parseRepoConfirmation({
        ...cloneConfirmation,
        operation: "deleteBranch",
        branch: "review-copy",
      }),
    ).toEqual({ ...cloneConfirmation, operation: "deleteBranch", branch: "review-copy" });
  });

  it("parseRepoOperationCompleted accepts only positive safe request IDs", () => {
    expect(parseRepoOperationCompleted({ requestId: 7 })).toEqual({ requestId: 7 });
    expect(parseRepoOperationCompleted({ requestId: 0 })).toBeNull();
    expect(parseRepoOperationCompleted({ requestId: Number.MAX_SAFE_INTEGER + 1 })).toBeNull();
  });

  it("parseDocDiscardCompleted accepts only correlated terminal results", () => {
    expect(parseDocDiscardCompleted({ requestId: 8, succeeded: false })).toEqual({
      requestId: 8,
      succeeded: false,
    });
    expect(parseDocDiscardCompleted({ requestId: 0, succeeded: false })).toBeNull();
    expect(parseDocDiscardCompleted({ requestId: 8 })).toBeNull();
  });
  it("parseDocOpenCompleted accepts only correlated terminal results", () => {
    expect(parseDocOpenCompleted({ requestId: 7, succeeded: true })).toEqual({
      requestId: 7,
      succeeded: true,
    });
    expect(parseDocOpenCompleted({ requestId: 0, succeeded: true })).toBeNull();
    expect(parseDocOpenCompleted({ requestId: 7 })).toBeNull();
    expect(parseDocOpenCompleted({ requestId: 7, succeeded: "yes" })).toBeNull();
  });

  it("parseDocLoaded accepts a well-formed payload and rejects malformed ones", () => {
    expect(parseDocLoaded({ path: "a.md", text: "x", docDir: "", readOnly: false })).toEqual({
      path: "a.md",
      text: "x",
      docDir: "",
      readOnly: false,
    });
    expect(parseDocLoaded({ path: "a.md", text: "x" })).toBeNull(); // missing docDir
    expect(parseDocLoaded({ path: 1, text: "x", docDir: "" })).toBeNull(); // wrong type
    expect(parseDocLoaded(null)).toBeNull();
    expect(parseDocLoaded("a.md")).toBeNull();
    expect(parseDocLoaded([])).toBeNull();
  });

  it("parsePreview validates the nested lineMap array", () => {
    expect(parsePreview({ html: "<p>x</p>", lineMap: [{ lineStart: 0, lineEnd: 1 }] })).toEqual({
      html: "<p>x</p>",
      lineMap: [{ lineStart: 0, lineEnd: 1 }],
    });
    expect(parsePreview({ html: "<p>x</p>", lineMap: [{ lineStart: 0 }] })).toBeNull(); // bad span
    expect(parsePreview({ html: "<p>x</p>", lineMap: "nope" })).toBeNull();
    expect(parsePreview({ lineMap: [] })).toBeNull(); // missing html
  });

  it("parseDiffResult validates entries and their children deeply", () => {
    // The wire is discriminated by kind — a changed entry carries only its own fields (no removed sentinels),
    // and its children are per-kind too. The decoder narrows to exactly this shape.
    const entry = {
      kind: "changed",
      lineStart: 0,
      lineEnd: 0,
      baseText: "",
      baseSource: "",
      children: [{ kind: "changed", childIndex: 1, baseText: "two", baseSource: "| two |" }],
    };
    expect(parseDiffResult({ entries: [entry] })).toEqual({ entries: [entry] });
    expect(parseDiffResult({ entries: [{ ...entry, lineStart: "0" }] })).toBeNull(); // bad field
    expect(parseDiffResult({ entries: [{ ...entry, children: [{ kind: "x" }] }] })).toBeNull(); // bad child
    // A changed child missing its baseSource is a contract drift (mirrors the whole-block field) — reject.
    expect(
      parseDiffResult({
        entries: [{ ...entry, children: [{ kind: "changed", childIndex: 1, baseText: "two" }] }],
      }),
    ).toBeNull();
    // A removed entry with a head line range is not a valid removed shape (removed has no range) — but the
    // decoder simply reads removed's own fields (anchorLine/removedText) and ignores the stray range.
    expect(
      parseDiffResult({ entries: [{ kind: "removed", anchorLine: 3, removedText: "gone" }] }),
    ).toEqual({
      entries: [{ kind: "removed", anchorLine: 3, removedText: "gone" }],
    });
    expect(parseDiffResult({ entries: "nope" })).toBeNull();
    expect(parseDiffResult({})).toBeNull();
  });

  it("parseStatus validates the state union and the optional branch", () => {
    expect(parseStatus({ state: "draft", label: "Draft" })).toEqual({
      state: "draft",
      label: "Draft",
    });
    expect(parseStatus({ state: "draft", label: "Draft", branch: "spec/x" })).toEqual({
      state: "draft",
      label: "Draft",
      branch: "spec/x",
    });
    expect(parseStatus({ state: "bogus", label: "x" })).toBeNull(); // not a StatusState
    expect(parseStatus({ state: "draft", label: "x", branch: 1 })).toBeNull(); // bad branch type
    expect(parseStatus({ state: "draft" })).toBeNull(); // missing label
  });

  it("parseTree accepts a nested tree and rejects malformed nodes", () => {
    const tree = parseTree({
      root: "/w",
      requestId: 4,
      nodes: [
        {
          name: "docs",
          path: "/w/docs",
          isDirectory: true,
          hasChildren: true,
          children: [
            {
              name: "a.md",
              path: "/w/docs/a.md",
              isDirectory: false,
              children: [],
              hasChildren: false,
            },
          ],
        },
        { name: "b.md", path: "/w/b.md", isDirectory: false, children: [], hasChildren: false },
      ],
    });
    expect(tree?.root).toBe("/w");
    expect(tree?.nodes[0]?.children[0]?.name).toBe("a.md");
    expect(tree?.nodes[1]?.isDirectory).toBe(false);
    expect(
      parseTree({ root: "/w/docs", requestId: 5, nodes: [], error: "Try again", remote: true }),
    ).toEqual({
      root: "/w/docs",
      requestId: 5,
      nodes: [],
      error: "Try again",
      remote: true,
    });

    expect(parseTree({ root: "/w", requestId: 4 })).toBeNull(); // missing nodes
    expect(parseTree({ root: 1, requestId: 4, nodes: [] })).toBeNull(); // bad root type
    expect(parseTree({ root: "/w", requestId: 4, nodes: [], error: 1 })).toBeNull();
    expect(parseTree({ root: "/w", requestId: 4, nodes: [], remote: "yes" })).toBeNull();
    // A node missing `children` (or with a bad child) is drift — the whole payload rejects.
    expect(
      parseTree({
        root: "/w",
        requestId: 4,
        nodes: [{ name: "x", path: "/w/x", isDirectory: true }],
      }),
    ).toBeNull();
    expect(
      parseTree({
        root: "/w",
        requestId: 4,
        nodes: [{ name: "x", path: "/w/x", isDirectory: "yes", children: [], hasChildren: false }],
      }),
    ).toBeNull(); // isDirectory not a boolean
  });

  it("parseWorkspaceState validates items and rejects drift", () => {
    const state = {
      recent: [{ path: "C:\\specs\\a.md", label: "a.md", isFolder: false, kind: "local" }],
      favorites: [{ path: "C:\\specs", label: "specs", isFolder: true, kind: "local" }],
      repositories: [
        {
          id: "octo/spec",
          name: "octo/spec",
          url: "https://github.com/octo/spec",
          defaultBranch: "master",
          clones: [
            {
              id: "octo_spec",
              path: "C:\\repos\\octo_spec",
              currentBranch: "draft",
              status: {
                ahead: 2,
                behind: 1,
                hasUncommitted: true,
                stashCount: 1,
                hasConflicts: false,
              },
              branches: [
                {
                  name: "draft",
                  canDelete: true,
                  status: {
                    ahead: 2,
                    behind: 1,
                    hasUncommitted: true,
                    stashCount: 1,
                    hasConflicts: false,
                  },
                },
              ],
            },
          ],
        },
      ],
    };
    expect(parseWorkspaceState(state)).toEqual(state);
    const repository = state.repositories[0];
    const clone = repository?.clones[0];
    if (repository === undefined || clone === undefined) {
      throw new Error("The workspace-state fixture must contain one local copy.");
    }
    const { currentBranch: _omittedCurrentBranch, ...cloneWithoutCurrentBranch } = clone;
    const nativeOmittedNull = parseWorkspaceState({
      ...state,
      repositories: [
        {
          ...repository,
          clones: [cloneWithoutCurrentBranch],
        },
      ],
    });
    expect(nativeOmittedNull?.repositories[0]?.clones[0]?.currentBranch).toBeNull();
    expect(
      parseWorkspaceState({
        ...state,
        repositories: [
          {
            ...repository,
            clones: [{ ...clone, status: { ahead: -1 } }],
          },
        ],
      }),
    ).toBeNull();
    for (const favorite of [
      { path: "docs/guide.md", label: "guide.md", isFolder: false, kind: "remote" },
      {
        path: "../escape.md",
        label: "escape.md",
        isFolder: false,
        kind: "remote",
        repositoryId: "octo/spec",
        branch: "main",
      },
      {
        path: "octo/other",
        label: "octo/spec",
        isFolder: true,
        kind: "repository",
        repositoryId: "octo/spec",
      },
      {
        path: "C:\\local.md",
        label: "local.md",
        isFolder: false,
        kind: "local",
        repositoryId: "octo/spec",
      },
      { path: "relative.md", label: "relative.md", isFolder: false, kind: "local" },
      {
        path: "bad owner/spec",
        label: "bad",
        isFolder: true,
        kind: "repository",
        repositoryId: "bad owner/spec",
      },
      {
        path: "-owner/spec",
        label: "bad",
        isFolder: true,
        kind: "repository",
        repositoryId: "-owner/spec",
      },
      {
        path: "octo/spec:bad",
        label: "bad",
        isFolder: true,
        kind: "repository",
        repositoryId: "octo/spec:bad",
      },
    ]) {
      expect(parseWorkspaceState({ ...state, favorites: [favorite] })).toBeNull();
    }

    // A missing list is drift — the whole payload rejects.
    expect(parseWorkspaceState({ recent: [], favorites: [] })).toBeNull();
    // A recent item missing `isFolder` (or with a wrong-typed field) nulls the whole payload.
    expect(
      parseWorkspaceState({
        recent: [{ path: "/a.md", label: "a.md" }],
        favorites: [],
        repositories: [],
      }),
    ).toBeNull();
    // A registered repo with a non-string url is drift.
    expect(
      parseWorkspaceState({
        recent: [],
        favorites: [],
        repositories: [{ id: "octo/spec", name: "octo/spec", url: 1 }],
      }),
    ).toBeNull();
    expect(parseWorkspaceState(null)).toBeNull();
  });

  it("the single-string payload decoders reject the wrong shape", () => {
    expect(parseError({ message: "boom" })).toEqual({ message: "boom" });
    expect(parseError({})).toBeNull();
    expect(parseImageInserted({ markdown: "![](x)" })).toEqual({ markdown: "![](x)" });
    expect(parseImageInserted({ markdown: 1 })).toBeNull();
    expect(parseBranchNameSuggested({ name: "spec/x" })).toEqual({ name: "spec/x" });
    expect(parseBranchNameSuggested(undefined)).toBeNull();
    expect(parseVersionNoteSuggested({ note: "n" })).toEqual({ note: "n" });
    expect(parseVersionNoteSuggested(null)).toBeNull();
  });
});
