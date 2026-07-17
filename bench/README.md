# Performance harness

A two-part performance suite for the document hot paths that grow with document size. It is **not** part
of the ordinary build or CI gate — it runs as a separate opt-in stage
([`.github/workflows/perf.yml`](../.github/workflows/perf.yml), nightly + `workflow_dispatch`) and from the
developer commands below, so a normal `dotnet build` / `npm run e2e` is never slowed by it.

The value: a performance regression on large specifications is caught here, before an author feels it on a
real document.

## Part 1 — BenchmarkDotNet (`bench/SpecDesk.Bench`)

Micro-benchmarks over the native pipeline on large synthetic documents (5–10k lines with headings, lists,
tables, and code):

- **`MarkdownBenchmarks`** — `Projection.toAst` full parse, the reparse a single block splice triggers, and
  the `childLineRanges` container projection.
- **`DiffBenchmarks`** — the structural AST diff (`SpecDesk.Diff.AstDiff`), both the pure O(m·n) pass over
  pre-parsed documents and the end-to-end parse-then-diff a "show changes" request runs.

The synthetic documents come from [`SpecDesk.Bench.DocGenerator`](SpecDesk.Bench/DocGenerator.fs), which is
deterministic and sized so **every section is six top-level AST nodes** regardless of its line span. That
keeps a ten-thousand-line document's node count (~1400 per side) well under `AstDiff.maxNodePairs` (4.0M
base×head pairs), so the diff benchmark measures the real bounded matching path rather than the flat
oversized-document fallback. `DocGenerator` is mirrored in TypeScript at
[`e2e/fixtures/large-doc.ts`](../e2e/fixtures/large-doc.ts) for the Layer 1 scenario — keep the two recipes
in step.

Run it:

```bash
# All benchmarks, full statistical rigour (minutes):
dotnet run -c Release --project bench/SpecDesk.Bench -- --filter '*'

# A quick pass (bounded iteration count — what the nightly uses):
dotnet run -c Release --project bench/SpecDesk.Bench -- --filter '*' --job short

# One class:
dotnet run -c Release --project bench/SpecDesk.Bench -- --filter '*Diff*'
```

Results (markdown + CSV) are written to `BenchmarkDotNet.Artifacts/` (git-ignored). BenchmarkDotNet does
not fail on a slowdown; the nightly uploads the reports so a regression is visible in the trend.

## Part 2 — Layer 1 interactivity budgets (`e2e/webview-mock/large-document.perf.e2e.ts`)

A real-Chromium Playwright scenario that opens a large generated document and asserts two interactivity
budgets, at two document sizes, so it also proves **no quadratic growth** with document size:

- **Reconciliation** — opening the document and letting both panes reach a settled, spacer-stable geometry
  (the height-sync reconcile). This carries the explicit sub-quadratic scaling assertion.
- **Scroll-synchronization** — a single genuine scroll gesture coupling the sibling pane. A settled pane's
  per-scroll couple is a cheap O(log n) map lookup by design, so it must stay fast and roughly flat as the
  document grows.

Run it (builds and verifies the bundle first, via the Layer 1 global-setup):

```bash
cd e2e && npm run e2e:perf
```

### Budgets and their rationale

The thresholds are deliberately generous so they do not false-positive on ordinary CI hardware (the task's
calibration criterion). They live beside the assertions in the scenario file; in summary:

| Budget | Value | Why |
| --- | --- | --- |
| Reconcile (large-doc load → settle) | < 20 s | Far above a healthy low-single-digit-second settle (even on a slower hosted runner); only a real blowup trips it. |
| Scroll couple (one scroll → sibling couples) | < 1.5 s | Coupling is O(log n) + a couple of frames (tens of ms healthy); a generous ceiling that catches reintroduced per-scroll O(n) work. |
| Growth guard (2x size → time ratio) | < 3x | Linear is ~2x and quadratic ~4x, so 3x fails on quadratic while tolerating noise. |

Each timing is measured **min-of-N with a discarded warmup**, so a transient CI slowdown only adds to a
sample and cannot drag the reported minimum up. Per KB **K-003**, a single red nightly on a Layer 1 job
should be cross-checked against the base-commit run before it is treated as a real regression rather than
known environment flakiness.
