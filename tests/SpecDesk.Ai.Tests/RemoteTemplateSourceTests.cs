using System.Net;
using System.Net.Http;
using SpecDesk.Ai;
using SpecDesk.Contracts;

namespace SpecDesk.Ai.Tests;

[TestFixture]
public sealed class RemoteTemplateSourceTests
{
	/// <summary>A canned HTTP handler: returns a preset status + body, or throws, for one request.</summary>
	private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
	{
		public int Calls { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Calls++;
			return Task.FromResult(respond());
		}
	}

	private static HttpResponseMessage Json(string body) =>
		new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

	[Test]
	public async Task FetchAsync_WithNoUrlConfigured_ReturnsEmptyAndNeverCallsTheNetwork()
	{
		StubHandler handler = new(() => Json("[]"));
		using HttpClient http = new(handler);
		RemoteTemplateSource source = new(http, url: null);

		Assert.Multiple(() =>
		{
			Assert.That(source.IsConfigured, Is.False);
			Assert.That(source.FetchAsync().Result, Is.Empty);
			Assert.That(handler.Calls, Is.EqualTo(0));
		});

		await Task.CompletedTask;
	}

	[Test]
	public async Task FetchAsync_ParsesTheRemoteJsonArray()
	{
		StubHandler handler = new(() => Json(
			"""[{"id":"r1","title":"Remote one","body":"body one"},{"id":"r2","title":"Remote two","body":"body two"}]"""));
		using HttpClient http = new(handler);
		RemoteTemplateSource source = new(http, new Uri("https://example.test/templates.json"));

		IReadOnlyList<PromptTemplate> templates = await source.FetchAsync();

		Assert.Multiple(() =>
		{
			Assert.That(templates, Has.Count.EqualTo(2));
			Assert.That(templates[0].Id, Is.EqualTo("r1"));
			Assert.That(templates[1].Title, Is.EqualTo("Remote two"));
		});
	}

	[Test]
	public async Task FetchAsync_OnAnHttpError_ReturnsEmptyRatherThanThrowing()
	{
		StubHandler handler = new(() => new HttpResponseMessage(HttpStatusCode.InternalServerError));
		using HttpClient http = new(handler);
		RemoteTemplateSource source = new(http, new Uri("https://example.test/templates.json"));

		Assert.That(await source.FetchAsync(), Is.Empty);
	}

	[Test]
	public async Task FetchAsync_OnMalformedJson_ReturnsEmptyRatherThanThrowing()
	{
		StubHandler handler = new(() => Json("not json at all"));
		using HttpClient http = new(handler);
		RemoteTemplateSource source = new(http, new Uri("https://example.test/templates.json"));

		Assert.That(await source.FetchAsync(), Is.Empty);
	}
}
