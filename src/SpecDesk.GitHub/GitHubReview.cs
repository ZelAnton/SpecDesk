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

/// <summary>The signed-in user's relationship to a review: they <see cref="Author"/>ed it, or they were
/// asked to <see cref="Reviewer"/> it.</summary>
public enum ReviewRole
{
    Author,
    Reviewer,
}

/// <summary>One open pull request in the user's review list: its <see cref="Number"/>, <see cref="Title"/>,
/// web <see cref="Url"/>, <see cref="Repo"/> (<c>owner/name</c>), the user's <see cref="Role"/>, and its
/// current review <see cref="Decision"/>.</summary>
public sealed record ReviewSummary(
    int Number, string Title, string Url, string Repo, ReviewRole Role, ReviewDecision Decision);

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

    /// <summary>The open pull requests the signed-in user is involved in — as author or requested reviewer —
    /// most recently updated first, across all their repositories. Empty when there are none. Throws on a
    /// transport / API failure (the host surfaces a plain "couldn't load your reviews"). A browse affordance,
    /// so the per-item decision is GitHub's own <c>reviewDecision</c> (best-effort; the authoritative status
    /// for the open document is <see cref="GetReviewStatusAsync"/>).</summary>
    Task<IReadOnlyList<ReviewSummary>> ListReviewsAsync(
        string accessToken, CancellationToken cancellationToken = default);
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

    // The recent PRs for the branch (newest first — we prefer an open one, see below), each with its
    // open/merged/closed state, head commit, and each reviewer's latest OPINIONATED review with the commit it
    // targeted. We aggregate the reviews ourselves rather than reading GitHub's `reviewDecision`, which is
    // null unless the repo/branch has a required-reviews rule — so an approval / change request would never
    // surface on an ordinary repo. `latestOpinionatedReviews` excludes COMMENTED / PENDING, so a reviewer who
    // approves then comments still reads as APPROVED. `first:10` covers a branch that spawned a few PRs across
    // cycles; `first:100` on the reviews is far more than any real spec PR (no paging fallback beyond either).
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
        using JsonDocument document = await PostGraphQlAsync(
            accessToken, new { query = ReviewStatusQuery, variables = new { owner, repo, branch } },
            "review-status", cancellationToken);
        JsonElement root = document.RootElement;
        // A partial GraphQL response carries a top-level `errors` array (a field resolved to null, throttling,
        // an eventual-consistency lag). Its `data` may still look navigable but be incomplete, which would
        // silently downgrade a real decision — so treat ANY errors as a fault and leave the last-known status.
        if (HasGraphQlErrors(root))
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

    // The signed-in user's open PRs as author and, separately, as requested reviewer — two searches so the
    // user's ROLE comes from which qualifier matched, not a broad `involves:@me` (which also pulls in mere
    // mentions / assignments). Each carries updatedAt so the merged list can be sorted most-recent-first.
    private const string ReviewListQuery =
        "query{authored:search(query:\"is:pr is:open author:@me sort:updated-desc\",type:ISSUE,first:20)"
        + "{nodes{... on PullRequest{number title url reviewDecision updatedAt repository{nameWithOwner}}}}"
        + "toReview:search(query:\"is:pr is:open review-requested:@me sort:updated-desc\",type:ISSUE,"
        + "first:20){nodes{... on PullRequest{number title url reviewDecision updatedAt "
        + "repository{nameWithOwner}}}}}";

    public async Task<IReadOnlyList<ReviewSummary>> ListReviewsAsync(
        string accessToken, CancellationToken cancellationToken = default)
    {
        using JsonDocument document =
            await PostGraphQlAsync(accessToken, new { query = ReviewListQuery }, "reviews", cancellationToken);
        JsonElement root = document.RootElement;

        // No usable data: distinguish a total failure (200 with errors + data:null — GitHub's shape for a
        // secondary rate-limit / scope problem) from a genuinely empty result. Throwing the former lets the
        // host show a load-failure reason instead of the misleading "You have no open reviews." Data present
        // → be lenient about a PARTIAL `errors` entry (one unresolvable node); render whatever resolved.
        if (!TryProperty(root, "data", out JsonElement data) || data.ValueKind == JsonValueKind.Null)
        {
            return HasGraphQlErrors(root)
                ? throw new HttpRequestException("GitHub returned errors for the reviews query.")
                : [];
        }

        // Data resolved: be lenient about a PARTIAL `errors` entry (e.g. one unresolvable node) — render
        // whatever resolved rather than blanking the whole list.

        // Collect authored (role = Author) then review-requested (role = Reviewer). A url is seen at most
        // once — you can't be asked to review your own PR — but dedupe defensively. Sort most-recent-first
        // across both groups by updatedAt (an ISO-8601 timestamp, so an ordinal string compare is correct).
        Dictionary<string, (ReviewSummary Summary, string UpdatedAt)> byUrl = [];
        CollectReviews(data, "authored", ReviewRole.Author, byUrl);
        CollectReviews(data, "toReview", ReviewRole.Reviewer, byUrl);

        return
        [
            .. byUrl.Values
                .OrderByDescending(r => r.UpdatedAt, StringComparer.Ordinal)
                .Select(r => r.Summary),
        ];
    }

    private static void CollectReviews(
        JsonElement data,
        string searchAlias,
        ReviewRole role,
        Dictionary<string, (ReviewSummary Summary, string UpdatedAt)> byUrl)
    {
        if (!TryProperty(data, searchAlias, out JsonElement search)
            || !TryProperty(search, "nodes", out JsonElement nodes)
            || nodes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement node in nodes.EnumerateArray())
        {
            // A search on ISSUE can include nodes with no PR fields (the inline fragment didn't match);
            // skip anything without a url so a malformed node can't surface as a blank list row. First
            // match (authored) wins a url over a later group.
            string url = StringOf(node, "url");
            if (url.Length == 0 || byUrl.ContainsKey(url))
            {
                continue;
            }

            string repo = TryProperty(node, "repository", out JsonElement repoNode)
                ? StringOf(repoNode, "nameWithOwner")
                : string.Empty;

            byUrl[url] = (
                new ReviewSummary(
                    NumberOf(node, "number"), StringOf(node, "title"), url, repo, role,
                    DecisionOf(StringOf(node, "reviewDecision"))),
                StringOf(node, "updatedAt"));
        }
    }

    // GitHub's own computed decision for the browse list (null / REVIEW_REQUIRED → In review). The open
    // document's authoritative status still comes from GetReviewStatusAsync's per-review aggregation.
    private static ReviewDecision DecisionOf(string reviewDecision) => reviewDecision switch
    {
        "APPROVED" => ReviewDecision.Approved,
        "CHANGES_REQUESTED" => ReviewDecision.ChangesRequested,
        _ => ReviewDecision.InReview,
    };

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

    private static readonly Uri GraphQlEndpoint = new("https://api.github.com/graphql");

    // POST a GraphQL request under the per-request timeout and return the parsed response document (the
    // caller owns it — `using`). Shared by the two review reads; each then applies its own errors / data
    // policy. Throws on a transport / non-2xx failure. <paramref name="what"/> names the query for the error.
    private async Task<JsonDocument> PostGraphQlAsync(
        string accessToken, object body, string what, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using HttpRequestMessage request = NewRequest(HttpMethod.Post, GraphQlEndpoint, accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token);
        string responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub rejected the {what} query (HTTP {(int)response.StatusCode}).");
        }

        return JsonDocument.Parse(responseBody);
    }

    // Whether a GraphQL response carries a non-empty top-level `errors` array (a partial or total failure).
    private static bool HasGraphQlErrors(JsonElement root) =>
        TryProperty(root, "errors", out JsonElement errors)
        && errors.ValueKind == JsonValueKind.Array
        && errors.GetArrayLength() > 0;

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
