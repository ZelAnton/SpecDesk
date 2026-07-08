/**
 * Runtime decoders for the native→webview JSON boundary (docs/design/09-ipc-protocol.md). Every native
 * message arrives as `unknown`; these narrow it to a validated domain payload or return `null` (a
 * malformed frame or a native/webview contract drift). Business logic never sees an unchecked cast — a
 * mismatch fails locally and silently here instead of surfacing as an undefined-field crash deep in the
 * editors. The C# host (SpecDesk.Contracts) is the only writer, so a `null` means the contract moved.
 */

import {
  type BranchNameSuggestedPayload,
  type ChildDiffPayload,
  type DiffEntryPayload,
  type DiffOverflowPayload,
  type DiffResultPayload,
  type DocLoadedPayload,
  type ErrorPayload,
  type GitHubAccountPayload,
  type GitHubCodePayload,
  type ImageInsertedPayload,
  isReviewState,
  type LineSpan,
  type PreviewPayload,
  type PrListItemPayload,
  type PrListPayload,
  type PrSuggestedPayload,
  STATUS_STATES,
  type StatusPayload,
  type StatusState,
  type VersionNoteSuggestedPayload,
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
    !isString(value.docDir)
  ) {
    return null;
  }
  return { path: value.path, text: value.text, docDir: value.docDir };
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
  // login / message are optional (exactOptionalPropertyTypes forbids an explicit undefined), so add them
  // only when present.
  const payload: GitHubAccountPayload = { available: value.available, signedIn: value.signedIn };
  if (value.login !== undefined) {
    payload.login = value.login;
  }
  if (value.message !== undefined) {
    payload.message = value.message;
  }
  return payload;
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
