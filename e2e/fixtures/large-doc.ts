/**
 * Deterministic large-document generator for the Layer 1 interactivity budget scenario
 * (large-document.perf.e2e.ts). It is the TypeScript mirror of bench/SpecDesk.Bench/DocGenerator.fs —
 * the same section recipe (heading, intro paragraph, an eight-item bulleted list, a GFM table with a
 * header and twelve body rows, a ten-line fenced code block, an outro paragraph). The two generators are
 * deliberate mirrors so the .NET benchmarks and this e2e stress the same class of document; keep their
 * shapes in step when either changes.
 *
 * Deterministic by construction (no RNG, no clock): a given line target always yields the same document,
 * so a budget run is reproducible. Byte-for-byte parity with the F# side is NOT required (the two are
 * separate consumers in separate runtimes) — only the structural shape and size are.
 */

const LIST_ITEMS = 8;
const TABLE_BODY_ROWS = 12;
const CODE_LINES = 10;

// heading + blank + intro + blank + 8 list items + blank + table header + delimiter + 12 body rows +
// blank + code fence-open + 10 code lines + fence-close + blank + outro + blank.
const LINES_PER_SECTION =
  1 + 1 + 1 + 1 + LIST_ITEMS + 1 + 1 + 1 + TABLE_BODY_ROWS + 1 + 1 + CODE_LINES + 1 + 1 + 1 + 1;

// A small closed vocabulary so filler text reads like prose while staying fully deterministic.
const VOCABULARY = [
  "spec", "review", "budget", "layout", "anchor", "editor", "preview", "diff", "document", "section",
  "table", "list", "heading", "scroll", "sync", "reconcile", "measure", "render", "block", "inline",
  "content", "change", "author", "column", "row", "sample", "value", "metric", "threshold", "latency",
] as const;

/** A deterministic word for a given index (wraps the vocabulary, negatives folded to non-negative). */
function word(index: number): string {
  const n = VOCABULARY.length;
  return VOCABULARY[((index % n) + n) % n] ?? VOCABULARY[0];
}

/** A deterministic space-joined phrase of `count` words seeded by `seed`. */
function phrase(seed: number, count: number): string {
  const parts: string[] = [];
  for (let k = 0; k < count; k++) {
    parts.push(word(seed * 17 + k * 3));
  }
  return parts.join(" ");
}

/** Render one section to its Markdown lines (see LINES_PER_SECTION for the exact shape). */
function renderSection(index: number): string[] {
  const lines: string[] = [`## Section ${index}: ${phrase(index * 53, 3)}`, "", phrase(index * 101, 14), ""];
  for (let i = 1; i <= LIST_ITEMS; i++) {
    lines.push(`- item ${i} ${phrase(index * 7 + i, 4)}`);
  }
  lines.push("", "| Name | Status | Note |", "| --- | --- | --- |");
  for (let r = 1; r <= TABLE_BODY_ROWS; r++) {
    lines.push(`| ${word(index + r)} ${r} | ${word(index * 3 + r)} | ${phrase(index * 5 + r, 3)} |`);
  }
  lines.push("", "```text");
  for (let c = 1; c <= CODE_LINES; c++) {
    lines.push(`line ${c}: ${phrase(index * 13 + c, 5)}`);
  }
  lines.push("```", "", phrase(index * 211 + 7, 12), "");
  return lines;
}

/** The number of sections whose rendered height first reaches `lineTarget` lines (at least one). */
export function sectionCountForLines(lineTarget: number): number {
  return Math.max(1, Math.ceil(lineTarget / LINES_PER_SECTION));
}

/** Generate a synthetic document of approximately `lineTarget` lines (rounded up to a whole section). */
export function generateLargeDoc(lineTarget: number): string {
  const lines: string[] = [];
  const sections = sectionCountForLines(lineTarget);
  for (let i = 0; i < sections; i++) {
    lines.push(...renderSection(i));
  }
  return lines.join("\n");
}
