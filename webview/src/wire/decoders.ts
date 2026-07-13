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
  type DiffEntryPayload,
  type DiffOverflowPayload,
  type DiffResultPayload,
  type DocLoadedPayload,
  type DocumentActivityPayload,
  type DocumentChange,
  type DocumentComment,
  type DocumentVersion,
  type ErrorPayload,
  type GitHubAccountPayload,
  type GitHubCodePayload,
  type GitHubRepositoriesPayload,
  type GitHubRepositoryOptionPayload,
  type ImageInsertedPayload,
  isReviewState,
  type LineSpan,
  type PreviewPayload,
  type PrListItemPayload,
  type PrListPayload,
  type PromptTemplate,
  type PrSuggestedPayload,
  type RegisteredRepo,
  STATUS_STATES,
  type StatusPayload,
  type StatusState,
  type TemplatesPayload,
  type TreeNode,
  type TreePayload,
  type VersionNoteSuggestedPayload,
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

export function parseChatDelta(value: unknown): ChatDeltaPayload | null {
  if (!isRecord(value) || !isString(value.text)) {
    return null;
  }
  return { text: value.text };
}

export function parseChatDone(value: unknown): ChatDonePayload | null {
  if (!isRecord(value) || !isString(value.id)) {
    return null;
  }
  return { id: value.id };
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
  return { name: value.name, path: value.path, isDirectory: value.isDirectory, children };
}

export function parseTree(value: unknown): TreePayload | null {
  if (!isRecord(value) || !isString(value.root)) {
    return null;
  }
  const nodes = parseArray(value.nodes, parseTreeNode);
  if (nodes === null) {
    return null;
  }
  return { root: value.root, nodes };
}

export function parseWorkspaceContext(value: unknown): WorkspaceContextPayload | null {
  if (
    !isRecord(value) ||
    !(value.repository === null || isString(value.repository)) ||
    !(value.repositoryRoot === null || isString(value.repositoryRoot)) ||
    !(value.branch === null || isString(value.branch)) ||
    !(
      value.branchState === "named" ||
      value.branchState === "detached" ||
      value.branchState === "unavailable"
    ) ||
    !(value.defaultBranch === null || isString(value.defaultBranch)) ||
    !isString(value.path)
  ) {
    return null;
  }
  if (
    (value.branchState === "named" && !isString(value.branch)) ||
    (value.branchState !== "named" && value.branch !== null) ||
    (value.repository === null &&
      (value.repositoryRoot !== null ||
        value.branch !== null ||
        value.branchState !== "unavailable" ||
        value.defaultBranch !== null))
  ) {
    return null;
  }
  return {
    repository: value.repository,
    repositoryRoot: value.repositoryRoot,
    branch: value.branch,
    branchState: value.branchState,
    defaultBranch: value.defaultBranch,
    path: value.path,
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
    (kind !== "local" && kind !== "remote" && kind !== "repository") ||
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
        value.path.toLowerCase() !== value.repositoryId.toLowerCase()))
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
    if (!isRecord(clone) || !isString(clone.id) || !isString(clone.path)) {
      return null;
    }
    const branches = parseArray(clone.branches, (branch) => (isString(branch) ? branch : null));
    return branches === null ? null : { id: clone.id, path: clone.path, branches };
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
