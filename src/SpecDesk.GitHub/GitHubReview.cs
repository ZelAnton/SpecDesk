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
/// (<see cref="PrState"/>), its review <see cref="Decision"/> (meaningful only while open), the PR
/// <see cref="Number"/>, and the PR's current <see cref="HeadSha"/> (the head commit the decision was
/// aggregated against — empty when unknown). Publish passes <see cref="HeadSha"/> back to the merge as a
/// freshness guard so a head that moved since approval can't be merged unreviewed.</summary>
public sealed record ReviewStatus(ReviewDecision Decision, int Number, PullRequestState PrState, string HeadSha = "");

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

/// <summary>One bounded inline review comment from GitHub, including its repository-relative file path.</summary>
public sealed record ReviewComment(
    string Id, string Path, string Author, string Body, DateTimeOffset When);

/// <summary>One inline PR review comment pulled from GitHub with the anchor SpecDesk needs to project it
/// back onto a document line. <see cref="Line"/> is the 1-based file line the comment is attached to
/// (GitHub's <c>line</c>, or <c>original_line</c> when the head moved on and the comment went outdated);
/// <see cref="Side"/> is <c>RIGHT</c> (head/new file) or <c>LEFT</c> (base/old file); <see cref="CommitId"/>
/// is the commit the comment was written against; <see cref="InReplyToId"/> is 0 for a root thread and the
/// parent id for a reply. The body is bounded exactly like <see cref="ReviewComment"/>.</summary>
public sealed record ReviewCommentAnchor(
    long Id, string Path, int Line, string Side, string CommitId, long InReplyToId,
    string Author, string Body, DateTimeOffset When);

/// <summary>The bounded projection one document needs to synchronise its inline comments with an open pull
/// request. <see cref="HeadCommitId"/> is the PR's current head commit (what a newly-posted comment anchors
/// to); <see cref="Path"/> is the document's repository-relative path; <see cref="CommentableLines"/> are the
/// 1-based head-side (RIGHT) lines of that file that fall inside a diff hunk — GitHub only accepts an inline
/// comment on those, so anything else stays local and labelled; <see cref="Comments"/> are the existing
/// review comments already on that file (for both sides). GitHub sync is a projection over the app's local
/// comment model, so this snapshot never carries the whole document, only these bounded anchors.</summary>
public sealed record ReviewSyncSnapshot(
    string HeadCommitId, string Path, IReadOnlyList<int> CommentableLines,
    IReadOnlyList<ReviewCommentAnchor> Comments);

/// <summary>One person or team participating in a pull-request review.</summary>
public sealed record PullRequestParticipant(string Login, string AvatarUrl, string Kind);

/// <summary>One conversation comment attached to the pull request as a whole.</summary>
public sealed record PullRequestComment(
    long Id, string Kind, string Path, string Author, string AvatarUrl, string Body, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, bool ViewerDidAuthor);

/// <summary>One commit in a pull request, with its latest combined GitHub checks result.</summary>
public sealed record PullRequestCommit(
    string Oid, string ShortOid, string Title, DateTimeOffset When, string CheckState);

/// <summary>The bounded pull-request document shown inside SpecDesk.</summary>
public sealed record PullRequestDetails(
    int Number, string Repo, string Title, string Body, string Url, string State, bool IsDraft,
    string Author, string AuthorAvatarUrl, string BaseBranch, string HeadBranch,
    IReadOnlyList<PullRequestParticipant> Reviewers,
    IReadOnlyList<PullRequestComment> Comments,
    IReadOnlyList<PullRequestCommit> Commits,
    bool CommentsIncomplete = false,
    bool CommitsIncomplete = false);

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

    /// <summary>Merge pull request <paramref name="pullNumber"/> in <paramref name="owner"/>/<paramref
    /// name="repo"/>, publishing the spec. <paramref name="expectedHeadSha"/> is passed to GitHub as the
    /// SHA the PR head must still match (a freshness guard): if the head moved since it was approved —
    /// someone pushed a new version — GitHub refuses the merge and this throws, so an unreviewed change can
    /// never be published. Throws on any non-success (a still-open review requirement, a merge conflict, a
    /// SHA mismatch, a revoked permission, or a transport failure); the caller reports a plain reason and
    /// leaves the document where it was — a failed publish never strands it in an in-between state.</summary>
    Task MergePullRequestAsync(
        string accessToken,
        string owner,
        string repo,
        int pullNumber,
        string expectedHeadSha,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Merging a pull request is not available from this provider.");

    /// <summary>Delete the branch named <paramref name="branch"/> in <paramref name="owner"/>/<paramref
    /// name="repo"/> (removing the merged draft's ref on GitHub after it is published). Throws on a transport
    /// / API failure; the caller treats this as best-effort cleanup — a failure to delete never undoes the
    /// merge that already published the document.</summary>
    Task DeleteBranchAsync(
        string accessToken,
        string owner,
        string repo,
        string branch,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Deleting a branch is not available from this provider.");

    /// <summary>The open pull requests the signed-in user is involved in — as author or requested reviewer —
    /// most recently updated first, across all their repositories. Empty when there are none. Throws on a
    /// transport / API failure (the host surfaces a plain "couldn't load your reviews"). A browse affordance,
    /// so the per-item decision is GitHub's own <c>reviewDecision</c> (best-effort; the authoritative status
    /// for the open document is <see cref="GetReviewStatusAsync"/>).</summary>
    Task<IReadOnlyList<ReviewSummary>> ListReviewsAsync(
        string accessToken, CancellationToken cancellationToken = default);

    /// <summary>The open pull requests waiting for the signed-in user's review. Includes direct
    /// <c>review-requested:@me</c> matches and team requests for memberships visible to the token. Search and
    /// team endpoints are paged; duplicate matches are returned once, newest first.</summary>
    Task<IReadOnlyList<ReviewSummary>> ListReviewRequestsAsync(
        string accessToken, CancellationToken cancellationToken = default);

    /// <summary>The signed-in user's open pull requests: authored first, plus any other open request they
    /// participate in. Closed and merged requests are intentionally outside this active-work list.</summary>
    Task<IReadOnlyList<ReviewSummary>> ListPullRequestsAsync(
        string accessToken, CancellationToken cancellationToken = default);

    /// <summary>Return at most 100 inline comments from the first API page of a review.</summary>
    Task<IReadOnlyList<ReviewComment>> ListReviewCommentsAsync(
        string accessToken,
        string owner,
        string repo,
        int pullNumber,
        CancellationToken cancellationToken = default);

    /// <summary>Load the bounded pull-request document used by the in-app review view.</summary>
    Task<PullRequestDetails> GetPullRequestDetailsAsync(
        string accessToken,
        string owner,
        string repo,
        int pullNumber,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Pull-request details are not available from this provider.");

    /// <summary>Create a conversation comment that is not anchored to a file.</summary>
    Task CreatePullRequestCommentAsync(
        string accessToken,
        string owner,
        string repo,
        int pullNumber,
        string body,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Pull-request comments are not available from this provider.");

    /// <summary>Update a conversation comment. GitHub enforces ownership for the token.</summary>
    Task UpdatePullRequestCommentAsync(
        string accessToken,
        string owner,
        string repo,
        long commentId,
        string body,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Pull-request comments are not available from this provider.");

    /// <summary>Reply to an inline review comment using GitHub's native review thread.</summary>
    Task ReplyToReviewCommentAsync(
        string accessToken, string owner, string repo, int pullNumber, long commentId, string body,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Review comment replies are not available from this provider.");

    /// <summary>Update an inline review comment. GitHub enforces ownership for the token.</summary>
    Task UpdateReviewCommentAsync(
        string accessToken, string owner, string repo, long commentId, string body,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Review comments are not available from this provider.");

    /// <summary>Post a new inline review comment on <paramref name="pullNumber"/>, anchored to
    /// <paramref name="path"/> at <paramref name="line"/> (1-based) on <paramref name="side"/>
    /// (<c>RIGHT</c>/<c>LEFT</c>) against <paramref name="commitId"/>. The caller has already established
    /// the line falls inside a diff hunk (GitHub rejects a comment on an unchanged line). Returns the new
    /// comment's GitHub id (0 when a 2xx body couldn't be parsed) so the local model can mark the thread
    /// synced. Throws on a transport / API failure.</summary>
    Task<long> CreateReviewCommentAsync(
        string accessToken, string owner, string repo, int pullNumber, string commitId, string path,
        int line, string side, string body, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Review comments are not available from this provider.");

    /// <summary>The bounded <see cref="ReviewSyncSnapshot"/> for <paramref name="path"/> in
    /// <paramref name="pullNumber"/>: the current head commit, the file's head-side lines that are inside a
    /// diff hunk (postable), and the existing review comments on that file. Read-only and best-effort — the
    /// host projects it over the local comment model and a failure leaves the last projection untouched.</summary>
    Task<ReviewSyncSnapshot> GetReviewSyncAsync(
        string accessToken, string owner, string repo, int pullNumber, string path,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Review sync is not available from this provider.");
}

/// <summary>
/// Production <see cref="IGitHubReview"/>: a single hand-rolled BCL <see cref="HttpClient"/> POST to the
/// REST API (no third-party GitHub SDK), under a per-request timeout, mirroring the device-flow transport.
/// </summary>
public sealed class GitHubReviewClient : IGitHubReview
{
    private const int MaxReviewCommentsResponseBytes = 1_048_576;
    private const int MaxGraphQlResponseBytes = 4_194_304;
    private const int MaxPullRequestCommentsPageBytes = 8_388_608;
    private const int MaxPullRequestCommentsTotalCharacters = 4_194_304;
    private const int MaxPullRequestCommentPages = 10;
    // A changed file's unified-diff patch can be large; bound one /files page independently of comments.
    private const int MaxReviewFilesPageBytes = 8_388_608;
    // Cap the projected anchors so a pathological PR can't inflate the sync payload the host ships back.
    private const int MaxCommentableLines = 50_000;
    private const int MaxReviewCommentAnchors = 500;
    private static readonly TimeSpan PullRequestCommentsTimeout = TimeSpan.FromSeconds(5);
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
        using CancellationTokenSource timeout = GitHubHttp.NewTimeout(cancellationToken);

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
            return new PullRequest(GitHubHttp.NumberOf(root, "number"), GitHubHttp.StringOf(root, "html_url"));
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

        using CancellationTokenSource timeout = GitHubHttp.NewTimeout(cancellationToken);

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

    public async Task<IReadOnlyList<ReviewComment>> ListReviewCommentsAsync(
        string accessToken,
        string owner,
        string repo,
        int pullNumber,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = GitHubHttp.NewTimeout(cancellationToken);
        Uri endpoint = new(
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls/{pullNumber}/comments?per_page=100&sort=created&direction=desc");
        using HttpRequestMessage request = NewRequest(HttpMethod.Get, endpoint, accessToken);
        using HttpResponseMessage response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GitHub rejected the review-comments request (HTTP {(int)response.StatusCode}).");
        }

        byte[] responseBody = await ReadBoundedAsync(
            response.Content, MaxReviewCommentsResponseBytes, timeout.Token);
        using JsonDocument document = JsonDocument.Parse(responseBody);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new HttpRequestException("GitHub returned malformed review comments.");
        }
        List<ReviewComment> comments = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray().Take(100))
        {
            string path = GitHubHttp.StringOf(item, "path");
            string body = GitHubHttp.StringOf(item, "body");
            if (path.Length == 0 || body.Length == 0)
            {
                continue;
            }
            string author = item.TryGetProperty("user", out JsonElement user)
                ? GitHubHttp.StringOf(user, "login")
                : string.Empty;
            string created = GitHubHttp.StringOf(item, "created_at");
            DateTimeOffset when = DateTimeOffset.TryParse(created, out DateTimeOffset parsed)
                ? parsed
                : DateTimeOffset.UnixEpoch;
            string id = item.TryGetProperty("id", out JsonElement idElement)
                ? idElement.GetRawText()
                : string.Empty;
            string boundedBody = body.Length <= 4_000 ? body : body[..4_000] + "…";
            comments.Add(new ReviewComment(id, path, author, boundedBody, when));
        }
        return comments;
    }

    private const string PullRequestDetailsQuery =
        "query($owner:String!,$repo:String!,$number:Int!){viewer{login}repository(owner:$owner,name:$repo){"
        + "pullRequest(number:$number){number title body url state isDraft createdAt updatedAt baseRefName "
        + "headRefName author{login avatarUrl} reviewRequests(first:50){nodes{requestedReviewer{"
        + "... on User{login avatarUrl} ... on Team{name slug organization{login}}}}}"
        + "latestReviews(first:100){nodes{author{login avatarUrl}}}"
        + "commits(last:50){totalCount nodes{commit{oid abbreviatedOid messageHeadline committedDate "
        + "statusCheckRollup{state}}}}}}}";

    public async Task<PullRequestDetails> GetPullRequestDetailsAsync(
        string accessToken,
        string owner,
        string repo,
        int pullNumber,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = GitHubHttp.NewTimeout(cancellationToken);
        using JsonDocument document = await PostGraphQlAsync(
            accessToken,
            new { query = PullRequestDetailsQuery, variables = new { owner, repo, number = pullNumber } },
            "pull-request details",
            timeout.Token);
        JsonElement root = document.RootElement;
        if (HasGraphQlErrors(root)
            || !TryProperty(root, "data", out JsonElement data)
            || !TryProperty(data, "repository", out JsonElement repository)
            || !TryProperty(repository, "pullRequest", out JsonElement pull)
            || pull.ValueKind != JsonValueKind.Object)
        {
            throw new HttpRequestException("GitHub returned no usable pull-request details.");
        }

        Dictionary<string, PullRequestParticipant> reviewers = new(StringComparer.OrdinalIgnoreCase);
        if (TryProperty(pull, "reviewRequests", out JsonElement reviewRequests)
            && TryProperty(reviewRequests, "nodes", out JsonElement reviewNodes)
            && reviewNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement node in reviewNodes.EnumerateArray())
            {
                if (!TryProperty(node, "requestedReviewer", out JsonElement reviewer))
                {
                    continue;
                }
                string login = GitHubHttp.StringOf(reviewer, "login");
                string kind = "user";
                if (login.Length == 0)
                {
                    string slug = GitHubHttp.StringOf(reviewer, "slug");
                    string org = TryProperty(reviewer, "organization", out JsonElement organization)
                        ? GitHubHttp.StringOf(organization, "login")
                        : string.Empty;
                    login = org.Length > 0 && slug.Length > 0 ? $"{org}/{slug}" : GitHubHttp.StringOf(reviewer, "name");
                    kind = "team";
                }
                if (login.Length > 0)
                {
                    reviewers[login] = new PullRequestParticipant(
                        login, GitHubHttp.StringOf(reviewer, "avatarUrl"), kind);
                }
            }
        }
        if (TryProperty(pull, "latestReviews", out JsonElement reviewConnection)
            && TryProperty(reviewConnection, "nodes", out JsonElement submittedReviewNodes)
            && submittedReviewNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement reviewNode in submittedReviewNodes.EnumerateArray())
            {
                if (!TryProperty(reviewNode, "author", out JsonElement reviewer))
                {
                    continue;
                }
                string login = GitHubHttp.StringOf(reviewer, "login");
                if (login.Length > 0)
                {
                    reviewers[login] = new PullRequestParticipant(
                        login, GitHubHttp.StringOf(reviewer, "avatarUrl"), "user");
                }
            }
        }

        string viewer = TryProperty(root, "data", out JsonElement rootData)
            && TryProperty(rootData, "viewer", out JsonElement viewerNode)
            ? GitHubHttp.StringOf(viewerNode, "login")
            : string.Empty;
        IReadOnlyList<PullRequestComment> comments = [];
        bool commentsIncomplete = true;
        using (CancellationTokenSource commentsTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
        {
            commentsTimeout.CancelAfter(PullRequestCommentsTimeout);
            try
            {
                (comments, commentsIncomplete) = await ListPullRequestCommentsAsync(
                    accessToken, owner, repo, pullNumber, viewer, commentsTimeout.Token);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && !timeout.IsCancellationRequested)
            {
                // Comments are supplementary; keep the PR document usable when their short budget expires.
            }
            catch (Exception exception) when (
                exception is HttpRequestException or InvalidDataException or JsonException)
            {
                // GitHub exposes core PR data and comments through separate endpoints. A comments failure
                // must not discard the already-loaded title, description, reviewers, and commit history.
            }
        }

        List<PullRequestCommit> commits = [];
        bool commitsIncomplete = false;
        if (TryProperty(pull, "commits", out JsonElement commitConnection)
            && TryProperty(commitConnection, "nodes", out JsonElement commitNodes)
            && commitNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement node in commitNodes.EnumerateArray())
            {
                if (!TryProperty(node, "commit", out JsonElement commit))
                {
                    continue;
                }
                string checks = TryProperty(commit, "statusCheckRollup", out JsonElement rollup)
                    ? GitHubHttp.StringOf(rollup, "state").ToLowerInvariant()
                    : "unknown";
                commits.Add(new PullRequestCommit(
                    GitHubHttp.StringOf(commit, "oid"), GitHubHttp.StringOf(commit, "abbreviatedOid"),
                    GitHubHttp.StringOf(commit, "messageHeadline"), DateOf(commit, "committedDate"), checks));
            }
            commitsIncomplete = GitHubHttp.NumberOf(commitConnection, "totalCount") > commits.Count;
        }

        JsonElement authorElement = TryProperty(pull, "author", out JsonElement pullAuthor) ? pullAuthor : default;
        return new PullRequestDetails(
            GitHubHttp.NumberOf(pull, "number"), $"{owner}/{repo}", GitHubHttp.StringOf(pull, "title"),
            GitHubHttp.StringOf(pull, "body"), GitHubHttp.StringOf(pull, "url"),
            GitHubHttp.StringOf(pull, "state").ToLowerInvariant(),
            pull.TryGetProperty("isDraft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True,
            GitHubHttp.StringOf(authorElement, "login"), GitHubHttp.StringOf(authorElement, "avatarUrl"),
            GitHubHttp.StringOf(pull, "baseRefName"), GitHubHttp.StringOf(pull, "headRefName"),
            [.. reviewers.Values.OrderBy(item => item.Login, StringComparer.OrdinalIgnoreCase)],
            comments, commits, commentsIncomplete, commitsIncomplete);
    }

    private async Task<(IReadOnlyList<PullRequestComment> Comments, bool Incomplete)>
        ListPullRequestCommentsAsync(
            string accessToken, string owner, string repo, int pullNumber, string viewer,
            CancellationToken cancellationToken)
    {
        List<PullRequestComment> comments = [];
        bool incomplete = false;
        bool reachedTotalCap = false;
        int totalCharacters = 0;
        foreach (string kind in new[] { "conversation", "review" })
        {
            for (int page = 1; page <= MaxPullRequestCommentPages; page++)
            {
                string segment = kind == "conversation"
                    ? $"issues/{pullNumber}/comments"
                    : $"pulls/{pullNumber}/comments";
                Uri endpoint = new(
                    $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/{segment}?per_page=100&page={page}&sort=created&direction=desc");
                using HttpRequestMessage request = NewRequest(HttpMethod.Get, endpoint, accessToken);
                using HttpResponseMessage response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"GitHub rejected the pull-request comments request (HTTP {(int)response.StatusCode}).");
                }
                byte[] responseBody = await ReadBoundedAsync(
                    response.Content, MaxPullRequestCommentsPageBytes, cancellationToken);
                using JsonDocument pageDocument = JsonDocument.Parse(responseBody);
                if (pageDocument.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new HttpRequestException("GitHub returned malformed pull-request comments.");
                }
                int count = 0;
                foreach (JsonElement item in pageDocument.RootElement.EnumerateArray())
                {
                    count++;
                    string body = GitHubHttp.StringOf(item, "body");
                    if (body.Length == 0)
                    {
                        continue;
                    }
                    if (totalCharacters + body.Length > MaxPullRequestCommentsTotalCharacters)
                    {
                        incomplete = true;
                        reachedTotalCap = true;
                        break;
                    }
                    totalCharacters += body.Length;
                    JsonElement authorNode = TryProperty(item, "user", out JsonElement author) ? author : default;
                    string authorLogin = GitHubHttp.StringOf(authorNode, "login");
                    comments.Add(new PullRequestComment(
                        item.TryGetProperty("id", out JsonElement id) && id.TryGetInt64(out long parsedId)
                            ? parsedId : 0,
                        kind,
                        kind == "review" ? GitHubHttp.StringOf(item, "path") : string.Empty,
                        authorLogin,
                        GitHubHttp.StringOf(authorNode, "avatar_url"),
                        body.Length <= 65_536 ? body : body[..65_536],
                        DateOf(item, "created_at"),
                        DateOf(item, "updated_at"),
                        viewer.Length > 0 && string.Equals(viewer, authorLogin, StringComparison.OrdinalIgnoreCase)));
                }
                if (reachedTotalCap)
                {
                    break;
                }
                if (count < 100)
                {
                    break;
                }
                if (page == MaxPullRequestCommentPages)
                {
                    incomplete = true;
                }
            }
            if (reachedTotalCap)
            {
                break;
            }
        }
        comments.Sort((left, right) => left.CreatedAt.CompareTo(right.CreatedAt));
        return (comments, incomplete);
    }

    public Task CreatePullRequestCommentAsync(
        string accessToken, string owner, string repo, int pullNumber, string body,
        CancellationToken cancellationToken = default) =>
        SendCommentMutationAsync(
            HttpMethod.Post,
            new Uri($"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/{pullNumber}/comments"),
            accessToken,
            body,
            "create comment",
            cancellationToken);

    public Task UpdatePullRequestCommentAsync(
        string accessToken, string owner, string repo, long commentId, string body,
        CancellationToken cancellationToken = default) =>
        SendCommentMutationAsync(
            HttpMethod.Patch,
            new Uri($"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/issues/comments/{commentId}"),
            accessToken,
            body,
            "update comment",
            cancellationToken);

    public Task ReplyToReviewCommentAsync(
        string accessToken, string owner, string repo, int pullNumber, long commentId, string body,
        CancellationToken cancellationToken = default) =>
        SendCommentMutationAsync(
            HttpMethod.Post,
            new Uri($"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls/{pullNumber}/comments/{commentId}/replies"),
            accessToken, body, "reply to review comment", cancellationToken);

    public Task UpdateReviewCommentAsync(
        string accessToken, string owner, string repo, long commentId, string body,
        CancellationToken cancellationToken = default) =>
        SendCommentMutationAsync(
            HttpMethod.Patch,
            new Uri($"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls/comments/{commentId}"),
            accessToken, body, "update review comment", cancellationToken);

    private async Task SendCommentMutationAsync(
        HttpMethod method, Uri endpoint, string accessToken, string body, string what,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 65_536)
        {
            throw new ArgumentException("Comment text must be between 1 and 65,536 characters.", nameof(body));
        }
        using CancellationTokenSource timeout = GitHubHttp.NewTimeout(cancellationToken);
        using HttpRequestMessage request = NewRequest(method, endpoint, accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(new { body }), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub rejected the {what} request (HTTP {(int)response.StatusCode}).");
        }
    }

    public async Task<long> CreateReviewCommentAsync(
        string accessToken, string owner, string repo, int pullNumber, string commitId, string path,
        int line, string side, string body, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 65_536)
        {
            throw new ArgumentException("Comment text must be between 1 and 65,536 characters.", nameof(body));
        }
        if (string.IsNullOrWhiteSpace(commitId) || string.IsNullOrWhiteSpace(path) || line <= 0)
        {
            throw new ArgumentException("A review comment needs a commit, a path, and a positive line.");
        }
        // The line/side model is what the caller already resolved through the lineMap; RIGHT is the head
        // (new-file) side. Normalise defensively so a stray value can't post to the wrong side of the diff.
        string normalizedSide = string.Equals(side, "LEFT", StringComparison.OrdinalIgnoreCase) ? "LEFT" : "RIGHT";

        using CancellationTokenSource timeout = GitHubHttp.NewTimeout(cancellationToken);
        Uri endpoint = new(
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls/{pullNumber}/comments");
        using HttpRequestMessage request = NewRequest(HttpMethod.Post, endpoint, accessToken);
        string json = JsonSerializer.Serialize(
            new { body, commit_id = commitId, path, line, side = normalizedSide });
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token);
        string responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GitHub rejected the review comment (HTTP {(int)response.StatusCode}).");
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("id", out JsonElement id)
                && id.TryGetInt64(out long value)
                ? value
                : 0;
        }
        catch (JsonException)
        {
            // GitHub accepted the comment (2xx); an unparseable body only costs the id echo. Report it posted
            // with an unknown id rather than as a failure that would push the author to re-post a duplicate.
            return 0;
        }
    }

    public async Task<ReviewSyncSnapshot> GetReviewSyncAsync(
        string accessToken, string owner, string repo, int pullNumber, string path,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = GitHubHttp.NewTimeout(cancellationToken);
        string headCommitId = await ReadHeadCommitAsync(accessToken, owner, repo, pullNumber, timeout.Token);
        IReadOnlyList<int> commentableLines =
            await ReadCommentableLinesAsync(accessToken, owner, repo, pullNumber, path, timeout.Token);
        IReadOnlyList<ReviewCommentAnchor> comments =
            await ReadReviewCommentAnchorsAsync(accessToken, owner, repo, pullNumber, path, timeout.Token);
        return new ReviewSyncSnapshot(headCommitId, path, commentableLines, comments);
    }

    private async Task<string> ReadHeadCommitAsync(
        string accessToken, string owner, string repo, int pullNumber, CancellationToken cancellationToken)
    {
        Uri endpoint = new(
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls/{pullNumber}");
        using HttpRequestMessage request = NewRequest(HttpMethod.Get, endpoint, accessToken);
        using HttpResponseMessage response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GitHub rejected the pull-request read (HTTP {(int)response.StatusCode}).");
        }
        byte[] responseBody = await ReadBoundedAsync(
            response.Content, MaxReviewCommentsResponseBytes, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(responseBody);
        return TryProperty(document.RootElement, "head", out JsonElement head)
            ? GitHubHttp.StringOf(head, "sha")
            : string.Empty;
    }

    private async Task<IReadOnlyList<int>> ReadCommentableLinesAsync(
        string accessToken, string owner, string repo, int pullNumber, string path,
        CancellationToken cancellationToken)
    {
        for (int page = 1; page <= MaxPullRequestCommentPages; page++)
        {
            Uri endpoint = new(
                $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls/{pullNumber}/files?per_page=100&page={page}");
            using HttpRequestMessage request = NewRequest(HttpMethod.Get, endpoint, accessToken);
            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"GitHub rejected the pull-request files request (HTTP {(int)response.StatusCode}).");
            }
            byte[] responseBody = await ReadBoundedAsync(
                response.Content, MaxReviewFilesPageBytes, cancellationToken);
            using JsonDocument document = JsonDocument.Parse(responseBody);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new HttpRequestException("GitHub returned malformed pull-request files.");
            }
            int count = 0;
            foreach (JsonElement file in document.RootElement.EnumerateArray())
            {
                count++;
                if (GitHubHttp.StringOf(file, "filename") == path)
                {
                    return CommentableHeadLines(GitHubHttp.StringOf(file, "patch"));
                }
            }
            if (count < 100)
            {
                break;
            }
        }
        // The path isn't among the PR's changed files — nothing sits inside a hunk, so nothing is postable.
        return [];
    }

    private async Task<IReadOnlyList<ReviewCommentAnchor>> ReadReviewCommentAnchorsAsync(
        string accessToken, string owner, string repo, int pullNumber, string path,
        CancellationToken cancellationToken)
    {
        List<ReviewCommentAnchor> anchors = [];
        for (int page = 1; page <= MaxPullRequestCommentPages; page++)
        {
            Uri endpoint = new(
                $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls/{pullNumber}/comments?per_page=100&page={page}&sort=created&direction=asc");
            using HttpRequestMessage request = NewRequest(HttpMethod.Get, endpoint, accessToken);
            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"GitHub rejected the review-comments request (HTTP {(int)response.StatusCode}).");
            }
            byte[] responseBody = await ReadBoundedAsync(
                response.Content, MaxPullRequestCommentsPageBytes, cancellationToken);
            using JsonDocument document = JsonDocument.Parse(responseBody);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new HttpRequestException("GitHub returned malformed review comments.");
            }
            int count = 0;
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                count++;
                if (anchors.Count >= MaxReviewCommentAnchors)
                {
                    return anchors;
                }
                if (GitHubHttp.StringOf(item, "path") != path)
                {
                    continue;
                }
                string body = GitHubHttp.StringOf(item, "body");
                if (body.Length == 0)
                {
                    continue;
                }
                // Guard on the JSON kind before TryGetInt64: `in_reply_to_id` is null on a root comment (and
                // GitHub can omit numeric fields), and TryGetInt64 throws on a Null/absent element.
                long id = item.TryGetProperty("id", out JsonElement idElement)
                    && idElement.ValueKind == JsonValueKind.Number
                    && idElement.TryGetInt64(out long parsedId)
                    ? parsedId
                    : 0;
                string side = GitHubHttp.StringOf(item, "side");
                long inReplyTo = item.TryGetProperty("in_reply_to_id", out JsonElement replyElement)
                    && replyElement.ValueKind == JsonValueKind.Number
                    && replyElement.TryGetInt64(out long parsedReply)
                    ? parsedReply
                    : 0;
                string author = item.TryGetProperty("user", out JsonElement user)
                    ? GitHubHttp.StringOf(user, "login")
                    : string.Empty;
                string boundedBody = body.Length <= 4_000 ? body : body[..4_000] + "…";
                anchors.Add(new ReviewCommentAnchor(
                    id, path, LineOf(item), side.Length == 0 ? "RIGHT" : side,
                    GitHubHttp.StringOf(item, "commit_id"), inReplyTo, author, boundedBody,
                    DateOf(item, "created_at")));
            }
            if (count < 100)
            {
                break;
            }
        }
        return anchors;
    }

    // A review comment's head-side line is GitHub's `line`; when the head moved past it and the comment went
    // outdated GitHub nulls `line` and keeps `original_line`, so fall back to that rather than dropping the
    // thread (the app then re-anchors it through its own index and may mark it detached).
    private static int LineOf(JsonElement item)
    {
        if (item.TryGetProperty("line", out JsonElement line)
            && line.ValueKind == JsonValueKind.Number
            && line.TryGetInt32(out int value))
        {
            return value;
        }
        return item.TryGetProperty("original_line", out JsonElement original)
            && original.ValueKind == JsonValueKind.Number
            && original.TryGetInt32(out int originalValue)
            ? originalValue
            : 0;
    }

    // Parse a unified-diff `patch` (one changed file) into the 1-based head-side (RIGHT) line numbers a
    // review comment can target: the lines that appear in a hunk as context (' ') or additions ('+'). A
    // removed ('-') line belongs to the base side and advances no head line. GitHub rejects an inline
    // comment on a line outside a hunk, so this set is exactly what is postable; everything else stays local.
    internal static IReadOnlyList<int> CommentableHeadLines(string patch)
    {
        if (string.IsNullOrEmpty(patch))
        {
            return [];
        }
        List<int> lines = [];
        int headLine = 0;
        bool inHunk = false;
        foreach (string raw in patch.Split('\n'))
        {
            string line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                headLine = ParseHunkHeadStart(line);
                inHunk = headLine > 0;
                continue;
            }
            if (!inHunk || line.Length == 0)
            {
                continue;
            }
            switch (line[0])
            {
                case '+':
                case ' ':
                    if (lines.Count >= MaxCommentableLines)
                    {
                        return lines;
                    }
                    lines.Add(headLine);
                    headLine++;
                    break;
                case '-':
                    break;
                case '\\':
                    // "\ No newline at end of file" — hunk metadata, not a line.
                    break;
                default:
                    // An unexpected leading character means we've walked past the hunk body (e.g. a diff
                    // trailer); stop trusting the head counter until the next @@ header re-seats it.
                    inHunk = false;
                    break;
            }
        }
        return lines;
    }

    // The new-file (head/RIGHT) start line from a `@@ -a,b +c,d @@` hunk header (the number after '+').
    private static int ParseHunkHeadStart(string header)
    {
        int plus = header.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return 0;
        }
        int index = plus + 1;
        int value = 0;
        bool any = false;
        while (index < header.Length && char.IsAsciiDigit(header[index]))
        {
            value = (value * 10) + (header[index] - '0');
            any = true;
            index++;
        }
        return any ? value : 0;
    }

    private static DateTimeOffset DateOf(JsonElement element, string property)
    {
        string value = GitHubHttp.StringOf(element, property);
        return DateTimeOffset.TryParse(value, out DateTimeOffset parsed) ? parsed : DateTimeOffset.UnixEpoch;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > maxBytes)
        {
            throw new InvalidDataException("GitHub response exceeded the size limit.");
        }
        await using Stream source = await content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream destination = new(capacity: Math.Min(maxBytes, 64 * 1024));
        byte[] buffer = new byte[8 * 1024];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return destination.ToArray();
            }
            if (destination.Length + read > maxBytes)
            {
                throw new InvalidDataException("GitHub response exceeded the size limit.");
            }
            destination.Write(buffer, 0, read);
        }
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
        // case, which the host reflects by leaving the last-known status (it does not force a transition).
        JsonElement node = nodes[0];
        foreach (JsonElement candidate in nodes.EnumerateArray())
        {
            if (PrStateOf(candidate) == PullRequestState.Open)
            {
                node = candidate;
                break;
            }
        }

        return new ReviewStatus(
            AggregateDecision(node), GitHubHttp.NumberOf(node, "number"), PrStateOf(node),
            GitHubHttp.StringOf(node, "headRefOid"));
    }

    public async Task MergePullRequestAsync(
        string accessToken,
        string owner,
        string repo,
        int pullNumber,
        string expectedHeadSha,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = GitHubHttp.NewTimeout(cancellationToken);
        Uri endpoint = new(
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls/{pullNumber}/merge");
        using HttpRequestMessage request = NewRequest(HttpMethod.Put, endpoint, accessToken);
        // Pin the merge to the exact head we approved: GitHub rejects (409) the merge if the head moved on,
        // so a version pushed after approval can't slip through unreviewed. An empty sha means we couldn't
        // read the head — omit it rather than send "" (which GitHub would treat as a real, never-matching sha).
        object body = string.IsNullOrEmpty(expectedHeadSha)
            ? new { }
            : new { sha = expectedHeadSha };
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            // 405 (not mergeable — an unmet review requirement / a conflict), 409 (the head moved since it was
            // approved), 403 (permission revoked), 404 — all surface as one plain, bounded line (never a body
            // that might echo the branch/PR internals), so the host reports a plain reason and leaves the
            // document where it was.
            throw new HttpRequestException(
                $"GitHub rejected the publish (HTTP {(int)response.StatusCode}).");
        }
    }

    public async Task DeleteBranchAsync(
        string accessToken,
        string owner,
        string repo,
        string branch,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = GitHubHttp.NewTimeout(cancellationToken);
        // The ref lives at git/refs/heads/{branch}; escape each path segment but keep the '/' separators a
        // hierarchical branch name (e.g. spec/refunds) carries, so the ref resolves rather than 404ing.
        string encodedRef = string.Join('/', branch.Split('/').Select(Uri.EscapeDataString));
        Uri endpoint = new(
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/git/refs/heads/{encodedRef}");
        using HttpRequestMessage request = NewRequest(HttpMethod.Delete, endpoint, accessToken);
        using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GitHub rejected the branch delete (HTTP {(int)response.StatusCode}).");
        }
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

    public async Task<IReadOnlyList<ReviewSummary>> ListReviewRequestsAsync(
        string accessToken, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = GitHubHttp.NewTimeout(cancellationToken);
        Dictionary<string, (ReviewSummary Summary, string UpdatedAt)> byUrl =
            new(StringComparer.OrdinalIgnoreCase);

        await CollectRestSearchAsync(
            accessToken, "is:pr is:open review-requested:@me sort:updated-desc", ReviewRole.Reviewer, byUrl,
            timeout.Token);

        // Team membership can be unavailable without `read:org`. Direct requests remain useful in that
        // case; search only the memberships GitHub actually disclosed instead of failing the whole panel.
        foreach (string team in await ListViewerTeamsBestEffortAsync(accessToken, timeout.Token))
        {
            await CollectRestSearchAsync(
                accessToken, $"is:pr is:open team-review-requested:{team} sort:updated-desc",
                ReviewRole.Reviewer, byUrl, timeout.Token);
        }

        return
        [
            .. byUrl.Values
                .OrderByDescending(item => item.UpdatedAt, StringComparer.Ordinal)
                .Select(item => item.Summary),
        ];
    }

    public async Task<IReadOnlyList<ReviewSummary>> ListPullRequestsAsync(
        string accessToken, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout = GitHubHttp.NewTimeout(cancellationToken);
        Dictionary<string, (ReviewSummary Summary, string UpdatedAt)> byUrl =
            new(StringComparer.OrdinalIgnoreCase);
        await CollectRestSearchAsync(
            accessToken, "is:pr is:open author:@me sort:updated-desc", ReviewRole.Author, byUrl,
            timeout.Token);
        // `involves` includes authored items, mentions, assignments, comments, and reviews. Authored results
        // were collected first, so dedupe preserves their more specific role.
        await CollectRestSearchAsync(
            accessToken, "is:pr is:open involves:@me sort:updated-desc", ReviewRole.Reviewer, byUrl,
            timeout.Token);
        return
        [
            .. byUrl.Values
                .OrderByDescending(item => item.UpdatedAt, StringComparer.Ordinal)
                .Select(item => item.Summary),
        ];
    }

    private async Task CollectRestSearchAsync(
        string accessToken,
        string query,
        ReviewRole role,
        Dictionary<string, (ReviewSummary Summary, string UpdatedAt)> byUrl,
        CancellationToken cancellationToken)
    {
        // GitHub Search exposes at most 1,000 results. A 100-row page keeps the request count bounded while
        // still following every page the API can return.
        for (int page = 1; page <= 10; page++)
        {
            string encoded = Uri.EscapeDataString(query);
            Uri endpoint = new($"https://api.github.com/search/issues?q={encoded}&per_page=100&page={page}");
            using HttpRequestMessage request = NewRequest(HttpMethod.Get, endpoint, accessToken);
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"GitHub rejected the review-request search (HTTP {(int)response.StatusCode}).");
            }

            using JsonDocument document = JsonDocument.Parse(body);
            if (!TryProperty(document.RootElement, "items", out JsonElement items)
                || items.ValueKind != JsonValueKind.Array)
            {
                throw new HttpRequestException("GitHub returned a malformed review-request search.");
            }

            foreach (JsonElement item in items.EnumerateArray())
            {
                string url = GitHubHttp.StringOf(item, "html_url");
                if (url.Length == 0 || byUrl.ContainsKey(url))
                {
                    continue;
                }

                string repo = RepoNameFromApiUrl(GitHubHttp.StringOf(item, "repository_url"));
                byUrl[url] = (
                    new ReviewSummary(
                        GitHubHttp.NumberOf(item, "number"), GitHubHttp.StringOf(item, "title"), url, repo,
                        role, ReviewDecision.InReview),
                    GitHubHttp.StringOf(item, "updated_at"));
            }

            if (items.GetArrayLength() < 100)
            {
                break;
            }
        }
    }

    private async Task<IReadOnlyList<string>> ListViewerTeamsBestEffortAsync(
        string accessToken, CancellationToken cancellationToken)
    {
        HashSet<string> teams = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (int page = 1; page <= 10; page++)
            {
                Uri endpoint = new($"https://api.github.com/user/teams?per_page=100&page={page}");
                using HttpRequestMessage request = NewRequest(HttpMethod.Get, endpoint, accessToken);
                using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return [.. teams];
                }

                using JsonDocument document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return [.. teams];
                }

                foreach (JsonElement team in document.RootElement.EnumerateArray())
                {
                    string slug = GitHubHttp.StringOf(team, "slug");
                    string organization = TryProperty(team, "organization", out JsonElement org)
                        ? GitHubHttp.StringOf(org, "login")
                        : string.Empty;
                    if (organization.Length > 0 && slug.Length > 0)
                    {
                        teams.Add($"{organization}/{slug}");
                    }
                }

                if (document.RootElement.GetArrayLength() < 100)
                {
                    break;
                }
            }
        }
        catch (HttpRequestException)
        {
            // Team discovery is optional: direct review requests still form a complete usable result.
        }
        catch (JsonException)
        {
            // An unreadable team page means those memberships are unknown, not that direct results vanish.
        }

        return [.. teams];
    }

    private static string RepoNameFromApiUrl(string repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out Uri? uri))
        {
            return string.Empty;
        }

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        return segments.Length >= 3 && segments[0].Equals("repos", StringComparison.OrdinalIgnoreCase)
            ? $"{segments[1]}/{segments[2]}"
            : string.Empty;
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
            string url = GitHubHttp.StringOf(node, "url");
            if (url.Length == 0 || byUrl.ContainsKey(url))
            {
                continue;
            }

            string repo = TryProperty(node, "repository", out JsonElement repoNode)
                ? GitHubHttp.StringOf(repoNode, "nameWithOwner")
                : string.Empty;

            byUrl[url] = (
                new ReviewSummary(
                    GitHubHttp.NumberOf(node, "number"), GitHubHttp.StringOf(node, "title"), url, repo, role,
                    DecisionOf(GitHubHttp.StringOf(node, "reviewDecision"))),
                GitHubHttp.StringOf(node, "updatedAt"));
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

    private static PullRequestState PrStateOf(JsonElement node) => GitHubHttp.StringOf(node, "state") switch
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
        string headOid = GitHubHttp.StringOf(node, "headRefOid");
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
            switch (GitHubHttp.StringOf(review, "state"))
            {
                case "CHANGES_REQUESTED":
                    changesRequested = true;
                    break;
                case "APPROVED"
                    when headOid.Length > 0
                        && TryProperty(review, "commit", out JsonElement commit)
                        && GitHubHttp.StringOf(commit, "oid") == headOid:
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
        using CancellationTokenSource timeout = GitHubHttp.NewTimeout(cancellationToken);

        using HttpRequestMessage request = NewRequest(HttpMethod.Post, GraphQlEndpoint, accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub rejected the {what} query (HTTP {(int)response.StatusCode}).");
        }

        byte[] responseBody = await ReadBoundedAsync(
            response.Content, MaxGraphQlResponseBytes, timeout.Token);
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
        request.Headers.UserAgent.Add(GitHubHttp.UserAgent);
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
}
