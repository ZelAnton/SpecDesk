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
	private readonly HashSet<string> _pickedChatAttachments = new(StringComparer.OrdinalIgnoreCase);

	// Stream one assistant turn: run the agent on a background task (it can run for seconds), emitting each
	// text chunk as chat.delta and a terminal chat.done. Single-flighted; ignores an empty message.
	private void OnChatSend(IpcMessage message)
	{
		ChatSendPayload? payload = SafeGetPayload<ChatSendPayload>(message);
		if (payload is null || string.IsNullOrWhiteSpace(payload.Text))
		{
			return;
		}

		string text = BuildChatPrompt(payload.Text, payload.Attachments ?? []);

		CancellationTokenSource? cts = null;
		string id = string.IsNullOrWhiteSpace(payload.Id) || payload.Id.Length > 128
			? NextChatTurnId()
			: payload.Id;
		bool rejected = false;
		lock (_sync)
		{
			if (_chatCts is not null && !_chatCts.IsCancellationRequested)
			{
				// A turn is already streaming (the composer is meant to be disabled until it finishes); drop
				// this one rather than run a second, concurrent turn on the same agent session. Close this
				// request's own id below so a stale/duplicate UI submit cannot leave its composer waiting forever.
				_logger.LogDebug("Ignoring a chat message while a turn is already in flight");
				rejected = true;
			}
			else
			{
				// A sign-out cancels the previous CTS before its background task reaches finally. It is safe to
				// replace that canceled slot: _chatAgentGate still serializes transports, and the old finally uses
				// ReferenceEquals so it cannot clear this new turn.
				cts = new CancellationTokenSource();
				_chatCts = cts;
			}
		}
		if (rejected)
		{
			EmitChatDone(id);
			return;
		}

		CancellationTokenSource activeCts = cts!;
		CancellationToken token = activeCts.Token;
		_ = Task.Run(async () =>
		{
			try
			{
				await _chatAgentGate.WaitAsync(token);
				try
				{
					IChatAgent? agent = await ResolveChatAgentAsync(token);
					if (agent is null)
					{
						EmitChatDelta(id, _chatAgentFactory is not null
							? "Connect to GitHub to use Copilot."
							: "The assistant isn't available right now.");
						EmitChatDone(id);
						return;
					}

					await foreach (string chunk in agent.StreamAsync(text, token))
					{
						if (chunk.Length > 0)
						{
							EmitChatDelta(id, chunk);
						}
					}
				}
				finally
				{
					_chatAgentGate.Release();
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
				EmitChatDelta(id, "Sorry — the assistant ran into a problem. Please try again.");
				EmitChatDone(id);
			}
			finally
			{
				lock (_sync)
				{
					if (ReferenceEquals(_chatCts, activeCts))
					{
						_chatCts = null;
					}
				}

				activeCts.Dispose();
			}
		});
	}

	// Called with _chatAgentGate held so sign-out/account replacement cannot dispose the session while a
	// turn is starting or streaming. The host hands the token directly to the factory; the SDK retains it
	// only in process for this account session. SpecDesk never logs, persists, or sends it over IPC, and
	// disposing the session on sign-out/account replacement releases it.
	private async Task<IChatAgent?> ResolveChatAgentAsync(CancellationToken cancellationToken)
	{
		if (_chatAgentFactory is null)
		{
			return _chatAgent;
		}
		if (_auth is null || !_auth.IsSignedIn())
		{
			return null;
		}
		if (_chatAgent is not null)
		{
			return _chatAgent;
		}

		_chatAgent = await _auth.WithAccessTokenAsync(
			(accessToken, _) => Task.FromResult(_chatAgentFactory.Create(accessToken)),
			cancellationToken);
		return _chatAgent;
	}

	private async Task ResetChatAgentAsync()
	{
		if (_chatAgentFactory is null)
		{
			return;
		}

		await _chatAgentGate.WaitAsync();
		try
		{
			IChatAgent? agent = _chatAgent;
			_chatAgent = null;
			try
			{
				switch (agent)
				{
					case IAsyncDisposable asyncDisposable:
						await asyncDisposable.DisposeAsync();
						break;
					case IDisposable disposable:
						disposable.Dispose();
						break;
				}
			}
			catch (Exception ex)
			{
				// The account is already disconnected and the reference cleared; a provider cleanup fault must
				// not retain the old account session or prevent a later sign-in from creating a fresh one.
				_logger.LogWarning(ex, "Could not fully dispose the previous Copilot chat session");
			}
		}
		finally
		{
			_chatAgentGate.Release();
		}
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

	private void EmitChatDelta(string id, string text) =>
		Emit(IpcSerializer.SerializeEvent(MessageKinds.ChatDelta, new ChatDeltaPayload(id, text)));

	private void EmitChatDone(string id) =>
		Emit(IpcSerializer.SerializeEvent(MessageKinds.ChatDone, new ChatDonePayload(id)));

	private void EmitTemplates(TemplatesPayload payload, string? id) =>
		Emit(IpcSerializer.SerializeEvent(MessageKinds.Templates, payload, id: id));

	private void OnChatAttachmentPick(IpcMessage message)
	{
		ChatAttachmentPickPayload? payload = SafeGetPayload<ChatAttachmentPickPayload>(message);
		string? path = payload?.Kind switch
		{
			"file" => _dialogs.PickAttachmentFile(),
			"folder" => _dialogs.PickAttachmentFolder(),
			_ => null,
		};
		ChatAttachmentPayload? attachment = path is null || payload is null
			? null
			: new ChatAttachmentPayload(
				payload.Kind, Path.GetFileName(path.TrimEnd('/', '\\')), Path.GetFullPath(path));
		if (attachment is not null)
		{
			lock (_sync)
			{
				_pickedChatAttachments.Add(AttachmentSelectionKey(attachment.Kind, attachment.Reference));
			}
		}
		Emit(IpcSerializer.SerializeEvent(MessageKinds.ChatAttachmentPicked, attachment, id: message.Id));
	}

	private string BuildChatPrompt(string text, IReadOnlyList<ChatAttachmentPayload> attachments)
	{
		const int maxChars = 200_000;
		List<string> contexts = [];
		int remaining = maxChars;
		foreach (ChatAttachmentPayload attachment in attachments.Take(12))
		{
			if (attachment.Kind is "file" or "folder" && !ConsumePickedChatAttachment(attachment))
			{
				continue;
			}
			string? context = attachment.Kind switch
			{
				"file" => ReadAttachmentFile(attachment.Label, attachment.Reference, remaining),
				"folder" => ReadAttachmentFolder(attachment.Label, attachment.Reference, remaining),
				"repository" => ReadAttachmentRepository(attachment.Label, attachment.Reference, remaining),
				_ => null,
			};
			if (string.IsNullOrEmpty(context))
			{
				continue;
			}
			contexts.Add(context);
			remaining -= context.Length;
			if (remaining <= 0)
			{
				break;
			}
		}
		return contexts.Count == 0 ? text : $"{text}\n\n--- Attached context ---\n{string.Join("\n\n", contexts)}";
	}

	private bool ConsumePickedChatAttachment(ChatAttachmentPayload attachment)
	{
		string? key = TryAttachmentSelectionKey(attachment.Kind, attachment.Reference);
		if (key is null)
		{
			return false;
		}
		lock (_sync)
		{
			return _pickedChatAttachments.Remove(key);
		}
	}

	private static string AttachmentSelectionKey(string kind, string reference) =>
		$"{kind}\0{Path.GetFullPath(reference)}";

	private static string? TryAttachmentSelectionKey(string kind, string reference)
	{
		try
		{
			return AttachmentSelectionKey(kind, reference);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return null;
		}
	}

	private static string? ReadAttachmentFile(string label, string path, int remaining)
	{
		if (remaining <= 0 || !File.Exists(path))
		{
			return null;
		}
		try
		{
			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			using StreamReader reader = new(stream, detectEncodingFromByteOrderMarks: true);
			int limit = Math.Min(remaining, 200_000);
			char[] buffer = new char[limit + 1];
			int read = reader.ReadBlock(buffer, 0, buffer.Length);
			string content = new(buffer, 0, Math.Min(read, limit));
			if (read > limit)
			{
				content += "\n[Attachment truncated]";
			}
			return $"File {label}:\n{content}";
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static string? ReadAttachmentFolder(string label, string path, int remaining)
	{
		if (remaining <= 0 || !Directory.Exists(path))
		{
			return null;
		}
		try
		{
			const int maxDirectories = 200;
			const int maxEntriesPerDirectory = 1_000;
			const int maxExaminedEntries = 2_000;
			Queue<string> pending = new();
			pending.Enqueue(path);
			List<string> files = [];
			int visitedDirectories = 0;
			int examinedEntries = 0;
			while (pending.Count > 0 && visitedDirectories < maxDirectories
				&& examinedEntries < maxExaminedEntries && files.Count < 20)
			{
				string directory = pending.Dequeue();
				visitedDirectories++;
				foreach (string entry in Directory.EnumerateFileSystemEntries(directory).Take(maxEntriesPerDirectory))
				{
					examinedEntries++;
					if (examinedEntries > maxExaminedEntries)
					{
						break;
					}
					FileAttributes attributes = File.GetAttributes(entry);
					if ((attributes & FileAttributes.Directory) != 0)
					{
						string name = Path.GetFileName(entry);
						if ((attributes & FileAttributes.ReparsePoint) == 0 && !IsAttachmentNoiseDirectory(name))
						{
							pending.Enqueue(entry);
						}
					}
					else if (entry.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
						|| entry.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
					{
						files.Add(Path.GetRelativePath(path, entry).Replace('\\', '/'));
						if (files.Count == 20)
						{
							break;
						}
					}
				}
			}
			return files.Count == 0 ? $"Folder {label}: no Markdown files" : $"Folder {label}:\n{Truncate(string.Join("\n", files), remaining)}";
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static bool IsAttachmentNoiseDirectory(string name) =>
		name is ".git" or ".jj" or "node_modules" or "bin" or "obj" or "artifacts";

	private string? ReadAttachmentRepository(string label, string reference, int remaining)
	{
		RegisteredRepo? repo = _workspace?.State().Repositories.FirstOrDefault(item =>
			string.Equals(item.Url, reference, StringComparison.OrdinalIgnoreCase));
		return repo is null ? null : Truncate($"Repository {label}: {repo.Name}", remaining);
	}

	private static string Truncate(string value, int length) =>
		value.Length <= length ? value : value[..Math.Max(0, length)] + "\n[Attachment truncated]";

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
