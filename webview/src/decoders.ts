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
  type DiffResultPayload,
  type DocLoadedPayload,
  type ErrorPayload,
  type ImageInsertedPayload,
  type LineSpan,
  type PreviewPayload,
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

function parseChildDiff(value: unknown): ChildDiffPayload | null {
  if (
    !isRecord(value) ||
    !isString(value.kind) ||
    !isNumber(value.childIndex) ||
    !isNumber(value.anchorIndex) ||
    !isString(value.removedText) ||
    !isString(value.baseText)
  ) {
    return null;
  }
  return {
    kind: value.kind,
    childIndex: value.childIndex,
    anchorIndex: value.anchorIndex,
    removedText: value.removedText,
    baseText: value.baseText,
  };
}

function parseDiffEntry(value: unknown): DiffEntryPayload | null {
  if (
    !isRecord(value) ||
    !isString(value.kind) ||
    !isNumber(value.lineStart) ||
    !isNumber(value.lineEnd) ||
    !isNumber(value.anchorLine) ||
    !isString(value.removedText) ||
    !isString(value.baseText) ||
    !isString(value.baseSource)
  ) {
    return null;
  }
  const children = parseArray(value.children, parseChildDiff);
  if (children === null) {
    return null;
  }
  return {
    kind: value.kind,
    lineStart: value.lineStart,
    lineEnd: value.lineEnd,
    anchorLine: value.anchorLine,
    removedText: value.removedText,
    children,
    baseText: value.baseText,
    baseSource: value.baseSource,
  };
}

export function parseDiffResult(value: unknown): DiffResultPayload | null {
  if (!isRecord(value)) {
    return null;
  }
  const entries = parseArray(value.entries, parseDiffEntry);
  if (entries === null) {
    return null;
  }
  return { entries };
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
