using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace SpecDesk.Ai;

/// <summary>
/// The default, offline <see cref="IChatClient"/> the agent chat runs on: it needs no API key and no
/// network, so SpecDesk starts and the assistant works with zero credentials configured. It returns a
/// short canned acknowledgement that echoes the author's last message, streamed word-by-word so the UI
/// exercises the real <c>chat.delta</c>/<c>chat.done</c> streaming path.
/// </summary>
/// <remarks>
/// This is the stub side of the provider seam described in docs/design/08-ai-agent.md: a real provider
/// (an <c>Microsoft.Extensions.AI</c>-based OpenAI / Azure OpenAI / Claude client, selected by the TOML
/// <c>provider</c>/<c>model</c>) implements the very same <see cref="IChatClient"/> and drops in behind
/// <see cref="AgentFrameworkChatAgent"/> without the host or the webview changing. The stub is deliberately
/// inert — it never calls a tool and never proposes a mutation — so it cannot breach the confirm-gate rule.
/// </remarks>
public sealed class EchoChatClient : IChatClient
{
	/// <summary>The canned reply, as the sequence of chunks it is streamed in (kept as chunks, rather than
	/// split at call time, so the streaming shape is explicit and testable).</summary>
	private static readonly string[] Preamble =
	[
		"SpecDesk ", "assistant ", "(offline ", "preview). ",
		"No ", "AI ", "provider ", "is ", "configured, ",
		"so ", "here ", "is ", "an ", "echo ", "of ", "your ", "message: ",
	];

	/// <summary>The single, non-streaming response — the same text the streaming path produces.</summary>
	public Task<ChatResponse> GetResponseAsync(
		IEnumerable<ChatMessage> messages,
		ChatOptions? options = null,
		CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string reply = string.Concat(Preamble) + Quote(LastUserText(messages));
		return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
	}

	/// <summary>Streams the canned reply as one <see cref="ChatResponseUpdate"/> per chunk.</summary>
	public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
		IEnumerable<ChatMessage> messages,
		ChatOptions? options = null,
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		foreach (string chunk in Preamble)
		{
			cancellationToken.ThrowIfCancellationRequested();
			// Yield to keep the stream cooperative (and genuinely asynchronous) without a wall-clock delay,
			// so tests stay fast while the webview still receives the reply as separate chunks.
			await Task.Yield();
			yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
		}

		yield return new ChatResponseUpdate(ChatRole.Assistant, Quote(LastUserText(messages)));
	}

	/// <summary>No backing service to expose; part of the <see cref="IChatClient"/> contract.</summary>
	public object? GetService(Type serviceType, object? serviceKey = null) => null;

	public void Dispose()
	{
		// Nothing to release — the stub holds no unmanaged or network resources.
	}

	// The framework passes the whole (system + history + newest) message list; the echo only needs the
	// last user turn. Missing/blank input degrades to a friendly placeholder rather than an empty echo.
	private static string LastUserText(IEnumerable<ChatMessage> messages)
	{
		string? text = messages
			.LastOrDefault(m => m.Role == ChatRole.User)?
			.Text;
		return string.IsNullOrWhiteSpace(text) ? "(your message)" : text.Trim();
	}

	private static string Quote(string text) => "“" + text + "”";
}
