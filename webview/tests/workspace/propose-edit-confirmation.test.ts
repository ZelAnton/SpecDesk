// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  ProposeEditConfirmation,
  type ProposeEditRequest,
} from "../../src/workspace/tools/propose-edit-confirmation.js";

function harness() {
  const onAccept = vi.fn<(id: string, text: string) => void>();
  const onReject = vi.fn<(id: string) => void>();
  const container = document.createElement("div");
  document.body.appendChild(container);
  const surface = new ProposeEditConfirmation({ container, onAccept, onReject });
  const root = () => container.querySelector<HTMLElement>(".propose-edit");
  const button = (cls: string) => container.querySelector<HTMLButtonElement>(`.${cls}`);
  return { surface, container, onAccept, onReject, root, button };
}

const request: ProposeEditRequest = {
  id: "42",
  currentText: "The refund window is 14 days.\n",
  proposedText: "The refund window is 30 days.\n",
  summary: "Extend the refund window.",
};

beforeEach(() => {
  document.body.replaceChildren();
});

describe("ProposeEditConfirmation", () => {
  it("mounts the confirmation surface with the summary and an inline word diff", () => {
    const { surface, root, container } = harness();
    surface.open(request);

    const mounted = root();
    expect(mounted).not.toBeNull();
    expect(mounted?.getAttribute("role")).toBe("group");
    expect(container.querySelector(".propose-edit-note")?.textContent).toContain(
      "Extend the refund window.",
    );
    // The inline diff highlights the changed word without a new diff algorithm (reuses wordDiff).
    expect(container.querySelector(".propose-edit-word-added")?.textContent).toBe("30");
    expect(container.querySelector(".propose-edit-word-removed")?.textContent).toBe("14");
    expect(surface.isOpen).toBe(true);
  });

  it("sends the proposed text on Apply and closes the surface", () => {
    const { surface, onAccept, onReject, button, root } = harness();
    surface.open(request);

    button("propose-edit-accept")?.click();

    expect(onAccept).toHaveBeenCalledWith("42", "The refund window is 30 days.\n");
    expect(onReject).not.toHaveBeenCalled();
    expect(root()).toBeNull();
    expect(surface.isOpen).toBe(false);
  });

  it("sends a rejection on Discard and applies nothing", () => {
    const { surface, onAccept, onReject, button, root } = harness();
    surface.open(request);

    button("propose-edit-reject")?.click();

    expect(onReject).toHaveBeenCalledWith("42");
    expect(onAccept).not.toHaveBeenCalled();
    expect(root()).toBeNull();
  });

  it("applies the author-edited text, not the original proposal, after Edit", () => {
    const { surface, onAccept, container, button } = harness();
    surface.open(request);

    button("propose-edit-edit")?.click();
    const editor = container.querySelector<HTMLTextAreaElement>(".propose-edit-editor");
    expect(editor?.hidden).toBe(false);
    if (!editor) {
      throw new Error("the editor did not reveal");
    }
    editor.value = "The refund window is 45 days.\n";
    editor.dispatchEvent(new Event("input"));
    // The inline diff re-renders against the edited text.
    expect(container.querySelector(".propose-edit-word-added")?.textContent).toBe("45");

    button("propose-edit-accept")?.click();

    expect(onAccept).toHaveBeenCalledWith("42", "The refund window is 45 days.\n");
  });

  it("discards the edited text without applying it when Discard is pressed after editing", () => {
    const { surface, onAccept, onReject, container, button } = harness();
    surface.open(request);
    button("propose-edit-edit")?.click();
    const editor = container.querySelector<HTMLTextAreaElement>(".propose-edit-editor");
    if (editor) {
      editor.value = "The refund window is 99 days.\n";
      editor.dispatchEvent(new Event("input"));
    }

    button("propose-edit-reject")?.click();

    expect(onReject).toHaveBeenCalledWith("42");
    expect(onAccept).not.toHaveBeenCalled();
  });

  it("Escape discards the proposal", () => {
    const { surface, onReject, root } = harness();
    surface.open(request);

    root()?.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));

    expect(onReject).toHaveBeenCalledWith("42");
  });

  it("close() dismisses without sending any decision", () => {
    const { surface, onAccept, onReject, root } = harness();
    surface.open(request);

    surface.close();

    expect(root()).toBeNull();
    expect(onAccept).not.toHaveBeenCalled();
    expect(onReject).not.toHaveBeenCalled();
  });

  it("a newer proposal supersedes an open one", () => {
    const { surface, container } = harness();
    surface.open(request);
    surface.open({
      ...request,
      id: "43",
      proposedText: "A wholly different body.\n",
      summary: null,
    });

    // Exactly one surface is mounted, and it reflects the newer proposal.
    expect(container.querySelectorAll(".propose-edit")).toHaveLength(1);
    expect(container.querySelector(".propose-edit-note")?.textContent).toContain(
      "Review the suggested change.",
    );
  });

  it("falls back to a whole-block before/after view for a large change", () => {
    const { surface, container } = harness();
    surface.open({
      id: "50",
      currentText: "alpha beta gamma delta epsilon",
      proposedText: "one two three four five six seven",
      summary: null,
    });

    expect(container.querySelector(".propose-edit-block-removed")?.textContent).toContain(
      "alpha beta gamma delta epsilon",
    );
    expect(container.querySelector(".propose-edit-block-added")?.textContent).toContain(
      "one two three four five six seven",
    );
  });
});
