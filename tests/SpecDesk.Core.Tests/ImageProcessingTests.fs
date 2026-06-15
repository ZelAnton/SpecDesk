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
