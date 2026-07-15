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

/** Session-local annotation. Persistence will be added with the document-comment host contract. */
export interface SelectionComment extends SourceSelection {
  readonly id: string;
  readonly body: string;
  readonly createdAt: string;
}

/** Stable in-session identity. Local paths are absolute (clone-safe); branch keeps the same path in
 * different working lines isolated. Remote callers additionally supply the repository id. */
export function selectionDocumentKey(path: string, repository?: string, branch?: string): string {
  return [repository ?? "local", branch ?? "unversioned", path].join("\u0000");
}

/** Keep the visual anchor on the last selected line. A table is the one exception: placing a block
 * widget between its source rows would split the Markdown construct, so it anchors after the table. */
export function sourceSelection(
  markdown: string,
  fromOffset: number,
  toOffset: number,
): SourceSelection | null {
  const from = Math.max(0, Math.min(fromOffset, toOffset, markdown.length));
  const to = Math.max(0, Math.min(Math.max(fromOffset, toOffset), markdown.length));
  if (from === to) return null;

  const lineAt = (offset: number): number => markdown.slice(0, offset).split("\n").length - 1;
  const fromLine = lineAt(from);
  // A selection ending exactly at a line start belongs to the preceding line.
  const toLine = lineAt(Math.max(from, to - 1));
  const blocks = splitTopLevelBlocks(markdown);
  const last = [...blocks].reverse().find((block) => block.lineStart <= toLine);
  const anchorLine =
    last?.containerKind === "table"
      ? Math.max(toLine, (last.contentLineEnd ?? last.lineEnd + 1) - 1)
      : toLine;
  const lines = markdown.split("\n");
  let anchorOffset = 0;
  for (let line = 0; line <= anchorLine && line < lines.length; line++) {
    anchorOffset += lines[line]?.length ?? 0;
    if (line < anchorLine) anchorOffset += 1;
  }
  return {
    fromLine,
    toLine,
    anchorLine,
    anchorKind: last?.containerKind === "table" ? "table" : "line",
    fromOffset: from,
    toOffset: to,
    anchorOffset,
    quote: markdown.slice(from, to),
  };
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

export class SelectionCommentSession {
  private readonly documents = new Map<string, SelectionComment[]>();
  private sequence = 0;
  private documentKey = "";
  private readonly texts = new Map<string, string>();

  setDocument(key: string, markdown?: string): void {
    this.documentKey = key;
    if (markdown !== undefined) this.texts.set(key, markdown);
  }

  private current(): SelectionComment[] {
    let comments = this.documents.get(this.documentKey);
    if (comments === undefined) {
      comments = [];
      this.documents.set(this.documentKey, comments);
    }
    return comments;
  }

  add(selection: SourceSelection, body: string): SelectionComment | null {
    const trimmed = body.trim();
    if (trimmed.length === 0) return null;
    const comment: SelectionComment = {
      ...selection,
      id: `selection-comment-${++this.sequence}`,
      body: trimmed,
      createdAt: new Date().toISOString(),
    };
    this.current().push(comment);
    return comment;
  }

  all(): readonly SelectionComment[] {
    return this.current();
  }

  /** Map every source offset through the document's ordered multi-range diff. Diff construction is
   * O(lines log lines + characters); each comment maps in O(log runs + log lines), so neither a common
   * one-character quote nor multi-line formatting causes per-comment document scans. */
  reanchor(markdown: string): void {
    const previous = this.texts.get(this.documentKey);
    this.texts.set(this.documentKey, markdown);
    if (previous === undefined || previous === markdown) return;
    const comments = this.current();
    if (comments.length === 0) return;
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
    const lineStarts = [0];
    for (let offset = 0; offset < markdown.length; offset++) {
      if (markdown.charCodeAt(offset) === 10) lineStarts.push(offset + 1);
    }
    const lineAt = (offset: number): number => {
      let low = 0;
      let high = lineStarts.length;
      while (low < high) {
        const middle = (low + high) >>> 1;
        if ((lineStarts[middle] ?? Number.POSITIVE_INFINITY) <= offset) low = middle + 1;
        else high = middle;
      }
      return Math.max(0, low - 1);
    };
    const tableBlocks = comments.some((comment) => comment.anchorKind === "table")
      ? splitTopLevelBlocks(markdown)
      : [];
    const lineEndOffset = (line: number): number => {
      const nextStart = lineStarts[line + 1];
      return nextStart === undefined ? markdown.length : Math.max(0, nextStart - 1);
    };
    for (let i = 0; i < comments.length; i++) {
      const comment = comments[i];
      if (comment === undefined) continue;
      const fromOffset = mapOffset(comment.fromOffset, 1);
      const toOffset = Math.max(fromOffset, mapOffset(comment.toOffset, -1));
      let anchorOffset = Math.max(toOffset, mapOffset(comment.anchorOffset, -1));
      let anchorLine = lineAt(anchorOffset);
      if (comment.anchorKind === "table") {
        const mappedToLine = lineAt(Math.max(fromOffset, toOffset - 1));
        const table = tableBlocks.find(
          (block) =>
            block.containerKind === "table" &&
            block.lineStart <= mappedToLine &&
            mappedToLine < (block.contentLineEnd ?? block.lineEnd + 1),
        );
        if (table !== undefined) {
          anchorLine = (table.contentLineEnd ?? table.lineEnd + 1) - 1;
          anchorOffset = lineEndOffset(anchorLine);
        }
      }
      comments[i] = {
        ...comment,
        fromOffset,
        toOffset,
        anchorOffset,
        fromLine: lineAt(fromOffset),
        toLine: lineAt(Math.max(fromOffset, toOffset - 1)),
        anchorLine,
        quote: markdown.slice(fromOffset, toOffset),
      };
    }
  }
}
