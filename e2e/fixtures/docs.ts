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

/**
 * The screenshot scenario for T-111: a level-3 heading IMMEDIATELY followed (one blank line) by a GFM
 * table, near the top of the document — the exact shape the author reported Code-pane spacers going
 * missing for (`.work/Screenshot 2026-07-11 165557.png`). A short, deliberately NON-wrapping preamble
 * sits above it (so the heading is "near the top", not at the very first line, mirroring the
 * screenshot's line ~21, while keeping the fixture wrap-state-independent), the table has four body
 * rows so the "first rows drift, later rows realign" pattern is visible, and a long filler tail sits
 * below so each body row can also be scrolled to the pane top.
 *
 * Every table body row renders TALLER (cell padding) in the Formatted pane than its single-line source
 * row, so the row-by-row anchoring is meant to add a Code-pane spacer after each row. It does so for
 * the LATER rows — but the first one or two stay packed at rest, because the non-rendered delimiter row
 * (`| --- | --- |`), the blank line after the heading, and the heading's own formatted top-margin each
 * make the CODE pane taller than the Formatted pane right at the table's start (a "source intrinsically
 * taller" region), pushing those rows below their Formatted target where additive spacers cannot reach.
 */
const HEADING_TABLE = [
  "A short intro line before the table.",
  "",
  "### A table",
  "",
  "| Feature | Status |",
  "| --- | --- |",
  "| Editor | Working |",
  "| Live preview | Working |",
  "| Scroll-sync | Working |",
  "| Search | Working |",
  "",
];

export const HEADING_TABLE_DOC = [...HEADING_TABLE, ...FILLER].join("\n");

/** The exact source-line text and formatted-pane needles for the heading-then-table scenario, kept
 *  beside the fixture so a fixture edit and the assertions can't drift. Body rows only — the header row
 *  aligns to the heading/first-row boundary and the delimiter row renders no anchor. Ordered top→bottom. */
export const HEADING_TABLE_ROWS = [
  { label: "row-editor", needle: "Editor", srcLine: "| Editor | Working |" },
  { label: "row-live", needle: "Live preview", srcLine: "| Live preview | Working |" },
  { label: "row-scroll", needle: "Scroll-sync", srcLine: "| Scroll-sync | Working |" },
  { label: "row-search", needle: "Search", srcLine: "| Search | Working |" },
] as const;

/**
 * The container-tail floor scenario (T-112): the SAME heading-then-table shape, but with a long tight
 * list ABOVE it whose items render taller than their source lines — so the accumulated Code padding
 * (height-sync's running maximum) climbs well past anything the table's own rows require. The blank
 * lines and the delimiter row then sink every table row's requirement BELOW that maximum: without the
 * container-tail floor the whole table gets no spacer and its LAST row drifts against its rendered
 * counterpart inside one viewport (the author-reported defect). Same construction for the trailing
 * list, separated by enough blank lines (Code-only height) that its items sit below the maximum too.
 */
const PREFIXED_CONTAINERS = [
  "Intro before the prefix list.",
  "",
  ...Array.from({ length: 10 }, (_, i) => `- prefix item ${i + 1}`),
  "",
  "",
  "",
  "### A table",
  "",
  "| Feature | Status |",
  "| --- | --- |",
  "| Editor | Working |",
  "| Live preview | Working |",
  "| Scroll-sync | Working |",
  "| Search | Working |",
  "",
  "",
  "",
  "",
  "",
  "- tail item one",
  "- tail item two",
  "- tail item three",
  "",
];

export const PREFIXED_CONTAINERS_DOC = [...PREFIXED_CONTAINERS, ...FILLER].join("\n");

/** First/last anchors of the two containers the T-112 scenario asserts on (the table group starts at
 *  its header row; the list group at its first item), plus the LAST source line of each — the line the
 *  floor's spacer must sit directly above. */
export const PREFIXED_CONTAINERS_ANCHORS = {
  tableFirst: { label: "header", tag: "tr", needle: "FeatureStatus", srcLine: "| Feature | Status |" },
  tableLast: { label: "row-search", tag: "tr", needle: "Search", srcLine: "| Search | Working |" },
  listFirst: { label: "item-one", tag: "li", needle: "tail item one", srcLine: "- tail item one" },
  listLast: { label: "item-three", tag: "li", needle: "tail item three", srcLine: "- tail item three" },
} as const;
