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
  openExternal: "action.openExternal",
  compare: "action.compare",
  // native → webview
  docLoaded: "doc.loaded",
  previewHtml: "preview.html",
  imageInserted: "image.inserted",
  branchNameSuggested: "branch.name.suggested",
  versionNoteSuggested: "version.note.suggested",
  status: "status",
  error: "error",
  diffResult: "diff.result",
} as const;

/** A changed child (table row / list item) of a changed container (native→webview, inside a
 *  {@link DiffEntryPayload}'s `children`). Ordinals match the container's rendered children. */
export interface ChildDiffPayload {
  /** "added" | "removed" | "changed" | "moved". */
  kind: string;
  /** 0-based HEAD child ordinal (added/changed/moved); -1 for "removed". */
  childIndex: number;
  /** For "removed": the head child it sat before (the marker anchors there); -1 otherwise. */
  anchorIndex: number;
  /** For "removed": the deleted child's flattened text; "" otherwise. */
  removedText: string;
}

/** A changed top-level block in a rendered diff (native→webview). Unchanged blocks are omitted. */
export interface DiffEntryPayload {
  /** "added" | "removed" | "changed" | "moved". */
  kind: string;
  /** 0-based inclusive HEAD source-line range of the (after) block; unused for "removed". */
  lineStart: number;
  lineEnd: number;
  /** For "removed": the head line the block sat before (the overlay places a marker there); -1 otherwise. */
  anchorLine: number;
  /** For "removed": the deleted block's base source text (for the marker); "" otherwise. */
  removedText: string;
  /** Non-empty only for a changed list/table whose individual rows/items changed — then the UI
   *  highlights those children rather than washing the whole container. */
  children: ChildDiffPayload[];
  /** The base rendered text of a changed plain block (paragraph/heading), for the webview's inline
   *  word-diff; "" otherwise. */
  baseText: string;
}

/** Payload of `diff.result` (native→webview): the changed blocks of the working copy vs the last
 *  committed version, in document order. The version rides on the envelope (drop a stale result). */
export interface DiffResultPayload {
  entries: DiffEntryPayload[];
}

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

/** Payload of `action.openExternal` (webview→native): a URL to open in the OS — an http/https page in
 *  the browser, or a mailto: address in the mail client. The host re-validates the scheme; only
 *  absolute http/https/mailto URLs are honoured (and a mailto: query is stripped). */
export interface OpenExternalPayload {
  url: string;
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
