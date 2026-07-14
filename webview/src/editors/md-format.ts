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
 * off instead of nesting to the non-rendering `****foo** bar**`. Fenced code toggling reuses the same
 * re-parse to find the enclosing FencedCode node (``` or ~~~, with or without an info string) rather
 * than text-matching the selection's own first/last lines, and a NEW fence's marker is always longer
 * than any backtick run already in the content, so wrapping can never nest into (and prematurely
 * close on) a fence the selection happens to contain. That parse is the only added machinery; the
 * module stays a pure function of (doc, from, to) with no live EditorView / editor.ts.
 */

import { markdownLanguage } from "@codemirror/lang-markdown";
import { assertNever } from "../util/assert.js";
import { trace } from "../util/trace.js";
import { type FormatCommand, type FormatKind, formatDef } from "./format-registry.js";

export type { FormatCommand } from "./format-registry.js";
// The FormatCommand union and the DOM-boundary guard both live in the single command registry
// (format-registry.ts, the source of truth); re-exported here so the source editor's long-standing
// public surface (editor.ts, format-toolbar.ts, the tests) keeps importing them from md-format unchanged.
export { isFormatCommand } from "./format-registry.js";

/** A single document edit plus the selection to set afterwards (offsets in the post-edit document). */
export interface FormatEdit {
  from: number;
  to: number;
  insert: string;
  selectionStart: number;
  selectionEnd: number;
}

/** The block kinds that toggle a per-line prefix (heading / list / quote), as opposed to the
 *  fenced-code wrap — the subset {@link toggleLinePrefix} handles. */
type LinePrefixKind = Extract<FormatKind, { type: "heading" | "list" | "quote" }>;

/** A lang-markdown syntax node, derived from the parser so this module needs no direct @lezer/common dep. */
type MdNode = ReturnType<ReturnType<typeof markdownLanguage.parser.parse>["resolveInner"]>;

// A CommonMark paragraph break: a blank line (a newline, optional spaces/tabs, then another newline).
const PARAGRAPH_BREAK = /\n[ \t]*\n/;
const PARAGRAPH_BREAK_SPLIT = /(\n[ \t]*\n)/;

/**
 * Compute the toolbar edit for a command over the selection [from, to) in `doc`, dispatching on the
 * command's {@link FormatKind} from the registry. The `default: assertNever(kind)` makes this exhaustive:
 * a new command whose `kind.type` this switch doesn't handle fails to compile.
 */
export function formatMarkdown(
  doc: string,
  from: number,
  to: number,
  command: FormatCommand,
): FormatEdit {
  const { kind } = formatDef(command);
  switch (kind.type) {
    case "inline":
      return kind.mark === "code"
        ? toggleInlineCode(doc, from, to)
        : toggleInline(doc, from, to, kind.marker, kind.node);
    case "fence":
      return toggleFence(doc, from, to);
    case "heading":
    case "list":
    case "quote":
      return toggleBlockPrefix(doc, from, to, kind);
    case "link":
      return insertLink(doc, from, to);
    case "image":
      return insertImage(doc, from, to);
    case "table":
      return insertTable(doc, from, to);
    case "rule":
      return insertRule(doc, from, to);
    default:
      return assertNever(kind);
  }
}

/** Insert an editable Markdown link. The selected words become the label; the URL placeholder is selected
 *  afterwards so the author can paste the destination immediately. */
function insertLink(doc: string, from: number, to: number): FormatEdit {
  const existing = enclosingNode(doc, from, to, "Link");
  if (existing !== null) {
    const open = existing.firstChild;
    const closeLabel = open?.nextSibling;
    if (open !== null && open !== undefined && closeLabel !== null && closeLabel !== undefined) {
      const label = doc.slice(open.to, closeLabel.from);
      const labelStart = existing.from;
      return {
        from: existing.from,
        to: existing.to,
        insert: label,
        selectionStart: labelStart,
        selectionEnd: labelStart + label.length,
      };
    }
  }
  const label = escapeLabel(doc.slice(from, to) || "link text");
  const target = "https://";
  const insert = `[${label}](${target})`;
  const targetStart = from + label.length + 3;
  return {
    from,
    to,
    insert,
    selectionStart: targetStart,
    selectionEnd: targetStart + target.length,
  };
}

/** Toggle an inline code span, choosing a delimiter longer than every backtick run in the selection. */
function toggleInlineCode(doc: string, from: number, to: number): FormatEdit {
  if (to > from) {
    const existing = enclosingNode(doc, from, to, "InlineCode");
    const unwrapped = existing !== null ? unwrapNode(doc, from, to, existing) : null;
    if (unwrapped !== null) {
      return unwrapped;
    }
  }
  const selected = doc.slice(from, to);
  const longestRun = Math.max(
    0,
    ...Array.from(selected.matchAll(/`+/g), (match) => match[0].length),
  );
  const marker = "`".repeat(longestRun + 1);
  const pad = selected.includes("`") || (/^\s/.test(selected) && /\s$/.test(selected)) ? " " : "";
  const insert = marker + pad + selected + pad + marker;
  const innerStart = from + marker.length + pad.length;
  return {
    from,
    to,
    insert,
    selectionStart: innerStart,
    selectionEnd: innerStart + selected.length,
  };
}

/** Insert an editable image reference. Image bytes can still be pasted/dropped through the repository
 *  image pipeline; this command covers an existing repository or web image by selecting its path. */
function insertImage(doc: string, from: number, to: number): FormatEdit {
  const alt = escapeLabel(doc.slice(from, to) || "Image");
  const target = "images/image.png";
  const insert = `![${alt}](${target})`;
  const targetStart = from + alt.length + 4;
  return {
    from,
    to,
    insert,
    selectionStart: targetStart,
    selectionEnd: targetStart + target.length,
  };
}

/** Insert a small starter table before the current block; the first heading is selected for immediate
 *  replacement. Structural row/column changes remain simplest in Code mode. */
function insertTable(doc: string, from: number, to: number): FormatEdit {
  const [blockStart] = blockLineRange(doc, from, to);
  const insert = "| Column 1 | Column 2 |\n| --- | --- |\n| Value | Value |\n\n";
  return {
    from: blockStart,
    to: blockStart,
    insert,
    selectionStart: blockStart + 2,
    selectionEnd: blockStart + 10,
  };
}

/** Insert a thematic break as its own block without consuming adjacent text. */
function insertRule(doc: string, from: number, to: number): FormatEdit {
  const [blockStart] = blockLineRange(doc, from, to);
  const insert = "---\n\n";
  const caret = blockStart + insert.length;
  return {
    from: blockStart,
    to: blockStart,
    insert,
    selectionStart: caret,
    selectionEnd: caret,
  };
}

function escapeLabel(value: string): string {
  return value.replace(/([\\\]])/g, "\\$1");
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
function toggleInline(
  doc: string,
  from: number,
  to: number,
  marker: string,
  nodeName: string,
): FormatEdit {
  if (to > from) {
    const node = enclosingWrap(doc, from, to, nodeName);
    const unwrapped = node !== null ? unwrapNode(doc, from, to, node) : null;
    if (unwrapped !== null) {
      trace("format", "format.inline", { kind: nodeName, decision: "unwrap-node", from, to });
      return unwrapped;
    }
  }
  trace("format", "format.inline", { kind: nodeName, decision: "wrap", from, to });
  return wrapSelection(doc, from, to, marker);
}

/**
 * The innermost syntax node of type `nodeName` (Emphasis / StrongEmphasis / Strikethrough) that
 * fully covers the selection [from, to), or null. Re-parses `doc` with the lang-markdown parser, so
 * the test is against the real grammar (only VALID CommonMark emphasis becomes a node) rather than
 * raw marker characters sitting next to the selection.
 */
function enclosingWrap(doc: string, from: number, to: number, nodeName: string): MdNode | null {
  return enclosingNode(doc, from, to, nodeName);
}

function enclosingNode(doc: string, from: number, to: number, nodeName: string): MdNode | null {
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

/**
 * The [start, end) offsets of the whole lines the selection [from, to) touches — the line-level unit the
 * block commands (heading / list / quote / fenced code) operate over, extending the selection out to the
 * enclosing line boundaries on each side.
 */
function blockLineRange(doc: string, from: number, to: number): [number, number] {
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
  return [blockStart, blockEnd];
}

/** Build the edit that replaces the block-line range with `insert`, selecting the whole result. */
function blockEdit(blockStart: number, blockEnd: number, insert: string): FormatEdit {
  return {
    from: blockStart,
    to: blockEnd,
    insert,
    selectionStart: blockStart,
    selectionEnd: blockStart + insert.length,
  };
}

/**
 * Toggle a fenced code block. Toggles OFF by unwrapping the enclosing FencedCode syntax node (see
 * {@link unwrapFence}) rather than by re-deriving a line range from the raw selection — a selection that
 * sits anywhere inside an existing fence (not just spanning its outer ``` lines) must unwrap; otherwise
 * wraps the selection's whole lines in a fresh fence, stripping any ATX heading marker each wrapped
 * line carries first: a `# heading` line the selection touches is its own standalone Heading block in
 * the source grammar (never mere text that happens to start with `#`), and a fenced code_block's content
 * is plain text with no block markup of its own — matching the PM tract's `setBlockType(code_block)`,
 * which takes the heading node's inline text and necessarily drops its ATX marker (the node has none;
 * `#` is a serialization detail of the heading node, not part of its text content).
 */
function toggleFence(doc: string, from: number, to: number): FormatEdit {
  const unwrapped = unwrapFence(doc, from, to);
  if (unwrapped !== null) {
    trace("format", "format.fence", { decision: "unwrap" });
    return unwrapped;
  }
  const [blockStart, blockEnd] = blockLineRange(doc, from, to);
  const lines = doc
    .slice(blockStart, blockEnd)
    .split("\n")
    .map((line) => line.replace(HEADING_RE, ""));
  trace("format", "format.fence", { decision: "wrap" });
  return blockEdit(blockStart, blockEnd, wrapFence(lines));
}

/**
 * Toggle a per-line prefix construct (heading / list / quote) over the selection's whole lines. Heading
 * gets one extra step first: when the selection's line range is entirely inside a single multi-line
 * Paragraph syntax node — several PHYSICAL lines joined by CommonMark soft breaks into one LOGICAL
 * block, e.g. a manually word-wrapped paragraph — the lines are collapsed to one logical line (soft
 * breaks become spaces, mirroring how the parsed Markdown treats them) before the heading prefix is
 * applied. An ATX heading occupies exactly the line it starts on (no lazy continuation), so prefixing
 * every physical line individually would fragment one paragraph into N one-line headings instead of
 * producing the single heading the selection's logical content represents.
 */
function toggleBlockPrefix(
  doc: string,
  from: number,
  to: number,
  kind: LinePrefixKind,
): FormatEdit {
  const [blockStart, blockEnd] = blockLineRange(doc, from, to);
  const raw = doc.slice(blockStart, blockEnd);
  if (
    kind.type === "heading" &&
    raw.includes("\n") &&
    inSingleParagraph(doc, blockStart, blockEnd)
  ) {
    const joined = raw
      .split("\n")
      .map((line) => line.trim())
      .filter((line) => line.length > 0)
      .join(" ");
    trace("format", "format.blockPrefix", { kind: kind.type, joinedHeading: true, lineCount: 1 });
    return blockEdit(blockStart, blockEnd, toggleLinePrefix([joined], kind));
  }
  const blockLines = raw.split("\n");
  trace("format", "format.blockPrefix", {
    kind: kind.type,
    joinedHeading: false,
    lineCount: blockLines.length,
  });
  return blockEdit(blockStart, blockEnd, toggleLinePrefix(blockLines, kind));
}

/** Whether [start, end) sits entirely inside a single (necessarily multi-line, since a one-line span
 *  can't straddle anything) Paragraph syntax node — the {@link toggleBlockPrefix} heading join guard. */
function inSingleParagraph(doc: string, start: number, end: number): boolean {
  const tree = markdownLanguage.parser.parse(doc);
  for (let node: MdNode | null = tree.resolveInner(start, 1); node !== null; node = node.parent) {
    if (node.name === "Paragraph") {
      return node.from <= start && node.to >= end;
    }
  }
  return false;
}

const HEADING_RE = /^#{1,6} +/;
const BULLET_RE = /^[-*+] +/;
const ORDERED_RE = /^\d+\. +/;
const QUOTE_RE = /^> ?/;

/**
 * A line's leading CONTAINER markers — an optional blockquote marker (outermost) and, nested inside it,
 * an optional list marker (bullet or ordered) — and the bare content after them. Mirrors how these two
 * kinds nest as ProseMirror ancestors (a blockquote can wrap a list, per CommonMark): the heading command
 * peels both off, applies its own prefix logic to the bare `rest`, and reattaches `prefix` UNCHANGED
 * around the result, so `# ` on a list line inside a quote lands as `> - # item` rather than jumbling the
 * markers' relative order (`# > - item`) or losing the container altogether.
 */
function splitContainers(line: string): { prefix: string; rest: string } {
  const quote = QUOTE_RE.exec(line);
  const afterQuote = quote !== null ? line.slice(quote[0].length) : line;
  const quotePrefix = quote !== null ? quote[0] : "";
  const list = BULLET_RE.exec(afterQuote) ?? ORDERED_RE.exec(afterQuote);
  return list !== null
    ? { prefix: quotePrefix + list[0], rest: afterQuote.slice(list[0].length) }
    : { prefix: quotePrefix, rest: afterQuote };
}

/** Strip a line's existing list marker (bullet OR ordered, whichever is present — a line can only
 *  carry one) — the {@link toggleLinePrefix} list case's shared "convert the other kind in place"
 *  step, mirroring the PM tract's `toggleList` (which converts a list of the other type rather than
 *  nesting a new one inside it). */
function stripListMarker(line: string): string {
  return line.replace(BULLET_RE, "").replace(ORDERED_RE, "");
}

/**
 * Add the kind's line prefix to every line, or strip it if every (non-blank) line already has it. The
 * `default: assertNever(kind)` keeps the switch exhaustive against {@link LinePrefixKind}.
 */
function toggleLinePrefix(lines: string[], kind: LinePrefixKind): string {
  const nonBlank = lines.filter((line) => line.trim() !== "");
  const has = (re: RegExp): boolean =>
    nonBlank.length > 0 && nonBlank.every((line) => re.test(line));

  switch (kind.type) {
    case "heading": {
      const prefix = `${"#".repeat(kind.level)} `;
      const parts = lines.map((line) => (line.trim() === "" ? null : splitContainers(line)));
      const nonBlankRest = parts.filter((part) => part !== null).map((part) => part.rest);
      const allAtLevel =
        nonBlankRest.length > 0 && nonBlankRest.every((rest) => rest.startsWith(prefix));
      return lines
        .map((line, i) => {
          const part = parts[i];
          if (part === undefined || part === null) {
            return line;
          }
          const bare = part.rest.replace(HEADING_RE, "");
          return part.prefix + (allAtLevel ? bare : prefix + bare);
        })
        .join("\n");
    }
    case "list": {
      if (kind.ordered) {
        const numbered = has(ORDERED_RE);
        let orderedDecision: "add" | "remove" | "convert";
        if (numbered) {
          orderedDecision = "remove";
        } else {
          orderedDecision = has(BULLET_RE) ? "convert" : "add";
        }
        trace("format", "format.linePrefix", {
          kind: "list",
          decision: orderedDecision,
          lineCount: nonBlank.length,
        });
        let n = 0;
        return lines
          .map((line) => {
            if (line.trim() === "") {
              return line;
            }
            n += 1;
            return numbered ? line.replace(ORDERED_RE, "") : `${n}. ${stripListMarker(line)}`;
          })
          .join("\n");
      }
      const allBulleted = has(BULLET_RE);
      let bulletDecision: "add" | "remove" | "convert";
      if (allBulleted) {
        bulletDecision = "remove";
      } else {
        bulletDecision = has(ORDERED_RE) ? "convert" : "add";
      }
      trace("format", "format.linePrefix", {
        kind: "list",
        decision: bulletDecision,
        lineCount: nonBlank.length,
      });
      return lines
        .map((line) => {
          if (line.trim() === "") {
            return line;
          }
          return allBulleted ? line.replace(BULLET_RE, "") : `- ${stripListMarker(line)}`;
        })
        .join("\n");
    }
    case "quote": {
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
    default:
      return assertNever(kind);
  }
}

/**
 * The innermost FencedCode syntax node (``` or ~~~, with or without an info string) that fully
 * covers the selection [from, to), or null. Re-parses `doc`, mirroring {@link enclosingWrap} — so a
 * selection anywhere INSIDE an existing fence (a middle line, a partial line, or the fence exactly)
 * is recognized by the real grammar, not by checking whether the selection's own first/last lines
 * happen to start with ``` (the old heuristic: a selection of interior lines only, which never
 * touches the fence's own delimiter lines, was invisible to it and got wrapped into a nested fence
 * whose inner ``` prematurely closed the outer one, corrupting everything after).
 */
function enclosingFence(doc: string, from: number, to: number): MdNode | null {
  const tree = markdownLanguage.parser.parse(doc);
  for (let node: MdNode | null = tree.resolveInner(from, 1); node !== null; node = node.parent) {
    if (node.name === "FencedCode" && node.from <= from && node.to >= to) {
      return node;
    }
  }
  return null;
}

/**
 * Unwrap the FencedCode node enclosing [from, to) (if any): drop its opening line (mark + optional
 * info string) and closing mark, keep the inner text, and map the selection onto that text — same
 * shape as {@link unwrapNode} for inline marks. Returns null when the selection isn't inside a fence
 * at all, so the caller falls through to wrapping a NEW fence around it.
 */
function unwrapFence(doc: string, from: number, to: number): FormatEdit | null {
  const node = enclosingFence(doc, from, to);
  if (node === null) {
    return null;
  }
  const openEnd = doc.indexOf("\n", node.from);
  const closeStart = openEnd === -1 ? node.from : doc.lastIndexOf("\n", node.to - 1);
  // A one-line fence (`` ``` `` immediately followed by `` ``` `` with nothing between, or a single
  // stray opening line) has no interior newline pair to split on — its content is empty.
  const inner = openEnd === -1 || closeStart <= openEnd ? "" : doc.slice(openEnd + 1, closeStart);
  const prefixLen = (openEnd === -1 ? node.to : openEnd + 1) - node.from;
  const clamp = (p: number): number =>
    Math.min(Math.max(p - prefixLen, node.from), node.from + inner.length);
  return {
    from: node.from,
    to: node.to,
    insert: inner,
    selectionStart: clamp(from),
    selectionEnd: clamp(to),
  };
}

/** The longest run of consecutive `char` characters anywhere in `text`. */
function longestRun(text: string, char: string): number {
  let max = 0;
  let current = 0;
  for (const c of text) {
    current = c === char ? current + 1 : 0;
    max = Math.max(max, current);
  }
  return max;
}

/**
 * Wrap the given lines in a ``` fence. The fence marker is at least one backtick LONGER than the
 * longest run of backticks anywhere in the content, so a selection that already contains a fenced
 * block (or any other run of backticks) can never be prematurely closed by content the outer fence is
 * meant to just pass through verbatim — CommonMark only treats a line of `n` fence characters as a
 * delimiter for a fence opened with `n` or fewer, so a strictly longer outer marker is safe.
 */
function wrapFence(lines: string[]): string {
  const marker = "`".repeat(Math.max(3, longestRun(lines.join("\n"), "`") + 1));
  return [marker, ...lines, marker].join("\n");
}
