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

[<Test>]
let ``a changed list item is one changed child, not a whole-list wash`` () =
    let w = toWire "- one\n- two\n- three\n" "- one\n- two changed\n- three\n"
    Assert.That(kinds w, Is.EqualTo "changed") // one top-level changed entry: the list
    Assert.That(w.[0].Children.Length, Is.EqualTo 1)
    Assert.That(w.[0].Children.[0].Kind, Is.EqualTo "changed")
    Assert.That(w.[0].Children.[0].ChildIndex, Is.EqualTo 1) // the second item
    Assert.That(w.[0].Children.[0].BaseText, Does.Contain "two") // base text, for the inline word-diff

[<Test>]
let ``an added table row is one added child on the table entry`` () =
    let w =
        toWire "| A | B |\n| - | - |\n| 1 | 2 |\n" "| A | B |\n| - | - |\n| 1 | 2 |\n| 3 | 4 |\n"

    Assert.That(kinds w, Is.EqualTo "changed")
    Assert.That(w.[0].Children.Length, Is.EqualTo 1)
    Assert.That(w.[0].Children.[0].Kind, Is.EqualTo "added")
    // Child 0 is the header row, 1 the first body row, 2 the appended row.
    Assert.That(w.[0].Children.[0].ChildIndex, Is.EqualTo 2)

[<Test>]
let ``a removed list item is a removed child anchored after the preceding item`` () =
    let w = toWire "- one\n- two\n- three\n" "- one\n- three\n"
    Assert.That(kinds w, Is.EqualTo "changed")
    Assert.That(w.[0].Children.Length, Is.EqualTo 1)
    Assert.That(w.[0].Children.[0].Kind, Is.EqualTo "removed")
    Assert.That(w.[0].Children.[0].AnchorIndex, Is.EqualTo 1) // after the kept first item
    Assert.That(w.[0].Children.[0].RemovedText, Does.Contain "two")

[<Test>]
let ``a changed paragraph carries no children (whole-block wash)`` () =
    let w = toWire "## Overview\n\nBody.\n" "### Overview\n\nBody.\n"
    Assert.That(kinds w, Is.EqualTo "changed")
    Assert.That(w.[0].Children.Length, Is.EqualTo 0)
