using System.Net;
using System.Text;
using SpecDesk.GitHub;

namespace SpecDesk.GitHub.Tests;

[TestFixture]
public sealed class GitHubRepositoryCatalogTests
{
	private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
	{
		public HttpRequestMessage? LastRequest { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			LastRequest = request;
			return Task.FromResult(respond(request));
		}
	}

	private sealed class AsyncHandler(
		Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
	{
		public int Requests { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests++;
			return respond(request, cancellationToken);
		}
	}

	[TestCase("master")]
	[TestCase("trunk")]
	public async Task Metadata_preserves_the_remote_default_branch(string defaultBranch)
	{
		Handler handler = new(_ => Json($$"""{"default_branch":"{{defaultBranch}}"}"""));
		using HttpClient http = new(handler);
		GitHubRepositoryCatalog catalog = new(http);

		GitHubRepositoryMetadata result = await catalog.GetMetadataAsync("octo", "specs", "secret");

		Assert.Multiple(() =>
		{
			Assert.That(result.DefaultBranch, Is.EqualTo(defaultBranch));
			Assert.That(handler.LastRequest?.Headers.Authorization?.Scheme, Is.EqualTo("Bearer"));
			Assert.That(handler.LastRequest?.Headers.Authorization?.Parameter, Is.EqualTo("secret"));
		});
	}

	[Test]
	public async Task Metadata_preserves_description_and_private_visibility()
	{
		Handler handler = new(_ => Json(
			"""{"default_branch":"main","description":"Internal specifications","private":true}"""));
		using HttpClient http = new(handler);
		GitHubRepositoryCatalog catalog = new(http);

		GitHubRepositoryMetadata result = await catalog.GetMetadataAsync("octo", "specs", "secret");

		Assert.Multiple(() =>
		{
			Assert.That(result.DefaultBranch, Is.EqualTo("main"));
			Assert.That(result.Description, Is.EqualTo("Internal specifications"));
			Assert.That(result.IsPrivate, Is.True);
		});
	}

	[Test]
	public async Task PublicMetadata_omits_authorization_and_preserves_description()
	{
		Handler handler = new(_ => Json(
			"""{"default_branch":"main","description":"Public specifications","private":false}"""));
		using HttpClient http = new(handler);
		IGitHubRepositoryCatalog catalog = new GitHubRepositoryCatalog(http);

		GitHubRepositoryMetadata result = await catalog.GetPublicMetadataAsync("outside", "specs");

		Assert.Multiple(() =>
		{
			Assert.That(handler.LastRequest?.Headers.Authorization, Is.Null);
			Assert.That(result.Description, Is.EqualTo("Public specifications"));
			Assert.That(result.IsPrivate, Is.False);
		});
	}

	[Test]
	public async Task Organizations_paginate_deduplicate_and_sort_authorized_memberships()
	{
		int requests = 0;
		Handler handler = new(request =>
		{
			requests++;
			if (requests == 1)
			{
				string items = string.Join(',', Enumerable.Range(0, 100)
					.Select(index => $$"""{"login":"org{{index}}"}"""));
				return Json($"[{items}]");
			}
			return Json("""[{"login":"ORG1"},{"login":"acme"}]""");
		});
		using HttpClient http = new(handler);
		GitHubRepositoryCatalog catalog = new(http);

		IReadOnlyList<string> organizations = await catalog.GetOrganizationsAsync("secret");

		Assert.Multiple(() =>
		{
			Assert.That(requests, Is.EqualTo(2));
			Assert.That(organizations, Has.Count.EqualTo(101));
			Assert.That(organizations[0], Is.EqualTo("acme"));
			Assert.That(organizations.Count(value =>
				string.Equals(value, "org1", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1));
			Assert.That(handler.LastRequest?.Headers.Authorization?.Parameter, Is.EqualTo("secret"));
		});
	}

	[Test]
	public async Task Repositories_paginate_deduplicate_case_insensitively_and_keep_full_names()
	{
		int requests = 0;
		string? firstQuery = null;
		Handler handler = new(request =>
		{
			requests++;
			if (requests == 1)
			{
				firstQuery = request.RequestUri?.Query;
				string items = string.Join(',', Enumerable.Range(0, 100)
					.Select(index => $$"""{"full_name":"acme/repo{{index}}","description":"Repo {{index}}"}"""));
				HttpResponseMessage response = Json($"[{items}]");
				response.Headers.TryAddWithoutValidation(
					"Link", "<https://api.github.com/user/repos?per_page=100&page=2>; rel=\"next\"");
				return response;
			}
			return Json("""[{"full_name":"ACME/REPO1"},{"full_name":"octocat/notes","description":null}]""");
		});
		using HttpClient http = new(handler);
		GitHubRepositoryCatalog catalog = new(http);

		IReadOnlyList<GitHubRepositoryOption> repositories = await catalog.GetRepositoriesAsync("secret");

		Assert.Multiple(() =>
		{
			Assert.That(requests, Is.EqualTo(2));
			Assert.That(repositories, Has.Count.EqualTo(101));
			Assert.That(repositories.Count(repository =>
				string.Equals(repository.FullName, "acme/repo1", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1));
			Assert.That(repositories, Has.Some.Property("FullName").EqualTo("octocat/notes"));
			Assert.That(firstQuery,
				Does.Contain("affiliation=owner,collaborator,organization_member"));
		});
	}

	[Test]
	public async Task Tree_decodes_directories_and_files()
	{
		Handler handler = new(_ => Json(
			"""{"tree":[{"path":"docs","type":"tree"},{"path":"docs/a.md","type":"blob","size":12}]}"""));
		using HttpClient http = new(handler);
		GitHubRepositoryCatalog catalog = new(http);

		IReadOnlyList<GitHubRepositoryEntry> entries =
			await catalog.GetTreeAsync("octo", "specs", "master", "secret");

		Assert.That(entries, Is.EqualTo(new[]
		{
			new GitHubRepositoryEntry("docs", true, 0),
			new GitHubRepositoryEntry("docs/a.md", false, 12),
		}));
	}

	[Test]
	public async Task TreeLevel_TraversesTreeShasWithoutRecursiveTraversal()
	{
		List<string> requests = [];
		Handler handler = new(request =>
		{
			requests.Add(request.RequestUri!.AbsoluteUri);
			return requests.Count == 1
				? Json("""{"tree":[{"path":"docs","type":"tree","sha":"docs-sha"}]}""")
				: Json("""{"tree":[{"path":"images","type":"tree","sha":"images-sha"},{"path":"guide.md","type":"blob","sha":"guide-sha","size":12}]}""");
		});
		using HttpClient http = new(handler);
		GitHubRepositoryCatalog catalog = new(http);

		IReadOnlyList<GitHubRepositoryEntry> entries = await catalog.GetTreeLevelAsync(
			"octo", "specs", "feature/docs", "docs", "secret");

		Assert.Multiple(() =>
		{
			Assert.That(requests, Has.Count.EqualTo(2));
			Assert.That(requests[0],
				Is.EqualTo("https://api.github.com/repos/octo/specs/git/trees/feature%2Fdocs"));
			Assert.That(requests[1],
				Is.EqualTo("https://api.github.com/repos/octo/specs/git/trees/docs-sha"));
			Assert.That(entries, Has.Count.EqualTo(2));
			Assert.That(entries[0], Is.EqualTo(new GitHubRepositoryEntry("docs/images", true, 0)));
			Assert.That(entries[1], Is.EqualTo(new GitHubRepositoryEntry("docs/guide.md", false, 12)));
		});
	}

	[Test]
	public async Task TreeLevel_ReturnsMoreThanContentsApiThousandEntryLimit()
	{
		string items = string.Join(',', Enumerable.Range(0, 1_001)
			.Select(index => $$"""{"path":"spec-{{index:D4}}.md","type":"blob","sha":"sha-{{index}}","size":{{index}}}"""));
		Handler handler = new(_ => Json($$"""{"truncated":false,"tree":[{{items}}]}"""));
		using HttpClient http = new(handler);
		GitHubRepositoryCatalog catalog = new(http);

		IReadOnlyList<GitHubRepositoryEntry> entries = await catalog.GetTreeLevelAsync(
			"octo", "specs", "main", string.Empty, "secret");

		Assert.Multiple(() =>
		{
			Assert.That(entries, Has.Count.EqualTo(1_001));
			Assert.That(entries[^1],
				Is.EqualTo(new GitHubRepositoryEntry("spec-1000.md", false, 1_000)));
			Assert.That(handler.LastRequest?.RequestUri?.Query, Is.Empty,
				"the non-recursive Git Trees endpoint must not use the truncating Contents listing");
		});
	}

	[Test]
	public void TreeLevel_RejectsTruncatedGitTreeInsteadOfPublishingPartialFolder()
	{
		Handler handler = new(_ => Json(
			"""{"truncated":true,"tree":[{"path":"partial.md","type":"blob","sha":"sha","size":1}]}"""));
		using HttpClient http = new(handler);
		GitHubRepositoryCatalog catalog = new(http);

		Assert.That(async () => await catalog.GetTreeLevelAsync(
			"octo", "specs", "main", string.Empty, "secret"),
			Throws.TypeOf<InvalidDataException>());
	}

	[Test]
	public void TreeLevel_AccountCancellationPreventsTheNextShaTraversalRequest()
	{
		TaskCompletionSource firstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<HttpResponseMessage> releaseFirst =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		AsyncHandler handler = new(async (_, _) =>
		{
			firstRequest.SetResult();
			return await releaseFirst.Task;
		});
		using HttpClient http = new(handler);
		GitHubRepositoryCatalog catalog = new(http);
		using CancellationTokenSource account = new();

		Task<IReadOnlyList<GitHubRepositoryEntry>> operation = catalog.GetTreeLevelAsync(
			"octo", "specs", "main", "private", "secret", account.Token);
		Assert.That(firstRequest.Task.Wait(TimeSpan.FromSeconds(2)), Is.True);
		account.Cancel();
		releaseFirst.SetResult(Json(
			"""{"tree":[{"path":"private","type":"tree","sha":"private-sha"}]}"""));

		Assert.Multiple(() =>
		{
			Assert.That(async () => await operation, Throws.InstanceOf<OperationCanceledException>());
			Assert.That(handler.Requests, Is.EqualTo(1),
				"a retired account must not start the next private tree request");
		});
	}

	[Test]
	public async Task File_returns_raw_text()
	{
		Handler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("# Remote", Encoding.UTF8, "text/plain"),
		});
		using HttpClient http = new(handler);
		GitHubRepositoryCatalog catalog = new(http);

		Assert.That(await catalog.GetFileAsync("octo", "specs", "main", "docs/a.md", "secret"),
			Is.EqualTo("# Remote"));
	}

	private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(json, Encoding.UTF8, "application/json"),
	};
}
