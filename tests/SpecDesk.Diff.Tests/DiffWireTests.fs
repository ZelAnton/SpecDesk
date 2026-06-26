module SpecDesk.Diff.Tests.DiffWireTests

open NUnit.Framework
open SpecDesk.Diff.DiffWire

let private kinds (entries: DiffWireEntry[]) = entries |> Array.map (fun e -> e.Kind) |> String.concat ","

[<Test>]
let ``no changes yields no entries`` () =
    let w = toWire "# H\n\npara\n" "# H\n\npara\n"
    Assert.That(w.Length, Is.EqualTo 0)

[<Test>]
let ``an appended block is one added entry on its head line range`` () =
    let w = toWire "# H\n" "# H\n\nNew paragraph.\n"
    Assert.That(kinds w, Is.EqualTo "added")
    Assert.That(w.[0].LineStart, Is.EqualTo 2)
    Assert.That(w.[0].LineEnd, Is.EqualTo 2)

[<Test>]
let ``a heading level change is one changed entry on the head line`` () =
    let w = toWire "## Overview\n\nBody.\n" "### Overview\n\nBody.\n"
    Assert.That(kinds w, Is.EqualTo "changed")
    Assert.That(w.[0].LineStart, Is.EqualTo 0)

[<Test>]
let ``a removed middle block anchors after the preceding head block and carries its base text`` () =
    // base: heading@0, "gone block"@2, "keep"@4 → head drops the middle block.
    let w = toWire "# H\n\ngone block\n\nkeep\n" "# H\n\nkeep\n"
    Assert.That(kinds w, Is.EqualTo "removed")
    Assert.That(w.[0].AnchorLine, Is.EqualTo 1) // right after the unchanged heading (head LineEnd 0)
    Assert.That(w.[0].RemovedText, Does.Contain "gone block")

[<Test>]
let ``a removed leading block anchors at line 0`` () =
    let w = toWire "first para\n\nsecond para\n" "second para\n"
    Assert.That(kinds w, Is.EqualTo "removed")
    Assert.That(w.[0].AnchorLine, Is.EqualTo 0)
    Assert.That(w.[0].RemovedText, Does.Contain "first para")

[<Test>]
let ``a reordered block is one moved entry on its head line`` () =
    let w = toWire "# Alpha\n\n# Beta\n\n# Gamma\n" "# Beta\n\n# Gamma\n\n# Alpha\n"
    Assert.That(kinds w, Is.EqualTo "moved")
    Assert.That(w.[0].LineStart, Is.EqualTo 4) // # Alpha is on line 4 in the head

[<Test>]
let ``unchanged blocks are omitted entirely`` () =
    // Only the heading changed; the two surrounding paragraphs are unchanged and not emitted.
    let w = toWire "intro\n\n## Sec\n\nbody\n" "intro\n\n### Sec\n\nbody\n"
    Assert.That(kinds w, Is.EqualTo "changed")
