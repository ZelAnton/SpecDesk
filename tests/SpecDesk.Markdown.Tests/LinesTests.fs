module SpecDesk.Markdown.Tests.LinesTests

open NUnit.Framework
open SpecDesk.Markdown

// Lines maps source character offsets to 0-based line numbers. It is small but load-bearing: every
// block's end line (Projection) and every scroll-sync anchor (Renderer) is derived from it, so its
// edge cases — offsets at/over the bounds, a trailing newline, consecutive blank lines, CRLF — are
// worth pinning directly rather than only through the parsers that consume it.

// --- build: the line-start offset table ---

[<Test>]
let ``build of empty text is a single line starting at 0`` () =
    Assert.That(Lines.build "" = [| 0 |], Is.True)

[<Test>]
let ``build of a single line without a newline is one start`` () =
    Assert.That(Lines.build "abc" = [| 0 |], Is.True)

[<Test>]
let ``build records the offset just past each newline`` () =
    Assert.That(Lines.build "a\nbb\nccc" = [| 0; 2; 5 |], Is.True)

[<Test>]
let ``a trailing newline opens a further (empty) line`` () =
    Assert.That(Lines.build "a\nb\n" = [| 0; 2; 4 |], Is.True)

[<Test>]
let ``consecutive newlines each start a line`` () =
    Assert.That(Lines.build "\n\n" = [| 0; 1; 2 |], Is.True)

[<Test>]
let ``build splits on the line feed only, so CRLF keeps the CR on the prior line`` () =
    // a(0) \r(1) \n(2) b(3) — the only '\n' is at index 2, so the next line starts at 3.
    Assert.That(Lines.build "a\r\nb" = [| 0; 3 |], Is.True)

// --- lineOfOffset: the offset -> line lookup ---

let private index = Lines.build "a\nb\nc" // -> [| 0; 2; 4 |]

[<Test>]
let ``offset zero is line zero`` () =
    Assert.That(Lines.lineOfOffset index 0, Is.EqualTo(0))

[<Test>]
let ``a negative offset clamps to line zero`` () =
    Assert.That(Lines.lineOfOffset index -5, Is.EqualTo(0))

[<Test>]
let ``an offset inside the first line stays on line zero`` () =
    Assert.That(Lines.lineOfOffset index 1, Is.EqualTo(0))

[<Test>]
let ``an offset at a line start is that line`` () =
    Assert.That(Lines.lineOfOffset index 2, Is.EqualTo(1))

[<Test>]
let ``an offset inside a later line stays on that line`` () =
    Assert.That(Lines.lineOfOffset index 3, Is.EqualTo(1))

[<Test>]
let ``the last line's start resolves to the last line`` () =
    Assert.That(Lines.lineOfOffset index 4, Is.EqualTo(2))

[<Test>]
let ``an offset past the end clamps to the last line`` () =
    Assert.That(Lines.lineOfOffset index 100, Is.EqualTo(2))

[<Test>]
let ``every interior offset maps to the line whose start last preceded it`` () =
    // A spot-check across a wider index that the binary search agrees with a linear scan.
    let idx = Lines.build "0\n22\n4\n666\n10"

    let linear (offset: int) =
        if offset <= 0 then
            0
        else
            idx |> Array.findIndexBack (fun start -> start <= offset)

    for offset in 0..12 do
        Assert.That(Lines.lineOfOffset idx offset, Is.EqualTo(linear offset), $"offset {offset}")
