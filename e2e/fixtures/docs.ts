/**
 * Rich geometry fixture for the Layer 1 real-Chromium scenarios. Mirrors the jsdom delivery gate's
 * FIXTURE (heading, short + tall paragraphs, a fenced code block, a table with a header + two body
 * rows, a list) plus a NESTED list item so indentation is testable against real rendering — and a
 * long tail of filler paragraphs so EVERY structured anchor can be scrolled to the pane top (with a
 * viewport of content below it), which is where per-anchor alignment is asserted.
 */
const STRUCTURED = [
  "# Heading One",
  "",
  "Short para.",
  "",
  "A much longer paragraph that should be considerably taller than the short one for sure indeed.",
  "",
  "```",
  "line1",
  "line2",
  "line3",
  "```",
  "",
  "| A | B |",
  "| - | - |",
  "| r1a | r1b |",
  "| r2a | r2b |",
  "",
  "- item one",
  "- item two",
  "    - sub one",
  "    - sub two",
  "- item three",
  "",
];

// Enough filler below the structured content that the table and the list can reach the pane top.
const FILLER = Array.from({ length: 30 }, (_, i) => [`Filler paragraph number ${i + 1}.`, ""]).flat();

export const GEOMETRY_DOC = [...STRUCTURED, ...FILLER].join("\n");

/** The exact source lines the indentation scenario matches against (kept beside the fixture so a
 *  fixture edit and the assertions can't drift). */
export const LIST_LINES = {
  parent: "- item two",
  nested: "    - sub one",
} as const;
