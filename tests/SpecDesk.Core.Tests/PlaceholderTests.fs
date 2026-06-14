namespace SpecDesk.Core.Tests

open NUnit.Framework

[<TestFixture>]
type PlaceholderTests() =

    [<Test>]
    member _.``core module name is Core``() =
        Assert.That(SpecDesk.Core.Placeholder.moduleName, Is.EqualTo "Core")
