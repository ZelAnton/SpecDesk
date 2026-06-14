namespace SpecDesk.Diff.Tests

open NUnit.Framework

[<TestFixture>]
type PlaceholderTests() =

    [<Test>]
    member _.``diff module name is Diff``() =
        Assert.That(SpecDesk.Diff.Placeholder.moduleName, Is.EqualTo "Diff")
