/**
 * Captures images pasted or dragged onto the editor and reports them as base64 plus the insert
 * position. All naming/processing/saving is native (docs/design/06-images.md); this is just the
 * capture glue. Uses a type-only editor import so the module has no runtime CodeMirror dependency.
 */

import type { MarkdownEditor } from "./editor.js";

export interface CapturedImage {
  base64: string;
  originalName: string;
  mime: string;
  /** Document position at which to insert the resulting link. */
  pos: number;
}

/** Strip the `data:<mime>;base64,` prefix from a data URL, leaving the raw base64. Pure. */
export function stripDataUrlPrefix(dataUrl: string): string {
  const comma = dataUrl.indexOf(",");
  return comma >= 0 ? dataUrl.slice(comma + 1) : dataUrl;
}

function readAsBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(stripDataUrlPrefix(String(reader.result)));
    reader.onerror = () => reject(reader.error ?? new Error("Could not read the image file."));
    reader.readAsDataURL(file);
  });
}

async function emit(
  files: File[],
  pos: number,
  onImage: (image: CapturedImage) => void,
): Promise<void> {
  for (const file of files) {
    const base64 = await readAsBase64(file).catch(() => null);
    if (base64 !== null) {
      onImage({ base64, originalName: file.name, mime: file.type, pos });
    }
  }
}

/** Wire paste and drop on the editor; each captured image is reported via {@link onImage}. */
export function attachImageCapture(
  editor: MarkdownEditor,
  onImage: (image: CapturedImage) => void,
): void {
  const dom = editor.contentDOM;

  dom.addEventListener("paste", (event) => {
    const items = event.clipboardData?.items;
    if (!items) {
      return;
    }
    // DataTransferItemList is array-like but not iterable in Chromium/WebView2 — index it.
    const files: File[] = [];
    for (let i = 0; i < items.length; i++) {
      const item = items[i];
      if (item && item.kind === "file" && item.type.startsWith("image/")) {
        const file = item.getAsFile();
        if (file) {
          files.push(file);
        }
      }
    }
    if (files.length === 0) {
      return;
    }
    event.preventDefault();
    void emit(files, editor.selectionHead(), onImage);
  });

  dom.addEventListener("drop", (event) => {
    const dropped = event.dataTransfer?.files;
    if (!dropped || dropped.length === 0) {
      return;
    }
    const files = Array.from(dropped).filter((file) => file.type.startsWith("image/"));
    if (files.length === 0) {
      return;
    }
    event.preventDefault();
    const pos = editor.posAtCoords(event.clientX, event.clientY) ?? editor.selectionHead();
    void emit(files, pos, onImage);
  });
}
