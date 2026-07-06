/**
 * Pure Markdown text transforms for the formatting toolbar in source (Code/Split) mode — wrap a
 * selection in inline markers, or toggle a line prefix / fence on the selected lines. Pure functions
 * over (doc, from, to) returning a single replacement edit + the resulting selection, so they are
 * unit-tested directly and the CodeMirror glue (editor.ts) just dispatches the edit. The formatted
 * (WYSIWYG) view uses ProseMirror commands instead (formatted.ts) — this module is the source side.
 */

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

/** Wrap (or unwrap) the selection with an inline marker like `**` / `*` / `~~`. */
function toggleInline(doc: string, from: number, to: number, marker: string): FormatEdit {
  const len = marker.length;
  const char = marker[0];
  const selected = doc.slice(from, to);

  // Markers sit just OUTSIDE the selection → unwrap them. The marker must be EXACT, not part of a
  // longer run of the same char, so an italic `*` toggle inside `**bold**` doesn't strip a bold
  // asterisk (it nests to `***…***` via the wrap branch instead).
  if (
    doc.slice(from - len, from) === marker &&
    doc.slice(to, to + len) === marker &&
    doc[from - len - 1] !== char &&
    doc[to + len] !== char
  ) {
    return {
      from: from - len,
      to: to + len,
      insert: selected,
      selectionStart: from - len,
      selectionEnd: to - len,
    };
  }

  // The selection itself includes the markers → unwrap the inner text (again, exact marker only).
  if (
    selected.length >= 2 * len &&
    selected.startsWith(marker) &&
    selected.endsWith(marker) &&
    selected[len] !== char &&
    selected[selected.length - len - 1] !== char
  ) {
    const inner = selected.slice(len, selected.length - len);
    return { from, to, insert: inner, selectionStart: from, selectionEnd: from + inner.length };
  }

  // Otherwise wrap. An empty selection leaves the caret between the markers.
  return {
    from,
    to,
    insert: marker + selected + marker,
    selectionStart: from + len,
    selectionEnd: from + len + selected.length,
  };
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
