namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class WorkflowSeedsTests
{
    // SanitizeBranchName mirrors the webview's live draft-name cleanup; these pin the ref rules.
    [TestCase("feature/login", ExpectedResult = "feature/login")] // already valid → untouched
    [TestCase(null, ExpectedResult = "")] // null → empty (caller falls back to the generated name)
    [TestCase("   ", ExpectedResult = "")] // whitespace-only → empty
    [TestCase("  trim me  ", ExpectedResult = "trim_me")] // outer whitespace trimmed, inner space → _
    [TestCase("a\\b", ExpectedResult = "a/b")] // backslash → forward slash
    [TestCase("billing#report!", ExpectedResult = "billing_report")] // illegal chars → _ (trailing trimmed)
    [TestCase("a   b", ExpectedResult = "a_b")] // a run of separators collapses to one
    [TestCase("a///b", ExpectedResult = "a/b")] // run of slashes collapses
    [TestCase("--lead", ExpectedResult = "lead")] // leading separators trimmed
    [TestCase("trail--", ExpectedResult = "trail")] // trailing separators trimmed
    [TestCase(".dotted.", ExpectedResult = "dotted")] // leading/trailing dots trimmed
    [TestCase("a..b", ExpectedResult = "a_b")] // ".." (illegal in a ref) → _
    [TestCase("my.lock", ExpectedResult = "my")] // a trailing ".lock" is stripped (and re-trimmed)
    [TestCase("My.LOCK", ExpectedResult = "My")] // ".lock" match is case-insensitive
    [TestCase("@#$", ExpectedResult = "")] // nothing usable → empty
    public string SanitizeBranchName_appliesRefRules(string? raw) => WorkflowSeeds.SanitizeBranchName(raw);

    [Test]
    public void DocSlug_dropsDirectoryAndExtension()
    {
        string slug = WorkflowSeeds.DocSlug(Path.Combine("repo", "specs", "billing.md"));
        Assert.That(slug, Is.EqualTo("billing"));
    }
}
