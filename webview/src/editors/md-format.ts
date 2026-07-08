/**
 * Pure Markdown text transforms for the formatting toolbar in source (Code/Split) mode — wrap a
 * selection in inline markers, or toggle a line prefix / fence on the selected lines. Pure functions
 * over (doc, from, to) returning a single replacement edit + the resulting selection, so they are
 * unit-tested directly and the CodeMirror glue (editor.ts) just dispatches the edit. The formatted
 * (WYSIWYG) view uses ProseMirror commands instead (formatted.ts) — this module is the source side.
 *
 * Inline wrapping keeps the emitted Markdown VALID: edge whitespace is expelled from the markers
 * (`**word **` breaks CommonMark's flanking rule), a selection crossing a blank line is wrapped per
 * paragraph (emphasis cannot span a paragraph break), and an existing wrapper the selection sits
 * inside is detected by RE-PARSING the document text with the lang-markdown parser
 * (`markdownLanguage.parser.parse`) — so a partial selection inside `**foo bar**` toggles the bold
 * off instead of nesting to the non-rendering `****foo** bar**`. That parse is the only added
 * machinery; the module stays a pure function of (doc, from, to) with no live EditorView / editor.ts.
 */

import { markdownLanguage } from "@codemirror/lang-markdown";

/** The formatting commands the toolbar issues (shared by both editor surfaces). */
export type FormatCommand =
  | "bold"
  | "italic"
  | "strike"
  | "h1"
  | "h2"
  | "bullet"
  | "ordered"
  | "quote"
  | "code";

const FORMAT_COMMANDS: readonly FormatCommand[] = [
  "bold",
  "italic",
  "strike",
  "h1",
  "h2",
  "bullet",
  "ordered",
  "quote",
  "code",
];

/** Validate a `data-format` attribute (DOM boundary) into a {@link FormatCommand}; false for anything else. */
export function isFormatCommand(value: string | undefined): value is FormatCommand {
  return value !== undefined && FORMAT_COMMANDS.some((command) => command === value);
}

/** A single document edit plus the selection to set afterwards (offsets in the post-edit document). */
export interface FormatEdit {
  from: number;
  to: number;
  insert: string;
  selectionStart: number;
  selectionEnd: number;
}

const INLINE_MARKERS: Partial<Record<FormatCommand, string>> = {
  bold: "**",
  italic: "*",
  strike: "~~",
};

/** The lang-markdown syntax-tree node each inline marker forms when it is valid CommonMark. */
const MARKER_NODE: Record<string, string> = {
  "*": "Emphasis",
  "**": "StrongEmphasis",
  "~~": "Strikethrough",
};

/** A lang-markdown syntax node, derived from the parser so this module needs no direct @lezer/common dep. */
type MdNode = ReturnType<ReturnType<typeof markdownLanguage.parser.parse>["resolveInner"]>;

// A CommonMark paragraph break: a blank line (a newline, optional spaces/tabs, then another newline).
const PARAGRAPH_BREAK = /\n[ \t]*\n/;
const PARAGRAPH_BREAK_SPLIT = /(\n[ \t]*\n)/;

/** Compute the toolbar edit for a command over the selection [from, to) in `doc`. */
export function formatMarkdown(
  doc: string,
  from: number,
  to: number,
  command: FormatCommand,
): FormatEdit {
  const marker = INLINE_MARKERS[command];
  return marker !== undefined
    ? toggleInline(doc, from, to, marker)
    : toggleBlock(doc, from, to, command);
}

/**
 * Wrap (or unwrap) the selection with an inline marker like `**` / `*` / `~~`, keeping the emitted
 * Markdown valid:
 *  - a selection that sits inside (or exactly spans) an existing wrapper of the same kind — detected
 *    on the parsed syntax tree, so a PARTIAL selection inside `**foo bar**` toggles the whole bold off
 *    rather than nesting to the non-rendering `****foo** bar**` — is unwrapped;
 *  - otherwise the selection is wrapped, with the markers pushed INSIDE any edge whitespace (a leading
 *    or trailing space breaks CommonMark's flanking rule, so `**word **` would stay literal) and a
 *    selection crossing a blank line wrapped per paragraph (emphasis cannot span a paragraph break).
 */
function toggleInline(doc: string, from: number, to: number, marker: string): FormatEdit {
  const nodeName = MARKER_NODE[marker];
  if (nodeName !== undefined && to > from) {
    const node = enclosingWrap(doc, from, to, nodeName);
    const unwrapped = node !== null ? unwrapNode(doc, from, to, node) : null;
    if (unwrapped !== null) {
      return unwrapped;
    }
  }
  return wrapSelection(doc, from, to, marker);
}

/**
 * The innermost syntax node of type `nodeName` (Emphasis / StrongEmphasis / Strikethrough) that
 * fully covers the selection [from, to), or null. Re-parses `doc` with the lang-markdown parser, so
 * the test is against the real grammar (only VALID CommonMark emphasis becomes a node) rather than
 * raw marker characters sitting next to the selection.
 */
function enclosingWrap(doc: string, from: number, to: number, nodeName: string): MdNode | null {
  const tree = markdownLanguage.parser.parse(doc);
  for (let node: MdNode | null = tree.resolveInner(from, 1); node !== null; node = node.parent) {
    if (node.name === nodeName && node.from <= from && node.to >= to) {
      return node;
    }
  }
  return null;
}

/**
 * Unwrap `node`: drop its opening and closing marker children, keep the inner text, and map the
 * selection onto that text (a selection that had included the markers collapses onto the content).
 * Returns null if the node isn't shaped like a wrapper (defensive — every emphasis node is).
 */
function unwrapNode(doc: string, from: number, to: number, node: MdNode): FormatEdit | null {
  const open = node.firstChild;
  const close = node.lastChild;
  if (open === null || close === null || open.from === close.from) {
    return null;
  }
  const inner = doc.slice(open.to, close.from);
  const openLen = open.to - open.from;
  // A content position `p` moves to `p - openLen` once the opening marker is gone; clamp into the
  // unwrapped text so markers included in the selection fold onto the content edges.
  const clamp = (p: number): number =>
    Math.min(Math.max(p - openLen, node.from), node.from + inner.length);
  return {
    from: node.from,
    to: node.to,
    insert: inner,
    selectionStart: clamp(from),
    selectionEnd: clamp(to),
  };
}

/** Wrap the selection: empty markers for an empty selection, per-paragraph across a blank line,
 *  otherwise a single pair with the markers pushed inside any edge whitespace. */
function wrapSelection(doc: string, from: number, to: number, marker: string): FormatEdit {
  const len = marker.length;
  const selected = doc.slice(from, to);

  // Empty selection → empty markers with the caret between them.
  if (selected.length === 0) {
    return {
      from,
      to,
      insert: marker + marker,
      selectionStart: from + len,
      selectionEnd: from + len,
    };
  }

  // Crosses a blank line → wrap each paragraph's content on its own so no emphasis spans the break.
  // Splitting on the capturing regex keeps the separators (odd indices) verbatim between the wraps.
  if (PARAGRAPH_BREAK.test(selected)) {
    const insert = selected
      .split(PARAGRAPH_BREAK_SPLIT)
      .map((part, index) => (index % 2 === 0 ? wrapChunk(part, marker) : part))
      .join("");
    return { from, to, insert, selectionStart: from, selectionEnd: from + insert.length };
  }

  const lead = selected.length - selected.trimStart().length;
  const trail = selected.length - selected.trimEnd().length;
  const core = selected.slice(lead, selected.length - trail);
  // An all-whitespace selection can't become valid emphasis (`** **`) → leave the document unchanged.
  if (core.length === 0) {
    return { from: to, to, insert: "", selectionStart: from, selectionEnd: to };
  }
  const insert =
    selected.slice(0, lead) + marker + core + marker + selected.slice(selected.length - trail);
  const innerStart = from + lead + len;
  return { from, to, insert, selectionStart: innerStart, selectionEnd: innerStart + core.length };
}

/** Wrap one paragraph chunk, pushing the markers inside any edge whitespace; empty core → unchanged. */
function wrapChunk(chunk: string, marker: string): string {
  const lead = chunk.length - chunk.trimStart().length;
  const trail = chunk.length - chunk.trimEnd().length;
  const core = chunk.slice(lead, chunk.length - trail);
  if (core.length === 0) {
    return chunk;
  }
  return chunk.slice(0, lead) + marker + core + marker + chunk.slice(chunk.length - trail);
}

/** Toggle a line-level construct (heading / list / quote / fenced code) over the selected lines. */
function toggleBlock(doc: string, from: number, to: number, command: FormatCommand): FormatEdit {
  // `doc.lastIndexOf("\n", from - 1)` is the standard "find the start of the current line" idiom, but
  // at `from === 0` the search position `-1` is clamped by the platform to `0` instead of "before the
  // string" — so if `doc[0]` is itself a newline (a leading blank line), it wrongly matches THAT
  // newline and returns 0, giving `blockStart = 1` (one character INTO the document, past its very
  // first byte) instead of 0. Guarding `from === 0` directly (the caret can't be preceded by a line
  // start earlier than the document start) sidesteps the clamping and keeps `blockStart <= blockEnd`.
  const blockStart = from === 0 ? 0 : doc.lastIndexOf("\n", from - 1) + 1;
  // If the selection ends exactly at a line break, don't pull in the following (empty-prefix) line.
  const endRef = to > from && doc[to - 1] === "\n" ? to - 1 : to;
  const newlineAfter = doc.indexOf("\n", endRef);
  const blockEnd = newlineAfter === -1 ? doc.length : newlineAfter;
  const lines = doc.slice(blockStart, blockEnd).split("\n");

  const insert = command === "code" ? toggleFence(lines) : toggleLinePrefix(lines, command);
  return {
    from: blockStart,
    to: blockEnd,
    insert,
    selectionStart: blockStart,
    selectionEnd: blockStart + insert.length,
  };
}

const HEADING_RE = /^#{1,6} +/;
const BULLET_RE = /^[-*+] +/;
const ORDERED_RE = /^\d+\. +/;
const QUOTE_RE = /^> ?/;

/** Add the command's line prefix to every line, or strip it if every (non-blank) line already has it. */
function toggleLinePrefix(lines: string[], command: FormatCommand): string {
  const nonBlank = lines.filter((line) => line.trim() !== "");
  const has = (re: RegExp): boolean =>
    nonBlank.length > 0 && nonBlank.every((line) => re.test(line));

  switch (command) {
    case "h1":
    case "h2": {
      const prefix = command === "h1" ? "# " : "## ";
      const allAtLevel = has(HEADING_RE) && nonBlank.every((line) => line.startsWith(prefix));
      return lines
        .map((line) => {
          if (line.trim() === "") {
            return line;
          }
          const bare = line.replace(HEADING_RE, "");
          return allAtLevel ? bare : prefix + bare;
        })
        .join("\n");
    }
    case "bullet": {
      const allBulleted = has(BULLET_RE);
      return lines
        .map((line) => {
          if (line.trim() === "") {
            return line;
          }
          const bare = line.replace(BULLET_RE, "");
          return allBulleted ? bare : `- ${bare}`;
        })
        .join("\n");
    }
    case "ordered": {
      const numbered = has(ORDERED_RE);
      let n = 0;
      return lines
        .map((line) => {
          if (line.trim() === "") {
            return line;
          }
          n += 1;
          const bare = line.replace(ORDERED_RE, "");
          return numbered ? bare : `${n}. ${bare}`;
        })
        .join("\n");
    }
    default: {
      // quote
      const allQuoted = has(QUOTE_RE);
      return lines
        .map((line) => {
          if (line.trim() === "") {
            return line;
          }
          const bare = line.replace(QUOTE_RE, "");
          return allQuoted ? bare : `> ${bare}`;
        })
        .join("\n");
    }
  }
}

/** Wrap the lines in a ``` fence, or unwrap if the selection already is a fenced block. */
function toggleFence(lines: string[]): string {
  const first = lines[0] ?? "";
  const last = lines[lines.length - 1] ?? "";
  if (lines.length >= 2 && first.startsWith("```") && last.startsWith("```")) {
    return lines.slice(1, -1).join("\n");
  }
  return ["```", ...lines, "```"].join("\n");
}
