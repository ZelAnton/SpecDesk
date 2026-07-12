using SpecDesk.Ai;

namespace SpecDesk.Ai.Tests;

[TestFixture]
public sealed class AiOptionsTests
{
	private static Func<string, string?> Env(Dictionary<string, string?> map) => key =>
		map.TryGetValue(key, out string? value) ? value : null;

	[Test]
	public void FromEnvironment_Unset_IsTheOfflineDefault()
	{
		AiOptions options = AiOptions.FromEnvironment(Env([]));

		Assert.Multiple(() =>
		{
			Assert.That(options.Provider, Is.EqualTo("offline"));
			Assert.That(options.Model, Is.Empty);
			Assert.That(options.RemoteTemplatesUrl, Is.Null);
		});
	}

	[Test]
	public void FromEnvironment_ReadsProviderModelAndTemplatesUrl()
	{
		AiOptions options = AiOptions.FromEnvironment(Env(new()
		{
			[AiOptions.ProviderEnvironmentVariable] = "claude",
			[AiOptions.ModelEnvironmentVariable] = "claude-opus-4-8",
			[AiOptions.RemoteTemplatesUrlEnvironmentVariable] = "https://example.test/templates.json",
		}));

		Assert.Multiple(() =>
		{
			Assert.That(options.Provider, Is.EqualTo("claude"));
			Assert.That(options.Model, Is.EqualTo("claude-opus-4-8"));
			Assert.That(options.RemoteTemplatesUrl, Is.EqualTo(new Uri("https://example.test/templates.json")));
		});
	}

	[Test]
	public void FromEnvironment_IgnoresAMalformedOrNonHttpTemplatesUrl()
	{
		AiOptions relative = AiOptions.FromEnvironment(Env(new()
		{
			[AiOptions.RemoteTemplatesUrlEnvironmentVariable] = "not a url",
		}));
		AiOptions ftp = AiOptions.FromEnvironment(Env(new()
		{
			[AiOptions.RemoteTemplatesUrlEnvironmentVariable] = "ftp://example.test/templates.json",
		}));

		Assert.Multiple(() =>
		{
			Assert.That(relative.RemoteTemplatesUrl, Is.Null);
			Assert.That(ftp.RemoteTemplatesUrl, Is.Null, "only http/https URLs are honoured");
		});
	}
}
