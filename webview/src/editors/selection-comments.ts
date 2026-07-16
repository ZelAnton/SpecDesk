import { splitTopLevelBlocks } from "./md-blocks.js";

/** A selection expressed in Markdown source lines, shared by Code and WYSIWYG. */
export interface SourceSelection {
  readonly fromLine: number;
  readonly toLine: number;
  /** The line after whose rendered block the comment belongs. */
  readonly anchorLine: number;
  /** Tables must remain whole constructs when their anchor is re-mapped through later edits. */
  readonly anchorKind: "line" | "table";
  readonly fromOffset: number;
  readonly toOffset: number;
  readonly anchorOffset: number;
  readonly quote: string;
}

/** Browser-local annotation, isolated by document and signed-in principal. */
export interface SelectionComment extends SourceSelection {
  readonly id: string;
  readonly body: string;
  readonly createdAt: string;
  readonly updatedAt?: string;
  readonly author: SelectionCommentAuthor;
  readonly replies: readonly SelectionCommentReply[];
  readonly anchorState?: "attached" | "detached";
  /** Original bounded fingerprint retained while a thread is detached so a later edit can reattach it. */
  readonly detachedAnchor?: StoredSelectionAnchor;
}

export interface SelectionCommentAuthor {
  readonly principalId: string;
  readonly displayName: string;
}

export interface SelectionCommentReply {
  readonly id: string;
  readonly body: string;
  readonly createdAt: string;
  readonly updatedAt?: string;
  readonly author: SelectionCommentAuthor;
}

export interface SelectionCommentDraft extends SourceSelection {
  readonly documentKey: string;
  readonly surface: "code" | "formatted";
  readonly mode: "create" | "edit" | "reply";
  readonly commentId?: string;
  readonly replyId?: string;
  initialBody: string;
}

export interface SelectionCommentView {
  readonly comments: readonly SelectionComment[];
  readonly draft: SelectionCommentDraft | null;
  readonly principalId: string;
  /** False until the current account/document snapshot has loaded successfully. */
  readonly commentsAvailable: boolean;
  readonly persistence: "saved" | "saving" | "error";
  readonly persistenceMessage?: string;
}

/** Browser-local persistence is deliberately behind a tiny port so the eventual native/GitHub store can
 * replace it without changing anchor mapping or either editor. */
export interface SelectionCommentStorage {
  load(principalKey: string, documentKey: string): Promise<readonly StoredSelectionComment[]>;
  save(
    principalKey: string,
    documentKey: string,
    comments: readonly StoredSelectionComment[],
  ): Promise<void>;
}

export interface StoredSelectionAnchor {
  readonly fromLine: number;
  readonly toLine: number;
  readonly anchorLine: number;
  readonly anchorKind: "line" | "table";
  readonly fromOffset: number;
  readonly toOffset: number;
  readonly quoteLength: number;
  readonly quoteHash: string;
  readonly quoteHead: string;
  readonly quoteTail: string;
  readonly before: string;
  readonly after: string;
}

export interface StoredSelectionComment {
  readonly anchor: StoredSelectionAnchor;
  readonly id: string;
  readonly body: string;
  readonly createdAt: string;
  readonly updatedAt?: string;
  readonly author: SelectionCommentAuthor;
  readonly replies: readonly SelectionCommentReply[];
}

export interface SelectionCommentDiagnostics {
  readonly onDocumentIndexBuilt?: (markdownLength: number) => void;
  readonly onFingerprintVerified?: (workUnits: number) => void;
}

interface SelectionPersistRequest {
  readonly principalKey: string;
  readonly documentKey: string;
  readonly comments: readonly StoredSelectionComment[];
  readonly revision: number;
  readonly mergeExisting?: boolean;
}

function persistRequestKey(principalKey: string, documentKey: string): string {
  return `${principalKey.length}:${principalKey}${documentKey}`;
}

const STORAGE_PREFIX = "specdesk.selection-comments.v4";
const CONTEXT_LIMIT = 96;
const QUOTE_PART_LIMIT = 160;
const RESTORE_CANDIDATE_LIMIT = 5_000;
const EDITED_RANGE_SPAN_LIMIT = 2_000_000;
const FINGERPRINT_BASE_LEFT = 1_000_003;
const FINGERPRINT_BASE_RIGHT = 1_000_033;

function validAuthor(value: unknown): value is SelectionCommentAuthor {
  if (typeof value !== "object" || value === null) return false;
  const item = value as Record<string, unknown>;
  return typeof item.principalId === "string" && typeof item.displayName === "string";
}

function validStoredSelection(value: unknown): value is StoredSelectionComment {
  if (typeof value !== "object" || value === null) return false;
  const item = value as Record<string, unknown>;
  const anchor = item.anchor as Record<string, unknown> | undefined;
  return (
    typeof item.id === "string" &&
    typeof item.body === "string" &&
    typeof item.createdAt === "string" &&
    validAuthor(item.author) &&
    typeof anchor === "object" &&
    anchor !== null &&
    typeof anchor.fromLine === "number" &&
    typeof anchor.toLine === "number" &&
    typeof anchor.anchorLine === "number" &&
    (anchor.anchorKind === "line" || anchor.anchorKind === "table") &&
    typeof anchor.fromOffset === "number" &&
    typeof anchor.toOffset === "number" &&
    typeof anchor.quoteLength === "number" &&
    typeof anchor.quoteHash === "string" &&
    typeof anchor.quoteHead === "string" &&
    typeof anchor.quoteTail === "string" &&
    typeof anchor.before === "string" &&
    typeof anchor.after === "string" &&
    Array.isArray(item.replies) &&
    item.replies.every(
      (reply) =>
        typeof reply === "object" &&
        reply !== null &&
        typeof (reply as Record<string, unknown>).id === "string" &&
        typeof (reply as Record<string, unknown>).body === "string" &&
        typeof (reply as Record<string, unknown>).createdAt === "string" &&
        validAuthor((reply as Record<string, unknown>).author),
    )
  );
}

/** Two independent 32-bit hashes keep raw account names, repository ids and absolute paths out of Web
 * Storage keys. Length is included so simple concatenation ambiguities cannot alias. */
export function opaqueSelectionStorageKey(value: string): string {
  let left = 0x811c9dc5;
  let right = 0x9e3779b9;
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    left = Math.imul(left ^ code, 0x01000193);
    right = Math.imul(right ^ code, 0x85ebca6b);
  }
  return `${value.length.toString(36)}-${(left >>> 0).toString(36)}-${(right >>> 0).toString(36)}`;
}

function selectionFingerprint(value: string): string {
  let left = 0;
  let right = 0;
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index) + 1;
    left = (Math.imul(left, FINGERPRINT_BASE_LEFT) + code) >>> 0;
    right = (Math.imul(right, FINGERPRINT_BASE_RIGHT) + code) >>> 0;
  }
  return `${value.length.toString(36)}-${left.toString(36)}-${right.toString(36)}`;
}

async function yieldForStorage(): Promise<void> {
  await new Promise<void>((resolve) => {
    if (typeof globalThis.requestIdleCallback === "function") {
      globalThis.requestIdleCallback(() => resolve(), { timeout: 500 });
    } else {
      globalThis.setTimeout(resolve, 0);
    }
  });
}

export class BrowserSelectionCommentStorage implements SelectionCommentStorage {
  async load(
    principalKey: string,
    documentKey: string,
  ): Promise<readonly StoredSelectionComment[]> {
    await yieldForStorage();
    const raw = globalThis.localStorage.getItem(this.key(principalKey, documentKey));
    if (raw === null) return [];
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed) || !parsed.every(validStoredSelection)) {
      throw new Error("Saved comments are damaged and couldn't be opened.");
    }
    return parsed;
  }

  async save(
    principalKey: string,
    documentKey: string,
    comments: readonly StoredSelectionComment[],
  ): Promise<void> {
    await yieldForStorage();
    const key = this.key(principalKey, documentKey);
    if (comments.length === 0) globalThis.localStorage.removeItem(key);
    else globalThis.localStorage.setItem(key, JSON.stringify(comments));
  }

  private key(principalKey: string, documentKey: string): string {
    return `${STORAGE_PREFIX}.${opaqueSelectionStorageKey(principalKey)}.${opaqueSelectionStorageKey(documentKey)}`;
  }
}

/** Stable in-session identity. Local paths are absolute (clone-safe); branch keeps the same path in
 * different working lines isolated. Remote callers additionally supply the repository id. */
export function selectionDocumentKey(path: string, repository?: string, branch?: string): string {
  return [repository ?? "local", branch ?? "unversioned", path].join("\u0000");
}

/** One immutable source index is shared by every comment restored for a document. */
class DocumentAnchorIndex {
  readonly blocks: readonly ReturnType<typeof splitTopLevelBlocks>[number][];
  private readonly lineStarts = [0];
  private fingerprintPrefixLeft: Uint32Array | null = null;
  private fingerprintPrefixRight: Uint32Array | null = null;
  private fingerprintPowersLeft: Uint32Array | null = null;
  private fingerprintPowersRight: Uint32Array | null = null;

  constructor(
    readonly markdown: string,
    diagnostics?: SelectionCommentDiagnostics,
  ) {
    for (let offset = 0; offset < markdown.length; offset++) {
      if (markdown.charCodeAt(offset) === 10) this.lineStarts.push(offset + 1);
    }
    this.blocks = splitTopLevelBlocks(markdown);
    diagnostics?.onDocumentIndexBuilt?.(markdown.length);
  }

  lineAt(offset: number): number {
    const clamped = Math.max(0, Math.min(offset, this.markdown.length));
    let low = 0;
    let high = this.lineStarts.length;
    while (low < high) {
      const middle = (low + high) >>> 1;
      if ((this.lineStarts[middle] ?? Number.POSITIVE_INFINITY) <= clamped) low = middle + 1;
      else high = middle;
    }
    return Math.max(0, low - 1);
  }

  lineEndOffset(line: number): number {
    const nextStart = this.lineStarts[line + 1];
    return nextStart === undefined ? this.markdown.length : Math.max(0, nextStart - 1);
  }

  fingerprint(fromOffset: number, length: number): string | null {
    const from = Math.max(0, Math.min(fromOffset, this.markdown.length));
    const boundedLength = Math.max(0, Math.min(length, this.markdown.length - from));
    if (boundedLength !== length) return null;
    this.ensureFingerprintIndex();
    const to = from + boundedLength;
    const prefixLeft = this.fingerprintPrefixLeft;
    const prefixRight = this.fingerprintPrefixRight;
    const powersLeft = this.fingerprintPowersLeft;
    const powersRight = this.fingerprintPowersRight;
    if (
      prefixLeft === null ||
      prefixRight === null ||
      powersLeft === null ||
      powersRight === null
    ) {
      return null;
    }
    const left =
      ((prefixLeft[to] ?? 0) - Math.imul(prefixLeft[from] ?? 0, powersLeft[boundedLength] ?? 0)) >>>
      0;
    const right =
      ((prefixRight[to] ?? 0) -
        Math.imul(prefixRight[from] ?? 0, powersRight[boundedLength] ?? 0)) >>>
      0;
    return `${boundedLength.toString(36)}-${left.toString(36)}-${right.toString(36)}`;
  }

  private ensureFingerprintIndex(): void {
    if (this.fingerprintPrefixLeft !== null) return;
    const size = this.markdown.length + 1;
    const prefixLeft = new Uint32Array(size);
    const prefixRight = new Uint32Array(size);
    const powersLeft = new Uint32Array(size);
    const powersRight = new Uint32Array(size);
    powersLeft[0] = 1;
    powersRight[0] = 1;
    for (let index = 0; index < this.markdown.length; index++) {
      const code = this.markdown.charCodeAt(index) + 1;
      prefixLeft[index + 1] =
        (Math.imul(prefixLeft[index] ?? 0, FINGERPRINT_BASE_LEFT) + code) >>> 0;
      prefixRight[index + 1] =
        (Math.imul(prefixRight[index] ?? 0, FINGERPRINT_BASE_RIGHT) + code) >>> 0;
      powersLeft[index + 1] = Math.imul(powersLeft[index] ?? 0, FINGERPRINT_BASE_LEFT);
      powersRight[index + 1] = Math.imul(powersRight[index] ?? 0, FINGERPRINT_BASE_RIGHT);
    }
    this.fingerprintPrefixLeft = prefixLeft;
    this.fingerprintPrefixRight = prefixRight;
    this.fingerprintPowersLeft = powersLeft;
    this.fingerprintPowersRight = powersRight;
  }

  selection(fromOffset: number, toOffset: number): SourceSelection | null {
    const from = Math.max(0, Math.min(fromOffset, toOffset, this.markdown.length));
    const to = Math.max(0, Math.min(Math.max(fromOffset, toOffset), this.markdown.length));
    if (from === to) return null;
    const fromLine = this.lineAt(from);
    const toLine = this.lineAt(Math.max(from, to - 1));
    const last = [...this.blocks].reverse().find((block) => block.lineStart <= toLine);
    const anchorLine =
      last?.containerKind === "table"
        ? Math.max(toLine, (last.contentLineEnd ?? last.lineEnd + 1) - 1)
        : toLine;
    return {
      fromLine,
      toLine,
      anchorLine,
      anchorKind: last?.containerKind === "table" ? "table" : "line",
      fromOffset: from,
      toOffset: to,
      anchorOffset: this.lineEndOffset(anchorLine),
      quote: this.markdown.slice(from, to),
    };
  }
}

/** Keep the visual anchor on the last selected line. A table is the one exception: placing a block
 * widget between its source rows would split the Markdown construct, so it anchors after the table. */
export function sourceSelection(
  markdown: string,
  fromOffset: number,
  toOffset: number,
): SourceSelection | null {
  return new DocumentAnchorIndex(markdown).selection(fromOffset, toOffset);
}

interface TextLine {
  readonly start: number;
  readonly end: number;
  readonly text: string;
}

interface UnchangedRun {
  readonly oldStart: number;
  readonly oldEnd: number;
  readonly newStart: number;
  readonly newEnd: number;
}

function textLines(text: string): TextLine[] {
  const lines: TextLine[] = [];
  let start = 0;
  for (let offset = 0; offset <= text.length; offset++) {
    if (offset === text.length || text.charCodeAt(offset) === 10) {
      lines.push({ start, end: offset, text: text.slice(start, offset) });
      start = offset + 1;
    }
  }
  return lines;
}

/** Ordered line correspondence for a multi-hunk source edit. Exact unique lines form patience-style
 * anchors; gaps pair by ordinal so mass prefix formatting (Quote/List on many lines) becomes one small
 * insertion per line rather than one destructive document-wide replacement. */
function pairedLines(previous: readonly TextLine[], next: readonly TextLine[]): [number, number][] {
  let prefix = 0;
  while (
    prefix < previous.length &&
    prefix < next.length &&
    previous[prefix]?.text === next[prefix]?.text
  ) {
    prefix++;
  }
  let suffix = 0;
  while (
    suffix < previous.length - prefix &&
    suffix < next.length - prefix &&
    previous[previous.length - suffix - 1]?.text === next[next.length - suffix - 1]?.text
  ) {
    suffix++;
  }

  const oldCounts = new Map<string, { count: number; index: number }>();
  const newCounts = new Map<string, { count: number; index: number }>();
  for (let index = prefix; index < previous.length - suffix; index++) {
    const text = previous[index]?.text ?? "";
    const found = oldCounts.get(text);
    oldCounts.set(text, { count: (found?.count ?? 0) + 1, index });
  }
  for (let index = prefix; index < next.length - suffix; index++) {
    const text = next[index]?.text ?? "";
    const found = newCounts.get(text);
    newCounts.set(text, { count: (found?.count ?? 0) + 1, index });
  }
  const candidates: [number, number][] = [];
  for (const [text, old] of oldCounts) {
    const current = newCounts.get(text);
    if (old.count === 1 && current?.count === 1) candidates.push([old.index, current.index]);
  }
  candidates.sort((left, right) => left[0] - right[0]);

  // Longest increasing subsequence by new-line ordinal keeps only non-crossing patience anchors.
  const tails: number[] = [];
  const previousCandidate = new Array<number>(candidates.length).fill(-1);
  const tailCandidate: number[] = [];
  for (let index = 0; index < candidates.length; index++) {
    const newIndex = candidates[index]?.[1] ?? 0;
    let low = 0;
    let high = tails.length;
    while (low < high) {
      const middle = (low + high) >>> 1;
      if ((tails[middle] ?? Number.POSITIVE_INFINITY) < newIndex) low = middle + 1;
      else high = middle;
    }
    tails[low] = newIndex;
    previousCandidate[index] = low > 0 ? (tailCandidate[low - 1] ?? -1) : -1;
    tailCandidate[low] = index;
  }
  const middleAnchors: [number, number][] = [];
  let candidate = tailCandidate[tails.length - 1] ?? -1;
  while (candidate >= 0) {
    const pair = candidates[candidate];
    if (pair !== undefined) middleAnchors.push(pair);
    candidate = previousCandidate[candidate] ?? -1;
  }
  middleAnchors.reverse();

  const anchors: [number, number][] = [];
  for (let index = 0; index < prefix; index++) anchors.push([index, index]);
  anchors.push(...middleAnchors);
  for (let offset = suffix; offset > 0; offset--) {
    anchors.push([previous.length - offset, next.length - offset]);
  }
  const pairs: [number, number][] = [];
  let oldCursor = 0;
  let newCursor = 0;
  for (const anchor of anchors) {
    const count = Math.min(anchor[0] - oldCursor, anchor[1] - newCursor);
    for (let offset = 0; offset < count; offset++) {
      pairs.push([oldCursor + offset, newCursor + offset]);
    }
    pairs.push(anchor);
    oldCursor = anchor[0] + 1;
    newCursor = anchor[1] + 1;
  }
  const tail = Math.min(previous.length - oldCursor, next.length - newCursor);
  for (let offset = 0; offset < tail; offset++)
    pairs.push([oldCursor + offset, newCursor + offset]);
  return pairs;
}

/** Linear KMP substring search; wrapper-heavy intraline mapping stays explicitly bounded. */
function substringIndex(haystack: string, needle: string): number {
  if (needle.length === 0) return 0;
  const prefix = new Array<number>(needle.length).fill(0);
  for (let index = 1, matched = 0; index < needle.length; index++) {
    while (matched > 0 && needle.charCodeAt(index) !== needle.charCodeAt(matched)) {
      matched = prefix[matched - 1] ?? 0;
    }
    if (needle.charCodeAt(index) === needle.charCodeAt(matched)) matched++;
    prefix[index] = matched;
  }
  for (let index = 0, matched = 0; index < haystack.length; index++) {
    while (matched > 0 && haystack.charCodeAt(index) !== needle.charCodeAt(matched)) {
      matched = prefix[matched - 1] ?? 0;
    }
    if (haystack.charCodeAt(index) === needle.charCodeAt(matched)) matched++;
    if (matched === needle.length) return index - needle.length + 1;
  }
  return -1;
}

/** Emit unchanged runs inside one paired line. Common ends handle ordinary typing; KMP recognizes
 * exact text wrapped by Markdown punctuation; remaining middles use unique-character patience/LIS
 * anchors and recurse into disjoint gaps. */
function intralineRuns(
  previous: string,
  next: string,
  oldBase: number,
  newBase: number,
  emit: (oldStart: number, oldEnd: number, newStart: number, newEnd: number) => void,
): void {
  const walk = (oldStart: number, oldEnd: number, newStart: number, newEnd: number): void => {
    let prefix = 0;
    const shared = Math.min(oldEnd - oldStart, newEnd - newStart);
    while (
      prefix < shared &&
      previous.charCodeAt(oldStart + prefix) === next.charCodeAt(newStart + prefix)
    ) {
      prefix++;
    }
    if (prefix > 0) {
      emit(
        oldBase + oldStart,
        oldBase + oldStart + prefix,
        newBase + newStart,
        newBase + newStart + prefix,
      );
      oldStart += prefix;
      newStart += prefix;
    }
    let suffix = 0;
    const remaining = Math.min(oldEnd - oldStart, newEnd - newStart);
    while (
      suffix < remaining &&
      previous.charCodeAt(oldEnd - suffix - 1) === next.charCodeAt(newEnd - suffix - 1)
    ) {
      suffix++;
    }
    const oldMiddleEnd = oldEnd - suffix;
    const newMiddleEnd = newEnd - suffix;
    if (oldStart < oldMiddleEnd && newStart < newMiddleEnd) {
      const oldMiddle = previous.slice(oldStart, oldMiddleEnd);
      const newMiddle = next.slice(newStart, newMiddleEnd);
      const embeddedOld = substringIndex(newMiddle, oldMiddle);
      if (embeddedOld >= 0) {
        emit(
          oldBase + oldStart,
          oldBase + oldMiddleEnd,
          newBase + newStart + embeddedOld,
          newBase + newStart + embeddedOld + oldMiddle.length,
        );
      } else {
        const embeddedNew = substringIndex(oldMiddle, newMiddle);
        if (embeddedNew >= 0) {
          emit(
            oldBase + oldStart + embeddedNew,
            oldBase + oldStart + embeddedNew + newMiddle.length,
            newBase + newStart,
            newBase + newMiddleEnd,
          );
        } else {
          const oldCounts = new Map<number, { count: number; index: number }>();
          const newCounts = new Map<number, { count: number; index: number }>();
          for (let index = oldStart; index < oldMiddleEnd; index++) {
            const code = previous.charCodeAt(index);
            const found = oldCounts.get(code);
            oldCounts.set(code, { count: (found?.count ?? 0) + 1, index });
          }
          for (let index = newStart; index < newMiddleEnd; index++) {
            const code = next.charCodeAt(index);
            const found = newCounts.get(code);
            newCounts.set(code, { count: (found?.count ?? 0) + 1, index });
          }
          const candidates: [number, number][] = [];
          for (const [code, old] of oldCounts) {
            const current = newCounts.get(code);
            if (old.count === 1 && current?.count === 1) {
              candidates.push([old.index, current.index]);
            }
          }
          candidates.sort((left, right) => left[0] - right[0]);
          const tails: number[] = [];
          const prior = new Array<number>(candidates.length).fill(-1);
          const tailCandidate: number[] = [];
          for (let index = 0; index < candidates.length; index++) {
            const position = candidates[index]?.[1] ?? 0;
            let low = 0;
            let high = tails.length;
            while (low < high) {
              const middle = (low + high) >>> 1;
              if ((tails[middle] ?? Number.POSITIVE_INFINITY) < position) low = middle + 1;
              else high = middle;
            }
            tails[low] = position;
            prior[index] = low > 0 ? (tailCandidate[low - 1] ?? -1) : -1;
            tailCandidate[low] = index;
          }
          const anchors: [number, number][] = [];
          let candidate = tailCandidate[tails.length - 1] ?? -1;
          while (candidate >= 0) {
            const pair = candidates[candidate];
            if (pair !== undefined) anchors.push(pair);
            candidate = prior[candidate] ?? -1;
          }
          anchors.reverse();
          if (anchors.length > 0) {
            let oldCursor = oldStart;
            let newCursor = newStart;
            for (const [oldAnchor, newAnchor] of anchors) {
              walk(oldCursor, oldAnchor, newCursor, newAnchor);
              emit(
                oldBase + oldAnchor,
                oldBase + oldAnchor + 1,
                newBase + newAnchor,
                newBase + newAnchor + 1,
              );
              oldCursor = oldAnchor + 1;
              newCursor = newAnchor + 1;
            }
            walk(oldCursor, oldMiddleEnd, newCursor, newMiddleEnd);
          }
        }
      }
    }
    if (suffix > 0) {
      emit(oldBase + oldMiddleEnd, oldBase + oldEnd, newBase + newMiddleEnd, newBase + newEnd);
    }
  };
  walk(0, previous.length, 0, next.length);
}

/** Multi-range line/intraline diff represented by unchanged runs. It is linear apart from the patience
 * anchor LIS (O(lines log lines)) and never allocates a document-sized LCS matrix. */
function unchangedRuns(previous: string, next: string): UnchangedRun[] {
  const oldLines = textLines(previous);
  const newLines = textLines(next);
  const runs: UnchangedRun[] = [];
  const push = (oldStart: number, oldEnd: number, newStart: number, newEnd: number): void => {
    if (oldStart === oldEnd) return;
    const last = runs[runs.length - 1];
    if (last !== undefined && last.oldEnd === oldStart && last.newEnd === newStart) {
      runs[runs.length - 1] = { ...last, oldEnd, newEnd };
    } else {
      runs.push({ oldStart, oldEnd, newStart, newEnd });
    }
  };
  for (const [oldIndex, newIndex] of pairedLines(oldLines, newLines)) {
    const oldLine = oldLines[oldIndex];
    const newLine = newLines[newIndex];
    if (oldLine === undefined || newLine === undefined) continue;
    if (oldLine.text === newLine.text) {
      push(oldLine.start, oldLine.end, newLine.start, newLine.end);
    } else {
      intralineRuns(oldLine.text, newLine.text, oldLine.start, newLine.start, push);
    }
    if (previous.charCodeAt(oldLine.end) === 10 && next.charCodeAt(newLine.end) === 10) {
      push(oldLine.end, oldLine.end + 1, newLine.end, newLine.end + 1);
    }
  }
  return runs;
}

function storedAnchor(selection: SourceSelection, markdown: string): StoredSelectionAnchor {
  const quote = markdown.slice(selection.fromOffset, selection.toOffset);
  return {
    fromLine: selection.fromLine,
    toLine: selection.toLine,
    anchorLine: selection.anchorLine,
    anchorKind: selection.anchorKind,
    fromOffset: selection.fromOffset,
    toOffset: selection.toOffset,
    quoteLength: quote.length,
    quoteHash: selectionFingerprint(quote),
    quoteHead: quote.slice(0, QUOTE_PART_LIMIT),
    quoteTail: quote.slice(Math.max(0, quote.length - QUOTE_PART_LIMIT)),
    before: markdown.slice(Math.max(0, selection.fromOffset - CONTEXT_LIMIT), selection.fromOffset),
    after: markdown.slice(selection.toOffset, selection.toOffset + CONTEXT_LIMIT),
  };
}

function storedComment(comment: SelectionComment, markdown: string): StoredSelectionComment {
  return {
    anchor:
      comment.anchorState === "detached" && comment.detachedAnchor !== undefined
        ? comment.detachedAnchor
        : storedAnchor(comment, markdown),
    id: comment.id,
    body: comment.body,
    createdAt: comment.createdAt,
    ...(comment.updatedAt === undefined ? {} : { updatedAt: comment.updatedAt }),
    author: comment.author,
    replies: comment.replies,
  };
}

function contextScore(markdown: string, offset: number, expected: string, before: boolean): number {
  const actual = before
    ? markdown.slice(Math.max(0, offset - expected.length), offset)
    : markdown.slice(offset, offset + expected.length);
  let score = 0;
  while (
    score < expected.length &&
    (before
      ? actual.charCodeAt(actual.length - score - 1) ===
        expected.charCodeAt(expected.length - score - 1)
      : actual.charCodeAt(score) === expected.charCodeAt(score))
  ) {
    score++;
  }
  return score;
}

interface RestoredAnchor {
  readonly selection: SourceSelection;
  readonly state: "attached" | "detached";
}

function detachedSelection(anchor: StoredSelectionAnchor): SourceSelection {
  return {
    fromLine: 0,
    toLine: 0,
    anchorLine: 0,
    anchorKind: anchor.anchorKind,
    fromOffset: 0,
    toOffset: 0,
    anchorOffset: 0,
    quote: "",
  };
}

function uniqueBestCandidate(
  candidates: readonly number[],
  score: (offset: number) => number,
): number | null {
  if (candidates.length === 0) return null;
  if (candidates.length === 1) return candidates[0] ?? null;
  let best = Number.NEGATIVE_INFINITY;
  let bestOffset: number | null = null;
  let tied = false;
  for (const offset of candidates) {
    const value = score(offset);
    if (value > best) {
      best = value;
      bestOffset = offset;
      tied = false;
    } else if (value === best) {
      tied = true;
    }
  }
  return tied ? null : bestOffset;
}

interface EditedRangeSearch {
  readonly candidates: readonly [number, number][];
  readonly truncated: boolean;
}

function editedRangeCandidates(anchor: StoredSelectionAnchor, markdown: string): EditedRangeSearch {
  if (anchor.before.length === 0 || anchor.after.length === 0) {
    return { candidates: [], truncated: false };
  }
  const candidates: [number, number][] = [];
  const seen = new Set<string>();
  const spanLimit = Math.min(
    markdown.length,
    Math.max(4_096, Math.min(EDITED_RANGE_SPAN_LIMIT, anchor.quoteLength * 4 + 1_024)),
  );
  let beforeCursor = 0;
  while (candidates.length < 256) {
    const beforeAt = markdown.indexOf(anchor.before, beforeCursor);
    if (beforeAt < 0) break;
    const start = beforeAt + anchor.before.length;
    const latestEnd = Math.min(markdown.length, start + spanLimit);
    let afterAt = markdown.indexOf(anchor.after, start);
    while (afterAt >= 0 && afterAt <= latestEnd && candidates.length < 256) {
      if (afterAt > start) {
        const key = `${start}:${afterAt}`;
        if (!seen.has(key)) {
          seen.add(key);
          candidates.push([start, afterAt]);
        }
      }
      afterAt = markdown.indexOf(anchor.after, afterAt + 1);
    }
    beforeCursor = beforeAt + 1;
  }
  return { candidates, truncated: candidates.length >= 256 };
}

function editedRangeScore(
  anchor: StoredSelectionAnchor,
  markdown: string,
  range: [number, number],
): number {
  const candidateLength = range[1] - range[0];
  let prefix = 0;
  const prefixLimit = Math.min(candidateLength, anchor.quoteHead.length);
  while (
    prefix < prefixLimit &&
    markdown.charCodeAt(range[0] + prefix) === anchor.quoteHead.charCodeAt(prefix)
  ) {
    prefix++;
  }
  let suffix = 0;
  const suffixLimit = Math.min(candidateLength - prefix, anchor.quoteTail.length);
  while (
    suffix < suffixLimit &&
    markdown.charCodeAt(range[1] - suffix - 1) ===
      anchor.quoteTail.charCodeAt(anchor.quoteTail.length - suffix - 1)
  ) {
    suffix++;
  }
  return prefix + suffix;
}

/** Re-find a persisted selection from bounded quote/context fingerprints. Exact duplicates and edited
 * context ranges must resolve uniquely; otherwise the thread is explicitly detached rather than projected
 * onto unrelated text at its old absolute offset. */
function restoreAnchor(
  anchor: StoredSelectionAnchor,
  index: DocumentAnchorIndex,
  diagnostics?: SelectionCommentDiagnostics,
): RestoredAnchor {
  const markdown = index.markdown;
  const candidates: number[] = [];
  const matchesFingerprint = (found: number): boolean => {
    const end = found + anchor.quoteLength;
    diagnostics?.onFingerprintVerified?.(1);
    return (
      found >= 0 &&
      end <= markdown.length &&
      markdown.slice(found, found + anchor.quoteHead.length) === anchor.quoteHead &&
      markdown.slice(Math.max(found, end - anchor.quoteTail.length), end) === anchor.quoteTail &&
      index.fingerprint(found, anchor.quoteLength) === anchor.quoteHash
    );
  };
  const previousOffset = Math.max(0, Math.min(anchor.fromOffset, markdown.length));
  if (matchesFingerprint(previousOffset)) candidates.push(previousOffset);
  let cursor = 0;
  let inspected = 0;
  const inspectionLimit = RESTORE_CANDIDATE_LIMIT;
  while (anchor.quoteHead.length > 0 && inspected < inspectionLimit) {
    const found = markdown.indexOf(anchor.quoteHead, cursor);
    if (found < 0) break;
    inspected++;
    if (found !== previousOffset && matchesFingerprint(found)) {
      candidates.push(found);
    }
    cursor = found + 1;
  }
  const exactSearchTruncated =
    inspected >= inspectionLimit && markdown.indexOf(anchor.quoteHead, cursor) >= 0;
  const exact = exactSearchTruncated
    ? null
    : uniqueBestCandidate(candidates, (offset) => {
        const end = offset + anchor.quoteLength;
        return (
          contextScore(markdown, offset, anchor.before, true) +
          contextScore(markdown, end, anchor.after, false)
        );
      });
  if (exact !== null) {
    const selection = index.selection(exact, exact + anchor.quoteLength);
    if (selection !== null) return { selection, state: "attached" };
  }

  const editedSearch = editedRangeCandidates(anchor, markdown);
  const edited = editedSearch.candidates;
  let bestRange: [number, number] | null = null;
  if (!editedSearch.truncated && edited.length === 1) bestRange = edited[0] ?? null;
  else if (!editedSearch.truncated && edited.length > 1) {
    const offsets = edited.map((_, candidate) => candidate);
    const best = uniqueBestCandidate(offsets, (candidate) => {
      const range = edited[candidate];
      return range === undefined
        ? Number.NEGATIVE_INFINITY
        : editedRangeScore(anchor, markdown, range);
    });
    bestRange = best === null ? null : (edited[best] ?? null);
  }
  if (bestRange !== null) {
    const selection = index.selection(bestRange[0], bestRange[1]);
    if (selection !== null) return { selection, state: "attached" };
  }
  return { selection: detachedSelection(anchor), state: "detached" };
}

function restoredComment(
  stored: StoredSelectionComment,
  index: DocumentAnchorIndex,
  diagnostics?: SelectionCommentDiagnostics,
): SelectionComment {
  const anchor = restoreAnchor(stored.anchor, index, diagnostics);
  return {
    ...anchor.selection,
    id: stored.id,
    body: stored.body,
    createdAt: stored.createdAt,
    ...(stored.updatedAt === undefined ? {} : { updatedAt: stored.updatedAt }),
    author: stored.author,
    replies: stored.replies,
    anchorState: anchor.state,
    ...(anchor.state === "detached" ? { detachedAnchor: stored.anchor } : {}),
  };
}

/** A user can submit before the idle storage read completes, then immediately navigate away. Preserve
 * both the older stored threads and those new local threads; numeric ids from two independent snapshots
 * are disambiguated before the merged snapshot is written. */
function mergeStoredComments(
  existing: readonly StoredSelectionComment[],
  local: readonly StoredSelectionComment[],
): readonly StoredSelectionComment[] {
  const merged = [...existing];
  const occupied = new Set(existing.map((comment) => comment.id));
  let sequence = 0;
  for (const comment of [...existing, ...local]) {
    sequence = Math.max(sequence, numericId(comment.id));
    for (const reply of comment.replies) sequence = Math.max(sequence, numericId(reply.id));
  }
  for (const comment of local) {
    const id = occupied.has(comment.id) ? `selection-comment-${++sequence}` : comment.id;
    occupied.add(id);
    merged.push({ ...comment, id });
  }
  return merged;
}

export class SelectionCommentSession {
  private comments: SelectionComment[] = [];
  private sequence = 0;
  private documentKey = "";
  private markdown = "";
  private draft: SelectionCommentDraft | null = null;
  private principal: SelectionCommentAuthor = {
    principalId: "signed-out",
    displayName: "Local author",
  };
  private generation = 0;
  private mutationRevision = 0;
  private saveTimer: ReturnType<typeof setTimeout> | null = null;
  private persistence: "saved" | "saving" | "error" = "saved";
  private persistenceMessage: string | undefined;
  private readonly failedRequests = new Map<string, SelectionPersistRequest>();
  private readonly latestEnqueuedRevisions = new Map<string, number>();
  private currentLoadError = false;
  private closeInProgress = false;
  private closeBlocked = false;
  private loadBarrier: Promise<void> = Promise.resolve();
  private persistenceTail: Promise<void> = Promise.resolve();
  private loadPending = false;
  private dirty = false;
  private notify: () => void = () => undefined;

  constructor(
    private readonly storage: SelectionCommentStorage = new BrowserSelectionCommentStorage(),
    private readonly diagnostics?: SelectionCommentDiagnostics,
  ) {}

  setNotifier(notify: () => void): void {
    this.notify = notify;
  }

  async setDocument(key: string, markdown = ""): Promise<boolean> {
    this.retireContext();
    this.documentKey = key;
    this.markdown = markdown;
    return await this.startLoad();
  }

  async setPrincipal(login: string | null, boundaryId?: string): Promise<boolean> {
    const principal =
      login === null
        ? { principalId: "signed-out", displayName: "Local author" }
        : login.trim().length === 0
          ? {
              principalId: `github-pending:${opaqueSelectionStorageKey(boundaryId ?? "current-session")}`,
              displayName: "GitHub user",
            }
          : {
              principalId: `github:${login.trim().toLocaleLowerCase()}`,
              displayName: login.trim(),
            };
    if (principal.principalId === this.principal.principalId) return true;
    this.retireContext();
    this.principal = principal;
    this.syncPersistenceStatus();
    return this.documentKey.length === 0 ? true : await this.startLoad();
  }

  private async startLoad(): Promise<boolean> {
    if (this.documentKey.length === 0) {
      this.loadPending = false;
      this.loadBarrier = Promise.resolve();
      this.syncPersistenceStatus();
      this.notify();
      return true;
    }
    this.loadPending = true;
    // Document/account transitions must publish the empty loading view synchronously. Both editor
    // surfaces cache their last comment view, so waiting for storage would project the previous
    // document's threads into the new text until that asynchronous read completed.
    this.syncPersistenceStatus();
    this.notify();
    const loading = this.loadCurrent();
    this.loadBarrier = loading.then(() => undefined);
    return await loading;
  }

  private retireContext(): void {
    if (this.saveTimer !== null) {
      clearTimeout(this.saveTimer);
      this.saveTimer = null;
    }
    if (this.dirty && this.documentKey.length > 0) {
      const request = this.capturePersistRequest();
      void this.persistRequest(
        this.loadPending || this.currentLoadError ? { ...request, mergeExisting: true } : request,
        null,
      );
    }
    this.generation++;
    this.mutationRevision++;
    this.comments = [];
    this.draft = null;
    this.currentLoadError = false;
    this.loadPending = false;
    this.dirty = false;
    this.syncPersistenceStatus();
  }

  private async loadCurrent(): Promise<boolean> {
    if (this.documentKey.length === 0) return true;
    const generation = this.generation;
    const mutationRevision = this.mutationRevision;
    const principalKey = this.principal.principalId;
    const documentKey = this.documentKey;
    try {
      await this.awaitPersistenceIdle();
      if (generation !== this.generation) return false;
      const stored = await this.storage.load(principalKey, documentKey);
      if (generation !== this.generation) return false;
      this.loadPending = false;
      this.currentLoadError = false;
      const failedRequest =
        this.failedRequests.get(persistRequestKey(principalKey, documentKey)) ?? null;
      const currentStored =
        failedRequest === null
          ? stored
          : failedRequest.mergeExisting
            ? mergeStoredComments(stored, failedRequest.comments)
            : failedRequest.comments;
      const index = new DocumentAnchorIndex(this.markdown, this.diagnostics);
      const restored = currentStored.map((comment) =>
        restoredComment(comment, index, this.diagnostics),
      );
      if (mutationRevision !== this.mutationRevision) {
        const local = this.comments;
        const occupied = new Set(restored.map((comment) => comment.id));
        this.comments = [...restored];
        this.refreshSequence();
        for (const comment of local) {
          const id = occupied.has(comment.id) ? `selection-comment-${++this.sequence}` : comment.id;
          if (id !== comment.id && this.draft?.commentId === comment.id) {
            this.draft = { ...this.draft, commentId: id };
          }
          occupied.add(id);
          this.comments.push({ ...comment, id });
        }
        this.refreshSequence();
        this.queuePersist(0);
        this.notify();
        return true;
      }
      this.comments = restored;
      this.refreshSequence();
      this.syncPersistenceStatus();
      this.notify();
      return true;
    } catch {
      if (generation !== this.generation) return false;
      this.loadPending = false;
      this.currentLoadError = true;
      this.syncPersistenceStatus();
      this.notify();
      return false;
    }
  }

  private async awaitPersistenceIdle(): Promise<void> {
    while (true) {
      const pending = this.persistenceTail;
      await pending;
      if (pending === this.persistenceTail) return;
    }
  }

  private syncPersistenceStatus(): void {
    const failed = this.currentPrincipalFailedRequests().length;
    const failedAcrossAccounts = this.failedRequests.size;
    if (this.currentLoadError) {
      this.persistence = "error";
      this.persistenceMessage =
        failed === 0
          ? "Comments are unavailable until their saved snapshot loads. Retry when storage is available."
          : `Comments are unavailable until their saved snapshot loads. ${failed} pending comment ${failed === 1 ? "snapshot" : "snapshots"} will also be retried.`;
      return;
    }
    if (this.closeBlocked && failedAcrossAccounts > 0) {
      this.persistence = "error";
      this.persistenceMessage =
        failedAcrossAccounts === 1
          ? "1 comment snapshot must be saved before closing. Retry pending comment storage."
          : `${failedAcrossAccounts} comment snapshots must be saved before closing. Retry pending comment storage.`;
      return;
    }
    if (this.closeBlocked) {
      this.persistence = "error";
      this.persistenceMessage =
        "Comments changed while the window was preparing to close. Retry, then close again.";
      return;
    }
    if (failed > 0) {
      this.persistence = "error";
      this.persistenceMessage =
        failed === 1
          ? "1 comment snapshot couldn't be saved. Please retry pending comment storage."
          : `${failed} comment snapshots couldn't be saved. Please retry pending comment storage.`;
      return;
    }
    this.persistence = "saved";
    this.persistenceMessage = undefined;
  }

  private currentPrincipalFailedRequests(): SelectionPersistRequest[] {
    return [...this.failedRequests.values()].filter(
      (request) => request.principalKey === this.principal.principalId,
    );
  }

  private refreshSequence(): void {
    for (const comment of this.comments) {
      this.sequence = Math.max(this.sequence, numericId(comment.id));
      for (const reply of comment.replies)
        this.sequence = Math.max(this.sequence, numericId(reply.id));
    }
  }

  add(selection: SourceSelection, body: string): SelectionComment | null {
    if (!this.mutationsAllowed()) return null;
    const trimmed = body.trim();
    if (trimmed.length === 0) return null;
    const comment: SelectionComment = {
      ...selection,
      id: `selection-comment-${++this.sequence}`,
      body: trimmed,
      createdAt: new Date().toISOString(),
      author: this.principal,
      replies: [],
      anchorState: "attached",
    };
    this.comments.push(comment);
    this.draft = null;
    this.mutated();
    return comment;
  }

  begin(selection: SourceSelection, surface: "code" | "formatted" = "code"): void {
    if (!this.mutationsAllowed()) return;
    this.draft = {
      ...selection,
      documentKey: this.documentKey,
      surface,
      mode: "create",
      initialBody: "",
    };
  }

  beginEdit(commentId: string, replyId?: string, surface: "code" | "formatted" = "code"): boolean {
    if (!this.mutationsAllowed()) return false;
    const comment = this.comments.find((item) => item.id === commentId);
    if (comment === undefined) return false;
    const entity =
      replyId === undefined ? comment : comment.replies.find((reply) => reply.id === replyId);
    if (entity?.author.principalId !== this.principal.principalId) return false;
    const body = entity.body;
    this.draft = {
      ...comment,
      documentKey: this.documentKey,
      surface,
      mode: "edit",
      commentId,
      ...(replyId === undefined ? {} : { replyId }),
      initialBody: body,
    };
    return true;
  }

  beginReply(commentId: string, surface: "code" | "formatted" = "code"): boolean {
    if (!this.mutationsAllowed()) return false;
    const comment = this.comments.find((item) => item.id === commentId);
    if (comment === undefined) return false;
    this.draft = {
      ...comment,
      documentKey: this.documentKey,
      surface,
      mode: "reply",
      commentId,
      initialBody: "",
    };
    return true;
  }

  cancelDraft(): void {
    this.draft = null;
  }

  updateDraft(body: string): boolean {
    if (this.draft === null || this.draft.documentKey !== this.documentKey) return false;
    this.draft.initialBody = body;
    return true;
  }

  submitDraft(body: string): boolean {
    if (!this.mutationsAllowed()) return false;
    const draft = this.draft;
    const trimmed = body.trim();
    if (draft === null || draft.documentKey !== this.documentKey || trimmed.length === 0)
      return false;
    if (draft.mode === "create") return this.add(draft, trimmed) !== null;
    const index = this.comments.findIndex((comment) => comment.id === draft.commentId);
    const comment = this.comments[index];
    if (comment === undefined) {
      this.draft = null;
      return false;
    }
    const now = new Date().toISOString();
    if (draft.mode === "reply") {
      const reply: SelectionCommentReply = {
        id: `selection-reply-${++this.sequence}`,
        body: trimmed,
        createdAt: now,
        author: this.principal,
      };
      this.comments[index] = { ...comment, replies: [...comment.replies, reply] };
    } else if (draft.replyId === undefined) {
      if (comment.author.principalId !== this.principal.principalId) return false;
      this.comments[index] = { ...comment, body: trimmed, updatedAt: now };
    } else {
      const replyIndex = comment.replies.findIndex((reply) => reply.id === draft.replyId);
      if (replyIndex < 0) {
        this.draft = null;
        return false;
      }
      const replies = [...comment.replies];
      const reply = replies[replyIndex];
      if (reply === undefined || reply.author.principalId !== this.principal.principalId)
        return false;
      replies[replyIndex] = { ...reply, body: trimmed, updatedAt: now };
      this.comments[index] = { ...comment, replies };
    }
    this.draft = null;
    this.mutated();
    return true;
  }

  delete(commentId: string, replyId?: string): boolean {
    if (!this.mutationsAllowed()) return false;
    const index = this.comments.findIndex((comment) => comment.id === commentId);
    const comment = this.comments[index];
    if (comment === undefined) return false;
    if (replyId === undefined) {
      if (comment.author.principalId !== this.principal.principalId) return false;
      this.comments.splice(index, 1);
    } else {
      const reply = comment.replies.find((item) => item.id === replyId);
      if (reply?.author.principalId !== this.principal.principalId) return false;
      this.comments[index] = {
        ...comment,
        replies: comment.replies.filter((reply) => reply.id !== replyId),
      };
    }
    if (
      this.draft?.commentId === commentId &&
      (replyId === undefined || this.draft.replyId === replyId)
    ) {
      this.draft = null;
    }
    this.mutated();
    return true;
  }

  all(): readonly SelectionComment[] {
    return this.comments;
  }

  view(): SelectionCommentView {
    return {
      comments: this.comments,
      draft: this.draft,
      principalId: this.principal.principalId,
      commentsAvailable: this.commentsAvailable(),
      persistence: this.persistence,
      ...(this.persistenceMessage === undefined
        ? {}
        : { persistenceMessage: this.persistenceMessage }),
    };
  }

  async retryPersistence(): Promise<void> {
    if (this.persistence !== "error") return;
    const failed = this.closeBlocked
      ? [...this.failedRequests.values()]
      : this.currentPrincipalFailedRequests();
    for (const request of failed) {
      if (
        this.failedRequests.get(persistRequestKey(request.principalKey, request.documentKey)) !==
        request
      ) {
        continue;
      }
      const isCurrentContext =
        request.principalKey === this.principal.principalId &&
        request.documentKey === this.documentKey;
      await this.persistRequest(request, isCurrentContext ? this.generation : null);
    }
    if (this.currentPrincipalFailedRequests().length === 0 && this.currentLoadError)
      await this.startLoad();
    if (this.failedRequests.size === 0 && !this.currentLoadError) this.closeBlocked = false;
    this.syncPersistenceStatus();
    this.notify();
  }

  async flushPersistence(): Promise<boolean> {
    const generation = this.generation;
    return await this.flushCurrentPersistence(generation);
  }

  async flushForClose(): Promise<boolean> {
    const generation = this.generation;
    this.closeInProgress = true;
    this.notify();
    await this.flushCurrentPersistence(generation);
    const succeeded =
      generation === this.generation &&
      !this.dirty &&
      !this.loadPending &&
      !this.currentLoadError &&
      this.failedRequests.size === 0;
    this.closeBlocked = !succeeded;
    if (!succeeded) this.closeInProgress = false;
    this.syncPersistenceStatus();
    this.notify();
    return succeeded;
  }

  private async flushCurrentPersistence(generation: number): Promise<boolean> {
    if (this.saveTimer !== null) clearTimeout(this.saveTimer);
    this.saveTimer = null;
    await this.loadBarrier;
    if (generation === this.generation && this.dirty && this.storageAvailable()) {
      await this.persist(generation);
    }
    await this.awaitPersistenceIdle();
    return (
      generation === this.generation &&
      !this.dirty &&
      !this.loadPending &&
      !this.currentLoadError &&
      this.currentPrincipalFailedRequests().length === 0
    );
  }

  releaseClosePersistence(): void {
    if (!this.closeInProgress) return;
    this.closeInProgress = false;
    this.syncPersistenceStatus();
    this.notify();
  }

  private mutated(): void {
    this.mutationRevision++;
    this.queuePersist(0);
  }

  private queuePersist(delay: number): void {
    if (this.saveTimer !== null) clearTimeout(this.saveTimer);
    this.dirty = true;
    if (this.currentPrincipalFailedRequests().length === 0) {
      this.persistence = "saving";
      this.persistenceMessage = undefined;
    } else {
      this.syncPersistenceStatus();
    }
    const generation = this.generation;
    this.saveTimer = setTimeout(() => {
      this.saveTimer = null;
      void this.persist(generation);
    }, delay);
  }

  private async persist(generation: number): Promise<void> {
    await this.loadBarrier;
    if (generation !== this.generation || !this.storageAvailable()) return;
    await this.persistRequest(this.capturePersistRequest(), generation);
  }

  private capturePersistRequest(): SelectionPersistRequest {
    return {
      principalKey: this.principal.principalId,
      documentKey: this.documentKey,
      comments: this.comments.map((comment) => storedComment(comment, this.markdown)),
      revision: this.mutationRevision,
    };
  }

  private async persistRequest(
    request: SelectionPersistRequest,
    generation: number | null,
  ): Promise<void> {
    const key = persistRequestKey(request.principalKey, request.documentKey);
    this.latestEnqueuedRevisions.set(
      key,
      Math.max(this.latestEnqueuedRevisions.get(key) ?? -1, request.revision),
    );
    const operation = this.persistenceTail.then(() =>
      this.performPersistRequest(request, generation),
    );
    this.persistenceTail = operation;
    await operation;
  }

  private async performPersistRequest(
    request: SelectionPersistRequest,
    generation: number | null,
  ): Promise<void> {
    const key = persistRequestKey(request.principalKey, request.documentKey);
    try {
      const currentContextUnknown =
        (this.loadPending || this.currentLoadError) &&
        request.principalKey === this.principal.principalId &&
        request.documentKey === this.documentKey;
      let comments = request.comments;
      if (request.mergeExisting) {
        comments = mergeStoredComments(
          await this.storage.load(request.principalKey, request.documentKey),
          request.comments,
        );
      } else if (currentContextUnknown) {
        // This request already contains the complete snapshot from an earlier successful load. The
        // read is still mandatory: after a current-context load failure, never replace storage until
        // it is reachable again. Re-merging a complete snapshot as if it were a pending-load delta
        // would duplicate every pre-existing thread and resurrect confirmed deletions.
        await this.storage.load(request.principalKey, request.documentKey);
      }
      if ((this.latestEnqueuedRevisions.get(key) ?? request.revision) > request.revision) {
        this.syncPersistenceStatus();
        this.notify();
        return;
      }
      await this.storage.save(request.principalKey, request.documentKey, comments);
      const failed = this.failedRequests.get(key);
      if (failed !== undefined && failed.revision <= request.revision) {
        this.failedRequests.delete(key);
      }
      if (generation !== null && generation === this.generation) {
        if (request.revision === this.mutationRevision) this.dirty = false;
      }
      this.syncPersistenceStatus();
      this.notify();
    } catch {
      const failed = this.failedRequests.get(key);
      if (failed === undefined || failed.revision <= request.revision) {
        this.failedRequests.set(key, request);
      }
      this.syncPersistenceStatus();
      this.notify();
    }
  }

  /** Map every source offset through the document's ordered multi-range diff. Diff construction is
   * O(lines log lines + characters); each comment maps in O(log runs + log lines), so neither a common
   * one-character quote nor multi-line formatting causes per-comment document scans. */
  reanchor(markdown: string): void {
    const previous = this.markdown;
    this.markdown = markdown;
    if (previous === markdown) return;
    if (!this.mutationsAllowed()) return;
    const comments = this.comments;
    if (comments.length === 0 && this.draft?.documentKey !== this.documentKey) return;
    const sourceIndex = new DocumentAnchorIndex(markdown, this.diagnostics);
    const runs = unchangedRuns(previous, markdown);
    const mapOffset = (offset: number, assoc: -1 | 1): number => {
      const clamped = Math.max(0, Math.min(previous.length, offset));
      let low = 0;
      let high = runs.length;
      while (low < high) {
        const middle = (low + high) >>> 1;
        if ((runs[middle]?.oldStart ?? Number.POSITIVE_INFINITY) <= clamped) low = middle + 1;
        else high = middle;
      }
      const index = low - 1;
      const run = runs[index];
      if (run !== undefined && clamped === run.oldStart && assoc < 0) {
        const prior = runs[index - 1];
        if (prior?.oldEnd === clamped) return prior.newEnd;
      }
      if (run !== undefined && clamped >= run.oldStart && clamped <= run.oldEnd) {
        if (clamped < run.oldEnd || assoc < 0) {
          return run.newStart + (clamped - run.oldStart);
        }
        const following = runs[index + 1];
        if (following?.oldStart === clamped) return following.newStart;
        return run.newEnd;
      }
      const following = runs[index + 1];
      if (assoc > 0 && following !== undefined) return following.newStart;
      if (run !== undefined) return assoc < 0 ? run.newEnd : markdown.length;
      if (following !== undefined) return assoc < 0 ? 0 : following.newStart;
      // No unchanged anchor exists (a complete replacement). Preserve the conventional association to
      // the replacement's left/right boundary without guessing from comment text.
      return assoc < 0 ? 0 : markdown.length;
    };
    for (let i = 0; i < comments.length; i++) {
      const comment = comments[i];
      if (comment === undefined) continue;
      if (comment.anchorState === "detached" && comment.detachedAnchor !== undefined) {
        const restored = restoreAnchor(comment.detachedAnchor, sourceIndex, this.diagnostics);
        if (restored.state === "attached") {
          const { detachedAnchor: _, ...attached } = comment;
          comments[i] = { ...attached, ...restored.selection, anchorState: "attached" };
        } else {
          comments[i] = { ...comment, ...restored.selection };
        }
        continue;
      }
      const previousAnchor = storedAnchor(comment, previous);
      const fromOffset = mapOffset(comment.fromOffset, 1);
      const toOffset = Math.max(fromOffset, mapOffset(comment.toOffset, -1));
      if (toOffset <= fromOffset) {
        comments[i] = {
          ...comment,
          ...detachedSelection(previousAnchor),
          anchorState: "detached",
          detachedAnchor: previousAnchor,
        };
        continue;
      }
      let anchorOffset = Math.max(toOffset, mapOffset(comment.anchorOffset, -1));
      let anchorLine = sourceIndex.lineAt(anchorOffset);
      if (comment.anchorKind === "table") {
        const mappedToLine = sourceIndex.lineAt(Math.max(fromOffset, toOffset - 1));
        const table = sourceIndex.blocks.find(
          (block) =>
            block.containerKind === "table" &&
            block.lineStart <= mappedToLine &&
            mappedToLine < (block.contentLineEnd ?? block.lineEnd + 1),
        );
        if (table !== undefined) {
          anchorLine = (table.contentLineEnd ?? table.lineEnd + 1) - 1;
          anchorOffset = sourceIndex.lineEndOffset(anchorLine);
        }
      }
      comments[i] = {
        ...comment,
        fromOffset,
        toOffset,
        anchorOffset,
        fromLine: sourceIndex.lineAt(fromOffset),
        toLine: sourceIndex.lineAt(Math.max(fromOffset, toOffset - 1)),
        anchorLine,
        quote: markdown.slice(fromOffset, toOffset),
      };
    }
    if (this.draft !== null && this.draft.documentKey === this.documentKey) {
      const anchor = comments.find((comment) => comment.id === this.draft?.commentId);
      if (anchor !== undefined) this.draft = { ...this.draft, ...anchor };
      else if (this.draft.mode === "create") {
        const mapped = sourceIndex.selection(
          mapOffset(this.draft.fromOffset, 1),
          mapOffset(this.draft.toOffset, -1),
        );
        this.draft =
          mapped === null
            ? null
            : {
                ...mapped,
                documentKey: this.documentKey,
                surface: this.draft.surface,
                mode: "create",
                initialBody: this.draft.initialBody,
              };
      }
    }
    this.mutationRevision++;
    this.queuePersist(500);
  }

  private commentsAvailable(): boolean {
    return this.storageAvailable() && !this.closeInProgress;
  }

  private mutationsAllowed(): boolean {
    return !this.currentLoadError && !this.closeInProgress;
  }

  private storageAvailable(): boolean {
    return !this.loadPending && !this.currentLoadError;
  }
}

function numericId(id: string): number {
  const match = /-(\d+)$/.exec(id);
  return match === null ? 0 : Number(match[1]);
}
