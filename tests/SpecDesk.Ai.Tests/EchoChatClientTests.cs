using Microsoft.Extensions.AI;
using SpecDesk.Ai;

namespace SpecDesk.Ai.Tests;

[TestFixture]
public sealed class EchoChatClientTests
{
	private static ChatMessage User(string text) => new(ChatRole.User, text);

	[Test]
	public async Task GetStreamingResponseAsync_YieldsMultipleChunksThatEchoTheLastUserMessage()
	{
		using EchoChatClient client = new();
		List<ChatResponseUpdate> updates = [];
		await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync([User("Tighten this section")]))
		{
			updates.Add(update);
		}

		string full = string.Concat(updates.Select(u => u.Text));
		Assert.Multiple(() =>
		{
			// Streamed as several chunks (not one blob), so the webview exercises real streaming.
			Assert.That(updates, Has.Count.GreaterThan(3));
			Assert.That(full, Does.Contain("Tighten this section"));
			Assert.That(full, Does.Contain("offline"));
		});
	}

	[Test]
	public async Task GetResponseAsync_ReturnsAnAssistantMessageEchoingTheInput()
	{
		using EchoChatClient client = new();
		ChatResponse response = await client.GetResponseAsync([User("Summarize the change")]);

		Assert.Multiple(() =>
		{
			Assert.That(response.Messages, Is.Not.Empty);
			Assert.That(response.Messages[0].Role, Is.EqualTo(ChatRole.Assistant));
			Assert.That(response.Text, Does.Contain("Summarize the change"));
		});
	}

	[Test]
	public async Task GetStreamingResponseAsync_WithNoUserMessage_DegradesToAPlaceholder()
	{
		using EchoChatClient client = new();
		List<ChatResponseUpdate> updates = [];
		await foreach (ChatResponseUpdate update in
			client.GetStreamingResponseAsync([new ChatMessage(ChatRole.System, "system only")]))
		{
			updates.Add(update);
		}

		Assert.That(string.Concat(updates.Select(u => u.Text)), Does.Contain("your message"));
	}
}
