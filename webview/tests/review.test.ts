import { describe, expect, it, vi } from "vitest";
import type { DiffMark } from "../src/editor.js";
import type { DiffEntryPayload } from "../src/protocol.js";
import { type DiffSurface, ReviewController, type ReviewDeps } from "../src/review.js";

// A fake editor surface: getText is fixed, setDiff/clearDiff are spies. No DOM — the controller is
// pure state + delegation, so the surfaces and host callbacks are all that need standing in.
function surface(text = "head text") {
  const setDiff = vi.fn<(marks: DiffMark[]) => void>();
  const clearDiff = vi.fn<() => void>();
  const view: DiffSurface = { getText: () => text, setDiff, clearDiff };
  return { view, setDiff, clearDiff };
}

// A single changed plain block (no children) → one whole-block mark when expanded.
function changedEntry(): DiffEntryPayload {
  return {
    kind: "changed",
    lineStart: 0,
    lineEnd: 0,
    anchorLine: -1,
    removedText: "",
    children: [],
    baseText: "old",
    baseSource: "old",
  };
}

function harness(version = 1) {
  const editor = surface();
  const formatted = surface();
  const setPressed = vi.fn();
  const requestCompare = vi.fn();
  const onEmptyState = vi.fn<(showing: boolean) => void>();
  let current = version;
  const deps: ReviewDeps = {
    surfaces: [editor.view, formatted.view],
    setPressed,
    requestCompare,
    docVersion: () => current,
    onEmptyState,
  };
  return {
    review: new ReviewController(deps),
    editor,
    formatted,
    setPressed,
    requestCompare,
    onEmptyState,
    setVersion: (v: number) => {
      current = v;
    },
  };
}

describe("ReviewController", () => {
  it("toggle from idle: presses the button and asks the host to compare, but paints nothing yet", () => {
    const h = harness();
    h.review.toggle();
    expect(h.setPressed).toHaveBeenCalledWith(true);
    expect(h.requestCompare).toHaveBeenCalledTimes(1);
    // The marks only arrive later via applyResult, so no surface is painted on the click itself.
    expect(h.editor.setDiff).not.toHaveBeenCalled();
    expect(h.formatted.setDiff).not.toHaveBeenCalled();
  });

  it("toggle while showing: clears the overlay (un-presses, clears both surfaces)", () => {
    const h = harness();
    h.review.toggle();
    h.review.toggle();
    expect(h.setPressed).toHaveBeenLastCalledWith(false);
    expect(h.editor.clearDiff).toHaveBeenCalledTimes(1);
    expect(h.formatted.clearDiff).toHaveBeenCalledTimes(1);
    // A second compare was NOT requested — toggling off only clears.
    expect(h.requestCompare).toHaveBeenCalledTimes(1);
  });

  it("re-arming after a clear requests a fresh compare", () => {
    const h = harness();
    h.review.toggle(); // on  → compare #1
    h.review.clear(); // edit/save/reload drops the overlay
    h.review.toggle(); // on again → compare #2, button pressed once more
    expect(h.requestCompare).toHaveBeenCalledTimes(2);
    expect(h.setPressed).toHaveBeenLastCalledWith(true);
    // And a result for the live version now paints again (the overlay is genuinely re-armed).
    h.review.applyResult(1, [changedEntry()]);
    expect(h.editor.setDiff).toHaveBeenCalledTimes(1);
  });

  it("clear is a no-op when nothing is showing", () => {
    const h = harness();
    h.review.clear();
    expect(h.setPressed).not.toHaveBeenCalled();
    expect(h.editor.clearDiff).not.toHaveBeenCalled();
  });

  it("applyResult paints both surfaces when showing and the version matches", () => {
    const h = harness(7);
    h.review.toggle();
    h.review.applyResult(7, [changedEntry()]);
    expect(h.editor.setDiff).toHaveBeenCalledTimes(1);
    expect(h.formatted.setDiff).toHaveBeenCalledTimes(1);
    const marks = h.editor.setDiff.mock.calls[0]?.[0];
    expect(Array.isArray(marks)).toBe(true);
    expect(marks).toHaveLength(1); // one changed block → one whole-block mark
    // Both surfaces get the SAME expanded marks (Split shows both at once).
    expect(h.formatted.setDiff.mock.calls[0]?.[0]).toBe(marks);
  });

  it("applyResult is dropped when the overlay is not showing", () => {
    const h = harness(3);
    h.review.applyResult(3, [changedEntry()]);
    expect(h.editor.setDiff).not.toHaveBeenCalled();
  });

  it("applyResult is dropped when the version is stale (the author edited past the snapshot)", () => {
    const h = harness(5);
    h.review.toggle();
    h.setVersion(6); // a genuine edit advanced the live version after the compare request
    h.review.applyResult(5, [changedEntry()]);
    expect(h.editor.setDiff).not.toHaveBeenCalled();
  });

  it("a result arriving after clear() is dropped", () => {
    const h = harness(2);
    h.review.toggle();
    h.review.clear();
    h.review.applyResult(2, [changedEntry()]);
    expect(h.editor.setDiff).not.toHaveBeenCalled();
  });

  it("empty entries paint an empty mark set (a clean diff shows no washes)", () => {
    const h = harness(1);
    h.review.toggle();
    h.review.applyResult(1, []);
    expect(h.editor.setDiff).toHaveBeenCalledWith([]);
    expect(h.formatted.setDiff).toHaveBeenCalledWith([]);
  });

  it("an empty result raises the no-changes notice", () => {
    const h = harness(1);
    h.review.toggle();
    h.review.applyResult(1, []);
    expect(h.onEmptyState).toHaveBeenLastCalledWith(true);
  });

  it("a result with changes lowers the no-changes notice", () => {
    const h = harness(1);
    h.review.toggle();
    h.review.applyResult(1, [changedEntry()]);
    expect(h.onEmptyState).toHaveBeenLastCalledWith(false);
  });

  it("clearing the overlay lowers the no-changes notice", () => {
    const h = harness(1);
    h.review.toggle();
    h.review.applyResult(1, []); // notice raised
    h.review.clear();
    expect(h.onEmptyState).toHaveBeenLastCalledWith(false);
  });

  it("a dropped result (stale / not showing) never touches the notice", () => {
    const h = harness(4);
    h.review.applyResult(4, []); // not reviewing → dropped
    expect(h.onEmptyState).not.toHaveBeenCalled();
  });
});
