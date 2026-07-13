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
