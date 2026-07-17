/**
 * Runtime decoders for the native→webview JSON boundary (docs/design/09-ipc-protocol.md). Every native
 * message arrives as `unknown`; these narrow it to a validated domain payload or return `null` (a
 * malformed frame or a native/webview contract drift). Business logic never sees an unchecked cast — a
 * mismatch fails locally and silently here instead of surfacing as an undefined-field crash deep in the
 * editors. The C# host (SpecDesk.Contracts) is the only writer, so a `null` means the contract moved.
 */

import {
  type BranchNameSuggestedPayload,
  type ChatAttachment,
  type ChatDeltaPayload,
  type ChatDonePayload,
  type ChildDiffPayload,
  type ConfirmAppliedPayload,
  type ConfirmRequestPayload,
  type DiffEntryPayload,
  type DiffOverflowPayload,
  type DiffResultPayload,
  type DocCreateCompletedPayload,
  type DocDiscardCompletedPayload,
  type DocLoadedPayload,
  type DocOpenCompletedPayload,
  type DocumentActivityPayload,
  type DocumentChange,
  type DocumentComment,
  type DocumentVersion,
  type ErrorPayload,
  type FileDeleteCompletedPayload,
  type GitHubAccountPayload,
  type GitHubCodePayload,
  type GitHubRepositoriesPayload,
  type GitHubRepositoryOptionPayload,
  type ImageInsertedPayload,
  isReviewState,
  type LineSpan,
  type PrCommentPayload,
  type PrCommitPayload,
  type PrDetailsPayload,
  type PreferencesPayload,
  type PreviewPayload,
  type PrListItemPayload,
  type PrListPayload,
  type PrMutationCompletedPayload,
  type PromptTemplate,
  type PrParticipantPayload,
  type PrSuggestedPayload,
  type RegisteredRepo,
  type RepoCloneConflictPayload,
  type RepoCloneDestinationPayload,
  type RepoConfirmationPayload,
  type RepoDescriptionPayload,
  type RepoOperationCompletedPayload,
  type RepositoryStatusPayload,
  type ReviewCommentAnchorPayload,
  type ReviewCommentPublishedPayload,
  type ReviewCommentSyncPayload,
  type ReviewConflictPayload,
  type SearchResultPayload,
  type SearchResultsPayload,
  STATUS_STATES,
  type StatusPayload,
  type StatusState,
  type TemplatesPayload,
  type TreeNode,
  type TreePayload,
  type VersionNoteSuggestedPayload,
  type WindowCloseCompletedPayload,
  type WindowCloseRequestedPayload,
  type WindowStatePayload,
  type WorkspaceContextPayload,
  type WorkspaceItem,
  type WorkspaceStatePayload,
} from "./protocol.js";

/** `value` is a non-null object whose fields can be read as `unknown` (the JSON-object boundary). */
export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

export function isString(value: unknown): value is string {
  return typeof value === "string";
}

export function isNumber(value: unknown): value is number {
  return typeof value === "number";
}

export function isBoolean(value: unknown): value is boolean {
  return typeof value === "boolean";
}

export function parseWindowState(value: unknown): WindowStatePayload | null {
  return isRecord(value) && isBoolean(value.maximized) ? { maximized: value.maximized } : null;
}

const PREFERENCES_VIEW_MODES = ["code", "split", "formatted"] as const;

function isPreferencesViewMode(value: unknown): value is PreferencesPayload["viewMode"] {
  return isString(value) && (PREFERENCES_VIEW_MODES as readonly string[]).includes(value);
}

export function parsePreferencesState(value: unknown): PreferencesPayload | null {
  if (!isRecord(value) || !isBoolean(value.wrap) || !isPreferencesViewMode(value.viewMode)) {
    return null;
  }
  if (value.theme !== undefined && value.theme !== "light" && value.theme !== "dark") {
    return null;
  }
  // `theme` is optional (exactOptionalPropertyTypes forbids an explicit `undefined`), so omit it.
  return value.theme === undefined
    ? { wrap: value.wrap, viewMode: value.viewMode }
    : { theme: value.theme, wrap: value.wrap, viewMode: value.viewMode };
}

export function parseWindowCloseRequested(value: unknown): WindowCloseRequestedPayload | null {
  return isRecord(value) && isPositiveRequestId(value.requestId)
    ? { requestId: value.requestId }
    : null;
}

export function parseWindowCloseCompleted(value: unknown): WindowCloseCompletedPayload | null {
  return isRecord(value) && isPositiveRequestId(value.requestId) && isBoolean(value.succeeded)
    ? { requestId: value.requestId, succeeded: value.succeeded }
    : null;
}

function isPositiveRequestId(value: unknown): value is number {
  return isNumber(value) && Number.isSafeInteger(value) && value > 0;
}

/** Validate every item of an array through `parseItem`; null if `value` isn't an array or an item fails. */
function parseArray<T>(value: unknown, parseItem: (item: unknown) => T | null): T[] | null {
  if (!Array.isArray(value)) {
    return null;
  }
  const result: T[] = [];
  for (const item of value) {
    const parsed = parseItem(item);
    if (parsed === null) {
      return null;
    }
    result.push(parsed);
  }
  return result;
}

export function parseDocLoaded(value: unknown): DocLoadedPayload | null {
  if (
    !isRecord(value) ||
    !isString(value.path) ||
    !isString(value.text) ||
    !isString(value.docDir) ||
    !isBoolean(value.readOnly)
  ) {
    return null;
  }
  if (
    (value.repository !== undefined && !isString(value.repository)) ||
    (value.branch !== undefined && !isString(value.branch)) ||
    (value.repositoryPath !== undefined && !isString(value.repositoryPath))
  ) {
    return null;
  }
  return {
    path: value.path,
    text: value.text,
    docDir: value.docDir,
    readOnly: value.readOnly,
    ...(isString(value.repository) ? { repository: value.repository } : {}),
    ...(isString(value.branch) ? { branch: value.branch } : {}),
    ...(isString(value.repositoryPath) ? { repositoryPath: value.repositoryPath } : {}),
  };
}

export function parseDocDiscardCompleted(value: unknown): DocDiscardCompletedPayload | null {
  if (
    !isRecord(value) ||
    !isNumber(value.requestId) ||
    !Number.isSafeInteger(value.requestId) ||
    value.requestId <= 0 ||
    !isBoolean(value.succeeded)
  ) {
    return null;
  }
  return { requestId: value.requestId, succeeded: value.succeeded };
}
export function parseDocOpenCompleted(value: unknown): DocOpenCompletedPayload | null {
  if (
    !isRecord(value) ||
    !isNumber(value.requestId) ||
    !Number.isSafeInteger(value.requestId) ||
    value.requestId <= 0 ||
    !isBoolean(value.succeeded)
  ) {
    return null;
  }
  return { requestId: value.requestId, succeeded: value.succeeded };
}

export function parseDocCreateCompleted(value: unknown): DocCreateCompletedPayload | null {
  if (
    !isRecord(value) ||
    !isNumber(value.requestId) ||
    !Number.isSafeInteger(value.requestId) ||
    value.requestId <= 0 ||
    !isBoolean(value.succeeded) ||
    (value.path !== undefined && !isString(value.path)) ||
    (value.error !== undefined && !isString(value.error)) ||
    // Exactly one of path/error accompanies the outcome: a success carries the created path and no error,
    // a failure carries the reason and no path. A shape that contradicts `succeeded` is contract drift.
    (value.succeeded && (!isString(value.path) || value.error !== undefined)) ||
    (!value.succeeded && (value.path !== undefined || !isString(value.error)))
  ) {
    return null;
  }
  return value.succeeded
    ? { requestId: value.requestId, succeeded: true, path: value.path as string }
    : { requestId: value.requestId, succeeded: false, error: value.error as string };
}

function parseLineSpan(value: unknown): LineSpan | null {
  if (!isRecord(value) || !isNumber(value.lineStart) || !isNumber(value.lineEnd)) {
    return null;
  }
  return { lineStart: value.lineStart, lineEnd: value.lineEnd };
}

export function parsePreview(value: unknown): PreviewPayload | null {
  if (!isRecord(value) || !isString(value.html)) {
    return null;
  }
  const lineMap = parseArray(value.lineMap, parseLineSpan);
  if (lineMap === null) {
    return null;
  }
  return { html: value.html, lineMap };
}

// The wire is discriminated by `kind` (the C# host serializes only each case's own fields, no sentinels),
// so each parser reads ONLY the fields its kind carries and narrows straight to the union case. An
// unknown/absent kind, or a field of the wrong type, falls through to null (a contract drift).
function parseChildDiff(value: unknown): ChildDiffPayload | null {
  if (!isRecord(value)) {
    return null;
  }
  if ((value.kind === "added" || value.kind === "moved") && isNumber(value.childIndex)) {
    return { kind: value.kind, childIndex: value.childIndex };
  }
  if (
    value.kind === "changed" &&
    isNumber(value.childIndex) &&
    isString(value.baseText) &&
    isString(value.baseSource)
  ) {
    return {
      kind: "changed",
      childIndex: value.childIndex,
      baseText: value.baseText,
      baseSource: value.baseSource,
    };
  }
  if (value.kind === "removed" && isNumber(value.anchorIndex) && isString(value.removedText)) {
    return { kind: "removed", anchorIndex: value.anchorIndex, removedText: value.removedText };
  }
  return null;
}

function parseDiffEntry(value: unknown): DiffEntryPayload | null {
  if (!isRecord(value)) {
    return null;
  }
  if (
    (value.kind === "added" || value.kind === "moved") &&
    isNumber(value.lineStart) &&
    isNumber(value.lineEnd)
  ) {
    return { kind: value.kind, lineStart: value.lineStart, lineEnd: value.lineEnd };
  }
  if (value.kind === "removed" && isNumber(value.anchorLine) && isString(value.removedText)) {
    return { kind: "removed", anchorLine: value.anchorLine, removedText: value.removedText };
  }
  if (
    value.kind === "changed" &&
    isNumber(value.lineStart) &&
    isNumber(value.lineEnd) &&
    isString(value.baseText) &&
    isString(value.baseSource)
  ) {
    const children = parseArray(value.children, parseChildDiff);
    if (children === null) {
      return null;
    }
    return {
      kind: "changed",
      lineStart: value.lineStart,
      lineEnd: value.lineEnd,
      children,
      baseText: value.baseText,
      baseSource: value.baseSource,
    };
  }
  return null;
}

// The overflow signal replaces `entries` when the native side's node-pair guard fired; validated the
// same way as every other wire shape here — a wrong-typed field falls through to null (a contract drift).
function parseDiffOverflow(value: unknown): DiffOverflowPayload | null {
  if (!isRecord(value) || !isNumber(value.removedCount) || !isNumber(value.addedCount)) {
    return null;
  }
  return { removedCount: value.removedCount, addedCount: value.addedCount };
}

export function parseDiffResult(value: unknown): DiffResultPayload | null {
  if (!isRecord(value)) {
    return null;
  }
  const entries = parseArray(value.entries, parseDiffEntry);
  if (entries === null) {
    return null;
  }
  if (value.overflow === undefined) {
    return { entries };
  }
  const overflow = parseDiffOverflow(value.overflow);
  if (overflow === null) {
    return null;
  }
  return { entries, overflow };
}

function isStatusState(value: unknown): value is StatusState {
  return isString(value) && STATUS_STATES.some((state) => state === value);
}

export function parseStatus(value: unknown): StatusPayload | null {
  if (!isRecord(value) || !isStatusState(value.state) || !isString(value.label)) {
    return null;
  }
  if (value.branch !== undefined && !isString(value.branch)) {
    return null;
  }
  // `branch` is optional (exactOptionalPropertyTypes forbids an explicit `undefined`), so omit it.
  return value.branch === undefined
    ? { state: value.state, label: value.label }
    : { state: value.state, label: value.label, branch: value.branch };
}

export function parseError(value: unknown): ErrorPayload | null {
  if (!isRecord(value) || !isString(value.message)) {
    return null;
  }
  return { message: value.message };
}

export function parseGitHubCode(value: unknown): GitHubCodePayload | null {
  if (!isRecord(value) || !isString(value.userCode) || !isString(value.verificationUri)) {
    return null;
  }
  return { userCode: value.userCode, verificationUri: value.verificationUri };
}

export function parseGitHubAccount(value: unknown): GitHubAccountPayload | null {
  if (!isRecord(value) || !isBoolean(value.available) || !isBoolean(value.signedIn)) {
    return null;
  }
  if (value.login !== undefined && !isString(value.login)) {
    return null;
  }
  if (value.message !== undefined && !isString(value.message)) {
    return null;
  }
  if (
    value.organizations !== undefined &&
    (!Array.isArray(value.organizations) || !value.organizations.every(isString))
  ) {
    return null;
  }
  if (value.avatarUrl !== undefined && !isString(value.avatarUrl)) {
    return null;
  }
  if (value.publicationId !== undefined && !isString(value.publicationId)) {
    return null;
  }
  // login / message are optional (exactOptionalPropertyTypes forbids an explicit undefined), so add them
  // only when present.
  const payload: GitHubAccountPayload = { available: value.available, signedIn: value.signedIn };
  if (value.login !== undefined) {
    payload.login = value.login;
  }
  if (value.message !== undefined) {
    payload.message = value.message;
  }
  if (value.organizations !== undefined) {
    payload.organizations = value.organizations;
  }
  if (value.avatarUrl !== undefined) {
    payload.avatarUrl = value.avatarUrl;
  }
  if (value.publicationId !== undefined) {
    payload.publicationId = value.publicationId;
  }
  return payload;
}

function parseGitHubRepositoryOption(value: unknown): GitHubRepositoryOptionPayload | null {
  if (!isRecord(value) || !isString(value.fullName)) {
    return null;
  }
  if (value.description !== undefined && !isString(value.description)) {
    return null;
  }
  return value.description === undefined
    ? { fullName: value.fullName }
    : { fullName: value.fullName, description: value.description };
}

export function parseGitHubRepositories(value: unknown): GitHubRepositoriesPayload | null {
  if (!isRecord(value)) {
    return null;
  }
  const repositories = parseArray(value.repositories, parseGitHubRepositoryOption);
  return repositories === null ? null : { repositories };
}

export function parseRepoCloneDestination(value: unknown): RepoCloneDestinationPayload | null {
  if (
    !isRecord(value) ||
    !isString(value.url) ||
    !isNumber(value.requestId) ||
    !isString(value.localName) ||
    !isBoolean(value.exists)
  ) {
    return null;
  }
  if (
    (value.path !== undefined && !isString(value.path)) ||
    (value.existingClonePath !== undefined && !isString(value.existingClonePath)) ||
    (value.exists && !isString(value.existingClonePath)) ||
    (!value.exists && value.existingClonePath !== undefined)
  ) {
    return null;
  }
  return {
    url: value.url,
    requestId: value.requestId,
    localName: value.localName,
    exists: value.exists,
    ...(isString(value.path) ? { path: value.path } : {}),
    ...(isString(value.existingClonePath) ? { existingClonePath: value.existingClonePath } : {}),
  };
}

export function parseRepoCloneConflict(value: unknown): RepoCloneConflictPayload | null {
  if (
    !isRecord(value) ||
    !isString(value.url) ||
    !isString(value.localName) ||
    !isString(value.existingClonePath) ||
    !isString(value.message)
  ) {
    return null;
  }
  return {
    url: value.url,
    localName: value.localName,
    existingClonePath: value.existingClonePath,
    message: value.message,
  };
}

export function parseRepoConfirmation(value: unknown): RepoConfirmationPayload | null {
  if (
    !isRecord(value) ||
    (value.operation !== "deleteClone" && value.operation !== "deleteBranch") ||
    !isString(value.id) ||
    !isString(value.clonePath) ||
    !isString(value.message) ||
    !isString(value.confirmationToken) ||
    value.confirmationToken === ""
  ) {
    return null;
  }
  const warnings = parseArray(value.warnings, (warning) => (isString(warning) ? warning : null));
  const branch = value.branch === undefined ? null : value.branch;
  if (
    warnings === null ||
    warnings.length === 0 ||
    (branch !== null && !isString(branch)) ||
    (value.operation === "deleteBranch" && !isString(branch)) ||
    (value.operation === "deleteClone" && branch !== null)
  ) {
    return null;
  }
  return {
    operation: value.operation,
    id: value.id,
    clonePath: value.clonePath,
    branch,
    message: value.message,
    warnings,
    confirmationToken: value.confirmationToken,
  };
}

export function parseRepoOperationCompleted(value: unknown): RepoOperationCompletedPayload | null {
  if (
    !isRecord(value) ||
    !isNumber(value.requestId) ||
    !Number.isSafeInteger(value.requestId) ||
    value.requestId <= 0
  ) {
    return null;
  }
  return { requestId: value.requestId };
}

export function parseFileDeleteCompleted(value: unknown): FileDeleteCompletedPayload | null {
  if (
    !isRecord(value) ||
    !isString(value.path) ||
    value.path === "" ||
    !isString(value.root) ||
    value.root === "" ||
    !isNumber(value.requestId) ||
    !Number.isSafeInteger(value.requestId) ||
    value.requestId <= 0 ||
    !isBoolean(value.succeeded) ||
    (value.error !== undefined && !isString(value.error)) ||
    (value.succeeded && value.error !== undefined) ||
    (!value.succeeded && value.error === undefined)
  ) {
    return null;
  }
  return value.error === undefined
    ? { path: value.path, root: value.root, requestId: value.requestId, succeeded: value.succeeded }
    : {
        path: value.path,
        root: value.root,
        requestId: value.requestId,
        succeeded: value.succeeded,
        error: value.error,
      };
}

export function parseRepoDescription(value: unknown): RepoDescriptionPayload | null {
  if (
    !isRecord(value) ||
    !isString(value.url) ||
    !isNumber(value.requestId) ||
    !isString(value.state) ||
    !["found", "private", "notFound", "error"].includes(value.state)
  ) {
    return null;
  }
  if (value.description !== undefined && !isString(value.description)) {
    return null;
  }
  const state = value.state as RepoDescriptionPayload["state"];
  return value.description === undefined
    ? { url: value.url, requestId: value.requestId, state }
    : { url: value.url, requestId: value.requestId, state, description: value.description };
}

export function parseImageInserted(value: unknown): ImageInsertedPayload | null {
  if (!isRecord(value) || !isString(value.markdown)) {
    return null;
  }
  return { markdown: value.markdown };
}

export function parseBranchNameSuggested(value: unknown): BranchNameSuggestedPayload | null {
  if (!isRecord(value) || !isString(value.name)) {
    return null;
  }
  return { name: value.name };
}

export function parseVersionNoteSuggested(value: unknown): VersionNoteSuggestedPayload | null {
  if (!isRecord(value) || !isString(value.note)) {
    return null;
  }
  return { note: value.note };
}

export function parsePrSuggested(value: unknown): PrSuggestedPayload | null {
  if (!isRecord(value) || !isString(value.title) || !isString(value.body)) {
    return null;
  }
  if (value.blocked !== undefined && !isString(value.blocked)) {
    return null;
  }
  // `blocked` is optional (exactOptionalPropertyTypes forbids an explicit undefined), so add it only when
  // present.
  return value.blocked === undefined
    ? { title: value.title, body: value.body }
    : { title: value.title, body: value.body, blocked: value.blocked };
}

function parsePrListItem(value: unknown): PrListItemPayload | null {
  if (
    !isRecord(value) ||
    !isNumber(value.number) ||
    !isString(value.title) ||
    !isString(value.url) ||
    !isString(value.repo) ||
    (value.role !== "author" && value.role !== "reviewer") ||
    // A review item's status is only ever a review-open state — reject published/draft at the boundary.
    !isReviewState(value.status) ||
    !isString(value.label)
  ) {
    return null;
  }
  return {
    number: value.number,
    title: value.title,
    url: value.url,
    repo: value.repo,
    role: value.role,
    status: value.status,
    label: value.label,
  };
}

export function parsePrList(value: unknown): PrListPayload | null {
  if (!isRecord(value) || !Array.isArray(value.items)) {
    return null;
  }
  // `error` is absent (or, defensively, JSON null) when the list loaded; a non-string, non-null value is a
  // malformed frame.
  if (value.error !== undefined && value.error !== null && !isString(value.error)) {
    return null;
  }
  // Skip an individual item that fails validation rather than discarding the whole list — one malformed
  // row shouldn't turn the author's reviews into a generic failure. (parseArray is all-or-nothing.)
  const items: PrListItemPayload[] = [];
  for (const raw of value.items) {
    const item = parsePrListItem(raw);
    if (item !== null) {
      items.push(item);
    }
  }
  // `error` is optional (exactOptionalPropertyTypes forbids an explicit undefined), so add it only when it's
  // a real (non-null) string.
  return isString(value.error) ? { items, error: value.error } : { items };
}

function parsePrParticipant(value: unknown): PrParticipantPayload | null {
  if (
    !isRecord(value) ||
    !isString(value.login) ||
    !isString(value.avatarUrl) ||
    (value.kind !== "user" && value.kind !== "team")
  ) {
    return null;
  }
  return { login: value.login, avatarUrl: value.avatarUrl, kind: value.kind };
}

function parsePrComment(value: unknown): PrCommentPayload | null {
  if (
    !isRecord(value) ||
    !isNumber(value.id) ||
    (value.kind !== "conversation" && value.kind !== "review") ||
    !isString(value.path) ||
    !isString(value.author) ||
    !isString(value.avatarUrl) ||
    !isString(value.body) ||
    !isString(value.createdAt) ||
    !isString(value.updatedAt) ||
    typeof value.viewerDidAuthor !== "boolean"
  ) {
    return null;
  }
  return {
    id: value.id,
    kind: value.kind,
    path: value.path,
    author: value.author,
    avatarUrl: value.avatarUrl,
    body: value.body,
    createdAt: value.createdAt,
    updatedAt: value.updatedAt,
    viewerDidAuthor: value.viewerDidAuthor,
  };
}

function parsePrCommit(value: unknown): PrCommitPayload | null {
  if (
    !isRecord(value) ||
    !isString(value.oid) ||
    !isString(value.shortOid) ||
    !isString(value.title) ||
    !isString(value.when) ||
    !isString(value.checkState)
  ) {
    return null;
  }
  return {
    oid: value.oid,
    shortOid: value.shortOid,
    title: value.title,
    when: value.when,
    checkState: value.checkState,
  };
}

export function parsePrDetails(value: unknown): PrDetailsPayload | null {
  if (
    !isRecord(value) ||
    !isNumber(value.number) ||
    !isString(value.repo) ||
    !isString(value.title) ||
    !isString(value.body) ||
    !isString(value.url) ||
    !isString(value.state) ||
    typeof value.isDraft !== "boolean" ||
    !isString(value.author) ||
    !isString(value.authorAvatarUrl) ||
    !isString(value.baseBranch) ||
    !isString(value.headBranch) ||
    !Array.isArray(value.reviewers) ||
    !Array.isArray(value.comments) ||
    !Array.isArray(value.commits) ||
    typeof value.commentsIncomplete !== "boolean" ||
    typeof value.commitsIncomplete !== "boolean" ||
    (value.error !== undefined && value.error !== null && !isString(value.error))
  ) {
    return null;
  }
  const reviewers = value.reviewers.map(parsePrParticipant);
  const comments = value.comments.map(parsePrComment);
  const commits = value.commits.map(parsePrCommit);
  if (reviewers.includes(null) || comments.includes(null) || commits.includes(null)) {
    return null;
  }
  const result: PrDetailsPayload = {
    number: value.number,
    repo: value.repo,
    title: value.title,
    body: value.body,
    url: value.url,
    state: value.state,
    isDraft: value.isDraft,
    author: value.author,
    authorAvatarUrl: value.authorAvatarUrl,
    baseBranch: value.baseBranch,
    headBranch: value.headBranch,
    reviewers: reviewers as PrParticipantPayload[],
    comments: comments as PrCommentPayload[],
    commits: commits as PrCommitPayload[],
    commentsIncomplete: value.commentsIncomplete,
    commitsIncomplete: value.commitsIncomplete,
  };
  if (isString(value.error)) {
    result.error = value.error;
  }
  return result;
}

export function parsePrMutationCompleted(value: unknown): PrMutationCompletedPayload | null {
  if (
    !isRecord(value) ||
    typeof value.succeeded !== "boolean" ||
    (value.error !== undefined && value.error !== null && !isString(value.error))
  ) {
    return null;
  }
  return isString(value.error)
    ? { succeeded: value.succeeded, error: value.error }
    : { succeeded: value.succeeded };
}

function parseReviewCommentAnchor(value: unknown): ReviewCommentAnchorPayload | null {
  if (
    !isRecord(value) ||
    !isNumber(value.id) ||
    !isNumber(value.line) ||
    !isString(value.side) ||
    !isString(value.commitId) ||
    !isNumber(value.inReplyToId) ||
    !isString(value.author) ||
    !isString(value.body) ||
    !isString(value.when)
  ) {
    return null;
  }
  return {
    id: value.id,
    line: value.line,
    side: value.side,
    commitId: value.commitId,
    inReplyToId: value.inReplyToId,
    author: value.author,
    body: value.body,
    when: value.when,
  };
}

export function parseReviewCommentSync(value: unknown): ReviewCommentSyncPayload | null {
  if (
    !isRecord(value) ||
    !isString(value.documentKey) ||
    !isNumber(value.number) ||
    !isString(value.headCommitId) ||
    !isString(value.path) ||
    (value.error !== undefined && value.error !== null && !isString(value.error))
  ) {
    return null;
  }
  const commentableLines = parseArray(value.commentableLines, (line) =>
    isNumber(line) && Number.isInteger(line) ? line : null,
  );
  const comments = parseArray(value.comments, parseReviewCommentAnchor);
  if (commentableLines === null || comments === null) {
    return null;
  }
  const payload: ReviewCommentSyncPayload = {
    documentKey: value.documentKey,
    number: value.number,
    headCommitId: value.headCommitId,
    path: value.path,
    commentableLines,
    comments,
  };
  if (isString(value.error)) {
    payload.error = value.error;
  }
  return payload;
}

export function parseReviewCommentPublished(value: unknown): ReviewCommentPublishedPayload | null {
  if (
    !isRecord(value) ||
    !isString(value.localId) ||
    !isNumber(value.githubId) ||
    typeof value.succeeded !== "boolean" ||
    (value.error !== undefined && value.error !== null && !isString(value.error))
  ) {
    return null;
  }
  return isString(value.error)
    ? {
        localId: value.localId,
        githubId: value.githubId,
        succeeded: value.succeeded,
        error: value.error,
      }
    : { localId: value.localId, githubId: value.githubId, succeeded: value.succeeded };
}

export function parseReviewConflict(value: unknown): ReviewConflictPayload | null {
  if (!isRecord(value) || !isString(value.document) || value.document === "") {
    return null;
  }
  return { document: value.document };
}

export function parseChatDelta(value: unknown): ChatDeltaPayload | null {
  if (!isRecord(value) || !isString(value.id) || !isString(value.text)) {
    return null;
  }
  return { id: value.id, text: value.text };
}

export function parseChatDone(value: unknown): ChatDonePayload | null {
  if (!isRecord(value) || !isString(value.id)) {
    return null;
  }
  return { id: value.id };
}

export function parseConfirmRequest(value: unknown): ConfirmRequestPayload | null {
  if (
    !isRecord(value) ||
    !isString(value.id) ||
    !isString(value.currentText) ||
    !isString(value.proposedText)
  ) {
    return null;
  }
  if (value.summary !== undefined && !isString(value.summary)) {
    return null;
  }
  return {
    id: value.id,
    currentText: value.currentText,
    proposedText: value.proposedText,
    ...(value.summary === undefined ? {} : { summary: value.summary }),
  };
}

export function parseConfirmApplied(value: unknown): ConfirmAppliedPayload | null {
  if (!isRecord(value) || !isString(value.id) || !isString(value.text)) {
    return null;
  }
  return { id: value.id, text: value.text };
}

export function parseChatAttachment(value: unknown): ChatAttachment | null {
  if (
    !isRecord(value) ||
    (value.kind !== "file" && value.kind !== "folder" && value.kind !== "repository") ||
    !isString(value.label) ||
    !isString(value.reference)
  ) {
    return null;
  }
  return { kind: value.kind, label: value.label, reference: value.reference };
}

function parseDocumentVersion(value: unknown): DocumentVersion | null {
  if (
    !isRecord(value) ||
    !isString(value.id) ||
    !isString(value.note) ||
    !isString(value.author) ||
    !isString(value.when)
  )
    return null;
  return { id: value.id, note: value.note, author: value.author, when: value.when };
}

function parseDocumentComment(value: unknown): DocumentComment | null {
  if (
    !isRecord(value) ||
    !isString(value.id) ||
    !isString(value.author) ||
    !isString(value.body) ||
    !isString(value.when)
  )
    return null;
  return {
    id: value.id,
    author: value.author,
    body: value.body,
    when: value.when,
  };
}

function parseDocumentChange(value: unknown): DocumentChange | null {
  if (
    !isRecord(value) ||
    !isString(value.id) ||
    !isString(value.label) ||
    !isString(value.note) ||
    !isString(value.author) ||
    !isString(value.when)
  )
    return null;
  return {
    id: value.id,
    label: value.label,
    note: value.note,
    author: value.author,
    when: value.when,
  };
}

export function parseDocumentActivity(value: unknown): DocumentActivityPayload | null {
  if (
    !isRecord(value) ||
    (value.document !== undefined && !isString(value.document)) ||
    (value.historyState !== "loaded" &&
      value.historyState !== "notVersioned" &&
      value.historyState !== "unavailable") ||
    (value.historyMessage !== undefined && !isString(value.historyMessage)) ||
    (value.commentsState !== "loaded" &&
      value.commentsState !== "notConnected" &&
      value.commentsState !== "unavailable") ||
    (value.commentsMessage !== undefined && !isString(value.commentsMessage))
  )
    return null;
  const versions = parseArray(value.versions, parseDocumentVersion);
  const comments = parseArray(value.comments, parseDocumentComment);
  const history = parseArray(value.history, parseDocumentChange);
  if (versions === null || comments === null || history === null) return null;
  return {
    ...(value.document === undefined ? {} : { document: value.document }),
    versions,
    historyState: value.historyState,
    ...(value.historyMessage === undefined ? {} : { historyMessage: value.historyMessage }),
    comments,
    commentsState: value.commentsState,
    ...(value.commentsMessage === undefined ? {} : { commentsMessage: value.commentsMessage }),
    history,
  };
}

function parsePromptTemplate(value: unknown): PromptTemplate | null {
  if (!isRecord(value) || !isString(value.id) || !isString(value.title) || !isString(value.body)) {
    return null;
  }
  return { id: value.id, title: value.title, body: value.body };
}

export function parseTemplates(value: unknown): TemplatesPayload | null {
  if (!isRecord(value)) {
    return null;
  }
  const personal = parseArray(value.personal, parsePromptTemplate);
  const remote = parseArray(value.remote, parsePromptTemplate);
  if (personal === null || remote === null) {
    return null;
  }
  return { personal, remote };
}

function parseTreeNode(value: unknown): TreeNode | null {
  if (
    !isRecord(value) ||
    !isString(value.name) ||
    !isString(value.path) ||
    !isBoolean(value.isDirectory)
  ) {
    return null;
  }
  // A well-formed node always carries `children` (an empty array for a file); a missing/bad array is drift.
  const children = parseArray(value.children, parseTreeNode);
  if (children === null) {
    return null;
  }
  if (!isBoolean(value.hasChildren)) {
    return null;
  }
  return {
    name: value.name,
    path: value.path,
    isDirectory: value.isDirectory,
    children,
    hasChildren: value.hasChildren,
  };
}

export function parseTree(value: unknown): TreePayload | null {
  if (
    !isRecord(value) ||
    !isString(value.root) ||
    !isNumber(value.requestId) ||
    !(value.error === undefined || isString(value.error)) ||
    !(value.remote === undefined || isBoolean(value.remote))
  ) {
    return null;
  }
  const nodes = parseArray(value.nodes, parseTreeNode);
  if (nodes === null) {
    return null;
  }
  const payload: TreePayload = { root: value.root, nodes, requestId: value.requestId };
  if (value.error !== undefined) payload.error = value.error;
  if (value.remote !== undefined) payload.remote = value.remote;
  return payload;
}

function parseSearchResult(value: unknown): SearchResultPayload | null {
  if (
    !isRecord(value) ||
    !isString(value.path) ||
    !isNumber(value.line) ||
    !isString(value.snippet)
  ) {
    return null;
  }
  return { path: value.path, line: value.line, snippet: value.snippet };
}

export function parseSearchResults(value: unknown): SearchResultsPayload | null {
  if (!isRecord(value) || !isString(value.query) || !isBoolean(value.truncated)) {
    return null;
  }
  const results = parseArray(value.results, parseSearchResult);
  if (results === null) {
    return null;
  }
  return { query: value.query, results, truncated: value.truncated };
}

export function parseWorkspaceContext(value: unknown): WorkspaceContextPayload | null {
  if (
    !isRecord(value) ||
    !(value.repository === undefined || value.repository === null || isString(value.repository)) ||
    !(
      value.repositoryRoot === undefined ||
      value.repositoryRoot === null ||
      isString(value.repositoryRoot)
    ) ||
    !(value.branch === undefined || value.branch === null || isString(value.branch)) ||
    !(value.localCopy === undefined || value.localCopy === null || isString(value.localCopy)) ||
    !(
      value.branchState === "named" ||
      value.branchState === "detached" ||
      value.branchState === "unavailable"
    ) ||
    !(
      value.defaultBranch === undefined ||
      value.defaultBranch === null ||
      isString(value.defaultBranch)
    ) ||
    !(value.canPublish === undefined || isBoolean(value.canPublish)) ||
    !isString(value.path)
  ) {
    return null;
  }

  // System.Text.Json's native wire convention omits nullable payload properties. Normalize those
  // missing keys before validating the context invariants so every consumer receives one stable shape.
  const repository = value.repository ?? null;
  const repositoryRoot = value.repositoryRoot ?? null;
  const branch = value.branch ?? null;
  const defaultBranch = value.defaultBranch ?? null;
  const localCopy = value.localCopy ?? null;
  if (
    (value.branchState === "named" && !isString(branch)) ||
    (value.branchState !== "named" && branch !== null) ||
    (localCopy !== null && repositoryRoot === null) ||
    (repository === null &&
      (repositoryRoot !== null ||
        branch !== null ||
        value.branchState !== "unavailable" ||
        defaultBranch !== null))
  ) {
    return null;
  }
  return {
    repository,
    repositoryRoot,
    branch,
    branchState: value.branchState,
    defaultBranch,
    path: value.path,
    localCopy,
    // The native side always writes canPublish (a bool is never omitted); default a missing key to false
    // so a hand-built or legacy frame safely hides the author-publish action rather than revealing it.
    canPublish: value.canPublish === true,
  };
}

function parseWorkspaceItem(value: unknown): WorkspaceItem | null {
  if (
    !isRecord(value) ||
    !isString(value.path) ||
    !isString(value.label) ||
    !isBoolean(value.isFolder)
  ) {
    return null;
  }
  const kind = value.kind === undefined ? "local" : value.kind;
  if (
    (kind !== "local" &&
      kind !== "remote" &&
      kind !== "repository" &&
      kind !== "clone" &&
      kind !== "branch") ||
    (value.repositoryId !== undefined && !isString(value.repositoryId)) ||
    (value.branch !== undefined && !isString(value.branch))
  ) {
    return null;
  }
  if (
    (kind === "local" &&
      (value.repositoryId !== undefined ||
        value.branch !== undefined ||
        !isAbsoluteLocalPath(value.path))) ||
    (kind === "remote" &&
      (!isRepositoryId(value.repositoryId) ||
        !isRemoteBranch(value.branch) ||
        !isRemoteRelativePath(value.path))) ||
    (kind === "repository" &&
      (!value.isFolder ||
        value.branch !== undefined ||
        !isRepositoryId(value.repositoryId) ||
        value.path.toLowerCase() !== value.repositoryId.toLowerCase())) ||
    (kind === "clone" &&
      (!value.isFolder ||
        value.branch !== undefined ||
        !isRepositoryId(value.repositoryId) ||
        !isAbsoluteLocalPath(value.path))) ||
    (kind === "branch" &&
      (!value.isFolder ||
        !isRepositoryId(value.repositoryId) ||
        !isRemoteBranch(value.branch) ||
        !isAbsoluteLocalPath(value.path)))
  ) {
    return null;
  }
  return {
    path: value.path,
    label: value.label,
    isFolder: value.isFolder,
    kind,
    ...(isString(value.repositoryId) ? { repositoryId: value.repositoryId } : {}),
    ...(isString(value.branch) ? { branch: value.branch } : {}),
  };
}

function isRepositoryId(value: unknown): value is string {
  if (!isString(value) || value.trim() === "" || value.length > 256) {
    return false;
  }
  const segments = value.split("/");
  return (
    segments.length === 2 && isGitHubOwner(segments[0] ?? "") && isGitHubRepoName(segments[1] ?? "")
  );
}

function isGitHubOwner(owner: string): boolean {
  return (
    owner.length > 0 &&
    !owner.startsWith("-") &&
    !owner.endsWith("-") &&
    /^[A-Za-z0-9-]+$/.test(owner)
  );
}

function isGitHubRepoName(name: string): boolean {
  return name !== "" && name !== "." && name !== ".." && /^[A-Za-z0-9._-]+$/.test(name);
}

function isAbsoluteLocalPath(path: string): boolean {
  return /^[A-Za-z]:[\\/]/.test(path) || path.startsWith("\\\\");
}

function isRemoteBranch(value: unknown): value is string {
  return (
    isString(value) && value.trim() !== "" && value.length <= 1024 && !hasControlCharacter(value)
  );
}

function isRemoteRelativePath(value: string): boolean {
  if (value.length > 4096 || hasControlCharacter(value)) return false;
  const segments = value.split("/");
  return (
    segments.length <= 64 &&
    segments.every((segment) => segment !== "" && segment !== "." && segment !== "..")
  );
}

function hasControlCharacter(value: string): boolean {
  return Array.from(value).some((character) => {
    const code = character.charCodeAt(0);
    return code < 32 || code === 127;
  });
}

function parseRegisteredRepo(value: unknown): RegisteredRepo | null {
  if (
    !isRecord(value) ||
    !isString(value.id) ||
    !isString(value.name) ||
    !isString(value.url) ||
    !isString(value.defaultBranch)
  ) {
    return null;
  }
  const clones = parseArray(value.clones, (clone) => {
    const currentBranch = isRecord(clone) ? (clone.currentBranch ?? null) : null;
    if (
      !isRecord(clone) ||
      !isString(clone.id) ||
      !isString(clone.path) ||
      (currentBranch !== null && !isRemoteBranch(currentBranch))
    ) {
      return null;
    }
    const status = parseRepositoryStatus(clone.status);
    const branches = parseArray(clone.branches, (branch) => {
      if (
        !isRecord(branch) ||
        !isRemoteBranch(branch.name) ||
        !isBoolean(branch.canDelete) ||
        !isBoolean(branch.canRename)
      ) {
        return null;
      }
      const branchStatus = parseRepositoryStatus(branch.status);
      return branchStatus === null
        ? null
        : {
            name: branch.name,
            status: branchStatus,
            canDelete: branch.canDelete,
            canRename: branch.canRename,
          };
    });
    return branches === null || status === null
      ? null
      : {
          id: clone.id,
          path: clone.path,
          currentBranch,
          branches,
          status,
        };
  });
  return clones === null
    ? null
    : {
        id: value.id,
        name: value.name,
        url: value.url,
        defaultBranch: value.defaultBranch,
        clones,
      };
}

function parseRepositoryStatus(value: unknown): RepositoryStatusPayload | null {
  if (
    !isRecord(value) ||
    !isNumber(value.ahead) ||
    !Number.isInteger(value.ahead) ||
    value.ahead < 0 ||
    !isNumber(value.behind) ||
    !Number.isInteger(value.behind) ||
    value.behind < 0 ||
    !isBoolean(value.hasUncommitted) ||
    !isNumber(value.stashCount) ||
    !Number.isInteger(value.stashCount) ||
    value.stashCount < 0 ||
    !isBoolean(value.hasConflicts)
  ) {
    return null;
  }
  return {
    ahead: value.ahead,
    behind: value.behind,
    hasUncommitted: value.hasUncommitted,
    stashCount: value.stashCount,
    hasConflicts: value.hasConflicts,
  };
}

export function parseWorkspaceState(value: unknown): WorkspaceStatePayload | null {
  if (!isRecord(value)) {
    return null;
  }
  // A single bad item nulls the whole payload (parseArray is all-or-nothing) — a drifted store shape must
  // fail here, not surface as a half-decoded workspace deep in the UI.
  const recent = parseArray(value.recent, parseWorkspaceItem);
  const favorites = parseArray(value.favorites, parseWorkspaceItem);
  const repositories = parseArray(value.repositories, parseRegisteredRepo);
  if (recent === null || favorites === null || repositories === null) {
    return null;
  }
  return { recent, favorites, repositories };
}
