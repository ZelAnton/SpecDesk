module SpecDesk.Core.Tests.ImagesConfigTests

open NUnit.Framework
open SpecDesk.Core

[<Test>]
let ``no file yields defaults`` () =
    Assert.That(ImagesConfig.parse None = ImagesConfig.defaults, Is.True)

[<Test>]
let ``provided fields override defaults`` () =
    let toml =
        "[images]\n"
        + "folder = \"assets/{docSlug}\"\n"
        + "max-width = 1000   # downscale\n"
        + "strip-metadata = false\n"
        + "allowed = [\"png\", \"jpg\"]\n"
        + "case = \"snake\"\n"

    let config = ImagesConfig.parse (Some toml)
    Assert.That(config.Folder, Is.EqualTo "assets/{docSlug}")
    Assert.That(config.MaxWidth, Is.EqualTo 1000)
    Assert.That(config.StripMetadata, Is.False)
    Assert.That(config.Allowed = [ "png"; "jpg" ], Is.True)
    Assert.That(config.Case, Is.EqualTo Slug.Snake)
    // Unspecified fields keep their defaults.
    Assert.That(config.Naming, Is.EqualTo ImagesConfig.defaults.Naming)

[<Test>]
let ``keys outside the images table are ignored`` () =
    let toml = "[repo]\nfolder = \"wrong\"\n\n[images]\npreferred = \"webp\"\n"
    let config = ImagesConfig.parse (Some toml)
    Assert.That(config.Preferred, Is.EqualTo "webp")
    Assert.That(config.Folder, Is.EqualTo ImagesConfig.defaults.Folder)

[<Test>]
let ``unparseable content falls back to defaults`` () =
    let config = ImagesConfig.parse (Some "}{ not valid at all")
    Assert.That(config.Folder, Is.EqualTo ImagesConfig.defaults.Folder)
