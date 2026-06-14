namespace SpecDesk.Markdown.Tests

open NUnit.Framework

[<TestFixture>]
type PlaceholderTests() =

    [<Test>]
    member _.``markdown module name is Markdown``() =
        Assert.That(SpecDesk.Markdown.Placeholder.moduleName, Is.EqualTo "Markdown")
