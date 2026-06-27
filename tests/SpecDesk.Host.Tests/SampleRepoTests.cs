using Microsoft.Extensions.Logging.Abstractions;

namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class SampleRepoTests
{
    private readonly List<string> _dirs = [];

    private string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "specdesk-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
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

    [Test]
    public void First_run_copies_the_bundled_samples_and_initializes_the_repo()
    {
        string repoRoot = TempDir();
        string bundled = TempDir();
        File.WriteAllText(Path.Combine(bundled, "welcome.md"), "ORIGINAL");
        File.WriteAllText(Path.Combine(bundled, ".spectool.toml"), "[images]\n");
        FakeVersioning versioning = new() { Versioned = false };

        string welcome = SampleRepo.EnsureSeeded(repoRoot, bundled, versioning, NullLogger.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(welcome, Is.EqualTo(Path.Combine(repoRoot, "welcome.md")));
            Assert.That(File.ReadAllText(welcome), Is.EqualTo("ORIGINAL"));
            Assert.That(File.Exists(Path.Combine(repoRoot, ".spectool.toml")), Is.True);
            Assert.That(versioning.InitializeCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public void A_second_run_neither_re_copies_over_the_author_s_edits_nor_re_initializes()
    {
        string repoRoot = TempDir();
        string bundled = TempDir();
        File.WriteAllText(Path.Combine(bundled, "welcome.md"), "ORIGINAL");
        FakeVersioning versioning = new() { Versioned = false };

        string welcome = SampleRepo.EnsureSeeded(repoRoot, bundled, versioning, NullLogger.Instance);
        File.WriteAllText(welcome, "EDITED"); // the author edits the seeded sample, then restarts
        SampleRepo.EnsureSeeded(repoRoot, bundled, versioning, NullLogger.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(welcome), Is.EqualTo("EDITED")); // not clobbered
            Assert.That(versioning.InitializeCalls, Is.EqualTo(1)); // already versioned → not re-initialized
        });
    }

    [Test]
    public void A_seeding_failure_does_not_crash_the_launch_and_still_returns_the_welcome_path()
    {
        string repoRoot = TempDir();
        string bundled = TempDir();
        File.WriteAllText(Path.Combine(bundled, "welcome.md"), "x");
        FakeVersioning versioning = new() { Versioned = false, ThrowOnInitialize = true };

        string welcome = "";
        Assert.DoesNotThrow(() =>
            welcome = SampleRepo.EnsureSeeded(repoRoot, bundled, versioning, NullLogger.Instance));
        Assert.That(welcome, Is.EqualTo(Path.Combine(repoRoot, "welcome.md")));
    }

    [Test]
    public void With_no_bundled_samples_directory_it_skips_the_copy_but_still_initializes()
    {
        string repoRoot = TempDir();
        string bundled = Path.Combine(Path.GetTempPath(), "specdesk-missing-" + Guid.NewGuid().ToString("N"));
        FakeVersioning versioning = new() { Versioned = false };

        SampleRepo.EnsureSeeded(repoRoot, bundled, versioning, NullLogger.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Combine(repoRoot, "welcome.md")), Is.False);
            Assert.That(versioning.InitializeCalls, Is.EqualTo(1));
        });
    }
}
