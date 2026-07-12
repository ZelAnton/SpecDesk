using SpecDesk.Ai;

namespace SpecDesk.Ai.Tests;

/// <summary>
/// End-to-end wiring of the Microsoft Agent Framework's <c>ChatClientAgent</c> over the offline stub: the
/// agent runs, creates/reuses its session, and streams the stub's echoed reply back as text chunks. This
/// pins that the framework integration actually works (not just the stub in isolation).
/// </summary>
[TestFixture]
public sealed class AgentFrameworkChatAgentTests
{
	[Test]
	public async Task StreamAsync_OverTheOfflineStub_StreamsTheEchoedReply()
	{
		using AgentFrameworkChatAgent agent = AgentFrameworkChatAgent.CreateDefault(AiOptions.Offline);

		List<string> chunks = [];
		await foreach (string chunk in agent.StreamAsync("Summarize the refund policy"))
		{
			chunks.Add(chunk);
		}

		string full = string.Concat(chunks);
		Assert.Multiple(() =>
		{
			Assert.That(chunks, Is.Not.Empty, "the agent produced no output");
			Assert.That(full, Does.Contain("Summarize the refund policy"));
		});
	}

	[Test]
	public async Task StreamAsync_ReusesItsSessionAcrossTurns()
	{
		using AgentFrameworkChatAgent agent = AgentFrameworkChatAgent.CreateDefault(AiOptions.Offline);

		// Two turns on one agent instance (one chat session) must both stream without error.
		string first = await Drain(agent.StreamAsync("first question"));
		string second = await Drain(agent.StreamAsync("second question"));

		Assert.Multiple(() =>
		{
			Assert.That(first, Does.Contain("first question"));
			Assert.That(second, Does.Contain("second question"));
		});
	}

	private static async Task<string> Drain(IAsyncEnumerable<string> stream)
	{
		List<string> chunks = [];
		await foreach (string chunk in stream)
		{
			chunks.Add(chunk);
		}

		return string.Concat(chunks);
	}
}
