module SpecDesk.Core.Tests.SlugTests

open NUnit.Framework
open SpecDesk.Core

[<Test>]
let ``kebab slug lowercases and hyphenates`` () =
    Assert.That(Slug.slugify Slug.Kebab "Hello World!", Is.EqualTo "hello-world")

[<Test>]
let ``snake slug uses underscores`` () =
    Assert.That(Slug.slugify Slug.Snake "Hello World", Is.EqualTo "hello_world")

[<Test>]
let ``lower slug removes separators`` () =
    Assert.That(Slug.slugify Slug.Lower "Hello World", Is.EqualTo "helloworld")

[<Test>]
let ``diacritics are stripped`` () =
    Assert.That(Slug.slugify Slug.Kebab "Café Déjà", Is.EqualTo "cafe-deja")

[<Test>]
let ``runs of separators collapse and trim`` () =
    Assert.That(Slug.slugify Slug.Kebab "  a -- b  ", Is.EqualTo "a-b")

[<Test>]
let ``parseCase reads the configured case`` () =
    Assert.That(Slug.parseCase "snake", Is.EqualTo Slug.Snake)
    Assert.That(Slug.parseCase "weird", Is.EqualTo Slug.Kebab)

[<Test>]
let ``truncate preserves the suffix within the limit`` () =
    let result = Slug.truncatePreservingSuffix 15 "9f3a1c0b" "averylongname-9f3a1c0b"
    Assert.That(result.Length, Is.LessThanOrEqualTo 15)
    Assert.That(result, Does.EndWith "9f3a1c0b")

[<Test>]
let ``truncate is a no-op when within length`` () =
    Assert.That(Slug.truncatePreservingSuffix 80 "9f3a1c0b" "short-9f3a1c0b", Is.EqualTo "short-9f3a1c0b")
