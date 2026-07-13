/**
 * Webview half of the cross-language contract guard (the C# half is
 * tests/SpecDesk.Contracts.Tests/ContractFixtureTests.cs). The fixture is real native wire JSON, emitted
 * by the C# host's serializer; here we assert every native→webview decoder accepts it and surfaces the
 * expected values. If the C# payloads change shape without the decoders following (or vice-versa), one of
 * the two halves fails — the drift can't reach runtime. Regenerate the fixture from the C# side with
 * UPDATE_CONTRACT_FIXTURE=1; see that test's class doc.
 */

import { describe, expect, it } from "vitest";
import {
  parseBranchNameSuggested,
  parseChatAttachment,
  parseChatDelta,
  parseChatDone,
  parseDiffResult,
  parseDocLoaded,
  parseDocumentActivity,
  parseError,
  parseGitHubAccount,
  parseGitHubCode,
  parseImageInserted,
  parsePreview,
  parsePrList,
  parsePrSuggested,
  parseStatus,
  parseTemplates,
  parseTree,
  parseVersionNoteSuggested,
  parseWorkspaceContext,
  parseWorkspaceState,
} from "../src/wire/decoders.js";
import { DIFF_KINDS, Kinds, STATUS_STATES } from "../src/wire/protocol.js";
import diffKinds from "./contract/diff-kinds.json" with { type: "json" };
import lifecycleStates from "./contract/lifecycle-states.json" with { type: "json" };
import fixture from "./contract/native-payloads.json" with { type: "json" };
import wireKinds from "./contract/wire-kinds.json" with { type: "json" };

describe("native→webview contract (decoders accept the C# host's wire shapes)", () => {
  it("doc.loaded", () => {
    const payload = parseDocLoaded(fixture["doc.loaded"]);
    expect(payload).not.toBeNull();
    expect(payload?.path).toBe("specs/billing.md");
    expect(payload?.docDir).toBe("specs");
  });

  it("preview.html (incl. the nested lineMap)", () => {
    const payload = parsePreview(fixture["preview.html"]);
    expect(payload).not.toBeNull();
    expect(payload?.html).toBe("<h1>Billing</h1>");
    expect(payload?.lineMap).toHaveLength(2);
    expect(payload?.lineMap[1]).toEqual({ lineStart: 2, lineEnd: 2 });
  });

  it("status (incl. the optional branch)", () => {
    const payload = parseStatus(fixture.status);
    expect(payload).not.toBeNull();
    expect(payload?.state).toBe("draft");
    expect(payload?.branch).toBe("spec/billing-refunds");
  });

  it("error", () => {
    const payload = parseError(fixture.error);
    expect(payload?.message).toContain("Could not reach GitHub");
  });

  it("image.inserted", () => {
    const payload = parseImageInserted(fixture["image.inserted"]);
    expect(payload?.markdown).toBe("![pasted image](images/diagram.png)");
  });

  it("branch.name.suggested", () => {
    const payload = parseBranchNameSuggested(fixture["branch.name.suggested"]);
    expect(payload?.name).toBe("spec/refund-window");
  });

  it("version.note.suggested", () => {
    const payload = parseVersionNoteSuggested(fixture["version.note.suggested"]);
    expect(payload?.note).toBe("Clarify the refund window is 30 days");
  });

  it("pr.suggested (ready to send: blocked absent)", () => {
    const payload = parsePrSuggested(fixture["pr.suggested"]);
    expect(payload).not.toBeNull();
    expect(payload?.title).toBe("Clarify the refund window");
    expect(payload?.body).toContain("billing.md");
    expect(payload?.blocked).toBeUndefined();
  });

  it("pr.list (author + reviewer items; error absent)", () => {
    const payload = parsePrList(fixture["pr.list"]);
    expect(payload).not.toBeNull();
    expect(payload?.error).toBeUndefined();
    expect(payload?.items).toHaveLength(2);
    expect(payload?.items[0]).toMatchObject({
      number: 42,
      repo: "octo/spec-repo",
      role: "author",
      status: "changesRequested",
    });
    expect(payload?.items[1]).toMatchObject({ role: "reviewer", status: "inReview" });
  });

  it("diff.result (incl. nested children: changed plain block, changed container, removed)", () => {
    const payload = parseDiffResult(fixture["diff.result"]);
    expect(payload).not.toBeNull();
    expect(payload?.entries).toHaveLength(3);

    // The wire is discriminated by kind — narrow each entry before reading its case-specific fields (a
    // removed entry has no baseText/children, a changed one no removedText: the compiler enforces it).
    const changedBlock = payload?.entries[0];
    expect(changedBlock?.kind).toBe("changed");
    if (changedBlock?.kind === "changed") {
      expect(changedBlock.baseText).toBe("The refund window is 14 days.");
      expect(changedBlock.children).toHaveLength(0);
    }

    const container = payload?.entries[1];
    expect(container?.kind).toBe("changed");
    if (container?.kind === "changed") {
      expect(container.children).toHaveLength(2);
      expect(container.children[0]).toMatchObject({
        kind: "changed",
        childIndex: 1,
        baseText: "Net 30",
        baseSource: "| Terms | Net 30 |",
      });
      expect(container.children[1]).toMatchObject({
        kind: "removed",
        anchorIndex: 2,
        removedText: "Legacy clause",
      });
    }

    const removed = payload?.entries[2];
    expect(removed?.kind).toBe("removed");
    if (removed?.kind === "removed") {
      expect(removed.removedText).toBe("Deprecated section");
    }
  });

  it("github.code", () => {
    const payload = parseGitHubCode(fixture["github.code"]);
    expect(payload).not.toBeNull();
    expect(payload?.userCode).toBe("WXYZ-1234");
    expect(payload?.verificationUri).toBe("https://github.com/login/device");
  });

  it("github.account (signed in; the optional message is omitted)", () => {
    const payload = parseGitHubAccount(fixture["github.account"]);
    expect(payload).not.toBeNull();
    expect(payload?.available).toBe(true);
    expect(payload?.signedIn).toBe(true);
    expect(payload?.login).toBe("octocat");
    expect(payload?.message).toBeUndefined();
    expect(payload?.organizations).toEqual(["acme", "octo-labs"]);
    expect(
      parseGitHubAccount({ available: true, signedIn: true, organizations: ["acme", 7] }),
    ).toBeNull();
  });

  it("chat.delta", () => {
    const payload = parseChatDelta(fixture["chat.delta"]);
    expect(payload).not.toBeNull();
    expect(payload?.text).toBe("Here is a summary of the change: ");
  });

  it("chat.done", () => {
    const payload = parseChatDone(fixture["chat.done"]);
    expect(payload?.id).toBe("7");
  });

  it("chat.attachment.picked", () => {
    const payload = parseChatAttachment(fixture["chat.attachment.picked"]);
    expect(payload).toEqual({
      kind: "file",
      label: "billing.md",
      reference: "C:\\specs\\billing.md",
    });
  });

  it("document.activity", () => {
    const payload = parseDocumentActivity(fixture["document.activity"]);
    expect(payload?.document).toBe("billing.md");
    expect(payload?.versions[0]?.note).toBe("Clarify refunds");
    expect(payload?.comments).toEqual([]);
    expect(payload?.history[0]).toMatchObject({
      label: "Document updated",
      note: "Clarify refunds",
    });
  });

  it("templates (personal + remote sets)", () => {
    const payload = parseTemplates(fixture.templates);
    expect(payload).not.toBeNull();
    expect(payload?.personal).toHaveLength(1);
    expect(payload?.personal[0]).toMatchObject({
      id: "summarize-changes",
      title: "Summarize the changes",
    });
    expect(payload?.remote).toHaveLength(1);
    expect(payload?.remote[0]?.id).toBe("team-style");
  });

  it("tree (nested folder + files)", () => {
    const payload = parseTree(fixture.tree);
    expect(payload).not.toBeNull();
    expect(payload?.root).toBe("C:\\specs\\billing-repo");
    expect(payload?.nodes.map((n) => n.name)).toEqual(["specs", "README.md"]);
    const specs = payload?.nodes[0];
    expect(specs?.isDirectory).toBe(true);
    expect(specs?.children.map((n) => n.name)).toEqual(["billing.md"]);
    expect(specs?.children[0]?.isDirectory).toBe(false);
    expect(payload?.nodes[1]?.isDirectory).toBe(false);
    expect(payload?.nodes[1]?.children).toEqual([]);
  });

  it("workspace.state (recent file + favorite folder + registered repo)", () => {
    const payload = parseWorkspaceState(fixture["workspace.state"]);
    expect(payload).not.toBeNull();
    expect(payload?.recent).toHaveLength(1);
    expect(payload?.recent[0]).toMatchObject({ label: "billing.md", isFolder: false });
    expect(payload?.favorites).toHaveLength(1);
    expect(payload?.favorites[0]).toMatchObject({ label: "specs", isFolder: true });
    expect(payload?.repositories).toHaveLength(1);
    expect(payload?.repositories[0]).toMatchObject({
      id: "octo/spec-repo",
      url: "https://github.com/octo/spec-repo",
    });
  });

  it("workspace.context (authoritative repository, branches, and relative path)", () => {
    const payload = parseWorkspaceContext(fixture["workspace.context"]);
    expect(payload).toEqual({
      repository: "billing-repo",
      repositoryRoot: "C:\\specs\\billing-repo",
      branch: "spec/billing-refunds",
      branchState: "named",
      defaultBranch: "main",
      path: "specs/billing.md",
    });
  });

  it("workspace.context rejects contradictory branch states", () => {
    const base = fixture["workspace.context"];
    expect(parseWorkspaceContext({ ...base, branch: null, branchState: "named" })).toBeNull();
    expect(
      parseWorkspaceContext({ ...base, branch: "feature", branchState: "detached" }),
    ).toBeNull();
    expect(
      parseWorkspaceContext({ ...base, branch: "feature", branchState: "unavailable" }),
    ).toBeNull();
    expect(
      parseWorkspaceContext({
        ...base,
        repository: null,
        repositoryRoot: null,
        branch: null,
        branchState: "detached",
        defaultBranch: null,
      }),
    ).toBeNull();
    expect(
      parseWorkspaceContext({
        ...base,
        repository: null,
        repositoryRoot: null,
        branch: null,
        branchState: "unavailable",
      }),
    ).toBeNull();
  });
});

describe("wire kinds (TS Kinds match the C# MessageKinds fixture)", () => {
  it("the Kinds values are exactly the committed wire-kinds set", () => {
    // wire-kinds.json is emitted from C# MessageKinds (ContractFixtureTests). A kind renamed/added on
    // either side fails here — order-independent, so only the set of strings matters.
    expect(new Set(Object.values(Kinds))).toEqual(new Set(wireKinds));
  });
});

describe("lifecycle states (TS StatusState matches the F# Lifecycle.State fixture)", () => {
  it("STATUS_STATES is exactly the committed lifecycle-states set", () => {
    // lifecycle-states.json is emitted from the F# Lifecycle.State union (LifecycleContractTests). A
    // state renamed/added on either side fails here — and because StatusState derives from STATUS_STATES,
    // this keeps the type, the runtime validator, and the F# source of truth in lockstep.
    expect(new Set(STATUS_STATES)).toEqual(new Set(lifecycleStates));
  });
});

describe("diff kinds (TS DiffKind matches the F# DiffWire.DiffKind fixture)", () => {
  it("DIFF_KINDS is exactly the committed diff-kinds set", () => {
    // diff-kinds.json is emitted from F# DiffWire.DiffKind (DiffKindContractTests). A kind renamed/added on
    // either side fails here — and because DiffKind derives from DIFF_KINDS (and the diff decoders validate
    // against it), this keeps the type, the runtime validator, and the F# source of truth in lockstep.
    expect(new Set(DIFF_KINDS)).toEqual(new Set(diffKinds));
  });
});
