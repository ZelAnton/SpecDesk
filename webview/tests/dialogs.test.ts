// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { Dialogs, sanitizeDraftName } from "../src/dialogs.js";

// Minimal markup mirroring the two inline bars' ids; both start hidden, as in index.html.
function setupDom(): void {
  document.body.innerHTML = `
    <div id="branch-name-bar" hidden>
      <input id="branch-name-input" />
      <button id="branch-name-confirm"></button>
      <button id="branch-name-cancel"></button>
    </div>
    <div id="version-note-bar" hidden>
      <input id="version-note-input" />
      <textarea id="version-note-textarea" hidden></textarea>
      <button id="version-note-expand"></button>
      <button id="version-note-confirm"></button>
      <button id="version-note-cancel"></button>
    </div>
    <div id="pr-text-bar" hidden>
      <input id="pr-title-input" />
      <textarea id="pr-body-textarea"></textarea>
      <button id="pr-text-confirm"></button>
      <button id="pr-text-cancel"></button>
    </div>
  `;
}

// instanceof narrowing (no `as`) — throws if the test markup drifts from what Dialogs queries.
function div(id: string): HTMLElement {
  const el = document.querySelector(`#${id}`);
  if (!(el instanceof HTMLElement)) {
    throw new Error(`#${id} missing`);
  }
  return el;
}
function input(id: string): HTMLInputElement {
  const el = document.querySelector(`#${id}`);
  if (!(el instanceof HTMLInputElement)) {
    throw new Error(`#${id} not an input`);
  }
  return el;
}
function textarea(id: string): HTMLTextAreaElement {
  const el = document.querySelector(`#${id}`);
  if (!(el instanceof HTMLTextAreaElement)) {
    throw new Error(`#${id} not a textarea`);
  }
  return el;
}
function button(id: string): HTMLButtonElement {
  const el = document.querySelector(`#${id}`);
  if (!(el instanceof HTMLButtonElement)) {
    throw new Error(`#${id} not a button`);
  }
  return el;
}

function mount(
  suggest: {
    branch?: string;
    version?: string;
    pr?: { title: string; body: string; blocked?: string };
  } = {},
) {
  const onBranchName = vi.fn();
  const onVersionNote = vi.fn();
  const onPrText = vi.fn();
  const onPrBlocked = vi.fn();
  const suggestBranchName = vi.fn(async () => suggest.branch ?? "");
  const suggestVersionNote = vi.fn(async () => suggest.version ?? "");
  const suggestPrText = vi.fn(async () => suggest.pr ?? { title: "", body: "" });
  const dialogs = new Dialogs({
    suggestBranchName,
    onBranchName,
    suggestVersionNote,
    onVersionNote,
    suggestPrText,
    onPrBlocked,
    onPrText,
  });
  return {
    dialogs,
    onBranchName,
    onVersionNote,
    onPrText,
    onPrBlocked,
    suggestBranchName,
    suggestVersionNote,
    suggestPrText,
  };
}

beforeEach(setupDom);

describe("sanitizeDraftName", () => {
  it("maps backslashes to '/' and any other disallowed char to '_', preserving length", () => {
    expect(sanitizeDraftName("Feature One")).toBe("Feature_One");
    expect(sanitizeDraftName("a\\b")).toBe("a/b");
    expect(sanitizeDraftName("ok._/-Name9")).toBe("ok._/-Name9"); // all allowed → unchanged
    expect(sanitizeDraftName("drop#these!chars")).toBe("drop_these_chars");
  });
});

describe("Dialogs — draft-name bar", () => {
  it("opens prefilled with the host suggestion and revealed", async () => {
    const { dialogs } = mount({ branch: "spec/suggested" });
    await dialogs.openBranchName();
    expect(input("branch-name-input").value).toBe("spec/suggested");
    expect(div("branch-name-bar").hidden).toBe(false);
  });

  it("does not stack requests when already open", async () => {
    const { dialogs, suggestBranchName } = mount({ branch: "x" });
    await dialogs.openBranchName();
    await dialogs.openBranchName(); // already open → no-op
    expect(suggestBranchName).toHaveBeenCalledTimes(1);
  });

  it("confirm trims the name, closes the bar, and reports it", async () => {
    const { dialogs, onBranchName } = mount();
    await dialogs.openBranchName();
    input("branch-name-input").value = "  spec/x  ";
    button("branch-name-confirm").click();
    expect(onBranchName).toHaveBeenCalledWith("spec/x");
    expect(div("branch-name-bar").hidden).toBe(true);
  });

  it("Enter confirms, Escape closes without reporting", async () => {
    const { dialogs, onBranchName } = mount();
    await dialogs.openBranchName();
    input("branch-name-input").value = "spec/y";
    input("branch-name-input").dispatchEvent(new KeyboardEvent("keydown", { key: "Enter" }));
    expect(onBranchName).toHaveBeenCalledWith("spec/y");

    await dialogs.openBranchName();
    input("branch-name-input").dispatchEvent(new KeyboardEvent("keydown", { key: "Escape" }));
    expect(div("branch-name-bar").hidden).toBe(true);
    expect(onBranchName).toHaveBeenCalledTimes(1); // Escape did not report
  });

  it("Escape with focus on a dialog button also closes the bar", async () => {
    const { dialogs, onBranchName } = mount();
    await dialogs.openBranchName();
    button("branch-name-confirm").focus();
    button("branch-name-confirm").dispatchEvent(
      new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
    );
    expect(div("branch-name-bar").hidden).toBe(true);
    expect(onBranchName).not.toHaveBeenCalled();
  });

  it("live-sanitizes the input as it is typed", async () => {
    const { dialogs } = mount();
    await dialogs.openBranchName();
    const el = input("branch-name-input");
    el.value = "a b\\c?";
    el.dispatchEvent(new Event("input"));
    expect(el.value).toBe("a_b/c_");
  });

  it("restores the caret after live-sanitizing mid-string (length is preserved 1:1)", async () => {
    const { dialogs } = mount();
    await dialogs.openBranchName();
    const el = input("branch-name-input");
    el.value = "ab cd";
    el.setSelectionRange(3, 3); // caret right after the space
    el.dispatchEvent(new Event("input"));
    expect(el.value).toBe("ab_cd");
    expect(el.selectionStart).toBe(3); // not flung to the end by the value replacement
  });

  it("Cancel closes the bar without reporting", async () => {
    const { dialogs, onBranchName } = mount();
    await dialogs.openBranchName();
    button("branch-name-cancel").click();
    expect(div("branch-name-bar").hidden).toBe(true);
    expect(onBranchName).not.toHaveBeenCalled();
  });

  it("focuses the input on open", async () => {
    const { dialogs } = mount({ branch: "spec/x" });
    await dialogs.openBranchName();
    expect(document.activeElement).toBe(input("branch-name-input"));
  });

  it("does not stack requests while the suggestion is still in flight", async () => {
    // A second open during the in-flight window (e.g. a double-click, or a key-mash through
    // onEditAttempt) must be latched out — not fire a second request whose late reply clobbers the field.
    const resolvers: Array<(value: string) => void> = [];
    const suggestBranchName = vi.fn(
      () =>
        new Promise<string>((resolve) => {
          resolvers.push(resolve);
        }),
    );
    const dialogs = new Dialogs({
      suggestBranchName,
      onBranchName: vi.fn(),
      suggestVersionNote: vi.fn(async () => ""),
      onVersionNote: vi.fn(),
      suggestPrText: vi.fn(async () => ({ title: "", body: "" })),
      onPrBlocked: vi.fn(),
      onPrText: vi.fn(),
    });
    const first = dialogs.openBranchName();
    const second = dialogs.openBranchName();
    expect(suggestBranchName).toHaveBeenCalledTimes(1);
    resolvers[0]?.("spec/from-host");
    await Promise.all([first, second]);
    expect(input("branch-name-input").value).toBe("spec/from-host");
  });

  it("a close during the in-flight request keeps the bar closed when the reply lands", async () => {
    const resolvers: Array<(value: string) => void> = [];
    const suggestBranchName = vi.fn(
      () =>
        new Promise<string>((resolve) => {
          resolvers.push(resolve);
        }),
    );
    const dialogs = new Dialogs({
      suggestBranchName,
      onBranchName: vi.fn(),
      suggestVersionNote: vi.fn(async () => ""),
      onVersionNote: vi.fn(),
      suggestPrText: vi.fn(async () => ({ title: "", body: "" })),
      onPrBlocked: vi.fn(),
      onPrText: vi.fn(),
    });
    const opening = dialogs.openBranchName(); // request in flight
    dialogs.closeAll(); // e.g. a new document loads before the suggestion resolves
    resolvers[0]?.("spec/late");
    await opening;
    expect(div("branch-name-bar").hidden).toBe(true); // the stale reply did not re-open it
  });
});

describe("Dialogs — version-note bar", () => {
  it("opens prefilled, revealed, and in the compact single-line state", async () => {
    const { dialogs } = mount({ version: "note text" });
    await dialogs.openVersionNote();
    expect(input("version-note-input").value).toBe("note text");
    expect(div("version-note-bar").hidden).toBe(false);
    expect(input("version-note-input").hidden).toBe(false);
    expect(textarea("version-note-textarea").hidden).toBe(true);
  });

  it("expands to the multi-line textarea, carrying the text over", async () => {
    const { dialogs } = mount();
    await dialogs.openVersionNote();
    input("version-note-input").value = "multi";
    button("version-note-expand").click();
    expect(textarea("version-note-textarea").hidden).toBe(false);
    expect(textarea("version-note-textarea").value).toBe("multi");
    expect(input("version-note-input").hidden).toBe(true);
  });

  it("ArrowDown also expands", async () => {
    const { dialogs } = mount();
    await dialogs.openVersionNote();
    input("version-note-input").value = "abc";
    input("version-note-input").dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown" }));
    expect(textarea("version-note-textarea").hidden).toBe(false);
    expect(textarea("version-note-textarea").value).toBe("abc");
  });

  it("single-line: confirm (click or Enter) trims and reports, then closes", async () => {
    const { dialogs, onVersionNote } = mount();
    await dialogs.openVersionNote();
    input("version-note-input").value = "  fix typo  ";
    button("version-note-confirm").click();
    expect(onVersionNote).toHaveBeenCalledWith("fix typo");
    expect(div("version-note-bar").hidden).toBe(true);
  });

  it("multi-line: Ctrl/Cmd+Enter saves the textarea (trimmed, newline kept)", async () => {
    const { dialogs, onVersionNote } = mount();
    await dialogs.openVersionNote();
    input("version-note-input").value = "line1";
    button("version-note-expand").click();
    const ta = textarea("version-note-textarea");
    ta.value = "line1\nline2  ";
    ta.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", ctrlKey: true }));
    expect(onVersionNote).toHaveBeenCalledWith("line1\nline2");
  });

  it("a bare Enter in the textarea does not save (it inserts a newline)", async () => {
    const { dialogs, onVersionNote } = mount();
    await dialogs.openVersionNote();
    button("version-note-expand").click();
    textarea("version-note-textarea").dispatchEvent(new KeyboardEvent("keydown", { key: "Enter" }));
    expect(onVersionNote).not.toHaveBeenCalled();
  });

  it("Escape (input) and Cancel close the bar without reporting", async () => {
    const { dialogs, onVersionNote } = mount();
    await dialogs.openVersionNote();
    input("version-note-input").dispatchEvent(new KeyboardEvent("keydown", { key: "Escape" }));
    expect(div("version-note-bar").hidden).toBe(true);

    await dialogs.openVersionNote();
    button("version-note-cancel").click();
    expect(div("version-note-bar").hidden).toBe(true);
    expect(onVersionNote).not.toHaveBeenCalled();
  });

  it("Escape with focus on a dialog button also closes the bar", async () => {
    const { dialogs, onVersionNote } = mount();
    await dialogs.openVersionNote();
    button("version-note-confirm").focus();
    button("version-note-confirm").dispatchEvent(
      new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
    );
    expect(div("version-note-bar").hidden).toBe(true);
    expect(onVersionNote).not.toHaveBeenCalled();
  });

  it("does not stack requests when already open", async () => {
    const { dialogs, suggestVersionNote } = mount({ version: "v" });
    await dialogs.openVersionNote();
    await dialogs.openVersionNote(); // already open → no-op
    expect(suggestVersionNote).toHaveBeenCalledTimes(1);
  });

  it("reopening resets a previously-expanded bar to the compact single-line state", async () => {
    const { dialogs } = mount({ version: "note" });
    await dialogs.openVersionNote();
    button("version-note-expand").click();
    expect(textarea("version-note-textarea").hidden).toBe(false); // expanded

    dialogs.closeVersionNote();
    await dialogs.openVersionNote();
    expect(textarea("version-note-textarea").hidden).toBe(true); // reset to single-line
    expect(input("version-note-input").hidden).toBe(false);
    expect(input("version-note-input").value).toBe("note");
  });

  it("a stacked open during the in-flight request never re-runs the reset block", async () => {
    // The reset-to-single-line block would discard an expanded multi-line note if a stacked reply ran
    // it late. The latch lets only one request through, so there is no late reply to do that.
    const resolvers: Array<(value: string) => void> = [];
    const suggestVersionNote = vi.fn(
      () =>
        new Promise<string>((resolve) => {
          resolvers.push(resolve);
        }),
    );
    const dialogs = new Dialogs({
      suggestBranchName: vi.fn(async () => ""),
      onBranchName: vi.fn(),
      suggestVersionNote,
      onVersionNote: vi.fn(),
      suggestPrText: vi.fn(async () => ({ title: "", body: "" })),
      onPrBlocked: vi.fn(),
      onPrText: vi.fn(),
    });
    const first = dialogs.openVersionNote();
    const second = dialogs.openVersionNote();
    expect(suggestVersionNote).toHaveBeenCalledTimes(1);
    resolvers[0]?.("");
    await Promise.all([first, second]);

    button("version-note-expand").click();
    textarea("version-note-textarea").value = "line1\nline2";
    // No second pending reply exists to re-run the reset block, so the note survives.
    expect(textarea("version-note-textarea").hidden).toBe(false);
    expect(textarea("version-note-textarea").value).toBe("line1\nline2");
  });

  it("a close during the in-flight request keeps the bar closed when the reply lands", async () => {
    const resolvers: Array<(value: string) => void> = [];
    const suggestVersionNote = vi.fn(
      () =>
        new Promise<string>((resolve) => {
          resolvers.push(resolve);
        }),
    );
    const dialogs = new Dialogs({
      suggestBranchName: vi.fn(async () => ""),
      onBranchName: vi.fn(),
      suggestVersionNote,
      onVersionNote: vi.fn(),
      suggestPrText: vi.fn(async () => ({ title: "", body: "" })),
      onPrBlocked: vi.fn(),
      onPrText: vi.fn(),
    });
    const opening = dialogs.openVersionNote(); // request in flight
    dialogs.closeAll(); // e.g. a new document loads before the suggestion resolves
    resolvers[0]?.("late note");
    await opening;
    expect(div("version-note-bar").hidden).toBe(true); // the stale reply did not re-open it
  });
});

describe("Dialogs — send-for-review (PR title/body) prompt", () => {
  it("opens prefilled with the host's suggested title and body, focused on the title", async () => {
    const { dialogs, suggestPrText } = mount({
      pr: { title: "Clarify refunds", body: "Body text." },
    });

    await dialogs.openPrText();

    expect(suggestPrText).toHaveBeenCalledTimes(1);
    expect(div("pr-text-bar").hidden).toBe(false);
    expect(input("pr-title-input").value).toBe("Clarify refunds");
    expect(textarea("pr-body-textarea").value).toBe("Body text.");
    expect(document.activeElement).toBe(input("pr-title-input"));
  });

  it("confirm sends the trimmed title and body, then closes", async () => {
    const { dialogs, onPrText } = mount({ pr: { title: "T", body: "B" } });
    await dialogs.openPrText();
    input("pr-title-input").value = "  Edited title  ";
    textarea("pr-body-textarea").value = "  Edited body  ";

    button("pr-text-confirm").click();

    expect(onPrText).toHaveBeenCalledWith({ title: "Edited title", body: "Edited body" });
    expect(div("pr-text-bar").hidden).toBe(true);
  });

  it("Enter in the title sends; Ctrl+Enter in the body sends", async () => {
    const { dialogs, onPrText } = mount({ pr: { title: "T", body: "B" } });

    await dialogs.openPrText();
    input("pr-title-input").dispatchEvent(
      new KeyboardEvent("keydown", { key: "Enter", bubbles: true }),
    );
    expect(onPrText).toHaveBeenCalledTimes(1);

    await dialogs.openPrText();
    textarea("pr-body-textarea").dispatchEvent(
      new KeyboardEvent("keydown", { key: "Enter", ctrlKey: true, bubbles: true }),
    );
    expect(onPrText).toHaveBeenCalledTimes(2);
  });

  it("cancel closes without sending", async () => {
    const { dialogs, onPrText } = mount({ pr: { title: "T", body: "B" } });
    await dialogs.openPrText();

    button("pr-text-cancel").click();

    expect(onPrText).not.toHaveBeenCalled();
    expect(div("pr-text-bar").hidden).toBe(true);
  });

  it("Escape with focus on a dialog button also closes the bar", async () => {
    const { dialogs, onPrText } = mount({ pr: { title: "T", body: "B" } });
    await dialogs.openPrText();
    button("pr-text-confirm").focus();
    button("pr-text-confirm").dispatchEvent(
      new KeyboardEvent("keydown", { key: "Escape", bubbles: true }),
    );
    expect(div("pr-text-bar").hidden).toBe(true);
    expect(onPrText).not.toHaveBeenCalled();
  });

  it("opening the send prompt closes an open version-note bar, and vice versa (one at a time)", async () => {
    const { dialogs } = mount({ version: "v", pr: { title: "T", body: "B" } });

    await dialogs.openVersionNote();
    expect(div("version-note-bar").hidden).toBe(false);
    await dialogs.openPrText();
    expect(div("version-note-bar").hidden).toBe(true); // send prompt closed the version bar
    expect(div("pr-text-bar").hidden).toBe(false);

    await dialogs.openVersionNote();
    expect(div("pr-text-bar").hidden).toBe(true); // version prompt closed the send bar
    expect(div("version-note-bar").hidden).toBe(false);
  });

  it("does not open when the host reports the send is blocked; shows the reason instead", async () => {
    const reason = "Save a version before sending it for review.";
    const { dialogs, onPrBlocked, onPrText } = mount({
      pr: { title: "", body: "", blocked: reason },
    });

    await dialogs.openPrText();

    expect(onPrBlocked).toHaveBeenCalledWith(reason);
    expect(div("pr-text-bar").hidden).toBe(true); // the prompt stays closed
    expect(onPrText).not.toHaveBeenCalled();
  });
});

describe("Dialogs — closing", () => {
  it("closeAll hides every bar; closeVersionNote leaves the draft-name bar alone", async () => {
    const { dialogs } = mount();
    div("branch-name-bar").hidden = false;
    div("version-note-bar").hidden = false;
    div("pr-text-bar").hidden = false;

    dialogs.closeVersionNote();
    expect(div("version-note-bar").hidden).toBe(true);
    expect(div("branch-name-bar").hidden).toBe(false);

    div("version-note-bar").hidden = false;
    dialogs.closeAll();
    expect(div("branch-name-bar").hidden).toBe(true);
    expect(div("version-note-bar").hidden).toBe(true);
    expect(div("pr-text-bar").hidden).toBe(true);
  });

  it("closePrText hides the send prompt but leaves the draft-name prompt open", async () => {
    // Leaving editing closes the draft-only prompts (version note, send-for-review) but must NOT close the
    // "name this draft" prompt, which is legitimately open in the published state before a draft exists.
    const { dialogs } = mount();
    div("branch-name-bar").hidden = false;
    div("pr-text-bar").hidden = false;

    dialogs.closePrText();

    expect(div("pr-text-bar").hidden).toBe(true);
    expect(div("branch-name-bar").hidden).toBe(false);
  });
});
