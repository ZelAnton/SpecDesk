/// Entry point for the SpecDesk performance harness. Delegates to BenchmarkDotNet's switcher so a run can
/// select benchmarks by filter and job from the command line, e.g.:
///
///   dotnet run -c Release --project bench/SpecDesk.Bench -- --filter '*'
///   dotnet run -c Release --project bench/SpecDesk.Bench -- --filter '*Diff*' --job short
///
/// See bench/README.md for the full workflow and the budget rationale shared with the Layer 1 scenario.
module SpecDesk.Bench.Program

open BenchmarkDotNet.Running

[<EntryPoint>]
let main argv =
    BenchmarkSwitcher.FromAssembly(typeof<MarkdownBenchmarks.MarkdownBenchmarks>.Assembly).Run(argv)
    |> ignore

    0
