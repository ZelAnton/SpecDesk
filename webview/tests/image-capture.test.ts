// @vitest-environment jsdom
import { describe, expect, it, vi } from "vitest";
import type { MarkdownEditor } from "../src/editor.js";
import {
  attachImageCapture,
  type CapturedImage,
  stripDataUrlPrefix,
} from "../src/image-capture.js";

describe("stripDataUrlPrefix", () => {
  it("removes the data URL prefix, leaving the base64 payload", () => {
    expect(stripDataUrlPrefix("data:image/png;base64,AAABBBCCC")).toBe("AAABBBCCC");
  });

  it("returns the input unchanged when there is no comma", () => {
    expect(stripDataUrlPrefix("AAABBBCCC")).toBe("AAABBBCCC");
  });
});

/** A minimal fake `DataTransferItem` — just the bits `attachImageCapture` reads. */
function fileItem(file: File): DataTransferItem {
  return { kind: "file", type: file.type, getAsFile: () => file } as unknown as DataTransferItem;
}

/** A fake `clipboardData`/`dataTransfer`: an item list (array-like, not iterable — matches the real
 *  DataTransferItemList in Chromium/WebView2) plus `getData("text/plain")`. */
function clipboard(items: DataTransferItem[], text = ""): DataTransfer {
  const list: Record<number, DataTransferItem> & { length: number } = { length: items.length };
  items.forEach((item, i) => {
    list[i] = item;
  });
  return {
    items: list as unknown as DataTransferItemList,
    getData: (format: string) => (format === "text/plain" ? text : ""),
  } as unknown as DataTransfer;
}

/** Build and dispatch a real `Event` with `clipboardData` (or `dataTransfer`) monkey-patched onto it —
 *  jsdom has no working `ClipboardEvent`/`DragEvent` constructors that actually carry file data. */
function dispatchPaste(dom: EventTarget, clipboardData: DataTransfer): Event {
  const event = new Event("paste", { cancelable: true });
  Object.defineProperty(event, "clipboardData", { value: clipboardData });
  dom.dispatchEvent(event);
  return event;
}

function dispatchDrop(dom: EventTarget, dataTransfer: DataTransfer): Event {
  const event = new Event("drop", { cancelable: true });
  Object.defineProperty(event, "dataTransfer", { value: dataTransfer });
  Object.defineProperty(event, "clientX", { value: 0 });
  Object.defineProperty(event, "clientY", { value: 0 });
  dom.dispatchEvent(event);
  return event;
}

/** A fake editor exposing just the members `attachImageCapture` reads. */
function fakeEditor(): { editor: MarkdownEditor; dom: HTMLDivElement } {
  const dom = document.createElement("div");
  const editor = {
    contentDOM: dom,
    selectionHead: () => 5,
    posAtCoords: () => 3,
  } as unknown as MarkdownEditor;
  return { editor, dom };
}

describe("attachImageCapture (jsdom)", () => {
  it("captures a pasted image when the clipboard has no plain text", async () => {
    const { editor, dom } = fakeEditor();
    const onImage = vi.fn<(image: CapturedImage) => void>();
    attachImageCapture(editor, onImage);
    const file = new File(["fake-bytes"], "photo.png", { type: "image/png" });

    const event = dispatchPaste(dom, clipboard([fileItem(file)]));
    // Wait for the async FileReader-based capture to settle (jsdom's FileReader can take more than one
    // microtask tick, so poll rather than a single fixed setTimeout).
    await vi.waitFor(() => expect(onImage).toHaveBeenCalled());

    expect(event.defaultPrevented).toBe(true);
    expect(onImage).toHaveBeenCalledTimes(1);
    expect(onImage.mock.calls[0]?.[0]).toMatchObject({
      originalName: "photo.png",
      mime: "image/png",
      pos: 5,
    });
  });

  it("skips the image when the clipboard also carries non-empty plain text (S-16)", async () => {
    // The trivial repro from the finding: a clipboard with both an image file AND plain text (an Excel
    // cell, a Word snippet, …) — CodeMirror's own default paste handling already inserted the text and
    // called preventDefault() itself; this listener must defer to it instead of ALSO inserting an image.
    const { editor, dom } = fakeEditor();
    const onImage = vi.fn<(image: CapturedImage) => void>();
    attachImageCapture(editor, onImage);
    const file = new File(["fake-bytes"], "photo.png", { type: "image/png" });

    const event = dispatchPaste(dom, clipboard([fileItem(file)], "some cell text"));
    // A short but generous settle window: there is nothing async to WAIT FOR here (the guard returns
    // synchronously, before any FileReader is ever created), so a fixed delay is enough to be confident
    // nothing fires later — unlike the "should eventually fire" tests above, longer would only ever
    // strengthen this assertion, never flip it.
    await new Promise((resolve) => setTimeout(resolve, 20));

    expect(onImage).not.toHaveBeenCalled();
    // Must not fight CodeMirror's own handling of the text paste by cancelling the event itself.
    expect(event.defaultPrevented).toBe(false);
  });

  it("still captures a non-image paste's absence of files (no items) without throwing", () => {
    const { editor, dom } = fakeEditor();
    const onImage = vi.fn<(image: CapturedImage) => void>();
    attachImageCapture(editor, onImage);

    expect(() => dispatchPaste(dom, clipboard([]))).not.toThrow();
    expect(onImage).not.toHaveBeenCalled();
  });

  it("captures a dropped image at the drop coordinates", async () => {
    const { editor, dom } = fakeEditor();
    const onImage = vi.fn<(image: CapturedImage) => void>();
    attachImageCapture(editor, onImage);
    const file = new File(["fake-bytes"], "diagram.png", { type: "image/png" });

    const dataTransfer = { files: [file] } as unknown as DataTransfer;
    const event = dispatchDrop(dom, dataTransfer);
    await vi.waitFor(() => expect(onImage).toHaveBeenCalled());

    expect(event.defaultPrevented).toBe(true);
    expect(onImage).toHaveBeenCalledTimes(1);
    expect(onImage.mock.calls[0]?.[0]).toMatchObject({ originalName: "diagram.png", pos: 3 });
  });
});
