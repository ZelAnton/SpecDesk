using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SpecDesk.GitHub;

/// <summary>A pull request SpecDesk opened: its <see cref="Number"/> and the <see cref="Url"/> to show
/// the author (the GitHub web page for the PR).</summary>
public sealed record PullRequest(int Number, string Url);

/// <summary>Where a review stands, in author-facing terms: awaiting reviewers (<see cref="InReview"/>),
/// a reviewer asked for changes (<see cref="ChangesRequested"/>), or it's approved (<see cref="Approved"/>).
/// Maps GitHub's computed review decision; no git/PR vocabulary reaches the author.</summary>
public enum ReviewDecision
{
    InReview,
    ChangesRequested,
    Approved,
}

/// <summary>Whether the branch's pull request is still <see cref="Open"/> (its <see cref="ReviewStatus.Decision"/>
/// is meaningful), or has been <see cref="Merged"/> (the spec shipped) or <see cref="Closed"/> without
/// merging (the review was abandoned) out of band on GitHub.</summary>
public enum PullRequestState
{
    Open,
    Merged,
    Closed,
}

/// <summary>The live status of the most recent pull request for a branch: whether it is still open
/// (<see cref="PrState"/>), its review <see cref="Decision"/> (meaningful only while open), and the PR
/// <see cref="Number"/>.</summary>
public sealed record ReviewStatus(ReviewDecision Decision, int Number, PullRequestState PrState);

/// <summary>
/// The GitHub review operations behind the "Send for review" flow. The access token is passed in (the host
/// gets it transiently via <see cref="IGitHubAuth.WithAccessTokenAsync{T}"/>) and used only as the Bearer
/// credential; it is never logged or stored here.
/// </summary>
public interface IGitHubReview
{
    /// <summary>Open a pull request from <paramref name="head"/> into <paramref name="baseBranch"/> in
    /// <paramref name="owner"/>/<paramref name="repo"/>. Throws on a transport / API failure (the host
    /// surfaces a plain "couldn't open the pull request").</summary>
    Task<PullRequest> OpenPullRequestAsync(
        string accessToken,
        string owner,
        string repo,
        string head,
        string baseBranch,
        string title,
        string body,
        CancellationToken cancellationToken = default);

    /// <summary>Request reviewers on pull request <paramref name="pullNumber"/> in
    /// <paramref name="owner"/>/<paramref name="repo"/>. Each entry is an <c>@user</c> or <c>@org/team</c>
    /// handle (the leading <c>@</c> is optional); a handle containing <c>/</c> is treated as a team (its
    /// slug is the segment after the last <c>/</c>), everything else as a user. Returns the number of
    /// reviewers <em>asked for</em> (users + teams sent in the request) — 0, with no HTTP call, when the
    /// handles resolve to nothing usable, so the caller never reports assigning reviewers it didn't send.
    /// (GitHub may still silently ignore a handle it won't honour, e.g. the PR author, so this is the
    /// request count, not a confirmed-assigned count.) Throws on a transport / API failure — the host
    /// requests reviewers best-effort, so a failure never undoes the already-open PR.</summary>
    Task<int> RequestReviewersAsync(
        string accessToken,
        string owner,
        string repo,
        int pullNumber,
        IReadOnlyList<string> reviewers,
        CancellationToken cancellationToken = default);

    /// <summary>The current <see cref="ReviewStatus"/> of the most recent pull request whose head is
    /// <paramref name="branch"/> in <paramref name="owner"/>/<paramref name="repo"/> — of ANY state, so a
    /// merged / closed PR is reported (via <see cref="ReviewStatus.PrState"/>), not hidden. <c>null</c> only
    /// when the branch has never had a pull request. The review decision is aggregated from the reviews
    /// client-side (GitHub's own <c>reviewDecision</c> is null on repos without required-review branch
    /// protection, so it can't be relied on). Throws on a transport / API failure (including a partial
    /// GraphQL <c>errors</c> response) — the host refreshes best-effort, so a failure leaves the last-known
    /// status untouched.</summary>
    Task<ReviewStatus?> GetReviewStatusAsync(
        string accessToken,
        string owner,
        string repo,
        string branch,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Production <see cref="IGitHubReview"/>: a single hand-rolled BCL <see cref="HttpClient"/> POST to the
/// REST API (no third-party GitHub SDK), under a per-request timeout, mirroring the device-flow transport.
/// </summary>
public sealed class GitHubReviewClient : IGitHubReview
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    // GitHub's REST API rejects requests without a User-Agent; this identifies the app (any value is fine).
    private static readonly ProductInfoHeaderValue UserAgent = new("SpecDesk", "1.0");

    private readonly HttpClient _http;

    public GitHubReviewClient(HttpClient http) => _http = http;

    public async Task<PullRequest> OpenPullRequestAsync(
        string accessToken,
        string owner,
        string repo,
        string head,
        string baseBranch,
        string title,
        string body,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        // Escape the path segments defensively; owner/repo come from a parsed github.com remote, but
        // never build a request URL from interpolated identifiers without escaping.
        Uri endpoint = new(
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls");
        using HttpRequestMessage request = NewRequest(HttpMethod.Post, endpoint, accessToken);
        // GitHub's create-PR fields are already lowercase, so no naming policy is needed; `base` is a C#
        // keyword escaped with @ (the serialized JSON key is "base").
        string json = JsonSerializer.Serialize(new { title, head, @base = baseBranch, body });
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token);
        string responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            // A 422 whose body says a pull request already exists is the IDEMPOTENT case: the review the
            // author is asking for is already open — e.g. they sent it earlier, restarted, and re-sent the
            // same day (the default branch name is date-to-the-day deterministic). Treat it as success with
            // unknown coordinates so the document settles to In review, rather than stranding the author in
            // Draft with a real PR open and a misleading "check your connection" error. Every other
            // rejection (invalid head/base, 404 no push access, 5xx, …) still throws.
            if ((int)response.StatusCode == 422
                && responseBody.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                return new PullRequest(0, string.Empty);
            }

            throw new HttpRequestException(
                $"GitHub rejected the pull-request create (HTTP {(int)response.StatusCode}).");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            JsonElement root = document.RootElement;
            return new PullRequest(NumberOf(root, "number"), StringOf(root, "html_url"));
        }
        catch (JsonException)
        {
            // A 2xx with an empty or non-JSON body still means GitHub created the PR — surfacing this as a
            // failure would strand the author in Draft with a pull request already open (and a retry would
            // then hit "already exists"). Treat it as success with unknown coordinates instead.
            return new PullRequest(0, string.Empty);
        }
    }

    public async Task<int> RequestReviewersAsync(
        string accessToken,
        string owner,
        string repo,
        int pullNumber,
        IReadOnlyList<string> reviewers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviewers);
        (IReadOnlyList<string> users, IReadOnlyList<string> teams) = Partition(reviewers);
        int count = users.Count + teams.Count;
        if (count == 0)
        {
            // The handles resolved to nothing usable (e.g. a "codeowners"-only list filtered upstream, or a
            // malformed entry) — make no HTTP call and report that zero were requested.
            return 0;
        }

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        Uri endpoint = new(
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls/{pullNumber}/requested_reviewers");
        using HttpRequestMessage request = NewRequest(HttpMethod.Post, endpoint, accessToken);
        string json = JsonSerializer.Serialize(new { reviewers = users, team_reviewers = teams });
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            // This is a single all-or-nothing batch: GitHub rejects the whole request (422 if a reviewer
            // isn't a collaborator, 403 if a team review needs read:org, …) rather than assigning the valid
            // ones. The host logs and moves on — the PR is already open, so this is never fatal and the
            // author can add reviewers on GitHub.
            throw new HttpRequestException(
                $"GitHub rejected the reviewer request (HTTP {(int)response.StatusCode}).");
        }

        return count;
    }

    // The GraphQL query for a branch's open PR and each reviewer's latest OPINIONATED review. We aggregate
    // these ourselves rather than reading GitHub's `reviewDecision`, because reviewDecision is null unless
    // the repo/branch has a required-reviews protection rule — so on an ordinary repo (no branch protection)
    // an approval or change request would never surface. `latestOpinionatedReviews` excludes COMMENTED and
    // PENDING, so a reviewer who approves and then comments still reads as their standing decision (APPROVED)
    // rather than the comment clobbering it.
    // The recent PRs for the branch (newest first — we prefer an open one, see below), each with its
    // open/merged/closed state, head commit, and each reviewer's latest opinionated review with the commit it
    // targeted. `first:10` covers a branch that has spawned a few PRs across review cycles; `first:100` on the
    // reviews is far more than any real spec PR carries (no paging fallback beyond either bound).
    private const string ReviewStatusQuery =
        "query($owner:String!,$repo:String!,$branch:String!){repository(owner:$owner,name:$repo){"
        + "pullRequests(headRefName:$branch,first:10,orderBy:{field:CREATED_AT,direction:DESC}){nodes{"
        + "number state headRefOid latestOpinionatedReviews(first:100){nodes{state commit{oid}}}}}}}";

    public async Task<ReviewStatus?> GetReviewStatusAsync(
        string accessToken,
        string owner,
        string repo,
        string branch,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using HttpRequestMessage request =
            NewRequest(HttpMethod.Post, new Uri("https://api.github.com/graphql"), accessToken);
        string json = JsonSerializer.Serialize(
            new { query = ReviewStatusQuery, variables = new { owner, repo, branch } });
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token);
        string responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub rejected the review-status query (HTTP {(int)response.StatusCode}).");
        }

        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;
        // A partial GraphQL response carries a top-level `errors` array (a field resolved to null, throttling,
        // an eventual-consistency lag). Its `data` may still look navigable but be incomplete, which would
        // silently downgrade a real decision — so treat any errors as a fault and leave the last-known status.
        if (TryProperty(root, "errors", out JsonElement errors)
            && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            throw new HttpRequestException("GitHub returned errors for the review-status query.");
        }

        // Navigate data.repository.pullRequests.nodes[0]; a cleanly-missing level (the repo / branch not
        // resolving) is "no open review" rather than a fault.
        if (!TryProperty(root, "data", out JsonElement data)
            || !TryProperty(data, "repository", out JsonElement repository)
            || !TryProperty(repository, "pullRequests", out JsonElement pulls)
            || !TryProperty(pulls, "nodes", out JsonElement nodes)
            || nodes.ValueKind != JsonValueKind.Array
            || nodes.GetArrayLength() == 0)
        {
            return null;
        }

        // Prefer the branch's OPEN pull request — a live review is what the author is acting on. The branch
        // can carry more than one PR (e.g. a reused branch across cycles, or a duplicate opened elsewhere);
        // picking the newest-created regardless of state could hand back a closed duplicate over the live
        // review. Fall back to the newest PR (nodes[0]) only when none is open — that's the merged/closed
        // case the host uses to pause polling.
        JsonElement node = nodes[0];
        foreach (JsonElement candidate in nodes.EnumerateArray())
        {
            if (PrStateOf(candidate) == PullRequestState.Open)
            {
                node = candidate;
                break;
            }
        }

        return new ReviewStatus(AggregateDecision(node), NumberOf(node, "number"), PrStateOf(node));
    }

    private static PullRequestState PrStateOf(JsonElement node) => StringOf(node, "state") switch
    {
        "MERGED" => PullRequestState.Merged,
        "CLOSED" => PullRequestState.Closed,
        _ => PullRequestState.Open,
    };

    // Derive the review decision from each reviewer's latest opinionated review. The two verdicts are treated
    // asymmetrically, on purpose:
    //   • CHANGES_REQUESTED counts regardless of which commit it targeted — a block persists across pushes
    //     until the reviewer actually re-reviews (GitHub keeps it too; a dismiss-stale repo turns it DISMISSED,
    //     which carries no standing, so it correctly clears there).
    //   • APPROVED counts ONLY if it targeted the current head commit — an approval is of the content that was
    //     reviewed, so pushing new versions returns the status to In review rather than marking unseen content
    //     "Approved" (which the author could then publish). This mirrors "dismiss stale approvals on push" for
    //     every repo, the safe default for an approval.
    // Any change request outranks an approval (a single block wins); else a live approval means approved; else
    // still in review. COMMENTED / PENDING are already excluded from this connection; DISMISSED carries no
    // standing.
    private static ReviewDecision AggregateDecision(JsonElement node)
    {
        string headOid = StringOf(node, "headRefOid");
        if (!TryProperty(node, "latestOpinionatedReviews", out JsonElement latestReviews)
            || !TryProperty(latestReviews, "nodes", out JsonElement reviews)
            || reviews.ValueKind != JsonValueKind.Array)
        {
            return ReviewDecision.InReview;
        }

        bool changesRequested = false;
        bool approved = false;
        foreach (JsonElement review in reviews.EnumerateArray())
        {
            switch (StringOf(review, "state"))
            {
                case "CHANGES_REQUESTED":
                    changesRequested = true;
                    break;
                case "APPROVED"
                    when headOid.Length > 0
                        && TryProperty(review, "commit", out JsonElement commit)
                        && StringOf(commit, "oid") == headOid:
                    approved = true;
                    break;
            }
        }

        return changesRequested ? ReviewDecision.ChangesRequested
            : approved ? ReviewDecision.Approved
            : ReviewDecision.InReview;
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    // Build a REST request with the standard GitHub headers (JSON accept, Bearer auth, User-Agent, and a
    // pinned API version so a future rolling-default bump can't silently change the contract).
    private static HttpRequestMessage NewRequest(HttpMethod method, Uri endpoint, string accessToken)
    {
        HttpRequestMessage request = new(method, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.Add(UserAgent);
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    // Split @user / @org/team handles into the API's separate `reviewers` (user logins) and
    // `team_reviewers` (team slugs) lists. The leading @ is optional; a handle containing '/' is a team
    // (its slug is the segment after the last '/'), everything else a user. Blank handles are dropped.
    private static (IReadOnlyList<string> Users, IReadOnlyList<string> Teams) Partition(
        IReadOnlyList<string> reviewers)
    {
        List<string> users = [];
        List<string> teams = [];
        foreach (string raw in reviewers)
        {
            string handle = raw.Trim().TrimStart('@');
            if (handle.Length == 0)
            {
                continue;
            }

            int slash = handle.LastIndexOf('/');
            if (slash < 0)
            {
                users.Add(handle);
            }
            else if (slash + 1 < handle.Length)
            {
                teams.Add(handle[(slash + 1)..]);
            }
        }

        return (users, teams);
    }

    private static int NumberOf(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out JsonElement element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetInt32(out int value)
            ? value
            : 0;

    private static string StringOf(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out JsonElement element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;
}
