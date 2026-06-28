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
}
