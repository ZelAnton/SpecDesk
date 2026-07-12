using System.Net;
using System.Net.Http;
using SpecDesk.Ai;
using SpecDesk.Contracts;

namespace SpecDesk.Ai.Tests;

[TestFixture]
public sealed class TemplateLibraryTests
{
	private string _dir = string.Empty;
	private string _path = string.Empty;

	[SetUp]
	public void SetUp()
	{
		_dir = Path.Combine(Path.GetTempPath(), "specdesk-lib-" + Guid.NewGuid().ToString("N"));
		_path = Path.Combine(_dir, "prompt-templates.json");
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(_dir))
		{
			Directory.Delete(_dir, recursive: true);
		}
	}

	private sealed class ConstantHandler(string body) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
			});
	}

	[Test]
	public async Task GetTemplatesAsync_WhenTheStoreIsEmpty_OffersTheBuiltInStarters()
	{
		using HttpClient http = new(new ConstantHandler("[]"));
		TemplateLibrary library = new(
			new PromptTemplateStore(_path),
			new RemoteTemplateSource(http, url: null));

		TemplatesPayload result = await library.GetTemplatesAsync();

		Assert.Multiple(() =>
		{
			Assert.That(result.Personal, Is.EqualTo(TemplateLibrary.DefaultPersonalTemplates));
			Assert.That(result.Remote, Is.Empty);
		});
	}

	[Test]
	public async Task GetTemplatesAsync_UsesTheStoredPersonalTemplatesWhenPresent()
	{
		PromptTemplateStore store = new(_path);
		store.Save([new PromptTemplate("mine", "My template", "my body")]);
		using HttpClient http = new(new ConstantHandler("[]"));
		TemplateLibrary library = new(store, new RemoteTemplateSource(http, url: null));

		TemplatesPayload result = await library.GetTemplatesAsync();

		Assert.Multiple(() =>
		{
			Assert.That(result.Personal, Has.Count.EqualTo(1));
			Assert.That(result.Personal[0].Id, Is.EqualTo("mine"));
		});
	}

	[Test]
	public async Task GetTemplatesAsync_CombinesPersonalAndRemoteTemplates()
	{
		using HttpClient http = new(new ConstantHandler(
			"""[{"id":"r1","title":"Remote one","body":"remote body"}]"""));
		TemplateLibrary library = new(
			new PromptTemplateStore(_path),
			new RemoteTemplateSource(http, new Uri("https://example.test/templates.json")));

		TemplatesPayload result = await library.GetTemplatesAsync();

		Assert.Multiple(() =>
		{
			Assert.That(result.Personal, Is.Not.Empty, "falls back to the built-in starters");
			Assert.That(result.Remote, Has.Count.EqualTo(1));
			Assert.That(result.Remote[0].Id, Is.EqualTo("r1"));
		});
	}
}
