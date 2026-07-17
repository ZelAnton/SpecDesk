module SpecDesk.Diff.Tests.ContainerOrdinalContractTests

open System
open System.IO
open System.Text.Json
open NUnit.Framework
open SpecDesk.Diff.DiffWire

// Cross-language parity guard for CONTAINER-CHILD ordinals (T-083): the per-child diff highlight is only
// correct while all three parsers agree on which source line/PM-row-or-item a given child ordinal is —
// the native childDiff (childTexts in DiffWire.fs, exercised here via toWire), the webview's markdown-it
// `childLineStarts` (md-blocks.ts) and the ProseMirror schema's container children (pm-markdown.ts /
// formatted.ts nodeRangeForLine). This file pins the NATIVE half against a shared fixture of container
// documents (nested list in an item, loose/tight lists, a table with an empty header row, a multi-
// paragraph item) that each cover a way the three parsers have historically been able to disagree on
// ordinal counting; the webview half (webview/tests/contract/container-ordinals.test.ts) asserts the
// SAME fixture's `childLines`/`childMarkers` against md-blocks + the ProseMirror parse. A drift in either
// language's child-ordinal counting fails its own half rather than silently shifting a highlight.
//
// Unlike diff-kinds.json (regenerated FROM the F# source of truth), this fixture has no single-language
// source of truth — `base`/`head` are hand-authored documents, `childLines` is webview-domain (source
// lines from markdown-it), and `changes` is native-domain (DiffWire.toWire's per-child Children). It is
// committed as plain shared test data, not regenerated via UPDATE_CONTRACT_FIXTURE.
//
// Every scenario's `base` differs from `head` only by an "old " (or "old-") prefix on each child's own
// marker word, keeping the same child COUNT/KIND on both sides so the top-level container is classified
// "changed" (never added/removed) and every differing child comes back as a per-child "changed" entry
// whose ChildIndex is that child's 0-based ordinal in `head` — the exact ordinal space `childLines` (and
// the webview's `childLineStarts`) must agree with.

// Not `private` — System.Text.Json's reflection-based deserializer needs the CLIMutable-generated
// parameterless constructor to be public, which F# only emits when the type itself isn't `private`.
[<CLIMutable>]
type ChangeFixture = { Index: int; Marker: string }

[<CLIMutable>]
type ScenarioFixture =
    { Name: string
      Base: string
      Head: string
      ChildLines: int[]
      ChildMarkers: string[]
      Changes: ChangeFixture[] }

/// Walk up from the test binary to the repo root (the dir holding SpecDesk.slnx) and resolve the shared
/// webview contract-fixture path — the same anchor DiffKindContractTests/ContractFixtureTests use.
let private fixturePath: string =
    let rec ascend (dir: DirectoryInfo | null) =
        match dir with
        | null -> invalidOp "Could not locate the repo root (no SpecDesk.slnx above the test binary)."
        | d when File.Exists(Path.Combine(d.FullName, "SpecDesk.slnx")) -> d.FullName
        | d -> ascend d.Parent

    Path.Combine(
        ascend (DirectoryInfo AppContext.BaseDirectory),
        "webview",
        "tests",
        "contract",
        "container-ordinals.json"
    )

/// Load and parse the shared fixture. Kept out of the module's static init (a `let` bound directly at
/// module scope runs in the type initializer, so an Assert/exception there surfaces as an opaque
/// TypeInitializationException instead of a normal failed test) — called from inside the `[<Test>]`.
let private loadScenarios () : ScenarioFixture[] =
    Assert.That(File.Exists fixturePath, Is.True, $"The container-ordinals fixture is missing ({fixturePath}).")

    let options = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    match JsonSerializer.Deserialize<ScenarioFixture[]>(File.ReadAllText fixturePath, options) with
    | null -> failwith $"The container-ordinals fixture at {fixturePath} is empty or unparseable."
    | parsed -> parsed

[<Test>]
let ``container child ordinals match the shared cross-language fixture`` () =
    for scenario in loadScenarios () do
        let w = toWire scenario.Base scenario.Head

        Assert.That(w.Length, Is.EqualTo 1, $"{scenario.Name}: expected exactly one top-level changed entry.")

        Assert.That(
            w.[0].Kind,
            Is.EqualTo DiffKind.Changed,
            $"{scenario.Name}: the container itself must be 'changed'."
        )

        Assert.That(
            w.[0].Children.Length,
            Is.EqualTo scenario.Changes.Length,
            $"{scenario.Name}: unexpected number of per-child diff entries."
        )

        for i in 0 .. scenario.Changes.Length - 1 do
            let expected = scenario.Changes.[i]
            let actual = w.[0].Children.[i]
            Assert.That(actual.Kind, Is.EqualTo DiffKind.Changed, $"{scenario.Name}: child {i} kind.")

            Assert.That(
                actual.ChildIndex,
                Is.EqualTo expected.Index,
                $"{scenario.Name}: child {i} ordinal drifted from the fixture — this is the exact class of "
                + "bug that silently shifts the webview highlight to the wrong row/item."
            )

            Assert.That(
                actual.BaseText,
                Does.Contain expected.Marker,
                $"{scenario.Name}: child {i} base text should carry its fixture marker."
            )
