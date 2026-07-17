module SpecDesk.Core.Tests.ImageEngineTests

open System
open System.IO
open NUnit.Framework
open SpecDesk.Core

let private tempRepo () : string =
    let dir =
        Path.Combine(Path.GetTempPath(), "specdesk-img-" + Guid.NewGuid().ToString("N"))

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

            let onDisk =
                Path.Combine(root, result.RelativePath.Replace('/', Path.DirectorySeparatorChar))

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

// M-02/M-03 —————————————————————————————————————————————————————————————————————————————————————

[<Test>]
let ``a folder containing a space is percent-encoded in the emitted link, not the on-disk path`` () =
    let root = tempRepo ()

    try
        let docPath = Path.Combine(root, "billing.md")
        File.WriteAllText(docPath, "# Billing")
        // A relative path with a space breaks a BARE CommonMark link destination (and un-escaped it
        // would render `![image](../my images/x.png)` literally, per M-03).
        let config =
            { ImagesConfig.defaults with
                Folder = "my images/{docSlug}" }

        match ImageEngine.insertImage root docPath config (capture ()) with
        | Ok result ->
            // The raw, on-disk relative path keeps the literal space — only the emitted link is escaped.
            Assert.That(result.RelativePath, Does.Contain "my images/")
            Assert.That(result.Markdown, Does.Not.Contain " ")
            Assert.That(result.Markdown, Does.Contain "my%20images/")

            let onDisk =
                Path.Combine(root, result.RelativePath.Replace('/', Path.DirectorySeparatorChar))

            Assert.That(File.Exists onDisk, Is.True)
        | Error e -> Assert.Fail e
    finally
        Directory.Delete(root, true)

[<Test>]
let ``percentEncodeForLink escapes space, parens, hash, and a literal percent`` () =
    Assert.That(ImageEngine.percentEncodeForLink "images/my images/x.png", Is.EqualTo "images/my%20images/x.png")
    Assert.That(ImageEngine.percentEncodeForLink "images/(draft)/x.png", Is.EqualTo "images/%28draft%29/x.png")
    Assert.That(ImageEngine.percentEncodeForLink "images/plan#1/x.png", Is.EqualTo "images/plan%231/x.png")
    // Escaped FIRST, so a literal "%" in a path never becomes ambiguous with these escapes on decode.
    Assert.That(ImageEngine.percentEncodeForLink "images/100%/x.png", Is.EqualTo "images/100%25/x.png")

[<Test>]
let ``percentEncodeForLink leaves an already-safe path untouched`` () =
    Assert.That(
        ImageEngine.percentEncodeForLink "images/billing/diagram-1.png",
        Is.EqualTo "images/billing/diagram-1.png"
    )

[<Test>]
let ``writeFileAtomically leaves no temp artifact behind and writes the exact bytes`` () =
    let root = tempRepo ()

    try
        let target = Path.Combine(root, "x.png")
        ImageEngine.writeFileAtomically target TestImages.tinyPng

        Assert.That(File.Exists target, Is.True)
        Assert.That(File.ReadAllBytes target = TestImages.tinyPng, Is.True)
        // No orphaned "<target>.<guid>.tmp" left in the directory after a successful write.
        Assert.That(Directory.GetFiles(root).Length, Is.EqualTo 1)
    finally
        Directory.Delete(root, true)

[<Test>]
let ``writeFileAtomically never leaves a partial file under the target name when the final move fails`` () =
    let root = tempRepo ()

    try
        // A directory already occupies the target name, so the rename step must fail. This can't
        // simulate a genuine OS-level crash mid-write (that guarantee comes from File.Move's platform
        // rename semantics, not something a fast unit test can force) — what IS checked here is the
        // surrounding behavior any failure of the final rename must have: the pre-existing target is
        // left untouched, and the temp file this attempt wrote gets cleaned up rather than orphaned.
        let target = Path.Combine(root, "x.png")
        Directory.CreateDirectory target |> ignore

        let threw =
            try
                ImageEngine.writeFileAtomically target TestImages.tinyPng
                false
            with _ ->
                true

        Assert.That(threw, Is.True, "writing over an existing directory must fail, not silently succeed")
        Assert.That(Directory.Exists target, Is.True, "the pre-existing directory must be untouched")
        // The temp file this attempt wrote is cleaned up, not left behind as an orphan.
        Assert.That(Directory.GetFiles(root).Length, Is.EqualTo 0)
    finally
        Directory.Delete(root, true)

[<Test>]
let ``a folder rule escaping the repo is rejected`` () =
    let root = tempRepo ()

    try
        let docPath = Path.Combine(root, "billing.md")
        File.WriteAllText(docPath, "# Billing")

        let config =
            { ImagesConfig.defaults with
                Folder = "../escape" }

        match ImageEngine.insertImage root docPath config (capture ()) with
        | Ok _ -> Assert.Fail "expected the escaping folder to be rejected"
        | Error _ -> Assert.Pass()
    finally
        Directory.Delete(root, true)

// —— Path-safety guards, exercised directly (internal via InternalsVisibleTo) ——————————————————————

[<Test>]
let ``isInside accepts a path within the repo and the root itself`` () =
    let root = tempRepo ()

    try
        Assert.That(ImageEngine.isInside root (Path.Combine(root, "images", "x.png")), Is.True)
        Assert.That(ImageEngine.isInside root root, Is.True)
    finally
        Directory.Delete(root, true)

[<Test>]
let ``isInside rejects a traversal out of the repo`` () =
    let root = tempRepo ()

    try
        Assert.That(ImageEngine.isInside root (Path.Combine(root, "..", "escape")), Is.False)
    finally
        Directory.Delete(root, true)

[<Test>]
let ``isInside rejects a sibling that merely shares the root's name prefix`` () =
    let root = tempRepo ()

    try
        // "<root>-evil" starts with the root string but is NOT inside it — the directory separator in
        // the prefix check is what blocks this near-miss.
        Assert.That(ImageEngine.isInside root (root + "-evil"), Is.False)
    finally
        Directory.Delete(root, true)

[<Test>]
let ``sanitizeExt lowercases and keeps only ascii alphanumerics`` () =
    Assert.That(ImageEngine.sanitizeExt "png", Is.EqualTo "png")
    Assert.That(ImageEngine.sanitizeExt "PNG", Is.EqualTo "png")
    Assert.That(ImageEngine.sanitizeExt "JPEG", Is.EqualTo "jpeg")
    Assert.That(ImageEngine.sanitizeExt "jp eg", Is.EqualTo "jpeg")
    // A traversal smuggled through the preferred ext is reduced to its alnum residue, never a path.
    Assert.That(ImageEngine.sanitizeExt "png/../..", Is.EqualTo "png")

[<Test>]
let ``sanitizeExt falls back to png when nothing usable remains`` () =
    Assert.That(ImageEngine.sanitizeExt "", Is.EqualTo "png")
    Assert.That(ImageEngine.sanitizeExt "..", Is.EqualTo "png")
    Assert.That(ImageEngine.sanitizeExt "///", Is.EqualTo "png")
