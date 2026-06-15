module SpecDesk.Core.Tests.ImageEngineTests

open System
open System.IO
open NUnit.Framework
open SpecDesk.Core

let private tempRepo () : string =
    let dir = Path.Combine(Path.GetTempPath(), "specdesk-img-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(dir) |> ignore
    dir

let private capture () : ImageEngine.ImageCapture =
    { Bytes = TestImages.tinyPng
      OriginalName = Some "diagram.png"
      Mime = Some "image/png" }

[<Test>]
let ``writes the image under the doc-slug folder and links it relatively`` () =
    let root = tempRepo ()

    try
        let docPath = Path.Combine(root, "billing.md")
        File.WriteAllText(docPath, "# Billing")

        match ImageEngine.insertImage root docPath ImagesConfig.defaults (capture ()) with
        | Ok result ->
            Assert.That(result.Reused, Is.False)
            Assert.That(result.RelativePath, Does.StartWith "images/billing/")
            Assert.That(result.RelativePath, Does.EndWith ".png")
            Assert.That(result.Markdown, Does.StartWith "![")

            let onDisk = Path.Combine(root, result.RelativePath.Replace('/', Path.DirectorySeparatorChar))
            Assert.That(File.Exists onDisk, Is.True)
        | Error e -> Assert.Fail e
    finally
        Directory.Delete(root, true)

[<Test>]
let ``identical content is de-duplicated to a single file`` () =
    let root = tempRepo ()

    try
        let docPath = Path.Combine(root, "billing.md")
        File.WriteAllText(docPath, "# Billing")

        match ImageEngine.insertImage root docPath ImagesConfig.defaults (capture ()) with
        | Ok first ->
            match ImageEngine.insertImage root docPath ImagesConfig.defaults (capture ()) with
            | Ok second ->
                Assert.That(second.Reused, Is.True)
                Assert.That(second.RelativePath, Is.EqualTo first.RelativePath)
                Assert.That(Directory.GetFiles(Path.Combine(root, "images", "billing")).Length, Is.EqualTo 1)
            | Error e -> Assert.Fail e
        | Error e -> Assert.Fail e
    finally
        Directory.Delete(root, true)

[<Test>]
let ``a folder rule escaping the repo is rejected`` () =
    let root = tempRepo ()

    try
        let docPath = Path.Combine(root, "billing.md")
        File.WriteAllText(docPath, "# Billing")
        let config = { ImagesConfig.defaults with Folder = "../escape" }

        match ImageEngine.insertImage root docPath config (capture ()) with
        | Ok _ -> Assert.Fail "expected the escaping folder to be rejected"
        | Error _ -> Assert.Pass()
    finally
        Directory.Delete(root, true)
