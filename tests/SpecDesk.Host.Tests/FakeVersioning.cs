using SpecDesk.Git;

namespace SpecDesk.Host.Tests;

/// <summary>
/// An in-memory <see cref="IDocumentVersioning"/> for controller tests: it records the calls the
/// host makes and returns plausible results without touching a real git repository.
/// </summary>
internal sealed class FakeVersioning : IDocumentVersioning
{
    private int _commits;

    public bool Versioned { get; set; } = true;

    public string Branch { get; private set; } = "main";

    public int BeginEditCalls { get; private set; }

    public int SaveVersionCalls { get; private set; }

    public string? LastCommitMessage { get; private set; }

    public bool DiscardCalled { get; private set; }

    public bool IsVersioned(string repoRoot) => Versioned;

    public void Initialize(string repoRoot, string commitMessage)
    {
        // Nothing to do; the fake is "born" with `main` checked out.
    }

    public string? CurrentBranch(string repoRoot) => Branch;

    public EditSession BeginEdit(string repoRoot, string branchName, string preferredBase)
    {
        BeginEditCalls++;
        Branch = branchName;
        return new EditSession(branchName, preferredBase);
    }

    public CommitResult SaveVersion(string repoRoot, string message)
    {
        SaveVersionCalls++;
        LastCommitMessage = message;
        _commits++;
        return new CommitResult(true, $"sha{_commits}", DateTimeOffset.UnixEpoch);
    }

    public void Discard(string repoRoot, string workingBranch, string baseBranch)
    {
        DiscardCalled = true;
        Branch = baseBranch;
    }
}
