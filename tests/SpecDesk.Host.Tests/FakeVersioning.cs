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

    /// <summary>The friendly name <see cref="CurrentBranch"/> returns — "main" by default. Settable so a
    /// test can simulate a repo left checked out on a working (draft) branch from a PREVIOUS session,
    /// before the host under test ever calls <see cref="BeginEdit"/> itself (M-16: the host must recover
    /// that from the repo's actual checkout, not assume Published just because it never saw an Edit).
    /// Set to <c>null</c> to simulate a detached HEAD (R-01: the real <see cref="LibGit2DocumentVersioning"/>
    /// reports <c>null</c>, never libgit2's own "(no branch)" placeholder, for that case).</summary>
    public string? Branch { get; set; } = "main";

    public bool BranchIsDetached { get; set; }

    public bool ThrowOnBranchInfo { get; set; }

    public string? DefaultBranchValue { get; set; } = "main";

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

    /// <summary>What <see cref="HasCommitsToReview"/> returns; true by default (the draft has a saved
    /// version). Set false to exercise the "save a version first" guard.</summary>
    public bool HasCommitsValue { get; set; } = true;

    /// <summary>Whether <see cref="SaveVersion"/> reports a real commit; true by default. Set false to
    /// exercise a no-op "Save a version" (nothing changed) — which must not count as a new version to
    /// share.</summary>
    public bool SaveCommits { get; set; } = true;

    /// <summary>When set, <see cref="PushBranch"/> throws — to exercise the "an Update push that fails
    /// surfaces a plain error, stays put, and does not wedge the single-flight claim" path.</summary>
    public bool ThrowOnPush { get; set; }

    /// <summary>When set, <see cref="PushBranch"/> throws <see cref="OperationCanceledException"/> —
    /// simulating the overall <c>SendForReviewTimeout</c> (120s) firing while the push is in flight (the
    /// connect/handshake phase is bounded only by the OS socket timeout; the transfer phase is bounded by
    /// this cancellation), so a test can confirm a timed-out push is reported through the same plain error
    /// as any other push fault — not a confusing, differently-worded message.</summary>
    public bool ThrowTimeoutOnPush { get; set; }

    public int PushBranchCalls { get; private set; }

    public string? PushedBranch { get; private set; }

    public string? PushedToken { get; private set; }

    /// <summary>When set, <see cref="PushBranch"/> blocks (after recording its arguments) until released —
    /// so an Update review test can keep one push in flight and assert a concurrent request is
    /// single-flighted away. (Send exercises the same via the review client's gate.)</summary>
    public ManualResetEventSlim? PushGate { get; set; }

    public bool IgnorePushCancellation { get; set; }

    /// <summary>When set, <see cref="SaveVersion"/> blocks (while holding the caller's _repoGate) until
    /// released — so a test can hold a "Save a version" commit mid-flight and interleave it deterministically
    /// with a concurrent review transition.</summary>
    public ManualResetEventSlim? SaveGate { get; set; }

    /// <summary>When set, <see cref="Initialize"/> throws — to exercise the seed-must-not-crash path.</summary>
    public bool ThrowOnInitialize { get; set; }

    /// <summary>Canned "last committed version" returned by <see cref="ReadHeadContent"/>.</summary>
    public string? HeadContent { get; set; }

    /// <summary>When set, <see cref="BeginEdit"/> throws <see cref="DirtyWorkingTreeException"/> with this
    /// branch name — to exercise the "another document's autosaved draft would be wiped by a forced
    /// checkout" refusal path.</summary>
    public string? DirtyBranchToThrow { get; set; }

    public int IsVersionedCalls { get; private set; }

    public bool IsVersioned(string repoRoot)
    {
        IsVersionedCalls++;
        return Versioned;
    }

    public string? ReadHeadContent(string repoRoot, string repoRelativePath) => HeadContent;

    public IReadOnlyList<DocumentVersion> DocumentVersions { get; set; } = [];

    public bool ThrowOnGetDocumentVersions { get; set; }

    public int GetDocumentVersionsCalls { get; private set; }

    public IReadOnlyList<DocumentVersion> GetDocumentVersions(
        string repoRoot,
        string repoRelativePath,
        int maxCount = 50,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GetDocumentVersionsCalls++;
        if (ThrowOnGetDocumentVersions)
        {
            throw new InvalidOperationException("history read boom");
        }
        return DocumentVersions.Take(maxCount).ToList();
    }

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

    public CurrentBranchInfo DescribeCurrentBranch(string repoRoot)
    {
        if (ThrowOnBranchInfo)
        {
            throw new InvalidOperationException("Repository branch unavailable.");
        }
        return new CurrentBranchInfo(BranchIsDetached ? null : Branch, BranchIsDetached);
    }

    public string? DefaultBranch(string repoRoot, string? preferredBranch) => DefaultBranchValue;

    public EditSession BeginEdit(string repoRoot, string branchName, string preferredBase)
    {
        if (DirtyBranchToThrow is not null)
        {
            throw new DirtyWorkingTreeException(DirtyBranchToThrow);
        }

        BeginEditCalls++;
        Branch = branchName;
        return new EditSession(branchName, preferredBase);
    }

    public CommitResult SaveVersion(string repoRoot, string message)
    {
        SaveVersionCalls++;
        LastCommitMessage = message;
        // Block in flight until the test releases it (bounded so a wiring bug fails fast, not hangs).
        SaveGate?.Wait(TimeSpan.FromSeconds(10));
        if (!SaveCommits)
        {
            // A no-op commit (nothing changed since the last version).
            return new CommitResult(false, string.Empty, DateTimeOffset.UnixEpoch);
        }

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

    public bool HasCommitsToReview(string repoRoot, string branchName, string baseBranch) => HasCommitsValue;

    public void PushBranch(
        string repoRoot, string branchName, string accessToken, string remoteName = "origin",
        CancellationToken cancellationToken = default)
    {
        if (ThrowOnPush)
        {
            // A transport / auth failure — thrown before recording the call, so it counts as "did not push".
            throw new InvalidOperationException("push boom");
        }

        if (ThrowTimeoutOnPush)
        {
            throw new OperationCanceledException("The operation was canceled.");
        }

        PushBranchCalls++;
        PushedBranch = branchName;
        PushedToken = accessToken;
        // Block in flight until the test releases it (bounded so a wiring bug fails fast, not hangs).
        PushGate?.Wait(TimeSpan.FromSeconds(10),
            IgnorePushCancellation ? CancellationToken.None : cancellationToken);
    }
}
