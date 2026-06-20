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
  actionEdit: "action.edit",
  actionSaveVersion: "action.saveVersion",
  actionDiscard: "action.discard",
  branchNameRequest: "branch.name.request",
  versionNoteRequest: "version.note.request",
  imagePaste: "image.paste",
  log: "log",
  exportLog: "action.exportLog",
  // native → webview
  docLoaded: "doc.loaded",
  previewHtml: "preview.html",
  imageInserted: "image.inserted",
  branchNameSuggested: "branch.name.suggested",
  versionNoteSuggested: "version.note.suggested",
  status: "status",
  error: "error",
} as const;

/** Document lifecycle state names (mirror of F# Lifecycle.stateName). */
export type StatusState = "published" | "draft" | "inReview" | "changesRequested" | "approved";

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
  /** Document directory relative to the repo root (forward slashes, "" at root) — for resolving
   *  relative image links to `app://repo/…` in the formatted view (mirrors the native preview). */
  docDir: string;
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

/** Payload of `action.edit` (webview→native): the author's chosen draft (branch) name (empty → generated). */
export interface EditPayload {
  branchName: string;
}

/** Payload of `branch.name.suggested` (native→webview): generated, editable draft name for the Edit prompt. */
export interface BranchNameSuggestedPayload {
  name: string;
}

/** Payload of `action.saveVersion` (webview→native): the author's version note (commit message). */
export interface SaveVersionPayload {
  note: string;
}

/** Payload of `version.note.suggested` (native→webview): generated, editable note to prefill the prompt. */
export interface VersionNoteSuggestedPayload {
  note: string;
}

/** Payload of `status` (native→webview): the lifecycle state surfaced to the author. */
export interface StatusPayload {
  state: StatusState;
  /** Author-facing text to display (including transient "Saving…" / "Saved just now"). */
  label: string;
  /** Working branch name — diagnostic only, never shown. */
  branch?: string;
}
