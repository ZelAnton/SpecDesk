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
  parseDiffResult,
  parseDocLoaded,
  parseError,
  parseImageInserted,
  parsePreview,
  parseStatus,
  parseVersionNoteSuggested,
} from "../src/decoders.js";
import { Kinds } from "../src/protocol.js";
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

  it("diff.result (incl. nested children: changed plain block, changed container, removed)", () => {
    const payload = parseDiffResult(fixture["diff.result"]);
    expect(payload).not.toBeNull();
    expect(payload?.entries).toHaveLength(3);

    const changedBlock = payload?.entries[0];
    expect(changedBlock?.kind).toBe("changed");
    expect(changedBlock?.baseText).toBe("The refund window is 14 days.");
    expect(changedBlock?.children).toHaveLength(0);

    const container = payload?.entries[1];
    expect(container?.children).toHaveLength(2);
    expect(container?.children[0]).toMatchObject({
      kind: "changed",
      childIndex: 1,
      baseText: "Net 30",
    });
    expect(container?.children[1]).toMatchObject({
      kind: "removed",
      anchorIndex: 2,
      removedText: "Legacy clause",
    });

    expect(payload?.entries[2]?.kind).toBe("removed");
    expect(payload?.entries[2]?.removedText).toBe("Deprecated section");
  });
});

describe("wire kinds (TS Kinds match the C# MessageKinds fixture)", () => {
  it("the Kinds values are exactly the committed wire-kinds set", () => {
    // wire-kinds.json is emitted from C# MessageKinds (ContractFixtureTests). A kind renamed/added on
    // either side fails here — order-independent, so only the set of strings matters.
    expect(new Set(Object.values(Kinds))).toEqual(new Set(wireKinds));
  });
});
