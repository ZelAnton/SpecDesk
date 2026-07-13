namespace SpecDesk.GitHub.Tests;

[TestFixture]
public sealed class FileTokenStoreTests
{
    private readonly List<string> _dirs = [];

    // A fresh, NOT-yet-created auth dir (the store is responsible for creating it).
    private string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "specdesk-gh-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(dir);
        return dir;
    }

    [TearDown]
    public void TearDown()
    {
        foreach (string dir in _dirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        _dirs.Clear();
    }

    private static FileTokenStore Store(string dir) => new(new IdentityTokenProtector(), dir);

    [Test]
    public void Save_then_Load_round_trips_the_token()
    {
        FileTokenStore store = Store(NewDir());
        StoredToken token = new("gho_abc", "octocat");

        store.Save(token);

        Assert.That(store.Load(), Is.EqualTo(token));
    }

    [Test]
    public void Load_with_no_file_returns_null()
    {
        Assert.That(Store(NewDir()).Load(), Is.Null);
    }

    [Test]
    public void Save_creates_the_auth_directory_when_missing()
    {
        string dir = NewDir();
        Assert.That(Directory.Exists(dir), Is.False);

        Store(dir).Save(new StoredToken("t", "u"));

        Assert.That(Directory.Exists(dir), Is.True);
    }

    [Test]
    public void A_second_Save_overwrites_the_first()
    {
        FileTokenStore store = Store(NewDir());
        store.Save(new StoredToken("old", "olduser"));
        store.Save(new StoredToken("new", "newuser"));

        Assert.That(store.Load(), Is.EqualTo(new StoredToken("new", "newuser")));
    }

    [Test]
    public void A_corrupt_token_file_loads_as_null_rather_than_throwing()
    {
        string dir = NewDir();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "github-token"), "not valid json");

        Assert.That(Store(dir).Load(), Is.Null);
    }

    [Test]
    public void Clear_removes_the_token_and_is_idempotent()
    {
        FileTokenStore store = Store(NewDir());
        store.Save(new StoredToken("t", "u"));

        store.Clear();

        Assert.Multiple(() =>
        {
            Assert.That(store.Load(), Is.Null);
            Assert.DoesNotThrow(store.Clear); // already gone
        });
    }

    [TestCase(typeof(IOException))]
    [TestCase(typeof(UnauthorizedAccessException))]
    public void Clear_records_sign_out_when_token_deletion_fails(Type exceptionType)
    {
        string dir = NewDir();
        FileTokenStore initial = Store(dir);
        initial.Save(new StoredToken("stale-token", "octocat"));
        FileTokenStore failingDelete = new(
            new IdentityTokenProtector(),
            dir,
            _ => throw (Exception)Activator.CreateInstance(exceptionType, "delete failed")!);

        Assert.DoesNotThrow(failingDelete.Clear);

        // A newly-created store models the next application process. The undeleted token must not
        // resurrect the session, and a later successful sign-in must make the store usable again.
        FileTokenStore restarted = Store(dir);
        FakeDeviceFlowApi api = new(
            new DeviceCodeResponse(
                "device-code", "ABCD-1234", new Uri("https://github.com/login/device"),
                TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5)),
            "octocat");
        GitHubDeviceFlowAuth restartedAuth = new(
            GitHubAuthOptions.ForClient("test-client-id"),
            api,
            restarted,
            TimeProvider.System,
            delay: null);
        bool callbackInvoked = false;

        Assert.Multiple(() =>
        {
            Assert.That(restartedAuth.IsSignedIn(), Is.False);
            Assert.That(restartedAuth.SignedInLogin(), Is.Null);
            Assert.ThrowsAsync<InvalidOperationException>(() =>
                restartedAuth.WithAccessTokenAsync((_, _) =>
                {
                    callbackInvoked = true;
                    return Task.FromResult(0);
                }));
            Assert.That(callbackInvoked, Is.False);
        });

        restarted.Save(new StoredToken("new-token", "hubot"));
        Assert.That(restarted.Load(), Is.EqualTo(new StoredToken("new-token", "hubot")));
    }

    [TestCase(typeof(IOException))]
    [TestCase(typeof(UnauthorizedAccessException))]
    public void Clear_surfaces_a_failure_to_persist_the_sign_out_marker(Type exceptionType)
    {
        FileTokenStore store = new(
            new IdentityTokenProtector(),
            NewDir(),
            File.Delete,
            (_, _) => throw (Exception)Activator.CreateInstance(exceptionType, "marker write failed")!);

        Assert.That(() => store.Clear(), Throws.TypeOf(exceptionType));
    }
}
