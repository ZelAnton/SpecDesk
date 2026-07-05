using SpecDesk.GitHub;

namespace SpecDesk.Host.Tests;

/// <summary>
/// Records the <see cref="IGitHubReview.OpenPullRequestAsync"/> call and returns a canned pull request —
/// or throws / blocks — so the controller tests can exercise the success, failure, and in-flight paths of
/// the "Send for review" round-trip without a network.
/// </summary>
internal sealed class FakeGitHubReview : IGitHubReview
{
    public int Calls { get; private set; }

    public string? Token { get; private set; }

    public string? Owner { get; private set; }

    public string? Repo { get; private set; }

    public string? Head { get; private set; }

    public string? Base { get; private set; }

    public string? Title { get; private set; }

    public string? Body { get; private set; }

    public bool ThrowOnOpen { get; init; }

    /// <summary>When set, <see cref="OpenPullRequestAsync"/> throws <see cref="OperationCanceledException"/> —
    /// simulating either of the two nested timeouts (the per-request 30s cap in <c>GitHubReviewClient</c>, or
    /// the overall 120s <c>SendForReviewTimeout</c> in <c>HostController</c>) firing while the call is in
    /// flight, so a test can confirm both are reported through the same plain, non-misleading error rather
    /// than a confusingly different message depending on which timer won the race.</summary>
    public bool ThrowTimeoutOnOpen { get; set; }

    /// <summary>When set, <see cref="RequestReviewersAsync"/> throws — to exercise the best-effort path
    /// where a reviewer-request failure must not undo the already-open pull request.</summary>
    public bool ThrowOnRequestReviewers { get; init; }

    public int RequestReviewersCalls { get; private set; }

    public int RequestedOnPull { get; private set; }

    public IReadOnlyList<string>? RequestedReviewers { get; private set; }

    /// <summary>What <see cref="GetReviewStatusAsync"/> returns — an open PR's decision, or null for "no
    /// open PR". Defaults to null; a test sets it to drive the review-status refresh.</summary>
    public ReviewStatus? ReviewStatusValue { get; set; }

    public int GetReviewStatusCalls { get; private set; }

    /// <summary>When set, the call blocks (after recording its arguments) until released — so a test
    /// can keep one round-trip in flight and assert a concurrent send is single-flighted away.</summary>
    public ManualResetEventSlim? ReleaseGate { get; init; }

    public Task<PullRequest> OpenPullRequestAsync(
        string accessToken, string owner, string repo, string head, string baseBranch,
        string title, string body, CancellationToken cancellationToken = default)
    {
        Calls++;
        Token = accessToken;
        Owner = owner;
        Repo = repo;
        Head = head;
        Base = baseBranch;
        Title = title;
        Body = body;
        if (ThrowOnOpen)
        {
            throw new HttpRequestException("GitHub rejected the pull-request create (HTTP 422).");
        }

        if (ThrowTimeoutOnOpen)
        {
            throw new OperationCanceledException("The operation was canceled.");
        }

        // Block in flight until the test releases it (bounded so a wiring bug fails fast, not hangs).
        ReleaseGate?.Wait(TimeSpan.FromSeconds(10), cancellationToken);
        return Task.FromResult(new PullRequest(42, $"https://github.com/{owner}/{repo}/pull/42"));
    }

    public Task<int> RequestReviewersAsync(
        string accessToken, string owner, string repo, int pullNumber,
        IReadOnlyList<string> reviewers, CancellationToken cancellationToken = default)
    {
        RequestReviewersCalls++;
        RequestedOnPull = pullNumber;
        RequestedReviewers = reviewers;
        if (ThrowOnRequestReviewers)
        {
            throw new HttpRequestException("GitHub rejected the reviewer request (HTTP 422).");
        }

        return Task.FromResult(reviewers.Count);
    }

    public Task<ReviewStatus?> GetReviewStatusAsync(
        string accessToken, string owner, string repo, string branch, CancellationToken cancellationToken = default)
    {
        GetReviewStatusCalls++;
        return Task.FromResult(ReviewStatusValue);
    }

    /// <summary>What <see cref="ListReviewsAsync"/> returns; a test sets it. Defaults to empty.</summary>
    public IReadOnlyList<ReviewSummary> ReviewsValue { get; set; } = [];

    /// <summary>When true, <see cref="ListReviewsAsync"/> throws (a transport / API failure).</summary>
    public bool ThrowOnListReviews { get; set; }

    public int ListReviewsCalls { get; private set; }

    public Task<IReadOnlyList<ReviewSummary>> ListReviewsAsync(
        string accessToken, CancellationToken cancellationToken = default)
    {
        ListReviewsCalls++;
        if (ThrowOnListReviews)
        {
            throw new HttpRequestException("boom");
        }

        return Task.FromResult(ReviewsValue);
    }
}
