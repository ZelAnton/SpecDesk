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
}
