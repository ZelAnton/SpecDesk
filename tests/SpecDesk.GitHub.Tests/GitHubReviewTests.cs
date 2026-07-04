using System.Net;

namespace SpecDesk.GitHub.Tests;

// Covers the production PR-create transport (GitHubReviewClient.OpenPullRequestAsync) against a stubbed
// HTTP response: the success parse, the non-2xx → throw, and the well-formed authorized request body.
[TestFixture]
public sealed class GitHubReviewTests
{
    private static async Task<PullRequest> Open(StubHttpMessageHandler handler)
    {
        using HttpClient http = new(handler);
        GitHubReviewClient client = new(http);
        return await client.OpenPullRequestAsync(
            "gho_token", "octo", "spec-repo", "spec/draft", "main", "Title", "Body");
    }

    [Test]
    public async Task OpenPullRequestAsync_returns_the_number_and_url_on_success()
    {
        using StubHttpMessageHandler handler = new(
            HttpStatusCode.Created,
            """{"number":42,"html_url":"https://github.com/octo/spec-repo/pull/42"}""");

        PullRequest pr = await Open(handler);

        Assert.Multiple(() =>
        {
            Assert.That(pr.Number, Is.EqualTo(42));
            Assert.That(pr.Url, Is.EqualTo("https://github.com/octo/spec-repo/pull/42"));
        });
    }

    [Test]
    public void OpenPullRequestAsync_throws_when_GitHub_rejects_the_create()
    {
        // A 422 that is NOT the "already exists" case (here an invalid base) is a genuine rejection.
        using StubHttpMessageHandler handler = new(
            HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed","errors":[{"field":"base","code":"invalid"}]}""");

        Assert.ThrowsAsync<HttpRequestException>(() => Open(handler));
    }

    [Test]
    public async Task OpenPullRequestAsync_reconciles_an_already_exists_422_as_an_open_pr()
    {
        // The branch already has an open PR (e.g. sent earlier, then re-sent the same day). GitHub 422s the
        // create with "already exists"; that must resolve to success (unknown coordinates) so the author
        // settles to In review rather than being stranded in Draft with a real PR open.
        using StubHttpMessageHandler handler = new(
            HttpStatusCode.UnprocessableEntity,
            """{"message":"Validation Failed","errors":[{"message":"A pull request already exists for octo:spec/draft."}]}""");

        PullRequest pr = await Open(handler);

        Assert.Multiple(() =>
        {
            Assert.That(pr.Number, Is.EqualTo(0));
            Assert.That(pr.Url, Is.EqualTo(string.Empty));
        });
    }

    [Test]
    public async Task OpenPullRequestAsync_treats_a_malformed_success_body_as_an_opened_pr()
    {
        // A 2xx with a non-JSON body still means GitHub created the PR — degrade to unknown coordinates
        // rather than throwing (which would strand the author in Draft with a PR already open).
        using StubHttpMessageHandler handler = new(HttpStatusCode.Created, "not json at all");

        PullRequest pr = await Open(handler);

        Assert.Multiple(() =>
        {
            Assert.That(pr.Number, Is.EqualTo(0));
            Assert.That(pr.Url, Is.EqualTo(string.Empty));
        });
    }

    [Test]
    public async Task OpenPullRequestAsync_pins_the_rest_api_version()
    {
        using StubHttpMessageHandler handler = new(
            HttpStatusCode.Created, """{"number":1,"html_url":"https://example/pull/1"}""");

        await Open(handler);

        Assert.That(
            handler.LastRequest!.Headers.GetValues("X-GitHub-Api-Version"), Does.Contain("2022-11-28"));
    }

    private static async Task<ReviewStatus?> GetStatus(StubHttpMessageHandler handler)
    {
        using HttpClient http = new(handler);
        GitHubReviewClient client = new(http);
        return await client.GetReviewStatusAsync("gho_token", "octo", "spec-repo", "spec/draft");
    }

    [Test]
    public async Task GetReviewStatusAsync_maps_an_approval_of_the_head_commit_and_pr_number()
    {
        using StubHttpMessageHandler handler = new(
            HttpStatusCode.OK,
            """{"data":{"repository":{"pullRequests":{"nodes":[{"number":42,"state":"OPEN","headRefOid":"HEAD1","latestOpinionatedReviews":{"nodes":[{"state":"APPROVED","commit":{"oid":"HEAD1"}}]}}]}}}}""");

        ReviewStatus? status = await GetStatus(handler);

        Assert.That(status, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(status!.Decision, Is.EqualTo(ReviewDecision.Approved));
            Assert.That(status!.PrState, Is.EqualTo(PullRequestState.Open));
            Assert.That(status!.Number, Is.EqualTo(42));
        });
    }

    [Test]
    public async Task GetReviewStatusAsync_lets_a_change_request_outrank_a_head_approval()
    {
        using StubHttpMessageHandler handler = new(
            HttpStatusCode.OK,
            """{"data":{"repository":{"pullRequests":{"nodes":[{"number":7,"url":"u","state":"OPEN","headRefOid":"H","latestOpinionatedReviews":{"nodes":[{"state":"APPROVED","commit":{"oid":"H"}},{"state":"CHANGES_REQUESTED","commit":{"oid":"H"}}]}}]}}}}""");

        Assert.That((await GetStatus(handler))!.Decision, Is.EqualTo(ReviewDecision.ChangesRequested));
    }

    [Test]
    public async Task GetReviewStatusAsync_drops_an_approval_of_an_earlier_commit_but_keeps_a_change_request()
    {
        // Both reviews target an earlier commit (the author has since pushed, head=HEAD2). The stale approval
        // must NOT count (unseen content isn't approved), but a change request is a block that persists until
        // the reviewer re-reviews — so this reads as Changes requested, not In review.
        using StubHttpMessageHandler approvalHandler = new(
            HttpStatusCode.OK,
            """{"data":{"repository":{"pullRequests":{"nodes":[{"number":7,"url":"u","state":"OPEN","headRefOid":"HEAD2","latestOpinionatedReviews":{"nodes":[{"state":"APPROVED","commit":{"oid":"OLD"}}]}}]}}}}""");
        using StubHttpMessageHandler changeHandler = new(
            HttpStatusCode.OK,
            """{"data":{"repository":{"pullRequests":{"nodes":[{"number":7,"url":"u","state":"OPEN","headRefOid":"HEAD2","latestOpinionatedReviews":{"nodes":[{"state":"CHANGES_REQUESTED","commit":{"oid":"OLD"}}]}}]}}}}""");

        Assert.That((await GetStatus(approvalHandler))!.Decision, Is.EqualTo(ReviewDecision.InReview));
        Assert.That((await GetStatus(changeHandler))!.Decision, Is.EqualTo(ReviewDecision.ChangesRequested));
    }

    [Test]
    public async Task GetReviewStatusAsync_treats_no_deciding_reviews_as_in_review()
    {
        // An open PR with no opinionated reviews (or only dismissed ones) — nobody currently approves or
        // blocks. This is also the ordinary-repo case where GitHub's own reviewDecision would be null.
        using StubHttpMessageHandler handler = new(
            HttpStatusCode.OK,
            """{"data":{"repository":{"pullRequests":{"nodes":[{"number":7,"url":"u","state":"OPEN","headRefOid":"H","latestOpinionatedReviews":{"nodes":[{"state":"DISMISSED","commit":{"oid":"H"}}]}}]}}}}""");

        Assert.That((await GetStatus(handler))!.Decision, Is.EqualTo(ReviewDecision.InReview));
    }

    [Test]
    public async Task GetReviewStatusAsync_reports_a_merged_or_closed_pull_request_state()
    {
        using StubHttpMessageHandler merged = new(
            HttpStatusCode.OK,
            """{"data":{"repository":{"pullRequests":{"nodes":[{"number":7,"url":"u","state":"MERGED","headRefOid":"H","latestOpinionatedReviews":{"nodes":[]}}]}}}}""");
        using StubHttpMessageHandler closed = new(
            HttpStatusCode.OK,
            """{"data":{"repository":{"pullRequests":{"nodes":[{"number":7,"url":"u","state":"CLOSED","headRefOid":"H","latestOpinionatedReviews":{"nodes":[]}}]}}}}""");

        Assert.That((await GetStatus(merged))!.PrState, Is.EqualTo(PullRequestState.Merged));
        Assert.That((await GetStatus(closed))!.PrState, Is.EqualTo(PullRequestState.Closed));
    }

    [Test]
    public async Task GetReviewStatusAsync_prefers_the_open_pull_request_over_a_newer_closed_one()
    {
        // The branch carries a newer CLOSED PR (listed first, newest-created) and the live OPEN review PR.
        // The open one must win — otherwise the host would freeze the live review as a dead PR.
        using StubHttpMessageHandler handler = new(
            HttpStatusCode.OK,
            """{"data":{"repository":{"pullRequests":{"nodes":[{"number":9,"state":"CLOSED","headRefOid":"H","latestOpinionatedReviews":{"nodes":[]}},{"number":8,"state":"OPEN","headRefOid":"H","latestOpinionatedReviews":{"nodes":[{"state":"CHANGES_REQUESTED","commit":{"oid":"H"}}]}}]}}}}""");

        ReviewStatus? status = await GetStatus(handler);

        Assert.That(status, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(status!.Number, Is.EqualTo(8));
            Assert.That(status!.PrState, Is.EqualTo(PullRequestState.Open));
            Assert.That(status!.Decision, Is.EqualTo(ReviewDecision.ChangesRequested));
        });
    }

    [Test]
    public async Task GetReviewStatusAsync_returns_null_when_the_branch_never_had_a_pull_request()
    {
        using StubHttpMessageHandler handler = new(
            HttpStatusCode.OK, """{"data":{"repository":{"pullRequests":{"nodes":[]}}}}""");

        Assert.That(await GetStatus(handler), Is.Null);
    }

    [Test]
    public void GetReviewStatusAsync_throws_on_a_partial_graphql_error_response()
    {
        // A 200 whose body carries a top-level `errors` array — its data may be incomplete, which would
        // silently downgrade a real decision, so it's treated as a fault (the host keeps the last status).
        using StubHttpMessageHandler handler = new(
            HttpStatusCode.OK,
            """{"errors":[{"message":"Something went wrong"}],"data":{"repository":null}}""");

        Assert.ThrowsAsync<HttpRequestException>(() => GetStatus(handler));
    }

    [Test]
    public void GetReviewStatusAsync_throws_on_a_non_success_status()
    {
        using StubHttpMessageHandler handler = new(HttpStatusCode.Unauthorized, "{}");

        Assert.ThrowsAsync<HttpRequestException>(() => GetStatus(handler));
    }

    [Test]
    public async Task GetReviewStatusAsync_posts_the_graphql_query_with_the_branch_variable()
    {
        using StubHttpMessageHandler handler = new(
            HttpStatusCode.OK, """{"data":{"repository":{"pullRequests":{"nodes":[]}}}}""");

        await GetStatus(handler);

        Assert.Multiple(() =>
        {
            Assert.That(handler.LastRequest!.RequestUri, Is.EqualTo(new Uri("https://api.github.com/graphql")));
            Assert.That(handler.LastRequestBody, Does.Contain("latestOpinionatedReviews"));
            Assert.That(handler.LastRequestBody, Does.Contain("\"branch\":\"spec/draft\""));
        });
    }

    private static async Task<int> RequestReviewers(StubHttpMessageHandler handler, params string[] reviewers)
    {
        using HttpClient http = new(handler);
        GitHubReviewClient client = new(http);
        return await client.RequestReviewersAsync("gho_token", "octo", "spec-repo", 42, reviewers);
    }

    [Test]
    public async Task RequestReviewersAsync_posts_users_and_teams_partitioned_to_the_pr_endpoint()
    {
        using StubHttpMessageHandler handler = new(HttpStatusCode.Created, "{}");

        int requested = await RequestReviewers(handler, "@alice", "bob", "@octo/reviewers");

        Assert.That(handler.LastRequest, Is.Not.Null);
        Assert.Multiple(() =>
        {
            // The return value is the count actually sent (2 users + 1 team).
            Assert.That(requested, Is.EqualTo(3));
            Assert.That(handler.LastRequest!.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(
                handler.LastRequest.RequestUri,
                Is.EqualTo(new Uri("https://api.github.com/repos/octo/spec-repo/pulls/42/requested_reviewers")));
            Assert.That(handler.LastRequest.Headers.Authorization?.Parameter, Is.EqualTo("gho_token"));
            Assert.That(handler.LastRequest.Headers.GetValues("X-GitHub-Api-Version"), Does.Contain("2022-11-28"));
            // The leading @ is stripped; a handle with '/' becomes a team slug (the last segment).
            Assert.That(handler.LastRequestBody, Does.Contain("\"reviewers\":[\"alice\",\"bob\"]"));
            Assert.That(handler.LastRequestBody, Does.Contain("\"team_reviewers\":[\"reviewers\"]"));
        });
    }

    [Test]
    public void RequestReviewersAsync_throws_when_GitHub_rejects_the_request()
    {
        using StubHttpMessageHandler handler = new(HttpStatusCode.UnprocessableEntity, "{}");

        Assert.ThrowsAsync<HttpRequestException>(() => RequestReviewers(handler, "@alice"));
    }

    [Test]
    public async Task RequestReviewersAsync_makes_no_request_when_there_is_nothing_to_ask()
    {
        using StubHttpMessageHandler handler = new(HttpStatusCode.Created, "{}");

        // Only blank / bare-@ / empty-team entries remain (a "codeowners"-only list is filtered upstream),
        // so there is nothing to request: no HTTP call, and a reported count of zero.
        int requested = await RequestReviewers(handler, "  ", "@", "@org/");

        Assert.That(requested, Is.EqualTo(0));
        Assert.That(handler.LastRequest, Is.Null);
    }

    [Test]
    public async Task OpenPullRequestAsync_posts_a_bearer_authorized_create_request()
    {
        using StubHttpMessageHandler handler = new(
            HttpStatusCode.Created, """{"number":1,"html_url":"https://example/pull/1"}""");

        await Open(handler);

        Assert.That(handler.LastRequest, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(handler.LastRequest!.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(
                handler.LastRequest.RequestUri,
                Is.EqualTo(new Uri("https://api.github.com/repos/octo/spec-repo/pulls")));
            Assert.That(handler.LastRequest.Headers.Authorization?.Scheme, Is.EqualTo("Bearer"));
            Assert.That(handler.LastRequest.Headers.Authorization?.Parameter, Is.EqualTo("gho_token"));
            Assert.That(handler.LastRequest.Headers.UserAgent.ToString(), Does.Contain("SpecDesk"));
            Assert.That(handler.LastRequestBody, Does.Contain("\"title\":\"Title\""));
            Assert.That(handler.LastRequestBody, Does.Contain("\"head\":\"spec/draft\""));
            Assert.That(handler.LastRequestBody, Does.Contain("\"base\":\"main\""));
            Assert.That(handler.LastRequestBody, Does.Contain("\"body\":\"Body\""));
        });
    }
}
