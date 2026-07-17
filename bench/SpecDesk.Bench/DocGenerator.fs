/// Deterministic synthetic Markdown generator for the performance harness. It emits large documents
/// (thousands of lines) carrying the block variety the real hot paths stress — headings, paragraphs,
/// bulleted lists, GFM tables, and fenced code — so the reparse and AST-diff benchmarks measure a
/// realistic mixed document, not a degenerate wall of paragraphs.
///
/// Deterministic by construction (no RNG, no clock): a given line target always yields byte-identical
/// output, so a benchmark run is reproducible and {@link generateEditPair}'s two sides diff the same way
/// every time. The recipe is mirrored in TypeScript at e2e/fixtures/large-doc.ts, which the Layer 1
/// interactivity budget scenario consumes — keep the two generators' shapes in step so the bench and the
/// e2e stress the same class of document.
///
/// Node-count budget (important for the diff benchmark). Every section contributes exactly six top-level
/// AST nodes (heading, intro paragraph, list, table, code block, outro paragraph) regardless of how many
/// LINES its list/table/code span. That keeps the top-level node count well under the square root of
/// `SpecDesk.Diff.AstDiff.maxNodePairs` even at a ten-thousand-line document (~233 sections → ~1400 nodes,
/// so ~2.0M base×head pairs < the 4.0M guard), so the diff benchmark measures the real O(m·n) LCS +
/// similarity path rather than falling through to the flat oversized-document fallback.
module SpecDesk.Bench.DocGenerator

// Fixed per-section block sizes — chosen so a section spans many lines but only six top-level nodes
// (see the node-count budget above). LINES_PER_SECTION is the exact rendered height of one section,
// used to translate a line target into a section count.
let private listItems = 8
let private tableBodyRows = 12
let private codeLines = 10

/// Rendered line height of a single section: heading + blank + intro + blank + 8 list items + blank +
/// table header + delimiter + 12 body rows + blank + code fence-open + 10 code lines + fence-close +
/// blank + outro + blank.
let private linesPerSection =
    1 + 1 + 1 + 1 + listItems + 1 + 1 + 1 + tableBodyRows + 1 + 1 + codeLines + 1 + 1 + 1 + 1

/// A small closed vocabulary so filler text reads like prose (and tokenizes into overlapping word sets
/// for the diff's similarity scoring) while staying fully deterministic.
let private vocabulary =
    [| "spec"; "review"; "budget"; "layout"; "anchor"; "editor"; "preview"; "diff"; "document"; "section"
       "table"; "list"; "heading"; "scroll"; "sync"; "reconcile"; "measure"; "render"; "block"; "inline"
       "content"; "change"; "author"; "column"; "row"; "sample"; "value"; "metric"; "threshold"; "latency" |]

/// A deterministic word for a given index (wraps the vocabulary, negatives folded to non-negative).
let private word (index: int) : string =
    let n = vocabulary.Length
    vocabulary.[((index % n) + n) % n]

/// A deterministic space-joined phrase of `count` words seeded by `seed` — the same seed always yields
/// the same phrase, and two nearby seeds share most words (so an edited paragraph stays a high-overlap
/// Changed match rather than an unrelated Removed + Added).
let private phrase (seed: int) (count: int) : string =
    String.concat " " [ for k in 0 .. count - 1 -> word (seed * 17 + k * 3) ]

/// One generated section, addressed by `index` (its ordinal, which seeds all of its text).
type private Section =
    { Index: int
      Intro: string
      Outro: string }

let private makeSection (index: int) : Section =
    { Index = index
      Intro = phrase (index * 101) 14
      Outro = phrase (index * 211 + 7) 12 }

/// Render one section to its Markdown lines (see {@link linesPerSection} for the exact shape).
let private renderSection (s: Section) : string list =
    [ yield $"## Section {s.Index}: {phrase (s.Index * 53) 3}"
      yield ""
      yield s.Intro
      yield ""
      for i in 1..listItems do
          yield $"- item {i} {phrase (s.Index * 7 + i) 4}"
      yield ""
      yield "| Name | Status | Note |"
      yield "| --- | --- | --- |"
      for r in 1..tableBodyRows do
          yield $"| {word (s.Index + r)} {r} | {word (s.Index * 3 + r)} | {phrase (s.Index * 5 + r) 3} |"
      yield ""
      yield "```text"
      for c in 1..codeLines do
          yield $"line {c}: {phrase (s.Index * 13 + c) 5}"
      yield "```"
      yield ""
      yield s.Outro
      yield "" ]

/// The number of sections whose rendered height first reaches `lineTarget` lines (at least one).
let private sectionCount (lineTarget: int) : int =
    max 1 ((lineTarget + linesPerSection - 1) / linesPerSection)

let private renderDoc (sections: Section list) : string =
    sections |> List.collect renderSection |> String.concat "\n"

/// Generate a synthetic document of approximately `lineTarget` lines (rounded up to a whole section).
let generate (lineTarget: int) : string =
    renderDoc [ for i in 0 .. sectionCount lineTarget - 1 -> makeSection i ]

/// A lightly edited copy of a section: its intro paragraph keeps most of its words (a one-word swap, so
/// the diff still reads it as a Changed edit of the base paragraph, not an unrelated replacement).
let private editSection (s: Section) : Section =
    { s with Intro = $"revised {s.Intro}" }

/// The base document with exactly ONE block spliced — a single mid-document section's intro paragraph
/// edited. This is the reparse a block-level edit triggers: the native pipeline has no incremental
/// splice, so it reparses the whole document, and this benchmark input measures that per-edit cost.
let generateSpliced (lineTarget: int) : string =
    let n = sectionCount lineTarget
    renderDoc [ for i in 0 .. n - 1 -> (if i = n / 2 then editSection else id) (makeSection i) ]

/// A (base, head) document pair for the AST-diff benchmark, sharing most structure so the diff exercises
/// its full backbone: an unchanged-heading/table/code LCS spine, a run of same-kind Changed paragraphs
/// (every sixth section's intro is lightly edited), a couple of Removed sections (dropped near the end),
/// and a couple of Added sections (appended) — the realistic mix a real review produces, kept below the
/// {@link maxNodePairs} guard by the six-nodes-per-section budget.
let generateEditPair (lineTarget: int) : string * string =
    let n = sectionCount lineTarget
    let baseSections = [ for i in 0 .. n - 1 -> makeSection i ]

    let headSections =
        baseSections
        // Drop two sections a few from the end (Removed), edit every sixth section's paragraph (Changed).
        |> List.filter (fun s -> s.Index <> n - 3 && s.Index <> n - 4)
        |> List.map (fun s -> if s.Index % 6 = 0 then editSection s else s)
        // Append two brand-new sections (Added), indexed past the base range so they can't collide.
        |> fun kept -> kept @ [ makeSection (n + 1); makeSection (n + 2) ]

    renderDoc baseSections, renderDoc headSections
