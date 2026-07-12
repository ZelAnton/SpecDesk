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

	private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(json, Encoding.UTF8, "application/json"),
	};
}
