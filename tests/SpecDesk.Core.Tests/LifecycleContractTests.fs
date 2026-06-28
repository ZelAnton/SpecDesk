module SpecDesk.Core.Tests.LifecycleContractTests

open System
open System.IO
open System.Text.Json
open FSharp.Reflection
open NUnit.Framework
open SpecDesk.Core

// Cross-language parity guard for the document lifecycle state names. The authoritative set is the F#
// Lifecycle.State union (turned into wire names by stateName); the webview mirrors it as protocol.ts
// STATUS_STATES (from which the StatusState type derives, and which the status decoder validates against).
// This pins the two: a state added/renamed on either side fails CI rather than drifting into a silent
// stale status bar / unstyled state dot. The webview half asserts STATUS_STATES against the same fixture
// (webview/tests/contract.test.ts). Regenerate after an intentional change with UPDATE_CONTRACT_FIXTURE=1
// over the whole solution (the C# ContractFixtureTests live in a different project but share the opt-in).
// Scope note: the status styling in webview/styles.css (one [data-state=...] rule per state) stays
// hand-maintained — a new state falls back to the faint default dot until it gets a rule.

/// Every wire state name, ordinal-sorted. Reflected from the State union so the list is exhaustive by
/// construction — a new case appears here automatically (and so trips this guard until the webview follows).
let private wireStateNames () : string[] =
    FSharpType.GetUnionCases typeof<Lifecycle.State>
    |> Array.map (fun case ->
        match FSharpValue.MakeUnion(case, Array.empty<obj>) with
        | :? Lifecycle.State as state -> Lifecycle.stateName state
        | _ -> failwith $"FSharpValue.MakeUnion did not yield a Lifecycle.State for case '{case.Name}'")
    |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))

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
let ``lifecycle state names match the committed webview contract fixture`` () =
    let actual = wireStateNames ()
    let path = fixturePath "lifecycle-states.json"

    // Regeneration is an explicit opt-in (like the C# fixture): a missing fixture is a failure, never a
    // silent regenerate — deleting it must not quietly disable the guard.
    if Environment.GetEnvironmentVariable "UPDATE_CONTRACT_FIXTURE" = "1" then
        Directory.CreateDirectory(nonNull (Path.GetDirectoryName path)) |> ignore
        let json = JsonSerializer.Serialize(actual, JsonSerializerOptions(WriteIndented = true)) + "\n"
        File.WriteAllText(path, json)
        Assert.Pass(
            $"Lifecycle-states fixture (re)generated at {path}. Commit it and keep webview/src/protocol.ts (STATUS_STATES) in sync."
        )
    else
        Assert.That(
            File.Exists path,
            Is.True,
            $"The lifecycle-states fixture is missing ({path}). Regenerate it with UPDATE_CONTRACT_FIXTURE=1 and commit it."
        )

        match JsonSerializer.Deserialize<string[]>(File.ReadAllText path) with
        | null -> Assert.Fail $"The lifecycle-states fixture at {path} is empty or unparseable."
        | expected ->
            // box selects Is.EqualTo(obj) unambiguously (string[] otherwise matches several generic
            // overloads); NUnit still compares the two arrays element-wise, with a diff on failure.
            Assert.That(
                actual,
                Is.EqualTo(box expected),
                "The lifecycle state names drifted from the committed fixture. If this is an intentional change, "
                + "regenerate with UPDATE_CONTRACT_FIXTURE=1 and update webview/src/protocol.ts (STATUS_STATES) to match."
            )
