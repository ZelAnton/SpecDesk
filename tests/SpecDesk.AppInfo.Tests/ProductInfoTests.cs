namespace SpecDesk.AppInfo.Tests;

[TestFixture]
public sealed class ProductInfoTests
{
    [Test]
    public void Name_IsSpecDesk()
    {
        Assert.That(ProductInfo.Name, Is.EqualTo("SpecDesk"));
    }

    // Directory.Build.props' <Version> (0.1.0 at the time of writing) is what the SDK stamps onto every
    // assembly's AssemblyVersion; ProductInfo.Version reads it back rather than a second hard-coded
    // literal, so this only checks the shape (three dot-separated numeric parts), not a specific value
    // that would otherwise need editing on every release bump.
    [Test]
    public void Version_HasThreeDotSeparatedNumericParts()
    {
        string[] parts = ProductInfo.Version.Split('.');

        Assert.Multiple(() =>
        {
            Assert.That(parts, Has.Length.EqualTo(3));
            Assert.That(parts, Has.All.Matches<string>(part => int.TryParse(part, out _)));
        });
    }
}
