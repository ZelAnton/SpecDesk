/**
 * Wire kinds and payload shapes for the PoC-2 editor/preview flow. These mirror the C# contracts
 * in SpecDesk.Contracts (MessageKinds + Payloads) — keep the two in sync. See
 * docs/design/09-ipc-protocol.md.
 */

export const Kinds = {
  // webview → native
  ready: "ready",
  editorChanged: "editor.changed",
  actionOpen: "action.open",
  actionSave: "action.save",
  imagePaste: "image.paste",
  log: "log",
  exportLog: "action.exportLog",
  // native → webview
  docLoaded: "doc.loaded",
  previewHtml: "preview.html",
  imageInserted: "image.inserted",
  error: "error",
} as const;

/** One rendered top-level block's 0-based, inclusive source line range. */
export interface LineSpan {
  lineStart: number;
  lineEnd: number;
}

/** Payload of `preview.html` (native→webview); the version rides on the envelope. */
export interface PreviewPayload {
  html: string;
  lineMap: LineSpan[];
}

/** Payload of `doc.loaded` (native→webview). */
export interface DocLoadedPayload {
  path: string;
  text: string;
}

/** Payload of `error` (native→webview). */
export interface ErrorPayload {
  message: string;
}

/** Payload of `image.paste` (webview→native): one captured image as base64. */
export interface ImagePastePayload {
  base64: string;
  originalName: string;
  mime: string;
}

/** Payload of `image.inserted` (native→webview): the Markdown link to insert (empty on failure). */
export interface ImageInsertedPayload {
  markdown: string;
}
