namespace SpecDesk.Ai;

/// <summary>
/// The host-facing seam over the AI assistant: one turn in, a stream of text chunks out. The host
/// (<c>HostController.Chat</c>) depends only on this, so it can be exercised with a fake agent (no
/// network, no framework) and the concrete <see cref="AgentFrameworkChatAgent"/> — built on the Microsoft
/// Agent Framework's <c>ChatClientAgent</c> — stays the only place that touches the framework API.
/// </summary>
/// <remarks>
/// Per docs/design/08-ai-agent.md the assistant only ever <em>proposes</em> mutating actions; every
/// mutating action passes the same confirm gate as a manual one. This scaffold streams plain text; a
/// later phase adds the tool surface (getCurrentDoc / suggestVersionNote / proposeEdit …) behind this
/// same seam, with <c>proposeEdit</c> and friends staging a <c>confirm.request</c> rather than acting.
/// </remarks>
public interface IChatAgent
{
	/// <summary>
	/// Run one assistant turn for <paramref name="userMessage"/>, yielding the reply as ordered text
	/// chunks (each becomes a <c>chat.delta</c>). Conversation state (history) is the agent's own concern:
	/// a single implementation instance is one chat session, reused across turns. Enumeration honours
	/// <paramref name="cancellationToken"/> (e.g. window teardown).
	/// </summary>
	IAsyncEnumerable<string> StreamAsync(string userMessage, CancellationToken cancellationToken = default);
}
