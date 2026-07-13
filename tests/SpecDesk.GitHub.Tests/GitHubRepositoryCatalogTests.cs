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
