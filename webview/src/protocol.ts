/**
 * Wire kinds and payload shapes for the PoC-2 editor/preview flow. These mirror the C# contracts
 * in SpecDesk.Contracts (MessageKinds + Payloads) — keep the two in sync. See
 * docs/design/09-ipc-protocol.md.
 */

export const Kinds = {
  // webview → native
  ready: "ready",
  editorChanged: "editor.changed",
  docOpen: "doc.open",
  docSave: "doc.save",
  docEdit: "doc.edit",
  docSaveVersion: "doc.saveVersion",
  docSendForReview: "doc.sendForReview",
  docUpdateReview: "doc.updateReview",
  reviewRefresh: "review.refresh",
  docDiscard: "doc.discard",
  branchNameRequest: "branch.name.request",
  versionNoteRequest: "version.note.request",
  prSuggestedRequest: "pr.suggested.request",
  prListRequest: "pr.list.request",
  imagePaste: "image.paste",
  log: "log",
  logExport: "log.export",
  linkOpen: "link.open",
  diffRequest: "diff.request",
  githubSignIn: "github.signIn",
  githubSignInCancel: "github.signInCancel",
  githubSignOut: "github.signOut",
  // native → webview
  docLoaded: "doc.loaded",
  previewHtml: "preview.html",
  imageInserted: "image.inserted",
  branchNameSuggested: "branch.name.suggested",
  versionNoteSuggested: "version.note.suggested",
  prSuggested: "pr.suggested",
  prList: "pr.list",
  status: "status",
  error: "error",
  diffResult: "diff.result",
  githubCode: "github.code",
  githubAccount: "github.account",
} as const;

/** The diff wire `kind` discriminator names — the single runtime source on the webview side; the
 *  {@link DiffKind} type derives from it, so the validated set and the type can't drift apart. Mirror of
 *  F# DiffWire.DiffKind, pinned by the cross-language guard in webview/tests/contract/diff-kinds.json. */
export const DIFF_KINDS = ["added", "changed", "moved", "removed"] as const;

/** How one changed block (or row/item) relates base→head (mirror of F# DiffWire.DiffKind). */
export type DiffKind = (typeof DIFF_KINDS)[number];

/** The `diff.request` `base` names — mirror of C# `DiffBaseKinds`. `"lastVersion"` is the only one wired
 *  today (the local "Show changes": working copy vs the last saved version); `"published"` (vs the
 *  published/main version) and `"pr"` (vs an open pull request's head, with the PR number in {@link
 *  DiffRequestPayload.pr}) are reserved for PoC-7's in-flight-review compares. Kept as a plain union
 *  (not a validated runtime set, unlike {@link DIFF_KINDS}) because the webview only ever *produces*
 *  these values, never decodes them from the wire. */
export type DiffBaseKind = "lastVersion" | "published" | "pr";

/** A changed child (table row / list item) of a changed container (native→webview, inside a
 *  {@link DiffEntryPayload}'s `children`). Ordinals match the container's rendered children. */
export interface ChildDiffPayload {
  kind: DiffKind;
  /** 0-based HEAD child ordinal (added/changed/moved); -1 for "removed". */
  childIndex: number;
  /** For "removed": the head child it sat before (the marker anchors there); -1 otherwise. */
  anchorIndex: number;
  /** For "removed": the deleted child's flattened text; "" otherwise. */
  removedText: string;
  /** For "changed": the base child's flattened text (inline word-diff inside the row/item); "" otherwise. */
  baseText: string;
}

/** A changed top-level block in a rendered diff (native→webview). Unchanged blocks are omitted. */
export interface DiffEntryPayload {
  kind: DiffKind;
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
  /** The base rendered text of a changed plain block (paragraph/heading), for the Formatted pane's
   *  inline word-diff; "" otherwise. */
  baseText: string;
  /** The base raw source of a changed plain block, for the Code pane's inline word-diff; "" otherwise. */
  baseSource: string;
}

/** Payload of `diff.result` (native→webview): the changed blocks of the working copy vs the last
 *  committed version, in document order. The version rides on the envelope (drop a stale result). */
export interface DiffResultPayload {
  entries: DiffEntryPayload[];
}

/** Payload of `diff.request` (webview→native): which base to diff the working copy against. The
 *  overlay (ReviewController) owns this choice — see {@link DiffBaseKind}. `pr` is the pull request
 *  number, present only when `base` is `"pr"`. */
export interface DiffRequestPayload {
  base: DiffBaseKind;
  pr?: number;
}

/** The document lifecycle state names — the single runtime source on the webview side; the
 *  {@link StatusState} type derives from it, so the validated set and the type can't drift apart.
 *  Mirror of F# Lifecycle.State (via stateName), pinned by the cross-language guard in
 *  webview/tests/contract/lifecycle-states.json. */
export const STATUS_STATES = [
  "published",
  "draft",
  "inReview",
  "changesRequested",
  "approved",
] as const;

/** Document lifecycle state name (mirror of F# Lifecycle.stateName). */
export type StatusState = (typeof STATUS_STATES)[number];

/** The states in which a review is open on GitHub — the single runtime source for review-scoped
 *  behaviour (Update-review visibility, status polling, a review item's status). Subset of STATUS_STATES. */
export const REVIEW_STATES = [
  "inReview",
  "changesRequested",
  "approved",
] as const satisfies StatusState[];

/** A review-open state name (a strict subset of {@link StatusState}). */
export type ReviewStatusState = (typeof REVIEW_STATES)[number];

/** Whether a value is a review-open state — the single membership check for review-scoped behaviour
 *  (Update-review visibility, status polling, decoding a review item's status). */
export function isReviewState(state: unknown): state is ReviewStatusState {
  return typeof state === "string" && REVIEW_STATES.some((review) => review === state);
}

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

/** Payload of `link.open` (webview→native): a URL to open in the OS — an http/https page in
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

/** Payload of `doc.edit` (webview→native): the author's chosen draft (branch) name (empty → generated). */
export interface EditPayload {
  branchName: string;
}

/** Payload of `branch.name.suggested` (native→webview): generated, editable draft name for the Edit prompt. */
export interface BranchNameSuggestedPayload {
  name: string;
}

/** Payload of `doc.saveVersion` (webview→native): the author's version note (commit message). */
export interface SaveVersionPayload {
  note: string;
}

/** Payload of `version.note.suggested` (native→webview): generated, editable note to prefill the prompt. */
export interface VersionNoteSuggestedPayload {
  note: string;
}

/** Payload of `doc.sendForReview` (webview→native): the author-confirmed PR title/body (edited from the
 *  suggestion). Either may be blank — the host falls back to a generated title, allows an empty body. */
export interface SendForReviewPayload {
  title: string;
  body: string;
}

/** Payload of `pr.suggested` (native→webview): whether the review can be sent now and, if so, the
 *  generated, editable PR title/body to prefill the "send for review" confirm prompt. `blocked` is a
 *  plain-language reason the send can't proceed (not connected / not a GitHub repo / no saved version) —
 *  when present, the webview shows it and does NOT open the prompt. Absent means ready. */
export interface PrSuggestedPayload {
  title: string;
  body: string;
  blocked?: string;
}

/** One open review in the author's review list (native→webview, inside {@link PrListPayload}). `role` is
 *  `author` (they opened it) or `reviewer` (they were asked to review); `status` is the wire review-state
 *  name (`inReview` / `changesRequested` / `approved`). */
export interface PrListItemPayload {
  number: number;
  title: string;
  url: string;
  /** `owner/name`. */
  repo: string;
  role: "author" | "reviewer";
  /** Wire review-state name, for styling the state pill. */
  status: ReviewStatusState;
  /** Author-facing label for the status — host-authoritative (same source as the status bar). */
  label: string;
}

/** Payload of `pr.list` (native→webview, correlated to `pr.list.request` by id): the open PRs the user is
 *  involved in, most recently updated first. `error` is a plain reason the list couldn't be loaded (not
 *  connected / transport failure) — present means `items` is empty and the panel shows the reason. */
export interface PrListPayload {
  items: PrListItemPayload[];
  error?: string;
}

/** Payload of `status` (native→webview): the lifecycle state surfaced to the author. */
export interface StatusPayload {
  state: StatusState;
  /** Author-facing text to display (including transient "Saving…" / "Saved just now"). */
  label: string;
  /** Working branch name — diagnostic only, never shown. */
  branch?: string;
}

/** Payload of `github.code` (native→webview): the one-time code the author enters at `verificationUri`
 *  to connect their GitHub account. */
export interface GitHubCodePayload {
  userCode: string;
  verificationUri: string;
}

/** Payload of `github.account` (native→webview): the GitHub connection state for the account affordance. */
export interface GitHubAccountPayload {
  /** False when sign-in isn't configured — the UI hides the affordance entirely. */
  available: boolean;
  signedIn: boolean;
  /** The GitHub handle when connected (may be empty if it couldn't be looked up). */
  login?: string;
  /** An author-facing line for a transient/failed sign-in (e.g. "Sign-in code expired"). */
  message?: string;
}
