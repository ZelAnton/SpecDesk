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
	// Assistant chat / attachment / prompt-template kinds (see the central RegisterMessageHandlers).
	private void RegisterChatHandlers()
	{
		_messageHandlers.Register(MessageKinds.ChatSend, OnChatSend);
		_messageHandlers.Register(MessageKinds.ChatAttachmentPick, OnChatAttachmentPick);
		_messageHandlers.Register(MessageKinds.TemplatesRequest, OnRequestTemplates);
		_messageHandlers.Register(MessageKinds.ConfirmResult, OnConfirmResult);
		// Bind the assistant's one gated mutating tool (proposeEdit) to this host's sink. The tool can only
		// stage a proposal; StageEditProposal renders it for confirmation and never mutates on its own.
		_proposeEditTool = new ProposeEditTool(new EditProposalSink(this));
	}

	// Cancels the in-flight chat turn (window teardown). Guarded by _sync; a non-null value also single-
	// flights: only one turn streams at a time, which matches the composer being disabled while streaming
	// and keeps concurrent runs off the agent's (not thread-safe) session.
	private CancellationTokenSource? _chatCts;

	// Monotonic per-turn id, stamped into chat.done so the webview can ignore a late/duplicate completion.
	private long _chatTurnCounter;
	private readonly HashSet<string> _pickedChatAttachments = new(StringComparer.OrdinalIgnoreCase);

	// The assistant's gated proposeEdit tool, bound to this host's IEditProposalSink in RegisterChatHandlers.
	// The tool can only STAGE a proposal (never apply); exposed so the wiring is exercisable end-to-end.
	private ProposeEditTool _proposeEditTool = null!;
	internal ProposeEditTool ProposeEditTool => _proposeEditTool;

	// The single in-flight proposeEdit proposal awaiting human confirmation (guarded by _sync). Single-
	// flighted: a new proposal replaces any earlier one, whose confirm.result then no longer matches by id
	// and is dropped. Captured with the document identity + generations it was proposed against, so a
	// concurrent edit while the author reviews it is detected before anything is applied.
	private PendingEditProposal? _pendingEditProposal;
	private long _editProposalCounter;

	private sealed record PendingEditProposal(
		string Id, DocumentMutationSnapshot Snapshot, string ProposedText, string? Summary);

	// Forwards the proposeEdit tool's staged proposal to the host. A separate type (not HostController
	// itself implementing IEditProposalSink) keeps the sink surface off the controller's public shape.
	private sealed class EditProposalSink(HostController owner) : IEditProposalSink
	{
		public EditProposalStatus Stage(EditProposal proposal) => owner.StageEditProposal(proposal);
	}

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
		AccountSession? accountSession = _chatAgentFactory is not null
			&& TryCaptureAccountSession(out AccountSession capturedSession)
				? capturedSession
				: null;

		CancellationTokenSource? cts = null;
		string id = string.IsNullOrWhiteSpace(payload.Id) || payload.Id.Length > 128
			? NextChatTurnId()
			: payload.Id;
		bool rejected = false;
		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}
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
				cts = accountSession is { } session
					? CancellationTokenSource.CreateLinkedTokenSource(session.CancellationToken)
					: new CancellationTokenSource();
				_chatCts = cts;
			}
		}
		if (rejected)
		{
			EmitChatDoneForSession(id, accountSession);
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
					IChatAgent? agent = await ResolveChatAgentAsync(accountSession, token);
					if (agent is null)
					{
						EmitChatDeltaForSession(id, _chatAgentFactory is not null
							? "Connect to GitHub to use Copilot."
							: "The assistant isn't available right now.", accountSession);
						EmitChatDoneForSession(id, accountSession);
						return;
					}

					if (!await StreamChatAgentAsync(agent, text, id, accountSession, token))
					{
						return;
					}
				}
				finally
				{
					_chatAgentGate.Release();
				}

				EmitChatDoneForSession(id, accountSession);
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				// The window is tearing down (or the turn was cancelled) — stay quiet; the webview is gone.
			}
			catch (Exception ex)
			{
				if (accountSession is { } session && !IsAccountSessionCurrent(session))
				{
					return;
				}
				// The provider/agent failed mid-turn. Surface a plain apology in the stream (never a stack
				// trace) and still close the turn so the composer re-enables.
				_logger.LogError(ex, "AI chat turn failed");
				EmitChatDeltaForSession(
					id, "Sorry — the assistant ran into a problem. Please try again.", accountSession);
				EmitChatDoneForSession(id, accountSession);
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
	// only in process for this account session. The auth layer persists the token with Windows DPAPI; this
	// chat path does not log it, create a separate persisted copy, or send it over IPC. Disposing the session
	// on sign-out/account replacement releases it.
	private async Task<bool> StreamChatAgentAsync(
		IChatAgent agent,
		string text,
		string id,
		AccountSession? accountSession,
		CancellationToken cancellationToken)
	{
		IAsyncEnumerable<string>? stream = null;
		if (accountSession is { } session)
		{
			if (!StartForAccountSession(session, () => stream = agent.StreamAsync(text, cancellationToken)))
			{
				return false;
			}
		}
		else
		{
			stream = agent.StreamAsync(text, cancellationToken);
		}

		await using IAsyncEnumerator<string> enumerator = stream!.GetAsyncEnumerator(cancellationToken);
		while (true)
		{
			ValueTask<bool> moveNext = default;
			if (accountSession is { } activeSession)
			{
				if (!StartForAccountSession(activeSession, () => moveNext = enumerator.MoveNextAsync()))
				{
					return false;
				}
			}
			else
			{
				moveNext = enumerator.MoveNextAsync();
			}
			if (!await moveNext)
			{
				return true;
			}
			if (enumerator.Current.Length > 0
				&& !EmitChatDeltaForSession(id, enumerator.Current, accountSession))
			{
				return false;
			}
		}
	}

	private async Task<IChatAgent?> ResolveChatAgentAsync(
		AccountSession? accountSession, CancellationToken cancellationToken)
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

		if (accountSession is not { } session)
		{
			return null;
		}
		Task<IChatAgent>? createOperation = null;
		if (!StartForAccountSession(session, () =>
			createOperation = _auth.WithAccessTokenAsync(
				(accessToken, _) => Task.FromResult(_chatAgentFactory.Create(accessToken)),
				cancellationToken)))
		{
			return null;
		}
		IChatAgent created = await createOperation!;
		if (!PublishForAccountSession(session, () => _chatAgent = created))
		{
			if (created is IAsyncDisposable asyncDisposable)
			{
				await asyncDisposable.DisposeAsync();
			}
			else if (created is IDisposable disposable)
			{
				disposable.Dispose();
			}
			return null;
		}
		return created;
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

	private bool EmitChatDeltaForSession(string id, string text, AccountSession? accountSession) =>
		accountSession is { } session
			? PublishForAccountSession(session, () => EmitChatDelta(id, text))
			: EmitUnboundChatDelta(id, text);

	private bool EmitChatDoneForSession(string id, AccountSession? accountSession) =>
		accountSession is { } session
			? PublishForAccountSession(session, () => EmitChatDone(id))
			: EmitUnboundChatDone(id);

	private bool EmitUnboundChatDelta(string id, string text)
	{
		EmitChatDelta(id, text);
		return true;
	}

	private bool EmitUnboundChatDone(string id)
	{
		EmitChatDone(id);
		return true;
	}

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
		int remaining = maxChars;

		// Implicit current-document context (getCurrentDoc + getDiff): the assistant sees the open document and
		// what has changed in it without the author attaching anything, so "tighten this section" or
		// "summarize what changed" have context. Bounded and framed as data, never instructions (see
		// DocumentContext/DocumentDiff.ToContextBlock). chat.attachment.pick stays the route for every other
		// attachment.
		string? currentDocument = BuildCurrentDocumentContext(Math.Min(remaining, 80_000));
		if (!string.IsNullOrEmpty(currentDocument))
		{
			remaining -= currentDocument.Length;
		}

		List<string> contexts = [];
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

		System.Text.StringBuilder prompt = new(text);
		if (!string.IsNullOrEmpty(currentDocument))
		{
			prompt.Append("\n\n").Append(currentDocument);
		}
		if (contexts.Count > 0)
		{
			prompt.Append("\n\n--- Attached context ---\n").Append(string.Join("\n\n", contexts));
		}
		return prompt.ToString();
	}

	// Capture the open local document as the getCurrentDoc context to include implicitly in a chat turn, or
	// null when there is no open local document (a remote-only preview is read-only and not included here).
	// Reads _currentPath/_repoRoot under _sync, matching the other document-state readers (see K-005).
	private string? BuildCurrentDocumentContext(int budget)
	{
		if (budget <= 0)
		{
			return null;
		}

		string? repoRoot;
		string? path;
		string text;
		string? branch;
		RemoteDocumentContext? remote;
		lock (_sync)
		{
			repoRoot = _repoRoot;
			path = _currentPath;
			text = _text;
			branch = _session.Branch;
			remote = _remoteDocument;
		}

		if (remote is not null || path is null || repoRoot is null)
		{
			return null;
		}

		string relativePath = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
		string repository = Path.GetFileName(Path.TrimEndingDirectorySeparator(repoRoot));
		DocumentContext document = new(Path.GetFileName(path), relativePath, text, repository, branch, null);
		string documentBlock = document.ToContextBlock(budget);

		string? diffBlock = TryBuildWorkingDiffContext(
			repoRoot, relativePath, text, Math.Min(budget - documentBlock.Length, 16_000));
		return diffBlock is null ? documentBlock : $"{documentBlock}\n\n{diffBlock}";
	}

	// The getDiff context for the implicit chat document, best-effort: read the committed base ONLY if the
	// repository gate is free right now (a bounded TryEnter, not a blocking lock), so a chat turn is never held
	// behind a long-running push/commit. When the gate is busy, or the read faults, the diff is simply omitted
	// — the assistant still has the document itself (getCurrentDoc). Runs on the message thread, so it must not
	// block; _repoGate is taken here without _sync held (BuildCurrentDocumentContext released _sync first), so
	// the repository→session lock order is preserved.
	private string? TryBuildWorkingDiffContext(string repoRoot, string relativePath, string text, int budget)
	{
		if (budget <= 0)
		{
			return null;
		}

		string? baseText = null;
		bool taken = false;
		try
		{
			Monitor.TryEnter(_repoGate, TimeSpan.FromMilliseconds(50), ref taken);
			if (!taken)
			{
				return null;
			}
			baseText = _versioning.ReadHeadContent(repoRoot, relativePath);
		}
		catch (Exception ex) when (
			ex is LibGit2Sharp.LibGit2SharpException
				or IOException
				or UnauthorizedAccessException
				or InvalidOperationException)
		{
			return null;
		}
		finally
		{
			if (taken)
			{
				Monitor.Exit(_repoGate);
			}
		}

		DocumentDiff diff = DocumentDiff.Between(baseText, text);
		return diff.HasChanges || diff.IsNewDocument ? diff.ToContextBlock(budget) : null;
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

	// The IEditProposalSink the assistant's proposeEdit tool stages onto (docs/design/08-ai-agent.md). It
	// captures the open editable document's identity + generations, remembers the single in-flight proposal,
	// and asks the author to confirm the difference — it NEVER touches the document. Only a later, still-
	// current confirm.result applies anything (OnConfirmResult). Returns Unavailable when there is no open,
	// editable local draft to propose against (nothing is staged, nothing changes).
	private EditProposalStatus StageEditProposal(EditProposal proposal)
	{
		string id;
		string currentText;
		lock (_sync)
		{
			DraftSession session = _session;
			if (_disposed
				|| _remoteDocument is not null
				|| _currentPath is null
				|| _repoRoot is null
				|| _closePreparationClaimed
				|| _documentMutationLeaseClaimed
				|| _documentOpenTransition
				|| _documentRepositoryTransition
				|| _documentDiscardTransition
				|| !IsEditingState(session.State))
			{
				return EditProposalStatus.Unavailable;
			}

			id = (++_editProposalCounter).ToString(CultureInfo.InvariantCulture);
			currentText = _text;
			DocumentMutationSnapshot snapshot = new(
				_currentPath,
				_repoRoot,
				_text,
				_lineEnding,
				Interlocked.Read(ref _draftGeneration),
				Interlocked.Read(ref _contentGeneration),
				session.Branch,
				session.BaseBranch);
			_pendingEditProposal = new PendingEditProposal(id, snapshot, proposal.ProposedText, proposal.Summary);
		}

		// Emitted outside _sync: the author confirms/edits/rejects the difference in the confirmation UI, and
		// only a matching, still-current confirm.result applies the edit through the ordinary editing path.
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.ConfirmRequest,
			new ConfirmRequestPayload(id, currentText, proposal.ProposedText, proposal.Summary)));
		_logger.LogInformation("Staged an assistant edit proposal for confirmation (id {Id})", id);
		return EditProposalStatus.Staged;
	}

	// The author's decision on a staged proposeEdit proposal. Rejection drops the proposal and leaves the
	// document untouched (no partial or intermediate state). Acceptance applies the confirmed (possibly
	// author-edited) text through the SAME path as a manual edit — _text/_contentGeneration/dirty/autosave —
	// but ONLY after re-checking the document is still exactly the one, at the same checkout and content
	// generation, the proposal was staged against, so a concurrent edit during the review cannot corrupt state.
	private void OnConfirmResult(IpcMessage message)
	{
		ConfirmResultPayload? payload = SafeGetPayload<ConfirmResultPayload>(message);
		if (payload is null || string.IsNullOrEmpty(payload.Id))
		{
			return;
		}

		string id = payload.Id;
		bool applied = false;
		bool announce = false;
		bool stale = false;
		string? appliedText = null;
		lock (_sync)
		{
			if (_pendingEditProposal is not { } pending
				|| !string.Equals(pending.Id, id, StringComparison.Ordinal))
			{
				// No matching proposal: a superseded, duplicate, or stale reply. Ignore it.
				return;
			}

			if (!string.Equals(payload.Decision, ConfirmDecisions.Accepted, StringComparison.Ordinal))
			{
				// Rejected (or any non-accept decision): discard, leaving no trace in the document.
				_pendingEditProposal = null;
				return;
			}

			string finalText = payload.Text ?? pending.ProposedText;
			if (!IsEditProposalCurrentLocked(pending.Snapshot))
			{
				// The document changed (a concurrent edit, a document switch, a discard) while the author
				// reviewed the proposal — refuse to apply it against a document it was not proposed for.
				_pendingEditProposal = null;
				stale = true;
			}
			else
			{
				DraftSession session = _session;
				_text = finalText;
				Interlocked.Increment(ref _contentGeneration);
				announce = !session.Dirty;
				_session = session with
				{
					Generation = Interlocked.Read(ref _draftGeneration),
					Dirty = true,
				};
				_autosaveTimer?.Dispose();
				_autosaveTimer = new Timer(
					_ => RunDiskAutosave(), null, _autosaveIdle, Timeout.InfiniteTimeSpan);
				_pendingEditProposal = null;
				appliedText = finalText;
				applied = true;
			}
		}

		if (applied)
		{
			_logger.LogInformation("Applied a confirmed assistant edit ({Length} chars)", appliedText!.Length);
			Emit(IpcSerializer.SerializeEvent(
				MessageKinds.ConfirmApplied, new ConfirmAppliedPayload(id, appliedText!)));
			if (announce)
			{
				SendTransientStatus("Unsaved changes");
			}
		}
		else if (stale)
		{
			SendError(
				"The document changed while the suggested edit was open, so it was not applied. "
					+ "Ask the assistant again for a fresh suggestion.");
		}
	}

	// Called under _sync: is the open document still exactly the one, at the same checkout and content
	// generation, the proposal was staged against? Mirrors the TryClaimDocumentMutation currency discipline
	// (identity via PathIdentity, generations via the _draftGeneration/_contentGeneration companions) plus the
	// editing-state requirement, since a confirmed edit applies through the ordinary editing path.
	private bool IsEditProposalCurrentLocked(DocumentMutationSnapshot snapshot)
	{
		DraftSession session = _session;
		return !_disposed
			&& _remoteDocument is null
			&& !_closePreparationClaimed
			&& !_documentMutationLeaseClaimed
			&& !_documentOpenTransition
			&& !_documentRepositoryTransition
			&& !_documentDiscardTransition
			&& _currentPath is not null
			&& _repoRoot is not null
			&& IsEditingState(session.State)
			&& PathIdentity.SameSessionPath(_currentPath, snapshot.Path)
			&& PathIdentity.SameSessionPath(_repoRoot, snapshot.RepoRoot)
			&& Interlocked.Read(ref _draftGeneration) == snapshot.DraftGeneration
			&& Interlocked.Read(ref _contentGeneration) == snapshot.ContentGeneration
			&& string.Equals(session.Branch, snapshot.Branch, StringComparison.Ordinal)
			&& string.Equals(session.BaseBranch, snapshot.BaseBranch, StringComparison.Ordinal);
	}
}
