module SpecDesk.Core.Tests.ImageProcessingTests

open NUnit.Framework
open SpecDesk.Core

[<Test>]
let ``a png is processed and the hash is deterministic`` () =
    match ImageProcessing.processImage ImagesConfig.defaults TestImages.tinyPng with
    | Ok first ->
        Assert.That(first.Ext, Is.EqualTo "png")
        Assert.That(first.Hash.Length, Is.EqualTo 64)

        match ImageProcessing.processImage ImagesConfig.defaults TestImages.tinyPng with
        | Ok second -> Assert.That(second.Hash, Is.EqualTo first.Hash)
        | Error e -> Assert.Fail e
    | Error e -> Assert.Fail e

[<Test>]
let ``corrupt bytes are rejected`` () =
    match ImageProcessing.processImage ImagesConfig.defaults [| 1uy; 2uy; 3uy |] with
    | Ok _ -> Assert.Fail "expected an error for non-image bytes"
    | Error _ -> Assert.Pass()

// M-04 ————————————————————————————————————————————————————————————————————————————————————————————

let private utf8Bom = [| 0xEFuy; 0xBBuy; 0xBFuy |]
let private svgBytes = System.Text.Encoding.UTF8.GetBytes "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"

[<Test>]
let ``an SVG with a leading UTF-8 BOM is recognised as SVG, not rejected`` () =
    let bomPrefixed = Array.append utf8Bom svgBytes

    match ImageProcessing.processImage ImagesConfig.defaults bomPrefixed with
    | Ok result ->
        Assert.That(result.Ext, Is.EqualTo "svg")
        // Passed through verbatim, BOM included — SVG is a pass-through format, never re-encoded.
        Assert.That(result.Bytes = bomPrefixed, Is.True)
    | Error e -> Assert.Fail e

[<Test>]
let ``an SVG with a leading BOM and an xml prolog is also recognised`` () =
    let withProlog =
        System.Text.Encoding.UTF8.GetBytes "<?xml version=\"1.0\"?>\n<svg></svg>"

    Assert.That(ImageProcessing.looksLikeSvg (Array.append utf8Bom withProlog), Is.True)

[<Test>]
let ``looksLikeSvg recognises a bare svg tag, with or without a BOM`` () =
    Assert.That(ImageProcessing.looksLikeSvg svgBytes, Is.True)
    Assert.That(ImageProcessing.looksLikeSvg (Array.append utf8Bom svgBytes), Is.True)

[<Test>]
let ``looksLikeSvg still rejects non-SVG content, BOM or not`` () =
    Assert.That(ImageProcessing.looksLikeSvg [| 1uy; 2uy; 3uy |], Is.False)
    Assert.That(ImageProcessing.looksLikeSvg (Array.append utf8Bom [| 1uy; 2uy; 3uy |]), Is.False)
    Assert.That(ImageProcessing.looksLikeSvg [||], Is.False)
