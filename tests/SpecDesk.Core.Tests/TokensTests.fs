module SpecDesk.Core.Tests.TokensTests

open System
open NUnit.Framework
open SpecDesk.Core

let private ctx: Tokens.TokenContext =
    { DocSlug = "billing"
      DocDir = "docs/specs"
      Date = DateTimeOffset(2026, 6, 14, 0, 0, 0, TimeSpan.Zero)
      Seq = 3
      Hash8 = "9f3a1c0b"
      OriginalName = Some "diagram" }

[<Test>]
let ``folder pattern expands docSlug`` () =
    Assert.That(Tokens.expand ctx "images/{docSlug}", Is.EqualTo "images/billing")

[<Test>]
let ``naming pattern expands all tokens (seq zero-padded)`` () =
    Assert.That(Tokens.expand ctx "{docSlug}-{date:yyyyMMdd}-{seq}-{hash8}", Is.EqualTo "billing-20260614-003-9f3a1c0b")

[<Test>]
let ``docDir token expands`` () =
    Assert.That(Tokens.expand ctx "{docDir}/images", Is.EqualTo "docs/specs/images")

[<Test>]
let ``originalName token expands`` () =
    Assert.That(Tokens.expand ctx "{originalName}-{hash8}", Is.EqualTo "diagram-9f3a1c0b")

[<Test>]
let ``slug-desc token expands to empty (deferred)`` () =
    Assert.That(Tokens.expand ctx "{docSlug}{slug:DESC}", Is.EqualTo "billing")
