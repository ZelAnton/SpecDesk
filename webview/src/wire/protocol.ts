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
  docPublish: "doc.publish",
  reviewRefresh: "review.refresh",
  docDiscard: "doc.discard",
  branchNameRequest: "branch.name.request",
  versionNoteRequest: "version.note.request",
  prSuggestedRequest: "pr.suggested.request",
  prListRequest: "pr.list.request",
  prDetailsRequest: "pr.details.request",
  prReviewersRequest: "pr.reviewers.request",
  prCommentCreate: "pr.comment.create",
  prCommentReply: "pr.comment.reply",
  prCommentUpdate: "pr.comment.update",
  reviewCommentSyncRequest: "review.commentSync.request",
  reviewCommentPublish: "review.comment.publish",
  imagePaste: "image.paste",
  log: "log",
  logExport: "log.export",
  traceDump: "trace.dump",
  linkOpen: "link.open",
  diffRequest: "diff.request",
  githubSignIn: "github.signIn",
  githubSignInCancel: "github.signInCancel",
  githubSignOut: "github.signOut",
  githubAccountRefresh: "github.account.refresh",
  githubAccountApplied: "github.accountApplied",
  chatSend: "chat.send",
  chatAttachmentPick: "chat.attachment.pick",
  confirmResult: "confirm.result",
  documentActivityRequest: "document.activity.request",
  templatesRequest: "templates.request",
  folderOpen: "folder.open",
  treeRequest: "tree.request",
  fileDelete: "file.delete",
  workspaceRequest: "workspace.request",
  workspaceFavorite: "workspace.favorite",
  repoRegister: "repo.register",
  repoUnregister: "repo.unregister",
  repoOpen: "repo.open",
  repoClone: "repo.clone",
  repoCloneManaged: "repo.cloneManaged",
  repoCloneToFolder: "repo.cloneToFolder",
  repoCloneDestinationRequest: "repo.cloneDestination.request",
  repoDescriptionRequest: "repo.description.request",
  repoBrowse: "repo.browse",
  repoSwitchBranch: "repo.switchBranch",
  repoCreateBranch: "repo.createBranch",
  repoRenameClone: "repo.renameClone",
  repoRenameBranch: "repo.renameBranch",
  repoDeleteClone: "repo.deleteClone",
  repoDeleteBranch: "repo.deleteBranch",
  repoRefreshAll: "repo.refreshAll",
  repoAutoSync: "repo.autoSync",
  repoPull: "repo.pull",
  repoPush: "repo.push",
  windowMinimize: "window.minimize",
  windowToggleMaximize: "window.toggleMaximize",
  windowClose: "window.close",
  windowDrag: "window.drag",
  // native → webview
  docLoaded: "doc.loaded",
  docOpenCompleted: "doc.openCompleted",
  docDiscardCompleted: "doc.discardCompleted",
  previewHtml: "preview.html",
  imageInserted: "image.inserted",
  branchNameSuggested: "branch.name.suggested",
  versionNoteSuggested: "version.note.suggested",
  prSuggested: "pr.suggested",
  prList: "pr.list",
  prDetails: "pr.details",
  prMutationCompleted: "pr.mutationCompleted",
  reviewCommentSync: "review.commentSync",
  reviewCommentPublished: "review.comment.published",
  status: "status",
  error: "error",
  diffResult: "diff.result",
  githubCode: "github.code",
  githubAccount: "github.account",
  githubRepositories: "github.repositories",
  chatDelta: "chat.delta",
  chatDone: "chat.done",
  chatAttachmentPicked: "chat.attachment.picked",
  confirmRequest: "confirm.request",
  confirmApplied: "confirm.applied",
  documentActivity: "document.activity",
  templates: "templates",
  tree: "tree",
  fileDeleteCompleted: "file.deleteCompleted",
  workspaceState: "workspace.state",
  repoCloneDestination: "repo.cloneDestination",
  repoCloneConflict: "repo.cloneConflict",
  repoConfirmation: "repo.confirmation",
  repoOperationCompleted: "repo.operationCompleted",
  repoDescription: "repo.description",
  workspaceContext: "workspace.context",
  windowState: "window.state",
  windowCloseRequested: "window.closeRequested",
  windowCloseCompleted: "window.closeCompleted",
} as const;

/** Native maximize state for the in-content title-bar button. */
export interface WindowStatePayload {
  maximized: boolean;
}

export interface WindowCloseRequestedPayload {
  requestId: number;
}

export interface WindowCloseCompletedPayload {
  requestId: number;
  succeeded: boolean;
}

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

/** A changed child (table row / list item) of a changed container (native→webview, inside a changed
 *  {@link DiffEntryPayload}'s `children`). Discriminated by `kind` (mirror of the C# `ChildDiffPayload`
 *  hierarchy): each case carries ONLY its own fields — a removed child anchors and carries its text, a
 *  changed child carries a base, added/moved just a head ordinal — so "a removed child with a head
 *  ordinal" or "a changed child with no base" is unrepresentable rather than sentinel-encoded. Ordinals
 *  match the container's rendered children. */
export type ChildDiffPayload =
  | {
      kind: "added";
      /** 0-based HEAD child ordinal the new row/item occupies. */
      childIndex: number;
    }
  | {
      kind: "moved";
      /** 0-based HEAD child ordinal the reordered row/item now occupies. */
      childIndex: number;
    }
  | {
      kind: "changed";
      /** 0-based HEAD child ordinal of the changed row/item. */
      childIndex: number;
      /** The base child's flattened text, for the Formatted pane's inline word-diff inside the row/item. */
      baseText: string;
      /** The base child's raw source, for the Code pane's inline word-diff inside the row/item. */
      baseSource: string;
    }
  | {
      kind: "removed";
      /** The head child the deleted row/item sat before (the marker anchors there). */
      anchorIndex: number;
      /** The deleted child's flattened text (for the marker). */
      removedText: string;
    };

/** A changed top-level block in a rendered diff (native→webview). Unchanged blocks are omitted.
 *  Discriminated by `kind` (mirror of the C# `DiffEntryPayload` hierarchy): each case carries ONLY its
 *  own fields — added/changed/moved carry the HEAD line range, removed carries the anchor + base text,
 *  changed additionally carries the per-child diff and inline-word-diff bases — so "a removed block with
 *  a line range" or "a changed block with no base" is unrepresentable rather than sentinel-encoded. */
export type DiffEntryPayload =
  | {
      kind: "added";
      /** 0-based inclusive HEAD source-line range. */
      lineStart: number;
      lineEnd: number;
    }
  | {
      kind: "moved";
      /** 0-based inclusive HEAD source-line range (reordered head position). */
      lineStart: number;
      lineEnd: number;
    }
  | {
      kind: "removed";
      /** The head line the deleted block sat before (the overlay places a marker there). */
      anchorLine: number;
      /** The deleted block's base source text (for the marker). */
      removedText: string;
    }
  | {
      kind: "changed";
      /** 0-based inclusive HEAD source-line range of the (after) block. */
      lineStart: number;
      lineEnd: number;
      /** Non-empty only for a changed list/table whose individual rows/items changed — then the UI
       *  highlights those children rather than washing the whole container. */
      children: ChildDiffPayload[];
      /** The base rendered text of a changed plain block (paragraph/heading), for the Formatted pane's
       *  inline word-diff; "" for a container that descended to children. */
      baseText: string;
      /** The base raw source of a changed plain block, for the Code pane's inline word-diff; "" otherwise. */
      baseSource: string;
    };

/** Compact overflow signal on {@link DiffResultPayload}: the (base, head) pair overflowed the native
 *  AstDiff node-pair size guard and fell back to a flat, coarse Removed+Added listing — sent as this
 *  count-only signal INSTEAD of enumerating every base/head block (which would ship every removed block's
 *  full text over IPC and paint thousands of decorations). `entries` is empty whenever this is present.
 *  Mirror of the C# `DiffOverflowPayload`. */
export interface DiffOverflowPayload {
  removedCount: number;
  addedCount: number;
}

/** Payload of `diff.result` (native→webview): the changed blocks of the working copy vs the last
 *  committed version, in document order. The version rides on the envelope (drop a stale result).
 *  `overflow` is present only for a pair too large to diff in detail (see {@link DiffOverflowPayload}),
 *  in which case `entries` is empty. */
export interface DiffResultPayload {
  entries: DiffEntryPayload[];
  overflow?: DiffOverflowPayload;
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
  readOnly: boolean;
  repository?: string;
  branch?: string;
  repositoryPath?: string;
}

/** Payload of `error` (native→webview). */
export interface ErrorPayload {
  message: string;
}

/** Terminal result for one correlated `doc.open` transition. */
export interface DocOpenCompletedPayload {
  requestId: number;
  succeeded: boolean;
}

/** Terminal result for one correlated `doc.discard` transition. */
export interface DocDiscardCompletedPayload {
  requestId: number;
  succeeded: boolean;
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

/** One entry of a `trace.dump` (webview→native): a flattened {@link TraceEntry} where `data` is
 *  PRE-STRINGIFIED (JSON, capped at 500 chars) rather than an object, so the whole dump is a flat
 *  wire shape. Mirror of the C# `TraceEntryPayload`. */
export interface TraceDumpEntry {
  seq: number;
  t: number;
  cat: string;
  event: string;
  data?: string;
}

/** Payload of `trace.dump` (webview→native): a snapshot of the in-page trace ring, sent when the
 *  author exports the log so the host can persist it beside the Serilog file and append its tail to
 *  the export. `t0Epoch` (`Date.now() - performance.now()` at ring init) lets the host reconstruct
 *  each entry's wall-clock time as `t0Epoch + t`. Mirror of the C# `TraceDumpPayload`. */
export interface TraceDumpPayload {
  t0Epoch: number;
  firstSeq: number;
  entries: TraceDumpEntry[];
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

export interface PrParticipantPayload {
  login: string;
  avatarUrl: string;
  kind: "user" | "team";
}

export interface PrCommentPayload {
  id: number;
  kind: "conversation" | "review";
  path: string;
  author: string;
  avatarUrl: string;
  body: string;
  createdAt: string;
  updatedAt: string;
  viewerDidAuthor: boolean;
}

export interface PrCommitPayload {
  oid: string;
  shortOid: string;
  title: string;
  when: string;
  checkState: string;
}

export interface PrDetailsPayload {
  number: number;
  repo: string;
  title: string;
  body: string;
  url: string;
  state: string;
  isDraft: boolean;
  author: string;
  authorAvatarUrl: string;
  baseBranch: string;
  headBranch: string;
  reviewers: PrParticipantPayload[];
  comments: PrCommentPayload[];
  commits: PrCommitPayload[];
  commentsIncomplete: boolean;
  commitsIncomplete: boolean;
  error?: string;
}

export interface PrMutationCompletedPayload {
  succeeded: boolean;
  error?: string;
}

/** Payload of `review.commentSync.request` (webview→native): ask the host to project the open PR's inline
 * review comments onto the open document. `documentKey` is the webview's opaque per-document key, echoed
 * back so a response that raced a navigation can be dropped. */
export interface ReviewCommentSyncRequestPayload {
  documentKey: string;
}

/** One inline PR review comment projected back to the webview (native→webview, inside
 * {@link ReviewCommentSyncPayload}; mirror of the C# `ReviewCommentAnchorPayload`). `line` is the 1-based
 * file line; `side` is `RIGHT` (head) or `LEFT` (base); `inReplyToId` is 0 for a root thread. */
export interface ReviewCommentAnchorPayload {
  id: number;
  line: number;
  side: string;
  commitId: string;
  inReplyToId: number;
  author: string;
  body: string;
  when: string;
}

/** Payload of `review.commentSync` (native→webview, correlated to `review.commentSync.request` by id): the
 * bounded projection for the open document. `number` is 0 when the branch has no open pull request (then no
 * thread can be posted); `commentableLines` are the 1-based head-side lines inside a diff hunk (postable);
 * `error` is a plain reason the sync couldn't run (the webview keeps its last projection). */
export interface ReviewCommentSyncPayload {
  documentKey: string;
  number: number;
  headCommitId: string;
  path: string;
  commentableLines: number[];
  comments: ReviewCommentAnchorPayload[];
  error?: string;
}

/** Payload of `review.comment.publish` (webview→native): post one local inline thread to the open PR.
 * `number`/`commitId` come from the last sync; `line`/`side` are the head-side anchor; `localId` is the
 * local thread id, echoed back so the returned GitHub id lands on the right thread. */
export interface ReviewCommentPublishPayload {
  documentKey: string;
  number: number;
  commitId: string;
  line: number;
  side: string;
  body: string;
  localId: string;
}

/** Payload of `review.comment.published` (native→webview, correlated by id): the outcome of a
 * `review.comment.publish`. `githubId` is the created comment's id on success; `error` is a plain reason
 * when it was rejected (e.g. the line fell outside the diff after the head moved). */
export interface ReviewCommentPublishedPayload {
  localId: string;
  githubId: number;
  succeeded: boolean;
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
  /** An author-facing line for a transient account-state failure (e.g. "Sign-in code expired"). */
  message?: string;
  /** Organizations visible to this authorization, after the host finishes loading them. */
  organizations?: string[];
  /** GitHub's HTTPS profile image URL, after account details load. */
  avatarUrl?: string;
  /** Opaque correlation acknowledged after this account boundary has been applied. */
  publicationId?: string;
}

export interface GitHubRepositoryOptionPayload {
  fullName: string;
  description?: string;
}

export interface GitHubRepositoriesPayload {
  repositories: GitHubRepositoryOptionPayload[];
}

/** Payload of `chat.send` (webview→native): the author's message to the AI assistant. */
export interface ChatSendPayload {
  id: string;
  text: string;
  attachments?: ChatAttachment[];
}

export interface ChatAttachment {
  kind: "file" | "folder" | "repository";
  label: string;
  reference: string;
}

export interface DocumentVersion {
  id: string;
  note: string;
  author: string;
  when: string;
}

export interface DocumentComment {
  id: string;
  author: string;
  body: string;
  when: string;
}

export interface DocumentChange {
  id: string;
  label: string;
  note: string;
  author: string;
  when: string;
}

export interface DocumentActivityPayload {
  document?: string;
  versions: DocumentVersion[];
  historyState: "loaded" | "notVersioned" | "unavailable";
  historyMessage?: string;
  comments: DocumentComment[];
  commentsState: "loaded" | "notConnected" | "unavailable";
  commentsMessage?: string;
  history: DocumentChange[];
}

/** Payload of `chat.delta` (native→webview): one streamed chunk of the assistant's reply, appended to
 *  the in-progress assistant message until `chat.done`. */
export interface ChatDeltaPayload {
  id: string;
  text: string;
}

/** Payload of `chat.done` (native→webview): the assistant turn identified by `id` finished streaming. */
export interface ChatDonePayload {
  id: string;
}

/** Payload of `confirm.request` (native→webview): the assistant's gated `proposeEdit` staged a full-document
 *  replacement and the host asks the author to confirm it, exactly like a manual edit (design §08-ai-agent).
 *  `id` correlates the round-trip to its {@link ConfirmResultPayload} reply; `currentText`/`proposedText` are
 *  the before/after the confirmation surface renders the difference from (reusing the existing word-diff — no
 *  new diff algorithm); `summary` is an optional short plain-language description. Nothing is applied until the
 *  author confirms. */
export interface ConfirmRequestPayload {
  id: string;
  currentText: string;
  proposedText: string;
  summary?: string;
}

/** The author's decision on a staged `proposeEdit` proposal (webview→native, {@link ConfirmResultPayload}). */
export type ConfirmDecision = "accepted" | "rejected";

/** Payload of `confirm.result` (webview→native): the author's decision on a staged `proposeEdit` proposal.
 *  `id` echoes the {@link ConfirmRequestPayload}; `decision` is accept/reject; `text` carries the
 *  author-confirmed (possibly edited) text on accept, and is absent on reject — a rejected or edited-then-
 *  rejected proposal leaves no trace in the document. */
export interface ConfirmResultPayload {
  id: string;
  decision: ConfirmDecision;
  text?: string;
}

/** Payload of `confirm.applied` (native→webview): the host verified the confirmed proposal was still current
 *  (same document identity and content generation as when it was staged) and applied it through the ordinary
 *  editing path. `id` correlates it to the request; `text` is the exact text the host applied, for the editor
 *  to reflect. Sent only on a successful apply — a proposal whose document changed underneath it comes back as
 *  a plain `error` instead. */
export interface ConfirmAppliedPayload {
  id: string;
  text: string;
}

/** One prompt-library entry (native→webview, inside {@link TemplatesPayload}). `body` is inserted into
 *  the chat composer when the author picks it; `title` is the picker label. */
export interface PromptTemplate {
  id: string;
  title: string;
  body: string;
}

/** Payload of `templates` (native→webview, correlated to `templates.request` by id): the prompt library
 *  the composer's picker inserts from — the author's `personal` (local) set and a `remote` set fetched
 *  from a configured URL (empty when none is configured or the fetch failed). */
export interface TemplatesPayload {
  personal: PromptTemplate[];
  remote: PromptTemplate[];
}

/** Payload of `doc.open` (webview→native): open a specific file (`path`), or `null`/absent to fall back to
 *  the native open dialog. */
export interface DocOpenPayload {
  path?: string;
  requestId: number;
}

/** Payload of `doc.discard` (webview→native): correlate the locked editor transition with its completion. */
export interface DocDiscardPayload {
  requestId: number;
}

/** Payload of `folder.open` (webview→native): open a folder as the file navigator's root (`path`), or
 *  `null`/absent to fall back to the native folder picker. */
export interface FolderOpenPayload {
  path?: string;
}

/** Payload of `tree.request` (webview→native): request one file-tree level, optionally scoped to `path`
 *  (absent → the current workspace folder, else the open document's folder). */
export interface TreeRequestPayload {
  path?: string;
  requestId: number;
}

/** Delete one file from the currently visible Disk root. `root` is a stale-view guard; native state is
 * authoritative and the host rejects paths outside it. */
export interface FileDeletePayload {
  path: string;
  root: string;
  requestId: number;
}

export interface FileDeleteCompletedPayload extends FileDeletePayload {
  succeeded: boolean;
  error?: string;
}

/** One node of the file tree (native→webview, inside {@link TreePayload}). A directory has
 *  `isDirectory: true` and its `children`; a file has an empty `children`. `path` is the absolute path (a
 *  file node opens via `doc.open` on click); `name` is the display label. */
export interface TreeNode {
  name: string;
  path: string;
  isDirectory: boolean;
  children: TreeNode[];
  /** True when this directory has descendants that are not included in this one-level response. */
  hasChildren: boolean;
}

/** Payload of `tree` (native→webview): one local or remote folder level and its direct entries. */
export interface TreePayload {
  root: string;
  nodes: TreeNode[];
  /** Correlates a requested root/directory level; zero denotes an unsolicited root publication. */
  requestId: number;
  /** A requested level failed and remains retryable; absent on successful publications. */
  error?: string;
  /** Explicit origin for empty/error GitHub levels that have no node path from which to infer it. */
  remote?: boolean;
}

/** Authoritative repository/version/path context for the open document. The file-tree root is separate. */
export interface WorkspaceContextPayload {
  repository: string | null;
  repositoryRoot: string | null;
  branch: string | null;
  branchState: "named" | "detached" | "unavailable";
  defaultBranch: string | null;
  path: string;
  /** Display name of the local clone that owns the document; absent for remote-only/outside files. */
  localCopy?: string | null;
  /** Whether this document's repository permits the author to publish their own approved document
   *  (`[review] allow-author-publish`). The lifecycle chrome reveals "Publish" only when true; the host
   *  re-checks it before merging, so this is a UX gate, not the security boundary. Defaults to false. */
  canPublish: boolean;
}

/** One recent/favorite entry (native→webview, inside {@link WorkspaceStatePayload}). Local paths are absolute;
 *  remote paths are repository-relative and paired with `repositoryId` + `branch`; repository items use their
 *  stable id. */
export interface WorkspaceItem {
  path: string;
  label: string;
  isFolder: boolean;
  kind?: "local" | "remote" | "repository" | "clone" | "branch";
  repositoryId?: string;
  branch?: string;
}

/** One registered GitHub repository (native→webview, inside {@link WorkspaceStatePayload}). A4 stores the
 *  entry only — no cloning yet. `id` is a stable key (`owner/name`); `name` is the display (`owner/name`);
 *  `url` is the normalized `https://github.com/owner/name` URL. */
export interface RegisteredRepo {
  id: string;
  name: string;
  url: string;
  defaultBranch: string;
  clones: RegisteredClone[];
}

export interface RegisteredClone {
  id: string;
  path: string;
  currentBranch: string | null;
  branches: RegisteredBranch[];
  status: RepositoryStatusPayload;
}

export interface RegisteredBranch {
  name: string;
  status: RepositoryStatusPayload;
  canDelete: boolean;
  canRename: boolean;
}

export interface RepositoryStatusPayload {
  ahead: number;
  behind: number;
  hasUncommitted: boolean;
  stashCount: number;
  hasConflicts: boolean;
}

/** Payload of `workspace.state` (native→webview): the persisted workspace store — the author's `recent`
 *  items (most-recent first), their `favorites`, and the `repositories` they registered. Emitted on
 *  `workspace.request` and after every mutation (`workspace.favorite` / `repo.register` / `repo.unregister`). */
export interface WorkspaceStatePayload {
  recent: WorkspaceItem[];
  favorites: WorkspaceItem[];
  repositories: RegisteredRepo[];
}

/** Payload of `workspace.favorite` (webview→native): toggle a local/remote file or folder, or a registered
 *  repository (`favorite` true adds it, false removes it). */
export interface WorkspaceFavoritePayload {
  path: string;
  favorite: boolean;
  kind?: "local" | "remote" | "repository" | "clone" | "branch";
  repositoryId?: string;
  branch?: string;
  isFolder?: boolean;
}

/** Payload of `repo.register` (webview→native): register a GitHub repository from a URL or spec
 *  (`https://github.com/owner/name(.git)`, `owner/name`, or `git@github.com:owner/name(.git)`). The host
 *  parses/normalizes it before storing. */
export interface RegisterRepoPayload {
  url: string;
}

/** Payload of `repo.unregister` (webview→native): remove the registered repository whose id matches `id`. */
export interface UnregisterRepoPayload {
  id: string;
}

/** Payload of `repo.open` (webview→native): open a GitHub repository named by `url` (an `owner/name` or a
 *  GitHub URL). The host clones it into a managed local folder (or reuses an existing clone) and opens that
 *  folder as the workspace, sending a `tree`; an unparseable value comes back as an `error`. */
export interface RepoOpenPayload {
  url: string;
  clonePath?: string;
}

export interface RepoSwitchBranchPayload {
  id: string;
  clonePath: string;
  branch: string;
  requestId: number;
}

export interface RepoCreateBranchPayload extends RepoSwitchBranchPayload {}

export interface RepoRenameClonePayload {
  id: string;
  clonePath: string;
  localName: string;
  requestId: number;
}

export interface RepoRenameBranchPayload extends RepoSwitchBranchPayload {
  newBranch: string;
}

export interface RepoPullPayload extends RepoSwitchBranchPayload {}

export interface RepoRefreshAllPayload {
  requestId: number;
}

export interface RepoPushPayload {
  id: string;
  clonePath: string;
  branch: string;
}

export interface RepoDeleteClonePayload {
  id: string;
  clonePath: string;
  confirmationToken?: string;
  requestId: number;
}

export interface RepoDeleteBranchPayload extends RepoDeleteClonePayload {
  branch: string;
}

export interface RepoCloneToFolderPayload {
  url: string;
  localName: string;
}

export interface RepoCloneManagedPayload {
  url: string;
  localName: string;
  destinationPath?: string;
}

export interface RepoCloneDestinationPayload {
  url: string;
  requestId: number;
  localName: string;
  path?: string;
  exists: boolean;
  existingClonePath?: string;
}

export interface RepoCloneConflictPayload {
  url: string;
  localName: string;
  existingClonePath: string;
  message: string;
}

export interface RepoConfirmationPayload {
  operation: "deleteClone" | "deleteBranch";
  id: string;
  clonePath: string;
  branch: string | null;
  message: string;
  warnings: string[];
  confirmationToken: string;
}

export interface RepoOperationCompletedPayload {
  requestId: number;
}

export type RepoDescriptionState = "found" | "private" | "notFound" | "error";

export interface RepoDescriptionPayload {
  url: string;
  requestId: number;
  state: RepoDescriptionState;
  description?: string;
}
