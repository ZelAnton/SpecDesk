using System.Globalization;
using Microsoft.Extensions.Logging;
using SpecDesk.Ai;
using SpecDesk.Contracts;

namespace SpecDesk.Host;

// The AI-assistant slice of HostController (docs/design/08-ai-agent.md): the streaming chat turn
// (chat.send → chat.delta* → chat.done) and the prompt-template library (templates.request → templates).
// The shared fields, locks, constructor, and the IPC router live in HostController.cs.
public sealed partial class HostController
{
	// Cancels the in-flight chat turn (window teardown). Guarded by _sync; a non-null value also single-
	// flights: only one turn streams at a time, which matches the composer being disabled while streaming
	// and keeps concurrent runs off the agent's (not thread-safe) session.
	private CancellationTokenSource? _chatCts;

	// Monotonic per-turn id, stamped into chat.done so the webview can ignore a late/duplicate completion.
	private long _chatTurnCounter;

	// Stream one assistant turn: run the agent on a background task (it can run for seconds), emitting each
	// text chunk as chat.delta and a terminal chat.done. Single-flighted; ignores an empty message.
	private void OnChatSend(IpcMessage message)
	{
		ChatSendPayload? payload = SafeGetPayload<ChatSendPayload>(message);
		if (payload is null || string.IsNullOrWhiteSpace(payload.Text))
		{
			return;
		}

		string text = payload.Text;

		if (_chatAgent is null)
		{
			// No agent configured: answer once so the composer doesn't hang waiting for a reply.
			string turnId = NextChatTurnId();
			EmitChatDelta("The assistant isn't available right now.");
			EmitChatDone(turnId);
			return;
		}

		CancellationTokenSource cts;
		string id;
		lock (_sync)
		{
			if (_chatCts is not null)
			{
				// A turn is already streaming (the composer is meant to be disabled until it finishes); drop
				// this one rather than run a second, concurrent turn on the same agent session.
				_logger.LogDebug("Ignoring a chat message while a turn is already in flight");
				return;
			}

			cts = new CancellationTokenSource();
			_chatCts = cts;
			id = NextChatTurnIdLocked();
		}

		CancellationToken token = cts.Token;
		_ = Task.Run(async () =>
		{
			try
			{
				await foreach (string chunk in _chatAgent.StreamAsync(text, token))
				{
					if (chunk.Length > 0)
					{
						EmitChatDelta(chunk);
					}
				}

				EmitChatDone(id);
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				// The window is tearing down (or the turn was cancelled) — stay quiet; the webview is gone.
			}
			catch (Exception ex)
			{
				// The provider/agent failed mid-turn. Surface a plain apology in the stream (never a stack
				// trace) and still close the turn so the composer re-enables.
				_logger.LogError(ex, "AI chat turn failed");
				EmitChatDelta("Sorry — the assistant ran into a problem. Please try again.");
				EmitChatDone(id);
			}
			finally
			{
				lock (_sync)
				{
					if (ReferenceEquals(_chatCts, cts))
					{
						_chatCts = null;
					}
				}

				cts.Dispose();
			}
		});
	}

	// Reply (correlated by id) with the prompt library: the author's personal templates plus the remote
	// set. Runs on a background task because the remote fetch is async; a null library or any failure
	// yields an empty set rather than an error, so the picker always gets an answer.
	private void OnRequestTemplates(IpcMessage message)
	{
		string? id = message.Id;
		if (_templates is null)
		{
			EmitTemplates(new TemplatesPayload([], []), id);
			return;
		}

		_ = Task.Run(async () =>
		{
			try
			{
				TemplatesPayload payload = await _templates.GetTemplatesAsync();
				EmitTemplates(payload, id);
			}
			catch (Exception ex)
			{
				// GetTemplatesAsync already degrades a remote failure to an empty list; this guards the
				// unexpected (a store read fault) so the reply still arrives and the webview's request settles.
				_logger.LogError(ex, "Could not gather the prompt-template library");
				EmitTemplates(new TemplatesPayload([], []), id);
			}
		});
	}

	private void EmitChatDelta(string text) =>
		Emit(IpcSerializer.SerializeEvent(MessageKinds.ChatDelta, new ChatDeltaPayload(text)));

	private void EmitChatDone(string id) =>
		Emit(IpcSerializer.SerializeEvent(MessageKinds.ChatDone, new ChatDonePayload(id)));

	private void EmitTemplates(TemplatesPayload payload, string? id) =>
		Emit(IpcSerializer.SerializeEvent(MessageKinds.Templates, payload, id: id));

	private string NextChatTurnId()
	{
		lock (_sync)
		{
			return NextChatTurnIdLocked();
		}
	}

	private string NextChatTurnIdLocked() =>
		(++_chatTurnCounter).ToString(CultureInfo.InvariantCulture);
}
