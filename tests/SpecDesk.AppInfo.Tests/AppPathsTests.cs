namespace SpecDesk.AppInfo.Tests;

[TestFixture]
public sealed class AppPathsTests
{
    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    [Test]
    public void Root_IsLocalAppDataSlashSpecDesk()
    {
        Assert.That(AppPaths.Root, Is.EqualTo(Path.Combine(LocalAppData, "SpecDesk")));
    }

    // Pins the exact string the DPAPI-encrypted token store resolves its file under (FileTokenStore
    // combines "github-token" onto whatever directory Program.cs passes as authDir). Before AppPaths
    // existed, Program.cs hand-rolled this same literal path; this test is the explicit guard mentioned
    // in AppPaths' doc comment that consolidating the three call sites must never silently change it and
    // orphan an existing user's signed-in session.
    [Test]
    public void Auth_IsByteIdenticalToTheOriginalHandRolledPath()
    {
        string expected = Path.Combine(LocalAppData, "SpecDesk", "auth");

        Assert.That(AppPaths.Auth, Is.EqualTo(expected));
    }

    [Test]
    public void SampleRepo_IsByteIdenticalToTheOriginalHandRolledPath()
    {
        string expected = Path.Combine(LocalAppData, "SpecDesk", "sample-repo");

        Assert.That(AppPaths.SampleRepo, Is.EqualTo(expected));
    }

    [Test]
    public void Logs_IsByteIdenticalToTheOriginalHandRolledPath()
    {
        string expected = Path.Combine(LocalAppData, "SpecDesk", "logs");

        Assert.That(AppPaths.Logs, Is.EqualTo(expected));
    }

    [Test]
    public void LogFilePrefix_IsTheOriginalLowercaseLiteral()
    {
        Assert.That(AppPaths.LogFilePrefix, Is.EqualTo("specdesk-"));
    }

    [Test]
    public void Workspace_IsRootSlashWorkspaceJson()
    {
        string expected = Path.Combine(LocalAppData, "SpecDesk", "workspace.json");

        Assert.That(AppPaths.Workspace, Is.EqualTo(expected));
    }

    // SPECDESK_DATA_ROOT overrides the root (moving sample repo / auth / logs together); unset or
    // malformed must keep the byte-identical default that the pins above depend on.
    [Test]
    public void ResolveRoot_Unset_IsByteIdenticalToTheDefault()
    {
        Assert.That(AppPaths.ResolveRoot(_ => null), Is.EqualTo(Path.Combine(LocalAppData, "SpecDesk")));
    }

    [TestCase("")]
    [TestCase("   ")]
    public void ResolveRoot_EmptyOrWhitespace_FallsBackToTheDefault(string raw)
    {
        Assert.That(AppPaths.ResolveRoot(_ => raw), Is.EqualTo(Path.Combine(LocalAppData, "SpecDesk")));
    }

    [Test]
    public void ResolveRoot_Set_ReturnsTheOverrideAsAnAbsolutePath()
    {
        string resolved = AppPaths.ResolveRoot(key => key == "SPECDESK_DATA_ROOT" ? "data/here" : null);

        Assert.That(resolved, Is.EqualTo(Path.GetFullPath("data/here")));
        Assert.That(Path.IsPathRooted(resolved), Is.True);
    }

    [Test]
    public void ResolveRoot_Malformed_FallsBackToTheDefaultWithoutThrowing()
    {
        Assert.That(AppPaths.ResolveRoot(_ => "bad\0path"), Is.EqualTo(Path.Combine(LocalAppData, "SpecDesk")));
    }
}
