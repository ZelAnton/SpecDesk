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

describe("IPC payload decoders (the native→webview JSON boundary)", () => {
  it("parseDocLoaded accepts a well-formed payload and rejects malformed ones", () => {
    expect(parseDocLoaded({ path: "a.md", text: "x", docDir: "" })).toEqual({
      path: "a.md",
      text: "x",
      docDir: "",
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
    const entry = {
      kind: "changed",
      lineStart: 0,
      lineEnd: 0,
      anchorLine: -1,
      removedText: "",
      baseText: "",
      baseSource: "",
      children: [
        { kind: "changed", childIndex: 1, anchorIndex: -1, removedText: "", baseText: "two" },
      ],
    };
    expect(parseDiffResult({ entries: [entry] })).toEqual({ entries: [entry] });
    expect(parseDiffResult({ entries: [{ ...entry, lineStart: "0" }] })).toBeNull(); // bad field
    expect(parseDiffResult({ entries: [{ ...entry, children: [{ kind: "x" }] }] })).toBeNull(); // bad child
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
