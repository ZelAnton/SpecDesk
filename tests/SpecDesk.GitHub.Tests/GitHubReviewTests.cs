using System.Net;
using System.Text.Json;

namespace SpecDesk.GitHub.Tests;

// Covers the production PR-create transport (GitHubReviewClient.OpenPullRequestAsync) against a stubbed
// HTTP response: the success parse, the non-2xx → throw, and the well-formed authorized request body.
[TestFixture]
public sealed class GitHubReviewTests
{
	// Hoisted out of the test bodies so the array literals aren't flagged as repeated constant arguments (CA1861).
	private static readonly int[] ExpectedCommentableLines = [1, 2, 3];
	private static readonly int[] ExpectedMultiHunkLines = [1, 20, 21];

	private sealed class OversizedCommentsHandler(int bytes = 1_048_577) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new UnknownLengthContent(bytes),
			});
	}

	private sealed class UnknownLengthContent(int bytes) : HttpContent
	{
		protected override bool TryComputeLength(out long length)
		{
			length = 0;
			return false;
		}

		protected override async Task SerializeToStreamAsync(
			Stream stream, TransportContext? context)
		{
			byte[] buffer = new byte[8_192];
			int remaining = bytes;
			while (remaining > 0)
			{
				int count = Math.Min(buffer.Length, remaining);
				await stream.WriteAsync(buffer.AsMemory(0, count));
				remaining -= count;
			}
		}
	}

	[Test]
	public void ListReviewCommentsAsync_RejectsUnknownLengthResponseOverTheByteCap()
	{
		using HttpClient http = new(new OversizedCommentsHandler());
		GitHubReviewClient client = new(http);
		Assert.ThrowsAsync<InvalidDataException>(async () =>
			await client.ListReviewCommentsAsync("token", "owner", "repo", 42));
	}

	[Test]
	public void GetPullRequestDetailsAsync_RejectsAnOversizedGraphQlResponse()
	{
		using HttpClient http = new(new OversizedCommentsHandler(4_194_305));
		GitHubReviewClient client = new(http);
		Assert.ThrowsAsync<InvalidDataException>(async () =>
			await client.GetPullRequestDetailsAsync("token", "owner", "repo", 42));
	}

	[Test]
	public async Task ListReviewCommentsAsync_RequestsNewestBoundedPageAndTruncatesBodies()
	{
		string body = "[{\"id\":123,\"path\":\"specs/billing.md\",\"body\":\""
			+ new string('x', 4_100)
			+ "\",\"created_at\":\"2026-07-13T00:00:00Z\",\"user\":{\"login\":\"octo\"}}]";
		StubHttpMessageHandler handler = new(HttpStatusCode.OK, body);
		using HttpClient http = new(handler);
		GitHubReviewClient client = new(http);

		IReadOnlyList<ReviewComment> comments = await client.ListReviewCommentsAsync(
			"token", "owner", "repo", 42);

		Assert.Multiple(() =>
		{
			Assert.That(handler.LastRequest?.RequestUri?.Query,
				Is.EqualTo("?per_page=100&sort=created&direction=desc"));
			Assert.That(comments, Has.Count.EqualTo(1));
			Assert.That(comments[0].Path, Is.EqualTo("specs/billing.md"));
			Assert.That(comments[0].Body, Has.Length.EqualTo(4_001));
			Assert.That(comments[0].Body, Does.EndWith("…"));
		});
	}

	[Test]
	public async Task GetPullRequestDetailsAsync_MapsConversationReviewersCommitsAndChecks()
	{
		const string response = """
			{"data":{"viewer":{"login":"octo"},"repository":{"pullRequest":{"number":42,"title":"Clarify refunds","body":"Explain the window.",
			"url":"https://github.com/octo/spec/pull/42","state":"OPEN","isDraft":true,"baseRefName":"main",
			"headRefName":"spec/refunds","author":{"login":"alex","avatarUrl":"https://img/alex"},
			"reviewRequests":{"nodes":[{"requestedReviewer":{"login":"sam","avatarUrl":"https://img/sam"}}]},
			"latestReviews":{"nodes":[{"author":{"login":"completed","avatarUrl":"https://img/completed"}}]},
			"commits":{"totalCount":51,"nodes":[{"commit":{"oid":"abcdef","abbreviatedOid":"abcdef0",
			"messageHeadline":"Clarify window","committedDate":"2026-07-14T09:00:00Z",
			"statusCheckRollup":{"state":"SUCCESS"}}}]}}}}}
			""";
		string longOwnComment = new('x', 9_000);
		string conversation = JsonSerializer.Serialize(new[]
		{
			new
			{
				id = 9,
				body = longOwnComment,
				created_at = "2026-07-14T10:00:00Z",
				updated_at = "2026-07-14T10:01:00Z",
				user = new { login = "octo", avatar_url = "https://img/octo" },
			},
		});
		const string inline = """
			[{"id":10,"path":"spec.md","body":"Inline note","created_at":"2026-07-14T10:02:00Z",
			"updated_at":"2026-07-14T10:02:00Z","user":{"login":"reviewer","avatar_url":""}}]
			""";
		using ScriptedHttpMessageHandler handler = new(
			(HttpStatusCode.OK, response),
			(HttpStatusCode.OK, conversation),
			(HttpStatusCode.OK, inline));
		using HttpClient http = new(handler);
		GitHubReviewClient client = new(http);

		PullRequestDetails details = await client.GetPullRequestDetailsAsync("token", "octo", "spec", 42);

		Assert.Multiple(() =>
		{
			Assert.That(details.Title, Is.EqualTo("Clarify refunds"));
			Assert.That(details.IsDraft, Is.True);
			Assert.That(details.Reviewers, Has.Count.EqualTo(2));
			Assert.That(details.Reviewers.Select(item => item.Login), Does.Contain("sam"));
			Assert.That(details.Reviewers.Select(item => item.Login), Does.Contain("completed"));
			Assert.That(details.Comments, Has.Count.EqualTo(2));
			Assert.That(details.Comments[0].Kind, Is.EqualTo("conversation"));
			Assert.That(details.Comments[0].Body, Has.Length.EqualTo(9_000));
			Assert.That(details.Comments[0].ViewerDidAuthor, Is.True);
			Assert.That(details.Comments[1].Path, Is.EqualTo("spec.md"));
			Assert.That(details.Commits.Single().CheckState, Is.EqualTo("success"));
			Assert.That(details.CommitsIncomplete, Is.True);
			Assert.That(handler.Requests[0], Is.EqualTo(new Uri("https://api.github.com/graphql")));
			Assert.That(handler.Requests[1].AbsolutePath, Is.EqualTo("/repos/octo/spec/issues/42/comments"));
			Assert.That(handler.Requests[2].AbsolutePath, Is.EqualTo("/repos/octo/spec/pulls/42/comments"));
		});
	}

	[Test]
	public async Task GetPullRequestDetailsAsync_SendsABalancedGraphQlDocument()
	{
		const string metadata = """
			{"data":{"viewer":{"login":"octo"},"repository":{"pullRequest":{"number":42,
			"title":"Review","body":"","url":"https://github.com/octo/spec/pull/42","state":"OPEN",
			"isDraft":false,"baseRefName":"main","headRefName":"spec/review","author":{"login":"octo"},
			"reviewRequests":{"nodes":[]},"latestReviews":{"nodes":[]},"commits":{"nodes":[]}}}}}
			""";
		using ScriptedHttpMessageHandler handler = new(
			(HttpStatusCode.OK, metadata),
			(HttpStatusCode.Forbidden, "{}"));
		using HttpClient http = new(handler);
		GitHubReviewClient client = new(http);

		await client.GetPullRequestDetailsAsync("token", "octo", "spec", 42);

		using JsonDocument request = JsonDocument.Parse(handler.RequestBodies[0]!);
		string query = request.RootElement.GetProperty("query").GetString()!;
		int depth = 0;
		foreach (char character in query)
		{
			if (character == '{')
			{
				depth++;
			}
			else if (character == '}')
			{
				depth--;
				Assert.That(depth, Is.GreaterThanOrEqualTo(0), "GraphQL closes a selection before it opens one.");
			}
		}
		Assert.That(depth, Is.Zero, "GraphQL selections must be balanced.");
	}

	[Test]
	public async Task GetPullRequestDetailsAsync_KeepsCoreDocumentWhenCommentsFail()
	{
		const string metadata = """
			{"data":{"viewer":{"login":"octo"},"repository":{"pullRequest":{"number":42,
			"title":"Review title","body":"Review description","url":"https://github.com/octo/spec/pull/42",
			"state":"OPEN","isDraft":false,"baseRefName":"main","headRefName":"spec/review",
			"author":{"login":"octo"},"reviewRequests":{"nodes":[]},"latestReviews":{"nodes":[]},
			"commits":{"totalCount":1,"nodes":[{"commit":{"oid":"abcdef","abbreviatedOid":"abcdef0",
			"messageHeadline":"Document history","committedDate":"2026-07-14T09:00:00Z",
			"statusCheckRollup":{"state":"SUCCESS"}}}]}}}}}
			""";
		using ScriptedHttpMessageHandler handler = new(
			(HttpStatusCode.OK, metadata),
			(HttpStatusCode.Forbidden, "{}"));
		using HttpClient http = new(handler);
		GitHubReviewClient client = new(http);

		PullRequestDetails details = await client.GetPullRequestDetailsAsync("token", "octo", "spec", 42);

		Assert.Multiple(() =>
		{
			Assert.That(details.Title, Is.EqualTo("Review title"));
			Assert.That(details.Body, Is.EqualTo("Review description"));
			Assert.That(details.Commits.Single().Title, Is.EqualTo("Document history"));
			Assert.That(details.Comments, Is.Empty);
			Assert.That(details.CommentsIncomplete, Is.True);
			Assert.That(handler.Requests, Has.Count.EqualTo(2));
		});
	}

	[Test]
	public async Task GetPullRequestDetailsAsync_MarksCommentsIncompleteAtThePagingSafetyLimit()
	{
		const string metadata = """
			{"data":{"viewer":{"login":"octo"},"repository":{"pullRequest":{"number":42,
			"title":"Review","body":"","url":"https://github.com/octo/spec/pull/42","state":"OPEN",
			"isDraft":false,"baseRefName":"main","headRefName":"spec/review","author":{"login":"octo"},
			"reviewRequests":{"nodes":[]},"latestReviews":{"nodes":[]},"commits":{"nodes":[]}}}}}
			""";
		string fullPage = JsonSerializer.Serialize(
			Enumerable.Range(1, 100).Select(index => new
			{
				id = index,
				body = "Comment",
				created_at = "2026-07-14T10:00:00Z",
				updated_at = "2026-07-14T10:00:00Z",
				user = new { login = "octo" },
			}));
		var responses = new List<(HttpStatusCode Status, string Body)> { (HttpStatusCode.OK, metadata) };
		responses.AddRange(Enumerable.Repeat((HttpStatusCode.OK, fullPage), 20));
		using ScriptedHttpMessageHandler handler = new([.. responses]);
		using HttpClient http = new(handler);
		GitHubReviewClient client = new(http);

		PullRequestDetails details = await client.GetPullRequestDetailsAsync("token", "octo", "spec", 42);

		Assert.Multiple(() =>
		{
			Assert.That(details.CommentsIncomplete, Is.True);
			Assert.That(details.Comments, Has.Count.EqualTo(2_000));
			Assert.That(handler.Requests, Has.Count.EqualTo(21));
		});
	}

	[Test]
	public async Task GetPullRequestDetailsAsync_BoundsTheCombinedCommentTextWithoutTruncatingEditableItems()
	{
		const string metadata = """
			{"data":{"viewer":{"login":"octo"},"repository":{"pullRequest":{"number":42,
			"title":"Review","body":"","url":"https://github.com/octo/spec/pull/42","state":"OPEN",
			"isDraft":false,"baseRefName":"main","headRefName":"spec/review","author":{"login":"octo"},
			"reviewRequests":{"nodes":[]},"latestReviews":{"nodes":[]},"commits":{"nodes":[]}}}}}
			""";
		string commentBody = new('x', 50_000);
		string oversizedPage = JsonSerializer.Serialize(
			Enumerable.Range(1, 100).Select(index => new
			{
				id = index,
				body = commentBody,
				created_at = "2026-07-14T10:00:00Z",
				updated_at = "2026-07-14T10:00:00Z",
				user = new { login = "octo" },
			}));
		using ScriptedHttpMessageHandler handler = new(
			(HttpStatusCode.OK, metadata),
			(HttpStatusCode.OK, oversizedPage));
		using HttpClient http = new(handler);
		GitHubReviewClient client = new(http);

		PullRequestDetails details = await client.GetPullRequestDetailsAsync("token", "octo", "spec", 42);

		Assert.Multiple(() =>
		{
			Assert.That(details.CommentsIncomplete, Is.True);
			Assert.That(details.Comments, Has.Count.EqualTo(83));
			Assert.That(details.Comments, Has.All.Property(nameof(PullRequestComment.Body)).Length.EqualTo(50_000));
			Assert.That(handler.Requests, Has.Count.EqualTo(2));
		});
	}

	[Test]
	public async Task PullRequestCommentMutations_UseIssueConversationEndpointsAndBoundedBodies()
	{
		using StubHttpMessageHandler create = new(HttpStatusCode.Created, "{}");
		using HttpClient createHttp = new(create);
		GitHubReviewClient createClient = new(createHttp);
		await createClient.CreatePullRequestCommentAsync("token", "octo", "spec", 42, "Hello");

		using StubHttpMessageHandler update = new(HttpStatusCode.OK, "{}");
		using HttpClient updateHttp = new(update);
		GitHubReviewClient updateClient = new(updateHttp);
		await updateClient.UpdatePullRequestCommentAsync("token", "octo", "spec", 9, "Updated");

		Assert.Multiple(() =>
		{
			Assert.That(create.LastRequest?.Method, Is.EqualTo(HttpMethod.Post));
			Assert.That(create.LastRequest?.RequestUri?.AbsolutePath,
				Is.EqualTo("/repos/octo/spec/issues/42/comments"));
			Assert.That(create.LastRequestBody, Does.Contain("Hello"));
			Assert.That(update.LastRequest?.Method, Is.EqualTo(HttpMethod.Patch));
			Assert.That(update.LastRequest?.RequestUri?.AbsolutePath,
				Is.EqualTo("/repos/octo/spec/issues/comments/9"));
			Assert.That(update.LastRequestBody, Does.Contain("Updated"));
		});
		Assert.ThrowsAsync<ArgumentException>(async () =>
			await createClient.CreatePullRequestCommentAsync("token", "octo", "spec", 42, " "));
	}
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

    private static async Task<IReadOnlyList<ReviewSummary>> ListReviews(StubHttpMessageHandler handler)
    {
        using HttpClient http = new(handler);
        GitHubReviewClient client = new(http);
        return await client.ListReviewsAsync("gho_token");
    }

    private static async Task<IReadOnlyList<ReviewSummary>> ListReviewRequests(
        ScriptedHttpMessageHandler handler)
    {
        using HttpClient http = new(handler);
        GitHubReviewClient client = new(http);
        return await client.ListReviewRequestsAsync("gho_token");
    }

    private static async Task<IReadOnlyList<ReviewSummary>> ListPullRequests(
        ScriptedHttpMessageHandler handler)
    {
        using HttpClient http = new(handler);
        GitHubReviewClient client = new(http);
        return await client.ListPullRequestsAsync("gho_token");
    }

    [Test]
    public async Task ListPullRequestsAsync_combines_author_and_involves_and_keeps_author_role()
    {
        const string authored =
            """{"items":[{"number":1,"title":"Mine","html_url":"https://github.com/o/r/pull/1","repository_url":"https://api.github.com/repos/o/r","updated_at":"2026-07-01T00:00:00Z"}]}""";
        const string involved =
            """{"items":[{"number":1,"title":"Duplicate","html_url":"https://github.com/o/r/pull/1","repository_url":"https://api.github.com/repos/o/r","updated_at":"2026-07-01T00:00:00Z"},{"number":2,"title":"Joined","html_url":"https://github.com/o/x/pull/2","repository_url":"https://api.github.com/repos/o/x","updated_at":"2026-07-02T00:00:00Z"}]}""";
        using ScriptedHttpMessageHandler handler = new(
            (HttpStatusCode.OK, authored), (HttpStatusCode.OK, involved));

        IReadOnlyList<ReviewSummary> requests = await ListPullRequests(handler);

        Assert.That(requests, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(requests.Single(item => item.Number == 1).Role, Is.EqualTo(ReviewRole.Author));
            Assert.That(requests[0].Title, Is.EqualTo("Joined"));
            Assert.That(handler.Requests[0].OriginalString, Does.Contain("author%3A%40me"));
            Assert.That(handler.Requests[1].OriginalString, Does.Contain("involves%3A%40me"));
            Assert.That(
                handler.Requests.All(uri => uri.OriginalString.Contains("is%3Aopen", StringComparison.Ordinal)),
                Is.True);
        });
    }

    [Test]
    public async Task ListReviewRequestsAsync_includes_known_teams_encodes_queries_and_deduplicates()
    {
        const string direct =
            """{"items":[{"number":7,"title":"Direct","html_url":"https://github.com/o/r/pull/7","repository_url":"https://api.github.com/repos/o/r","updated_at":"2026-07-01T00:00:00Z"}]}""";
        const string teams = """[{"slug":"docs team","organization":{"login":"acme"}}]""";
        const string team =
            """{"items":[{"number":7,"title":"Duplicate","html_url":"https://github.com/o/r/pull/7","repository_url":"https://api.github.com/repos/o/r","updated_at":"2026-07-01T00:00:00Z"},{"number":9,"title":"Team","html_url":"https://github.com/acme/spec/pull/9","repository_url":"https://api.github.com/repos/acme/spec","updated_at":"2026-07-03T00:00:00Z"}]}""";
        using ScriptedHttpMessageHandler handler = new(
            (HttpStatusCode.OK, direct), (HttpStatusCode.OK, teams), (HttpStatusCode.OK, team));

        IReadOnlyList<ReviewSummary> reviews = await ListReviewRequests(handler);

        Assert.That(reviews, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(reviews[0].Title, Is.EqualTo("Team"));
            Assert.That(reviews[1].Repo, Is.EqualTo("o/r"));
            Assert.That(handler.Requests[0].OriginalString, Does.Contain("review-requested%3A%40me"));
            Assert.That(handler.Requests[2].OriginalString, Does.Contain("team-review-requested%3Aacme%2Fdocs%20team"));
        });
    }

    [Test]
    public async Task ListReviewRequestsAsync_pages_full_search_results()
    {
        object[] firstPage = Enumerable.Range(1, 100)
            .Select(number => new
            {
                number,
                title = $"Review {number}",
                html_url = $"https://github.com/o/r/pull/{number}",
                repository_url = "https://api.github.com/repos/o/r",
                updated_at = "2026-07-01T00:00:00Z",
            })
            .Cast<object>()
            .ToArray();
        string first = JsonSerializer.Serialize(new { items = firstPage });
        const string second =
            """{"items":[{"number":101,"title":"Review 101","html_url":"https://github.com/o/r/pull/101","repository_url":"https://api.github.com/repos/o/r","updated_at":"2026-07-02T00:00:00Z"}]}""";
        using ScriptedHttpMessageHandler handler = new(
            (HttpStatusCode.OK, first), (HttpStatusCode.OK, second), (HttpStatusCode.OK, "[]"));

        IReadOnlyList<ReviewSummary> reviews = await ListReviewRequests(handler);

        Assert.That(reviews, Has.Count.EqualTo(101));
        Assert.That(handler.Requests[1].Query, Does.Contain("page=2"));
    }

    [Test]
    public async Task ListReviewsAsync_maps_role_from_the_search_and_sorts_most_recent_first()
    {
        // Role comes from WHICH search matched (authored → Author, review-requested → Reviewer). The merged
        // list is sorted by updatedAt descending, so the newer to-review PR precedes the older authored one.
        using StubHttpMessageHandler handler = new(
            HttpStatusCode.OK,
            """{"data":{"authored":{"nodes":[{"number":42,"title":"Clarify refunds","url":"https://github.com/octo/spec/pull/42","reviewDecision":"CHANGES_REQUESTED","updatedAt":"2026-07-01T00:00:00Z","repository":{"nameWithOwner":"octo/spec"}}]},"toReview":{"nodes":[{"number":7,"title":"Payment terms","url":"https://github.com/octo/other/pull/7","reviewDecision":null,"updatedAt":"2026-07-03T00:00:00Z","repository":{"nameWithOwner":"octo/other"}}]}}}""");

        IReadOnlyList<ReviewSummary> reviews = await ListReviews(handler);

        Assert.That(reviews, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            // The to-review PR (updated 07-03) sorts ahead of the authored one (07-01).
            Assert.That(reviews[0].Role, Is.EqualTo(ReviewRole.Reviewer));
            Assert.That(reviews[0].Decision, Is.EqualTo(ReviewDecision.InReview));
            Assert.That(reviews[1].Role, Is.EqualTo(ReviewRole.Author));
            Assert.That(reviews[1].Repo, Is.EqualTo("octo/spec"));
            Assert.That(reviews[1].Decision, Is.EqualTo(ReviewDecision.ChangesRequested));
            Assert.That(reviews[1].Url, Is.EqualTo("https://github.com/octo/spec/pull/42"));
        });
    }

    [Test]
    public async Task ListReviewsAsync_skips_a_node_with_no_pr_fields()
    {
        // A search on ISSUE can include a node the PullRequest fragment didn't match (no url) — skip it.
        using StubHttpMessageHandler handler = new(
            HttpStatusCode.OK,
            """{"data":{"authored":{"nodes":[{},{"number":7,"title":"T","url":"https://github.com/o/r/pull/7","reviewDecision":"APPROVED","updatedAt":"2026-07-03T00:00:00Z","repository":{"nameWithOwner":"o/r"}}]},"toReview":{"nodes":[]}}}""");

        IReadOnlyList<ReviewSummary> reviews = await ListReviews(handler);

        Assert.That(reviews, Has.Count.EqualTo(1));
        Assert.That(reviews[0].Decision, Is.EqualTo(ReviewDecision.Approved));
    }

    [Test]
    public void ListReviewsAsync_throws_on_a_non_success_status()
    {
        using StubHttpMessageHandler handler = new(HttpStatusCode.Unauthorized, "{}");

        Assert.ThrowsAsync<HttpRequestException>(() => ListReviews(handler));
    }

    [Test]
    public void ListReviewsAsync_throws_on_a_total_graphql_failure()
    {
        // 200 with errors and data:null (GitHub's shape for a secondary rate-limit / scope problem) is a
        // total failure — throwing lets the host show a reason rather than "you have no open reviews".
        using StubHttpMessageHandler handler = new(
            HttpStatusCode.OK, """{"errors":[{"message":"rate limited"}],"data":null}""");

        Assert.ThrowsAsync<HttpRequestException>(() => ListReviews(handler));
    }

    [Test]
    public async Task ListReviewsAsync_still_returns_results_on_a_partial_graphql_error()
    {
        // errors present but data resolved — render what came back rather than blanking the list.
        using StubHttpMessageHandler handler = new(
            HttpStatusCode.OK,
            """{"errors":[{"message":"one node failed"}],"data":{"authored":{"nodes":[{"number":7,"title":"T","url":"https://github.com/o/r/pull/7","reviewDecision":"APPROVED","updatedAt":"2026-07-03T00:00:00Z","repository":{"nameWithOwner":"o/r"}}]},"toReview":{"nodes":[]}}}""");

        IReadOnlyList<ReviewSummary> reviews = await ListReviews(handler);

        Assert.That(reviews, Has.Count.EqualTo(1));
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

    [Test]
    public async Task CreateReviewCommentAsync_posts_the_commit_path_line_and_side_and_returns_the_id()
    {
        using StubHttpMessageHandler handler = new(HttpStatusCode.Created, """{"id":9099}""");
        using HttpClient http = new(handler);
        GitHubReviewClient client = new(http);

        long id = await client.CreateReviewCommentAsync(
            "gho_token", "octo", "spec-repo", 42, "headsha", "specs/billing.md", 7, "RIGHT", "Clarify this.");

        Assert.That(handler.LastRequest, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(id, Is.EqualTo(9099));
            Assert.That(handler.LastRequest!.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(
                handler.LastRequest.RequestUri,
                Is.EqualTo(new Uri("https://api.github.com/repos/octo/spec-repo/pulls/42/comments")));
            Assert.That(handler.LastRequestBody, Does.Contain("\"commit_id\":\"headsha\""));
            Assert.That(handler.LastRequestBody, Does.Contain("\"path\":\"specs/billing.md\""));
            Assert.That(handler.LastRequestBody, Does.Contain("\"line\":7"));
            Assert.That(handler.LastRequestBody, Does.Contain("\"side\":\"RIGHT\""));
            Assert.That(handler.LastRequestBody, Does.Contain("\"body\":\"Clarify this.\""));
        });
    }

    [Test]
    public void CreateReviewCommentAsync_throws_when_GitHub_rejects_the_post()
    {
        // GitHub 422s a comment on a line outside the diff — the host surfaces a plain reason and the thread
        // stays local. A blank body is rejected locally without a request.
        using StubHttpMessageHandler handler = new(HttpStatusCode.UnprocessableEntity, "{}");
        using HttpClient http = new(handler);
        GitHubReviewClient client = new(http);

        Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.CreateReviewCommentAsync(
                "gho_token", "octo", "spec-repo", 42, "headsha", "spec.md", 7, "RIGHT", "Body"));
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.CreateReviewCommentAsync(
                "gho_token", "octo", "spec-repo", 42, "headsha", "spec.md", 7, "RIGHT", "  "));
    }

    [Test]
    public async Task GetReviewSyncAsync_reads_head_commentable_lines_and_the_files_inline_comments()
    {
        const string pull = """{"number":42,"head":{"sha":"headsha123"}}""";
        const string files = """
            [{"filename":"specs/billing.md","patch":"@@ -1,2 +1,3 @@\n ctx\n+added line\n more"},
             {"filename":"README.md","patch":"@@ -1 +1 @@\n-old\n+new"}]
            """;
        // One comment on the target file (kept, RIGHT side, root) and one on another file (dropped).
        const string comments = """
            [{"id":1001,"path":"specs/billing.md","line":2,"side":"RIGHT","commit_id":"headsha123",
              "in_reply_to_id":null,"user":{"login":"sam"},"body":"Clarify the window here.",
              "created_at":"2026-07-14T10:00:00Z"},
             {"id":2002,"path":"README.md","line":1,"side":"RIGHT","commit_id":"headsha123",
              "user":{"login":"alex"},"body":"Elsewhere","created_at":"2026-07-14T10:01:00Z"}]
            """;
        using ScriptedHttpMessageHandler handler = new(
            (HttpStatusCode.OK, pull), (HttpStatusCode.OK, files), (HttpStatusCode.OK, comments));
        using HttpClient http = new(handler);
        GitHubReviewClient client = new(http);

        ReviewSyncSnapshot snapshot = await client.GetReviewSyncAsync(
            "gho_token", "octo", "spec-repo", 42, "specs/billing.md");

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.HeadCommitId, Is.EqualTo("headsha123"));
            Assert.That(snapshot.Path, Is.EqualTo("specs/billing.md"));
            // ' ctx' (line 1), '+added line' (line 2), ' more' (line 3) are commentable head lines.
            Assert.That(snapshot.CommentableLines, Is.EqualTo(ExpectedCommentableLines));
            Assert.That(snapshot.Comments, Has.Count.EqualTo(1));
            Assert.That(snapshot.Comments[0].Id, Is.EqualTo(1001));
            Assert.That(snapshot.Comments[0].Line, Is.EqualTo(2));
            Assert.That(snapshot.Comments[0].Side, Is.EqualTo("RIGHT"));
            Assert.That(snapshot.Comments[0].Author, Is.EqualTo("sam"));
            Assert.That(snapshot.Comments[0].InReplyToId, Is.EqualTo(0));
            Assert.That(handler.Requests[0].AbsolutePath, Is.EqualTo("/repos/octo/spec-repo/pulls/42"));
            Assert.That(handler.Requests[1].AbsolutePath, Is.EqualTo("/repos/octo/spec-repo/pulls/42/files"));
            Assert.That(handler.Requests[2].AbsolutePath, Is.EqualTo("/repos/octo/spec-repo/pulls/42/comments"));
        });
    }

    [Test]
    public void CommentableHeadLines_counts_context_and_additions_and_skips_removals()
    {
        // A removed ('-') line advances no head line, so the addition that follows takes the vacated number.
        IReadOnlyList<int> lines = GitHubReviewClient.CommentableHeadLines(
            "@@ -1,3 +1,3 @@\n ctx\n-removed\n+added\n ctx2");
        Assert.That(lines, Is.EqualTo(ExpectedCommentableLines));
    }

    [Test]
    public void CommentableHeadLines_reseats_the_head_counter_across_multiple_hunks()
    {
        IReadOnlyList<int> lines = GitHubReviewClient.CommentableHeadLines(
            "@@ -1,1 +1,1 @@\n a\n@@ -10,1 +20,2 @@\n+b\n c");
        Assert.That(lines, Is.EqualTo(ExpectedMultiHunkLines));
    }

    [Test]
    public void CommentableHeadLines_returns_empty_for_an_empty_or_headerless_patch()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GitHubReviewClient.CommentableHeadLines(string.Empty), Is.Empty);
            Assert.That(GitHubReviewClient.CommentableHeadLines("no hunk header here"), Is.Empty);
        });
    }
}
