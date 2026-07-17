/// Benchmarks for the structural AST diff (`SpecDesk.Diff.AstDiff`) on large synthetic documents. The
/// diff's cost is O(m·n) in the base/head top-level node counts (an LCS backbone plus same-kind
/// similarity scoring), so it is the most size-sensitive step in the review pipeline. The generated pair
/// is sized (six nodes per section) to stay under `AstDiff.maxNodePairs`, so these measure the real
/// bounded path — the flat oversized-document fallback would defeat the purpose.
module SpecDesk.Bench.DiffBenchmarks

open BenchmarkDotNet.Attributes
open SpecDesk.Markdown
open SpecDesk.Diff

[<MemoryDiagnoser>]
type DiffBenchmarks() =

    let mutable baseText = ""
    let mutable headText = ""
    let mutable baseDoc: Ast.Document = []
    let mutable headDoc: Ast.Document = []

    [<Params(5000, 10000)>]
    member val LineCount = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() =
        let b, h = DocGenerator.generateEditPair this.LineCount
        baseText <- b
        headText <- h
        baseDoc <- Projection.toAst b
        headDoc <- Projection.toAst h

    /// The pure structural diff over two already-parsed documents — the O(m·n) LCS + similarity pass in
    /// isolation, so a change to the matching algorithm is measured without parse cost masking it.
    [<Benchmark(Baseline = true)>]
    member _.DiffAst() : AstDiff.DocumentDiff = AstDiff.diff baseDoc headDoc

    /// The end-to-end review cost: parse both source strings, then diff — the reparse-then-diff pipeline a
    /// "show changes" request actually runs.
    [<Benchmark>]
    member _.DiffTextEndToEnd() : AstDiff.DocumentDiff = AstDiff.diffText baseText headText
