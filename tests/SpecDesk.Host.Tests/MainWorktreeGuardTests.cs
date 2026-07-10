using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;

namespace SpecDesk.Host.Tests;

// The runtime startup gate: given the app's base directory, it classifies how the working copy the app
// was built from relates to the local 'main' and refuses to launch a stale MAIN working copy (which
// would run an old UI). A published app has no SpecDesk.slnx above it (integrity-not-applicable); the
// dev-tree tests plant a slnx marker so the guard treats the tree as a source checkout and compares it
// to 'main'. Each test drives a throwaway git repository under the system temp directory.
[TestFixture]
public sealed class MainWorktreeGuardTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "specdesk-worktree-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        // Git marks pack files read-only; clear the attribute so the throwaway tree can be deleted.
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void Inspect_MainWorkingCopyBehindMain_IsBehind()
    {
        (string baseDir, _) = BuildBehindMainWorktree();

        MainWorktreeStatus status = MainWorktreeGuard.Inspect(baseDir);

        Assert.Multiple(() =>
        {
            Assert.That(status.Relation, Is.EqualTo(MainWorktreeRelation.Behind));
            Assert.That(status.Role, Is.EqualTo("main-worktree"));
            Assert.That(status.IsStale, Is.True);
            Assert.That(status.Head, Is.Not.Null);
            Assert.That(status.Target, Is.Not.Null);
        });
    }

    [Test]
    public void EnsureCurrent_MainWorkingCopyBehindMain_Throws()
    {
        (string baseDir, _) = BuildBehindMainWorktree();

        Assert.That(
            () => MainWorktreeGuard.EnsureCurrent(baseDir, NullLogger.Instance, allowStale: false),
            Throws.TypeOf<MainWorktreeStaleException>());
    }

    [Test]
    public void EnsureCurrent_BehindButOptedIn_DoesNotThrow()
    {
        (string baseDir, _) = BuildBehindMainWorktree();

        MainWorktreeStatus status =
            MainWorktreeGuard.EnsureCurrent(baseDir, NullLogger.Instance, allowStale: true);

        Assert.That(status.Relation, Is.EqualTo(MainWorktreeRelation.Behind));
    }

    [Test]
    public void Inspect_MainWorkingCopyAtMain_IsCurrent()
    {
        string workDir = InitRepo();
        string baseDir = NestedBase(workDir);
        Commit(workDir, "src/app.ts", "v1", "commit1");
        EnsureMainBranch(workDir);
        Commit(workDir, "src/app.ts", "v2", "commit2");

        MainWorktreeStatus status = MainWorktreeGuard.Inspect(baseDir);

        Assert.Multiple(() =>
        {
            Assert.That(status.Relation, Is.EqualTo(MainWorktreeRelation.Current));
            Assert.That(status.IsStale, Is.False);
        });
        Assert.DoesNotThrow(
            () => MainWorktreeGuard.EnsureCurrent(baseDir, NullLogger.Instance, allowStale: false));
    }

    [Test]
    public void Inspect_DivergedTopicCheckout_IsNotStale()
    {
        string workDir = InitRepo();
        string baseDir = NestedBase(workDir);
        string baseSha = Commit(workDir, "src/app.ts", "v1", "base");
        EnsureMainBranch(workDir);

        using (Repository repo = new(workDir))
        {
            // A topic branch off the base with its own commit, while main gets a different commit.
            Branch topic = repo.CreateBranch("topic", baseSha);
            Commands.Checkout(repo, topic);
        }

        Commit(workDir, "src/topic.ts", "T", "topic-only");
        using (Repository repo = new(workDir))
        {
            Commands.Checkout(repo, repo.Branches["main"]);
        }

        Commit(workDir, "src/main.ts", "M", "main-only");
        using (Repository repo = new(workDir))
        {
            Commands.Checkout(repo, repo.Branches["topic"]);
        }

        MainWorktreeStatus status = MainWorktreeGuard.Inspect(baseDir);

        Assert.Multiple(() =>
        {
            Assert.That(status.Relation, Is.EqualTo(MainWorktreeRelation.Diverged));
            Assert.That(status.IsStale, Is.False);
        });
    }

    [Test]
    public void Inspect_NoLocalMain_IsNoTarget()
    {
        string workDir = InitRepo();
        string baseDir = NestedBase(workDir);
        Commit(workDir, "src/app.ts", "v1", "commit1");
        // Deliberately do NOT create a 'main' branch — e.g. a fresh CI checkout on another branch.
        using (Repository repo = new(workDir))
        {
            if (repo.Branches["main"] is not null)
            {
                repo.Branches.Remove("main");
            }
        }

        MainWorktreeStatus status = MainWorktreeGuard.Inspect(baseDir);

        Assert.Multiple(() =>
        {
            Assert.That(status.Relation, Is.EqualTo(MainWorktreeRelation.NoTarget));
            Assert.That(status.IsStale, Is.False);
        });
    }

    [Test]
    public void Inspect_IsolatedTaskWorktree_IsExemptEvenWhenBehind()
    {
        // Shared repo whose main has advanced past HEAD (behind, if it were the main copy)...
        (_, string workDir) = BuildBehindMainWorktree();
        // ...but the app runs from a workspace NESTED under the shared repo, with its own slnx marker and
        // no own .git — a jj workspace. git discovery walks up to the shared repo, so the role must come
        // from structure (the checkout root differs from git's working directory), not the git HEAD.
        string nested = Path.Combine(workDir, ".work", "worktrees", "T-1");
        Directory.CreateDirectory(Path.Combine(nested, ".jj"));
        File.WriteAllText(Path.Combine(nested, "SpecDesk.slnx"), "<Solution />\n");
        string baseDir = Path.Combine(nested, "src", "SpecDesk.Host", "bin");
        Directory.CreateDirectory(baseDir);

        MainWorktreeStatus status = MainWorktreeGuard.Inspect(baseDir);

        Assert.Multiple(() =>
        {
            Assert.That(status.Relation, Is.EqualTo(MainWorktreeRelation.IsolatedWorktree));
            Assert.That(status.Role, Is.EqualTo("task-worktree"));
            Assert.That(status.IsStale, Is.False);
        });
    }

    [Test]
    public void Inspect_PublishedAppWithNoSourceTree_IsNotApplicable()
    {
        // A base directory with no SpecDesk.slnx anywhere above it — a shipped app, not a checkout.
        string baseDir = Path.Combine(_root, "installed", "app");
        Directory.CreateDirectory(baseDir);

        MainWorktreeStatus status = MainWorktreeGuard.Inspect(baseDir);

        Assert.Multiple(() =>
        {
            Assert.That(status.Relation, Is.EqualTo(MainWorktreeRelation.NotApplicable));
            Assert.That(status.IsStale, Is.False);
        });
        Assert.DoesNotThrow(
            () => MainWorktreeGuard.EnsureCurrent(baseDir, NullLogger.Instance, allowStale: false));
    }

    // ---- helpers -------------------------------------------------------------------------------

    // A main working copy whose 'main' has advanced one commit past the detached HEAD. Returns the app
    // base directory (nested under the work tree) and the work-tree root.
    private (string BaseDir, string WorkDir) BuildBehindMainWorktree()
    {
        string workDir = InitRepo();
        string baseDir = NestedBase(workDir);
        string ancestor = Commit(workDir, "src/app.ts", "v1", "commit1");
        EnsureMainBranch(workDir);
        Commit(workDir, "src/app.ts", "v2", "commit2");
        using (Repository repo = new(workDir))
        {
            // Detach onto the ancestor: the working copy now lags 'main' by one commit.
            Commands.Checkout(repo, repo.Lookup<Commit>(ancestor));
        }

        return (baseDir, workDir);
    }

    // Initialise a git work tree with an (untracked) SpecDesk.slnx marker at its root, so the guard
    // treats it as a source checkout. The marker is untracked so it survives every checkout.
    private string InitRepo()
    {
        string workDir = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        Repository.Init(workDir);
        File.WriteAllText(Path.Combine(workDir, "SpecDesk.slnx"), "<Solution />\n");
        return workDir;
    }

    // The app base directory a real build would use, nested under the work tree.
    private static string NestedBase(string workDir)
    {
        string baseDir = Path.Combine(workDir, "src", "SpecDesk.Host", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(baseDir);
        return baseDir;
    }

    private static string Commit(string workDir, string relPath, string content, string message)
    {
        string full = Path.Combine(workDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        using Repository repo = new(workDir);
        Commands.Stage(repo, relPath);
        Signature who = new("SpecDesk Test", "test@specdesk.local", DateTimeOffset.UtcNow);
        return repo.Commit(message, who, who, new CommitOptions()).Sha;
    }

    // Ensure a local 'main' branch exists and is checked out (git's default branch name varies by config).
    private static void EnsureMainBranch(string workDir)
    {
        using Repository repo = new(workDir);
        Branch main = repo.Branches["main"] ?? repo.CreateBranch("main");
        Commands.Checkout(repo, main);
    }
}
