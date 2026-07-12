using System.Runtime.CompilerServices;
using SpecDesk.Ai;

namespace SpecDesk.Host.Tests;

/// <summary>
/// A fake <see cref="IChatAgent"/> that streams a preset sequence of chunks (optionally throwing partway
/// through), so the controller's chat streaming can be exercised without the Agent Framework or a network.
/// </summary>
internal sealed class FakeChatAgent : IChatAgent
{
	/// <summary>The chunks yielded for a turn, in order.</summary>
	public IReadOnlyList<string> Chunks { get; init; } = ["Hello ", "there"];

	/// <summary>When set, the stream throws after yielding its first chunk — to exercise the mid-turn
	/// failure path (an apology chunk + a chat.done).</summary>
	public bool ThrowAfterFirstChunk { get; init; }

	/// <summary>The last message the controller sent to the agent (asserted by the round-trip test).</summary>
	public string? LastMessage { get; private set; }

	public int Calls { get; private set; }

	public async IAsyncEnumerable<string> StreamAsync(
		string userMessage,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		Calls++;
		LastMessage = userMessage;

		int emitted = 0;
		foreach (string chunk in Chunks)
		{
			cancellationToken.ThrowIfCancellationRequested();
			await Task.Yield();
			yield return chunk;
			emitted++;
			if (ThrowAfterFirstChunk && emitted == 1)
			{
				throw new InvalidOperationException("simulated agent failure");
			}
		}
	}
}
