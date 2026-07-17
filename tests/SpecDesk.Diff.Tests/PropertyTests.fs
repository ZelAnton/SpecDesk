module SpecDesk.Diff.Tests.PropertyTests

open NUnit.Framework
open FsCheck
open FsCheck.FSharp
open SpecDesk.Markdown
open SpecDesk.Diff
open SpecDesk.Diff.AstDiff

// Property-based tests (FsCheck) for the structural diff. The hand-written example tests pin specific
// base/head pairs; these assert invariants over generated documents — reflexivity (diff(x, x) is empty),
// count conservation, and that every classification maps to a known wire DiffKind. Each property runs
// through Check.QuickThrowOnFailure so a falsifying case throws and NUnit reports it.

let private pair (a: Gen<'a>) (b: Gen<'b>) : Gen<'a * 'b> = Gen.map2 (fun x y -> (x, y)) a b

// A small pool of block texts, including the empty string, so generated documents contain both distinct and
// repeated content (repeats exercise the LCS backbone and the Move/Change pairing, not just Add/Remove).
let private words =
    [ "alpha"; "beta"; "gamma"; "delta"; "epsilon"; "quick"; "fox"; "lazy"; "" ]

let private wordInlines: Gen<Ast.Inline list> = Gen.elements words |> Gen.map (fun w -> [ Ast.Text w ])

/// A single top-level block of a varied-but-small set of kinds — enough for the diff's kind-tagged matching
/// (headings vs. paragraphs vs. code) to be exercised without pulling in container descent (list/table),
/// which the DiffWire example tests already cover.
let private blockGen: Gen<Ast.Block> =
    Gen.oneof
        [ Gen.map2 (fun level xs -> Ast.Heading(level, xs)) (Gen.choose (1, 6)) wordInlines
          wordInlines |> Gen.map Ast.Paragraph
          Gen.elements words |> Gen.map (fun code -> Ast.CodeBlock(None, code))
          Gen.constant Ast.ThematicBreak ]

/// A document of ordered top-level nodes; each node's line range is just its position (the diff matches on
/// Content, not on line ranges, so the positions only need to be well-formed).
let private documentGen: Gen<Ast.Document> =
    Gen.listOf blockGen
    |> Gen.map (List.mapi (fun i block -> { Ast.Content = block; Ast.LineStart = i; Ast.LineEnd = i }))

let private isUnchanged =
    function
    | Unchanged _ -> true
    | _ -> false

// ------------------------------------------------------------------------------------------------
// Stage 1 smoke: prove the FsCheck generator pipeline and runner work inside this NUnit F# project.
// ------------------------------------------------------------------------------------------------

[<Test>]
let ``FsCheck smoke: list reverse is an involution`` () =
    Check.QuickThrowOnFailure(fun (xs: int list) -> List.rev (List.rev xs) = xs)

// ------------------------------------------------------------------------------------------------
// Stage 4: AstDiff / DiffWire invariants.
// ------------------------------------------------------------------------------------------------

[<Test>]
let ``diff of a document against itself is entirely Unchanged`` () =
    // Reflexivity: a document diffed against itself reports no structural change — every entry is Unchanged
    // (one per node) and hasChanges is false, even when the document repeats identical blocks.
    let prop (doc: Ast.Document) =
        let d = diff doc doc
        not (hasChanges d) && d.Length = List.length doc && List.forall isUnchanged d

    Check.QuickThrowOnFailure(Prop.forAll (Arb.fromGen documentGen) prop)

[<Test>]
let ``the diff conserves base and head node counts`` () =
    // Every head node is represented exactly once as Unchanged / Added / Changed / Moved, and every base
    // node exactly once as Unchanged / Removed / Changed / Moved — so no node is dropped or double-counted
    // regardless of how the two documents relate.
    let prop (baseDoc: Ast.Document, headDoc: Ast.Document) =
        let d = diff baseDoc headDoc
        let countOf f = d |> List.filter f |> List.length
        let unchanged = countOf isUnchanged
        let added = countOf (function Added _ -> true | _ -> false)
        let removed = countOf (function Removed _ -> true | _ -> false)
        let changed = countOf (function Changed _ -> true | _ -> false)
        let moved = countOf (function Moved _ -> true | _ -> false)

        unchanged + added + changed + moved = List.length headDoc
        && unchanged + removed + changed + moved = List.length baseDoc

    Check.QuickThrowOnFailure(Prop.forAll (Arb.fromGen (pair documentGen documentGen)) prop)

/// The wire kind of a classification — the lowercased union-case name. Unchanged is the only case never
/// shipped to the wire; the other four must each be a recognised DiffKind (pinned to the union by
/// DiffKindContractTests), so this projection has no "unknown" fall-through.
let private kindName (entry: DiffEntry) : string =
    match entry with
    | Unchanged _ -> "unchanged"
    | Added _ -> "added"
    | Removed _ -> "removed"
    | Changed _ -> "changed"
    | Moved _ -> "moved"

let private knownKinds = Set.ofArray (Array.append [| "unchanged" |] DiffWire.DiffKind.all)

[<Test>]
let ``every diff entry carries a known DiffKind`` () =
    // No entry classifies to anything outside {unchanged} ∪ DiffWire.DiffKind.all — every result node
    // carries a kind the wire and the webview know how to render.
    let prop (baseDoc: Ast.Document, headDoc: Ast.Document) =
        diff baseDoc headDoc |> List.forall (fun entry -> Set.contains (kindName entry) knownKinds)

    Check.QuickThrowOnFailure(Prop.forAll (Arb.fromGen (pair documentGen documentGen)) prop)

[<Test>]
let ``diff from an empty base is all Added and to an empty head is all Removed`` () =
    let prop (doc: Ast.Document) =
        let fromEmpty = diff [] doc
        let toEmpty = diff doc []

        (fromEmpty |> List.forall (function Added _ -> true | _ -> false))
        && fromEmpty.Length = List.length doc
        && (toEmpty |> List.forall (function Removed _ -> true | _ -> false))
        && toEmpty.Length = List.length doc

    Check.QuickThrowOnFailure(Prop.forAll (Arb.fromGen documentGen) prop)

// A pool of parseable Markdown lines so the DiffWire round-trip runs on real parsed documents (parse → diff
// → wire) rather than synthetic AST, exercising the whole projection end to end.
let private markdownLines =
    [ "# Heading"; "## Section"; ""; "A paragraph of text."; "Another line here."
      "- item one"; "- item two"; "> a quote"; "```"; "code line"; "---"; "Final words." ]

let private markdownText: Gen<string> =
    Gen.elements markdownLines |> Gen.listOf |> Gen.map (String.concat "\n")

[<Test>]
let ``every DiffWire entry carries a wire kind the contract recognizes`` () =
    // End to end: for any pair of parseable Markdown sources, every wire entry the host would ship carries a
    // Kind from DiffWire.DiffKind.all (never an unstyled/unknown discriminator), and building the wire never
    // throws.
    let prop (baseText: string, headText: string) =
        DiffWire.toWire baseText headText
        |> Array.forall (fun entry -> Array.contains entry.Kind DiffWire.DiffKind.all)

    Check.QuickThrowOnFailure(Prop.forAll (Arb.fromGen (pair markdownText markdownText)) prop)
