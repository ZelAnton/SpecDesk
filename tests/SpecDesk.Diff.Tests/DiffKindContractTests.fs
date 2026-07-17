module SpecDesk.Diff.Tests.DiffKindContractTests

open System
open System.IO
open System.Text.Json
open FSharp.Reflection
open NUnit.Framework
open SpecDesk.Diff

// Cross-language parity guard for the diff wire `kind` discriminator (added / removed / changed / moved).
// The authoritative set is DiffKind.all in SpecDesk.Diff.DiffWire (the C# host passes Kind through verbatim;
// the webview keys styling, the label pill, and the inline-word-diff special-cases off it). This pins the
// set to webview/tests/contract/diff-kinds.json — a kind added/renamed on either side fails CI rather than
// drifting into an unstyled wash or a generic label. The webview half asserts DIFF_KINDS against the same
// fixture (webview/tests/contract.test.ts). Regenerate after an intentional change with
// UPDATE_CONTRACT_FIXTURE=1 over the whole solution (the C# ContractFixtureTests share the opt-in).

/// Walk up from the test binary to the repo root (the dir holding SpecDesk.slnx) and resolve the shared
/// webview contract-fixture path — the same anchor the C# ContractFixtureTests writes to.
let private fixturePath (fileName: string) : string =
    let rec ascend (dir: DirectoryInfo | null) =
        match dir with
        | null -> invalidOp "Could not locate the repo root (no SpecDesk.slnx above the test binary)."
        | d when File.Exists(Path.Combine(d.FullName, "SpecDesk.slnx")) -> d.FullName
        | d -> ascend d.Parent

    Path.Combine(ascend (DirectoryInfo AppContext.BaseDirectory), "webview", "tests", "contract", fileName)

[<Test>]
let ``diff kinds match the committed webview contract fixture`` () =
    let actual =
        DiffWire.DiffKind.all |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))

    let path = fixturePath "diff-kinds.json"

    // Regeneration is an explicit opt-in (like the C# fixtures): a missing fixture is a failure, never a
    // silent regenerate — deleting it must not quietly disable the guard.
    if Environment.GetEnvironmentVariable "UPDATE_CONTRACT_FIXTURE" = "1" then
        Directory.CreateDirectory(nonNull (Path.GetDirectoryName path)) |> ignore

        let json =
            JsonSerializer.Serialize(actual, JsonSerializerOptions(WriteIndented = true))
            + "\n"

        File.WriteAllText(path, json)

        Assert.Pass(
            $"Diff-kinds fixture (re)generated at {path}. Commit it and keep webview/src/protocol.ts (DIFF_KINDS) in sync."
        )
    else
        Assert.That(
            File.Exists path,
            Is.True,
            $"The diff-kinds fixture is missing ({path}). Regenerate it with UPDATE_CONTRACT_FIXTURE=1 and commit it."
        )

        match JsonSerializer.Deserialize<string[]>(File.ReadAllText path) with
        | null -> Assert.Fail $"The diff-kinds fixture at {path} is empty or unparseable."
        | expected ->
            // box selects Is.EqualTo(obj) unambiguously (string[] otherwise matches several generic
            // overloads); NUnit still compares the two arrays element-wise, with a diff on failure.
            Assert.That(
                actual,
                Is.EqualTo(box expected),
                "The diff kinds drifted from the committed fixture. If this is an intentional change, regenerate "
                + "with UPDATE_CONTRACT_FIXTURE=1 and update webview/src/protocol.ts (DIFF_KINDS) to match."
            )

[<Test>]
let ``every wire kind is a non-Unchanged AstDiff classification`` () =
    // The wire kinds ARE a projection of the AstDiff.DiffEntry union — each case's lowercased name, with
    // Unchanged omitted (it never reaches the wire). Tie the two together so adding a structural
    // classification (a new DiffEntry case) forces a matching DiffKind constant rather than silently
    // shipping an entry the webview can't style. Reflects the case names only (no instantiation needed).
    // Assumes single-word case names (lowercased name == wire kind). A future multi-word case (e.g.
    // InlineChanged → wire "inlineChanged") needs an explicit case→kind map here, the way Lifecycle.stateName
    // spells out the camelCase state names — this test will fail until that mapping is added.
    let ordinal = Array.sortWith (fun a b -> String.CompareOrdinal(a, b))

    let fromUnion =
        FSharpType.GetUnionCases typeof<AstDiff.DiffEntry>
        |> Array.map (fun case -> case.Name.ToLowerInvariant())
        |> Array.filter (fun name -> name <> "unchanged")
        |> ordinal

    Assert.That(
        ordinal DiffWire.DiffKind.all,
        Is.EqualTo(box fromUnion),
        "DiffKind.all and the AstDiff.DiffEntry cases drifted. Every non-Unchanged case must have a DiffKind "
        + "constant equal to its lowercased name (and vice versa)."
    )
