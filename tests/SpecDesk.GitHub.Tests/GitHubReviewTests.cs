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
        using StubHttpMessageHandler handler = new(
            HttpStatusCode.UnprocessableEntity,
            """{"message":"A pull request already exists for octo:spec/draft."}""");

        Assert.ThrowsAsync<HttpRequestException>(() => Open(handler));
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
