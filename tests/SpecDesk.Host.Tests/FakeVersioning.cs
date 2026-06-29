using SpecDesk.Git;

namespace SpecDesk.Host.Tests;

/// <summary>
/// An in-memory <see cref="IDocumentVersioning"/> (and <see cref="IGitPublishing"/>, mirroring the real
/// LibGit2 type) for controller tests: it records the calls the host makes and returns plausible results
/// without touching a real git repository.
/// </summary>
internal sealed class FakeVersioning : IDocumentVersioning, IGitPublishing
{
    private int _commits;

    public bool Versioned { get; set; } = true;

    public string Branch { get; private set; } = "main";

    public int BeginEditCalls { get; private set; }

    public int SaveVersionCalls { get; private set; }

    public string? LastCommitMessage { get; private set; }

    public bool DiscardCalled { get; private set; }

    public int InitializeCalls { get; private set; }

    /// <summary>The remote URL <see cref="RemoteUrl"/> returns; a GitHub HTTPS URL by default. Set to a
    /// non-GitHub URL (or null) to exercise the "not a GitHub repository" path.</summary>
    public string? RemoteUrlValue { get; set; } = "https://github.com/octo/spec-repo.git";

    /// <summary>The note <see cref="LastVersionNote"/> returns (the seed for the pull-request title).</summary>
    public string? LastNoteValue { get; set; } = "Clarify the refund window";

    /// <summary>When set, <see cref="RemoteUrl"/> throws — to exercise the "a libgit2 fault on the
    /// synchronous read must not wedge Send for review" path.</summary>
    public bool ThrowOnRemoteUrl { get; set; }

    public int PushBranchCalls { get; private set; }

    public string? PushedBranch { get; private set; }

    public string? PushedToken { get; private set; }

    /// <summary>When set, <see cref="Initialize"/> throws — to exercise the seed-must-not-crash path.</summary>
    public bool ThrowOnInitialize { get; set; }

    /// <summary>Canned "last committed version" returned by <see cref="ReadHeadContent"/>.</summary>
    public string? HeadContent { get; set; }

    public bool IsVersioned(string repoRoot) => Versioned;

    public string? ReadHeadContent(string repoRoot, string repoRelativePath) => HeadContent;

    public void Initialize(string repoRoot, string commitMessage)
    {
        InitializeCalls++;
        if (ThrowOnInitialize)
        {
            throw new IOException("seed boom");
        }

        // A real Initialize makes the repo versioned; reflect that so a second EnsureSeeded is idempotent.
        Versioned = true;
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

    public string? RemoteUrl(string repoRoot, string remoteName = "origin") =>
        ThrowOnRemoteUrl ? throw new InvalidOperationException("remote read boom") : RemoteUrlValue;

    public string? LastVersionNote(string repoRoot, string branchName) => LastNoteValue;

    public void PushBranch(string repoRoot, string branchName, string accessToken, string remoteName = "origin")
    {
        PushBranchCalls++;
        PushedBranch = branchName;
        PushedToken = accessToken;
    }
}
