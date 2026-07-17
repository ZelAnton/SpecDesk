module SpecDesk.Diff.Tests.DiffWireTests

open NUnit.Framework
open SpecDesk.Diff.DiffWire

let private kinds (entries: DiffWireEntry[]) =
    entries |> Array.map (fun e -> e.Kind) |> String.concat ","

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

// M-06 regression guards: `Inlines.flatten` strips formatting marks, and `childDiff` compares children
// by their FLATTENED text — so a formatting-only edit inside one item/cell (no word actually added or
// removed) leaves every child looking identical to childDiff, even though the container's real,
// mark-aware AST differs enough for the top-level diff to classify it as Changed. Without a fallback,
// that combination used to emit an empty `Children` AND an empty `BaseText`/`BaseSource`, leaving the
// webview nothing to word-diff against — which it read as "everything is new" and washed the whole
// list/table instead of the (non-existent) single changed row.

[<Test>]
let ``a formatting-only edit inside a list item falls back to the whole-list base text, not empty`` () =
    // "two" → "**two**": same flattened text, so childDiff finds nothing — the whole-list plain text
    // must be carried in BaseText instead of "".
    let w = toWire "- one\n- two\n- three\n" "- one\n- **two**\n- three\n"
    Assert.That(kinds w, Is.EqualTo "changed")
    Assert.That(w.[0].Children.Length, Is.EqualTo 0)
    Assert.That(w.[0].BaseText, Is.Not.Empty)
    Assert.That(w.[0].BaseText, Does.Contain "two")
    Assert.That(w.[0].BaseSource, Does.Contain "two")

[<Test>]
let ``a formatting-only edit inside a table cell falls back to the whole-table base text, not empty`` () =
    let w =
        toWire "| A | B |\n| - | - |\n| 1 | 2 |\n" "| A | B |\n| - | - |\n| **1** | 2 |\n"

    Assert.That(kinds w, Is.EqualTo "changed")
    Assert.That(w.[0].Children.Length, Is.EqualTo 0)
    Assert.That(w.[0].BaseText, Is.Not.Empty)
    Assert.That(w.[0].BaseText, Does.Contain "1")

[<Test>]
let ``a real text edit inside a list item still emits a per-child diff, not a whole-list fallback`` () =
    // Sanity check that the fallback is specific to the "childDiff found nothing" case — an ACTUAL text
    // change still takes the normal per-child path (already covered above, restated here alongside the
    // formatting-only case for contrast).
    let w = toWire "- one\n- two\n- three\n" "- one\n- two changed\n- three\n"
    Assert.That(w.[0].Children.Length, Is.EqualTo 1)
    Assert.That(w.[0].BaseText, Is.Empty)

// T-098: a changed child now also carries its base RAW SOURCE slice (BaseSource), the Code-pane inline
// word-diff basis — symmetric to a top-level Changed block's BaseSource. It is the base child's own
// source lines (marker/pipes included), not the flattened BaseText.

[<Test>]
let ``a changed list item carries its base source slice in BaseSource`` () =
    let w = toWire "- one\n- two\n- three\n" "- one\n- two changed\n- three\n"
    Assert.That(w.[0].Children.Length, Is.EqualTo 1)
    let child = w.[0].Children.[0]
    Assert.That(child.Kind, Is.EqualTo "changed")
    Assert.That(child.ChildIndex, Is.EqualTo 1)
    // The base item's own source line — the list marker is part of the slice, unlike the flattened BaseText.
    Assert.That(child.BaseSource, Is.EqualTo "- two")

[<Test>]
let ``a changed table row carries its base source slice in BaseSource`` () =
    // Children: 0 = header row, 1 = first body row, 2 = second body row. The first body row's cell changes.
    // The edit ADDS a word (keeping the base tokens a subset) so the row stays above the change-similarity
    // threshold and is matched as one changed child, not split into a removed + added pair.
    let w =
        toWire "| A | B |\n| - | - |\n| 1 | 2 |\n| 3 | 4 |\n" "| A | B |\n| - | - |\n| 1 | 2 changed |\n| 3 | 4 |\n"

    Assert.That(w.[0].Children.Length, Is.EqualTo 1)
    let child = w.[0].Children.[0]
    Assert.That(child.Kind, Is.EqualTo "changed")
    Assert.That(child.ChildIndex, Is.EqualTo 1)
    // The base row's own source line (the delimiter row on line 1 is NOT a child), pipes included.
    Assert.That(child.BaseSource, Is.EqualTo "| 1 | 2 |")

[<Test>]
let ``a changed multi-paragraph list item carries its full multi-line base source`` () =
    // The first item spans its marker line, a blank line and a continuation paragraph — its BaseSource
    // must cover the whole item, not just the marker line, so the Code-pane word-diff sees the full item.
    let baseText = "- india\n\n  second paragraph of india\n\n- juliett\n"
    let headText = "- india edited\n\n  second paragraph of india\n\n- juliett\n"
    let w = toWire baseText headText
    Assert.That(w.[0].Children.Length, Is.EqualTo 1)
    let child = w.[0].Children.[0]
    Assert.That(child.Kind, Is.EqualTo "changed")
    Assert.That(child.ChildIndex, Is.EqualTo 0)
    Assert.That(child.BaseSource, Does.StartWith "- india")
    Assert.That(child.BaseSource, Does.Contain "second paragraph of india")
    Assert.That(child.BaseSource, Does.Contain "\n") // the slice is genuinely multi-line

[<Test>]
let ``a non-changed child carries an empty BaseSource`` () =
    // A removed child has no head/base pairing to word-diff — BaseSource stays the neutral "".
    let w = toWire "- one\n- two\n- three\n" "- one\n- three\n"
    Assert.That(w.[0].Children.[0].Kind, Is.EqualTo "removed")
    Assert.That(w.[0].Children.[0].BaseSource, Is.Empty)

// T-081: above AstDiff.maxNodePairs base×head node pairs, AstDiff falls back to a flat Removed+Added
// listing — toWireDetailed must NOT build/ship that listing's entries (every removed block's full base
// text) over the wire; a compact count-only OverflowSignal replaces them.

let private paragraphDocText (count: int) (label: string) : string =
    [ for i in 0 .. count - 1 -> sprintf "%s paragraph %d" label i ]
    |> String.concat "\n\n"

[<Test>]
let ``an overflowing pair yields no entries and a count-only overflow signal`` () =
    // Comfortably over the limit (size² > maxNodePairs with margin), mirroring AstDiffTests' guard tests.
    let size = int (sqrt (float SpecDesk.Diff.AstDiff.maxNodePairs)) + 50
    let baseText = paragraphDocText size "base"
    let headText = paragraphDocText size "head"

    let entries, overflow = toWireDetailed baseText headText

    Assert.That(entries, Is.Empty)
    Assert.That(overflow.Overflowed, Is.True)
    Assert.That(overflow.RemovedCount, Is.EqualTo size)
    Assert.That(overflow.AddedCount, Is.EqualTo size)

[<Test>]
let ``a non-overflowing pair carries a not-overflowed signal alongside toWire's normal entries`` () =
    let baseText = "# H\n"
    let headText = "# H\n\nNew paragraph.\n"
    let entries: DiffWireEntry[] = toWireDetailed baseText headText |> fst
    let overflow = toWireDetailed baseText headText |> snd
    Assert.That(overflow.Overflowed, Is.False)
    Assert.That(overflow.RemovedCount, Is.EqualTo 0)
    Assert.That(overflow.AddedCount, Is.EqualTo 0)
    // toWireDetailed's entries, for a non-overflowing pair, are exactly what toWire itself returns.
    let expected: DiffWireEntry[] = toWire baseText headText
    Assert.That(kinds entries, Is.EqualTo(kinds expected))
    Assert.That(entries.Length, Is.EqualTo expected.Length)
