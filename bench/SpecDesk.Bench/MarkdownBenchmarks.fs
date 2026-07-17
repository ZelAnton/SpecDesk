/// Benchmarks for the SpecDesk.Markdown pipeline on large synthetic documents: the full parse-and-project
/// (`Projection.toAst`), the reparse a single block splice triggers, and the container child-line-range
/// projection the diff wire layer consumes. All three grow with document size, so a regression that turns
/// any of them super-linear shows up here before an author feels it on a real specification.
module SpecDesk.Bench.MarkdownBenchmarks

open BenchmarkDotNet.Attributes
open SpecDesk.Markdown

[<MemoryDiagnoser>]
type MarkdownBenchmarks() =

    let mutable document = ""
    let mutable spliced = ""

    /// The five-to-ten-thousand-line range the task calibrates against — the size at which parse cost
    /// stops being noise and a super-linear regression becomes visible.
    [<Params(5000, 10000)>]
    member val LineCount = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() =
        document <- DocGenerator.generate this.LineCount
        spliced <- DocGenerator.generateSpliced this.LineCount

    /// Full parse + projection to the line-stamped AST — the baseline the reparse is compared against.
    [<Benchmark(Baseline = true)>]
    member _.ParseFull() : Ast.Document = Projection.toAst document

    /// Reparse after a single block-level splice (one paragraph edited). The native pipeline reparses the
    /// whole document per edit, so this is the interactive cost each block change pays.
    [<Benchmark>]
    member _.ReparseAfterBlockSplice() : Ast.Document = Projection.toAst spliced

    /// The container child-line-range projection (list items / table rows) the diff wire layer keys on —
    /// a second full parse of the same source, benchmarked separately because it walks the tree again.
    [<Benchmark>]
    member _.ChildLineRanges() : Map<int, (int * int) list> = Projection.childLineRanges document
