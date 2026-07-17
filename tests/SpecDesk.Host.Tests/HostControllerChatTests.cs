using GitHub.Copilot;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using SpecDesk.Ai;
using SpecDesk.Contracts;
using SpecDesk.GitHub;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

/// <summary>
/// The AI-assistant slice of the controller (HostController.Chat.cs): the streaming chat turn
/// (chat.send → chat.delta* → chat.done) and the prompt-library reply (templates.request → templates),
/// driven with fakes so no real agent, file, or network is involved.
/// </summary>
[TestFixture]
public sealed class HostControllerChatTests
{
	private sealed class NoDialogs : IFileDialogs
	{
		public string? PickOpenFile() => null;
		public string? PickOpenFolder() => null;

		public string? PickSaveFile(string? suggestedPath) => null;
	}

	private sealed class AttachmentDialogs(string file, string folder) : IFileDialogs
	{
		public string? PickOpenFile() => file;
		public string? PickOpenFolder() => folder;
		public string? PickSaveFile(string? suggestedPath) => null;
	}

	private static Renderer.RenderResult StubRender(string docDir, string text) => new(string.Empty, []);

	private readonly List<string> _sent = [];
	private readonly object _gate = new();
	private string? _docDir;

	[SetUp]
	public void SetUp()
	{
		_docDir = null;
		lock (_gate)
		{
			_sent.Clear();
		}
	}

	[TearDown]
	public void TearDown()
	{
		if (_docDir is not null && Directory.Exists(_docDir))
		{
			Directory.Delete(_docDir, recursive: true);
		}
	}

	// Create a temp repo folder with one Markdown document and return its path, tracked for teardown.
	private string CreateDocument(string content)
	{
		_docDir = Path.Combine(Path.GetTempPath(), "specdesk-chat-doc-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_docDir);
		string path = Path.Combine(_docDir, "spec.md");
		File.WriteAllText(path, content);
		return path;
	}

	// A controller with an open (auto-loaded) local document, an optional AI suggestion agent, and the given
	// fake versioning — used to exercise the version-note suggestion and the implicit chat document context.
	private HostController NewControllerWithDocument(
		string docPath,
		FakeVersioning versioning,
		IChatAgent? agent = null,
		SpecDesk.Ai.ISuggestionAgent? suggestionAgent = null)
	{
		void Send(string json)
		{
			lock (_gate)
			{
				_sent.Add(json);
			}
		}

		HostController controller = new(
			StubRender,
			Send,
			new NoDialogs(),
			(_, _, _, _, _) => null,
			versioning,
			NullLogger<HostController>.Instance,
			docPath,
			chatAgent: agent,
			suggestionAgent: suggestionAgent);
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
		return controller;
	}

	private HostController NewController(
		IChatAgent? agent = null,
		ITemplateLibrary? templates = null,
		IFileDialogs? dialogs = null,
		SpecDesk.GitHub.IGitHubAuth? auth = null,
		IChatAgentFactory? agentFactory = null)
	{
		void Send(string json)
		{
			lock (_gate)
			{
				_sent.Add(json);
			}
		}

		return new HostController(
			StubRender,
			Send,
			dialogs ?? new NoDialogs(),
			(_, _, _, _, _) => null,
			new FakeVersioning(),
			NullLogger<HostController>.Instance,
			auth: auth,
			chatAgent: agent,
			chatAgentFactory: agentFactory,
			templates: templates);
	}

	[Test]
	public void CopilotChat_WhenSignedOut_RequestsGitHubConnectionWithoutCreatingAnAgent()
	{
		TrackingAgentFactory factory = new();
		using HostController controller = NewController(
			auth: new FakeGitHubAuth(signedIn: false), agentFactory: factory);

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.ChatSend, new ChatSendPayload("Hello", [])));

		Assert.That(WaitForKind(MessageKinds.ChatDone), Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(factory.CreateCount, Is.Zero);
			Assert.That(FindKind(MessageKinds.ChatDelta)?.GetPayload<ChatDeltaPayload>()?.Text,
				Is.EqualTo("Connect to GitHub to use Copilot."));
		});
	}

	[Test]
	public void ChatSend_EchoesTheClientTurnIdOnEveryStreamingFrame()
	{
		FakeChatAgent agent = new();
		using HostController controller = NewController(agent);

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.ChatSend, new ChatSendPayload("Hello", [], "web-42")));

		IpcMessage? done = WaitForKind(MessageKinds.ChatDone);
		ChatDeltaPayload[] deltas = SnapshotSent()
			.Select(IpcSerializer.TryDeserialize)
			.Where(message => message?.Kind == MessageKinds.ChatDelta)
			.Select(message => message!.GetPayload<ChatDeltaPayload>()!)
			.ToArray();
		Assert.Multiple(() =>
		{
			Assert.That(deltas, Is.Not.Empty);
			Assert.That(deltas.All(delta => delta.Id == "web-42"), Is.True);
			Assert.That(done?.GetPayload<ChatDonePayload>()?.Id, Is.EqualTo("web-42"));
		});
	}

	[Test]
	public async Task CopilotChat_SignOutDisposesTheSession_AndReauthenticationCreatesANewOne()
	{
		const string token = "gho_secret_never_on_wire";
		FakeGitHubAuth auth = new(signedIn: true, token);
		TrackingAgentFactory factory = new();
		using HostController controller = NewController(auth: auth, agentFactory: factory);

		SendChat(controller, "first");
		Assert.That(WaitForKind(MessageKinds.ChatDone), Is.Not.Null);
		Assert.That(factory.CreateCount, Is.EqualTo(1));
		Assert.That(SnapshotSent().Any(json => json.Contains(token, StringComparison.Ordinal)), Is.False);

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignOut));
		await factory.Agents[0].Disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
		auth.SignedIn = true;
		SendChat(controller, "second");
		Assert.That(WaitForChatDoneCount(2), Is.True);
		Assert.Multiple(() =>
		{
			Assert.That(factory.CreateCount, Is.EqualTo(2));
			Assert.That(SnapshotSent().Any(json => json.Contains(token, StringComparison.Ordinal)), Is.False);
		});
	}

	[Test]
	public async Task CopilotChat_NeverCompletingAbort_DoesNotBlockSignOutReauthenticationOrDispose()
	{
		NeverAbortTransport firstTransport = new();
		IdleTransport secondTransport = new();
		QueueAgentFactory factory = new(
			new CopilotChatAgent(
				() => firstTransport,
				abortTimeout: TimeSpan.FromMilliseconds(30),
				disposeTimeout: TimeSpan.FromMilliseconds(30)),
			new CopilotChatAgent(
				() => secondTransport,
				abortTimeout: TimeSpan.FromMilliseconds(30),
				disposeTimeout: TimeSpan.FromMilliseconds(30)));
		ReauthGitHubAuth auth = new();
		HostController controller = NewController(auth: auth, agentFactory: factory);

		SendChat(controller, "first");
		await firstTransport.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignOut));
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignIn));
		Assert.That(WaitForSignedInAccount(), Is.True, "re-authentication stayed blocked behind abort");

		SendChat(controller, "second");
		Assert.That(WaitForKind(MessageKinds.ChatDone), Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(factory.CreateCount, Is.EqualTo(2));
			Assert.That(secondTransport.SendCount, Is.EqualTo(1));
		});
		await Task.Run(controller.Dispose).WaitAsync(TimeSpan.FromSeconds(1));
	}

	private static void SendChat(HostController controller, string text) =>
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.ChatSend, new ChatSendPayload(text, [])));

	private bool WaitForChatDoneCount(int expected)
	{
		for (int i = 0; i < 200; i++)
		{
			lock (_gate)
			{
				if (_sent.Count(json => IpcSerializer.TryDeserialize(json)?.Kind == MessageKinds.ChatDone) >= expected)
				{
					return true;
				}
			}
			Thread.Sleep(10);
		}
		return false;
	}

	private bool WaitForSignedInAccount()
	{
		for (int i = 0; i < 200; i++)
		{
			lock (_gate)
			{
				if (_sent
					.Select(IpcSerializer.TryDeserialize)
					.Where(message => message?.Kind == MessageKinds.GitHubAccount)
					.Any(message => message?.GetPayload<GitHubAccountPayload>()?.SignedIn == true))
				{
					return true;
				}
			}
			Thread.Sleep(10);
		}
		return false;
	}

	private string[] SnapshotSent()
	{
		lock (_gate)
		{
			return [.. _sent];
		}
	}

	private sealed class TrackingAgentFactory : IChatAgentFactory
	{
		public List<TrackingAgent> Agents { get; } = [];
		public int CreateCount => Agents.Count;

		public IChatAgent Create(string githubAccessToken)
		{
			TrackingAgent agent = new();
			Agents.Add(agent);
			return agent;
		}
	}

	private sealed class TrackingAgent : IChatAgent, IAsyncDisposable
	{
		public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async IAsyncEnumerable<string> StreamAsync(
			string userMessage,
			[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			await Task.Yield();
			cancellationToken.ThrowIfCancellationRequested();
			yield return $"Copilot: {userMessage}";
		}

		public ValueTask DisposeAsync()
		{
			Disposed.TrySetResult();
			return ValueTask.CompletedTask;
		}
	}

	private sealed class QueueAgentFactory(params IChatAgent[] agents) : IChatAgentFactory
	{
		private readonly Queue<IChatAgent> _agents = new(agents);
		public int CreateCount { get; private set; }

		public IChatAgent Create(string githubAccessToken)
		{
			CreateCount++;
			return _agents.Dequeue();
		}
	}

	private sealed class ReauthGitHubAuth : IGitHubAuth
	{
		private bool _signedIn = true;

		public Task<DeviceCodePrompt> StartSignInAsync(CancellationToken cancellationToken = default) =>
			Task.FromResult(new DeviceCodePrompt(
				"CODE", new Uri("https://github.com/login/device"), TimeSpan.FromMinutes(1),
				TimeSpan.FromSeconds(1), "device"));

		public Task<SignInResult> AwaitAuthorizationAsync(
			DeviceCodePrompt prompt, CancellationToken cancellationToken = default)
		{
			_signedIn = true;
			return Task.FromResult(SignInResult.Authorized("octocat"));
		}

		public bool IsSignedIn() => _signedIn;

		public string? SignedInLogin() => _signedIn ? "octocat" : null;

		public Task<T> WithAccessTokenAsync<T>(
			Func<string, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default) =>
			use("gho_test", cancellationToken);

		public void SignOut() => _signedIn = false;
	}

	private sealed class NeverAbortTransport : ICopilotSessionTransport
	{
		public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public IDisposable On(Action<SessionEvent> handler) => new ActionDisposable(static () => { });

		public async Task SendAsync(string message, CancellationToken cancellationToken)
		{
			SendStarted.SetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
		}

		public Task AbortAsync(CancellationToken cancellationToken) => new TaskCompletionSource().Task;

		public ValueTask DisposeAsync() => new(new TaskCompletionSource().Task);
	}

	private sealed class IdleTransport : ICopilotSessionTransport
	{
		private Action<SessionEvent>? _handler;
		public int SendCount { get; private set; }

		public IDisposable On(Action<SessionEvent> handler)
		{
			_handler = handler;
			return new ActionDisposable(() => _handler = null);
		}

		public Task SendAsync(string message, CancellationToken cancellationToken)
		{
			SendCount++;
			_handler?.Invoke(new SessionIdleEvent { Data = new SessionIdleData() });
			return Task.CompletedTask;
		}

		public Task AbortAsync(CancellationToken cancellationToken) => Task.CompletedTask;

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class ActionDisposable(Action action) : IDisposable
	{
		public void Dispose() => action();
	}

	[Test]
	public void AttachmentPick_UsesNativeDialogsWithoutOpeningTheSelection()
	{
		using HostController controller = NewController(
			dialogs: new AttachmentDialogs(@"C:\specs\billing.md", @"C:\specs"));

		controller.OnMessage(IpcSerializer.Serialize(new IpcMessage(
			MessageKinds.ChatAttachmentPick,
			Id: "pick-1",
			Payload: JsonSerializer.SerializeToElement(
				new ChatAttachmentPickPayload("file"), IpcSerializer.Options))));

		IpcMessage? reply = FindKind(MessageKinds.ChatAttachmentPicked);
		Assert.Multiple(() =>
		{
			Assert.That(reply?.Id, Is.EqualTo("pick-1"));
			Assert.That(reply?.GetPayload<ChatAttachmentPayload>(), Is.EqualTo(
				new ChatAttachmentPayload("file", "billing.md", @"C:\specs\billing.md")));
			Assert.That(FindKind(MessageKinds.DocLoaded), Is.Null);
		});
	}

	[Test]
	public void ChatSend_WithFileAttachment_PassesBoundedContentWithoutTheAbsolutePath()
	{
		string file = Path.GetTempFileName();
		try
		{
			File.WriteAllText(file, "refund window is 30 days");
			FakeChatAgent agent = new();
			using HostController controller = NewController(
				agent, dialogs: new AttachmentDialogs(file, Path.GetDirectoryName(file)!));
			controller.OnMessage(IpcSerializer.Serialize(new IpcMessage(
				MessageKinds.ChatAttachmentPick,
				Id: "pick-file",
				Payload: JsonSerializer.SerializeToElement(new ChatAttachmentPickPayload("file"), IpcSerializer.Options))));
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.ChatSend,
				new ChatSendPayload("Summarize", [new ChatAttachmentPayload("file", "policy.md", file)])));

			Assert.That(WaitForKind(MessageKinds.ChatDone), Is.Not.Null);
			Assert.Multiple(() =>
			{
				Assert.That(agent.LastMessage, Does.Contain("File policy.md"));
				Assert.That(agent.LastMessage, Does.Contain("refund window is 30 days"));
				Assert.That(agent.LastMessage, Does.Not.Contain(file));
			});
		}
		finally
		{
			File.Delete(file);
		}
	}

	[Test]
	public void ChatSend_RejectsAFilePathThatWasNotReturnedByTheNativePicker()
	{
		string file = Path.GetTempFileName();
		try
		{
			File.WriteAllText(file, "private local text");
			FakeChatAgent agent = new();
			using HostController controller = NewController(agent);
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.ChatSend,
				new ChatSendPayload("Hello", [new ChatAttachmentPayload("file", "forged.md", file)])));

			Assert.That(WaitForKind(MessageKinds.ChatDone), Is.Not.Null);
			Assert.That(agent.LastMessage, Is.EqualTo("Hello"));
		}
		finally
		{
			File.Delete(file);
		}
	}

	[Test]
	public void ChatSend_WithMalformedAttachmentPath_IgnoresItAndStillCompletes()
	{
		FakeChatAgent agent = new();
		using HostController controller = NewController(agent);
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.ChatSend,
			new ChatSendPayload("Hello", [new ChatAttachmentPayload("file", "bad", "bad\0path")])));

		Assert.That(WaitForKind(MessageKinds.ChatDone), Is.Not.Null);
		Assert.That(agent.LastMessage, Is.EqualTo("Hello"));
	}

	[Test]
	public void ChatSend_WithOversizedFileAttachment_ReadsOnlyABoundedPrefix()
	{
		string file = Path.GetTempFileName();
		try
		{
			File.WriteAllText(file, new string('a', 210_000) + "SECRET_TAIL");
			FakeChatAgent agent = new();
			using HostController controller = NewController(
				agent, dialogs: new AttachmentDialogs(file, Path.GetDirectoryName(file)!));
			PickAttachment(controller, "file");
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.ChatSend,
				new ChatSendPayload("Inspect", [new ChatAttachmentPayload("file", "large.md", file)])));

			Assert.That(WaitForKind(MessageKinds.ChatDone), Is.Not.Null);
			Assert.Multiple(() =>
			{
				Assert.That(agent.LastMessage, Does.Contain("[Attachment truncated]"));
				Assert.That(agent.LastMessage, Does.Not.Contain("SECRET_TAIL"));
				Assert.That(agent.LastMessage?.Length, Is.LessThan(201_000));
			});
		}
		finally
		{
			File.Delete(file);
		}
	}

	[Test]
	public void ChatSend_WithLargeFolderAttachment_CapsTraversalAndSkipsNoiseDirectories()
	{
		string root = Path.Combine(Path.GetTempPath(), $"specdesk-chat-{Guid.NewGuid():N}");
		Directory.CreateDirectory(root);
		try
		{
			for (int i = 0; i < 240; i++)
			{
				string directory = Directory.CreateDirectory(Path.Combine(root, $"d{i:D3}")).FullName;
				File.WriteAllText(Path.Combine(directory, $"doc{i:D3}.md"), "content");
			}
			string noise = Directory.CreateDirectory(Path.Combine(root, "node_modules")).FullName;
			File.WriteAllText(Path.Combine(noise, "secret.md"), "must not be traversed");

			FakeChatAgent agent = new();
			using HostController controller = NewController(agent, dialogs: new AttachmentDialogs("", root));
			PickAttachment(controller, "folder");
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.ChatSend,
				new ChatSendPayload("List", [new ChatAttachmentPayload("folder", "large", root)])));

			Assert.That(WaitForKind(MessageKinds.ChatDone), Is.Not.Null);
			Assert.Multiple(() =>
			{
				Assert.That(agent.LastMessage, Does.Contain("Folder large"));
				Assert.That(agent.LastMessage, Does.Not.Contain("secret.md"));
				Assert.That(agent.LastMessage?.Split(".md").Length - 1, Is.LessThanOrEqualTo(20));
			});
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private static void PickAttachment(HostController controller, string kind) =>
		controller.OnMessage(IpcSerializer.Serialize(new IpcMessage(
			MessageKinds.ChatAttachmentPick,
			Id: $"pick-{kind}",
			Payload: JsonSerializer.SerializeToElement(new ChatAttachmentPickPayload(kind), IpcSerializer.Options))));

	[Test]
	public void ChatSend_WithThousandsOfIrrelevantFiles_StopsAtTheGlobalEntryCap()
	{
		string root = Path.Combine(Path.GetTempPath(), $"specdesk-chat-flat-{Guid.NewGuid():N}");
		Directory.CreateDirectory(root);
		try
		{
			for (int i = 0; i < 2_100; i++)
			{
				File.WriteAllText(Path.Combine(root, $"noise{i:D4}.txt"), string.Empty);
			}
			File.WriteAllText(Path.Combine(root, "tail.md"), "outside the bounded scan");

			FakeChatAgent agent = new();
			using HostController controller = NewController(agent, dialogs: new AttachmentDialogs("", root));
			PickAttachment(controller, "folder");
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.ChatSend,
				new ChatSendPayload("List", [new ChatAttachmentPayload("folder", "flat", root)])));

			Assert.That(WaitForKind(MessageKinds.ChatDone), Is.Not.Null);
			Assert.That(agent.LastMessage, Does.Contain("Folder flat"));
			Assert.That(agent.LastMessage, Does.Not.Contain("tail.md"));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Test]
	public void ChatSend_StreamsEveryChunkAsDeltaThenDone()
	{
		FakeChatAgent agent = new() { Chunks = ["Hello ", "there, ", "author"] };
		using HostController controller = NewController(agent);

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.ChatSend, new ChatSendPayload("Summarize this")));

		Assert.That(WaitForKind(MessageKinds.ChatDone), Is.Not.Null, "the turn never completed");
		Assert.Multiple(() =>
		{
			// Every chunk arrived, in order, before the done marker.
			Assert.That(DeltaText(), Is.EqualTo("Hello there, author"));
			Assert.That(agent.LastMessage, Is.EqualTo("Summarize this"));
			Assert.That(agent.Calls, Is.EqualTo(1));
			// chat.done carries a turn id.
			Assert.That(FindKind(MessageKinds.ChatDone)!.GetPayload<ChatDonePayload>()!.Id, Is.Not.Empty);
		});
	}

	[Test]
	public void ChatSend_WithBlankText_DoesNothing()
	{
		FakeChatAgent agent = new();
		using HostController controller = NewController(agent);

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.ChatSend, new ChatSendPayload("   ")));

		// Give a background turn a chance to (wrongly) start, then assert nothing streamed and the agent was
		// never called.
		Thread.Sleep(80);
		Assert.Multiple(() =>
		{
			Assert.That(agent.Calls, Is.EqualTo(0));
			Assert.That(FindKind(MessageKinds.ChatDelta), Is.Null);
			Assert.That(FindKind(MessageKinds.ChatDone), Is.Null);
		});
	}

	[Test]
	public void ChatSend_WhenTheAgentFailsMidTurn_EmitsAPlainApologyAndStillCompletes()
	{
		FakeChatAgent agent = new() { Chunks = ["Working"], ThrowAfterFirstChunk = true };
		using HostController controller = NewController(agent);

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.ChatSend, new ChatSendPayload("Do the thing")));

		Assert.That(WaitForKind(MessageKinds.ChatDone), Is.Not.Null, "a failed turn must still complete");
		Assert.Multiple(() =>
		{
			// The first chunk plus a plain-language apology (never a stack trace).
			Assert.That(DeltaText(), Does.Contain("Working"));
			Assert.That(DeltaText(), Does.Contain("problem"));
			Assert.That(DeltaText(), Does.Not.Contain("Exception"));
		});
	}

	[Test]
	public void ChatSend_WithNoAgentConfigured_RepliesThatTheAssistantIsUnavailable()
	{
		using HostController controller = NewController(agent: null);

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.ChatSend, new ChatSendPayload("Hello?")));

		Assert.That(WaitForKind(MessageKinds.ChatDone), Is.Not.Null);
		Assert.That(DeltaText(), Does.Contain("isn't available"));
	}

	[Test]
	public void TemplatesRequest_RepliesWithThePersonalAndRemoteSetsCorrelatedById()
	{
		FakeTemplateLibrary library = new();
		using HostController controller = NewController(templates: library);

		controller.OnMessage(IpcSerializer.Serialize(new IpcMessage(MessageKinds.TemplatesRequest, Id: "req-1")));

		IpcMessage? reply = WaitForKind(MessageKinds.Templates);
		Assert.That(reply, Is.Not.Null);
		TemplatesPayload? payload = reply!.GetPayload<TemplatesPayload>();
		Assert.Multiple(() =>
		{
			Assert.That(reply!.Id, Is.EqualTo("req-1"), "the reply must echo the request id");
			Assert.That(library.Calls, Is.EqualTo(1));
			Assert.That(payload!.Personal, Has.Count.EqualTo(1));
			Assert.That(payload.Personal[0].Title, Is.EqualTo("Personal one"));
			Assert.That(payload.Remote, Has.Count.EqualTo(1));
			Assert.That(payload.Remote[0].Id, Is.EqualTo("r1"));
		});
	}

	[Test]
	public void TemplatesRequest_WithNoLibrary_RepliesWithAnEmptySet()
	{
		using HostController controller = NewController(templates: null);

		controller.OnMessage(IpcSerializer.Serialize(new IpcMessage(MessageKinds.TemplatesRequest, Id: "req-2")));

		IpcMessage? reply = WaitForKind(MessageKinds.Templates);
		Assert.That(reply, Is.Not.Null);
		TemplatesPayload? payload = reply!.GetPayload<TemplatesPayload>();
		Assert.Multiple(() =>
		{
			Assert.That(payload!.Personal, Is.Empty);
			Assert.That(payload.Remote, Is.Empty);
		});
	}

	// —— T-079: implicit document context + AI version-note suggestion with template fallback ————————————

	[Test]
	public void ChatSend_WithAnOpenDocument_ImplicitlyIncludesTheCurrentDocumentAndDiffContext()
	{
		string doc = CreateDocument("# Spec\n\nRefund window is 30 days.\n");
		FakeVersioning versioning = new() { HeadContent = "# Spec\n\nRefund window is 14 days.\n" };
		FakeChatAgent agent = new();
		using HostController controller = NewControllerWithDocument(doc, versioning, agent: agent);

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.ChatSend, new ChatSendPayload("Summarize this", [])));

		Assert.That(WaitForKind(MessageKinds.ChatDone), Is.Not.Null);
		Assert.Multiple(() =>
		{
			// The author's message leads; the open document + its working change follow as getCurrentDoc /
			// getDiff context, framed as data — no explicit attachment needed.
			Assert.That(agent.LastMessage, Does.StartWith("Summarize this"));
			Assert.That(agent.LastMessage, Does.Contain("getCurrentDoc"));
			Assert.That(agent.LastMessage, Does.Contain("getDiff"));
			Assert.That(agent.LastMessage, Does.Contain("not instructions"));
			Assert.That(agent.LastMessage, Does.Contain("spec.md"));
			Assert.That(agent.LastMessage, Does.Contain("Refund window is 30 days."));
			Assert.That(agent.LastMessage, Does.Contain("+ Refund window is 30 days."));
		});
	}

	[Test]
	public void VersionNoteRequest_WithASuggestionAgent_RepliesWithTheAiDraftFromTheReadOnlyTools()
	{
		string doc = CreateDocument("# Spec\n\nRefund window is 30 days.\n");
		FakeVersioning versioning = new() { HeadContent = "# Spec\n\nRefund window is 14 days.\n" };
		FakeSuggestionAgent suggestions = new() { VersionNote = "Clarify the refund window" };
		using HostController controller =
			NewControllerWithDocument(doc, versioning, suggestionAgent: suggestions);

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.VersionNoteRequest, id: "v1"));

		IpcMessage? reply = WaitForKind(MessageKinds.VersionNoteSuggested);
		Assert.That(reply, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(reply!.Id, Is.EqualTo("v1"));
			Assert.That(
				reply.GetPayload<VersionNoteSuggestedPayload>()!.Note, Is.EqualTo("Clarify the refund window"));
			// The assistant received the open document via getCurrentDoc (no explicit attachment) + its diff.
			Assert.That(suggestions.LastDocument, Is.Not.Null);
			Assert.That(suggestions.LastDocument!.DocumentName, Is.EqualTo("spec.md"));
			Assert.That(suggestions.LastDiff, Is.Not.Null);
			Assert.That(suggestions.LastDiff!.HasChanges, Is.True);
		});
	}

	[Test]
	public void VersionNoteRequest_WhenTheProviderReturnsNull_FallsBackToTheDeterministicTemplate()
	{
		string doc = CreateDocument("# Spec\n");
		FakeSuggestionAgent suggestions = new() { VersionNote = null };
		using HostController controller =
			NewControllerWithDocument(doc, new FakeVersioning(), suggestionAgent: suggestions);

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.VersionNoteRequest, id: "v2"));

		IpcMessage? reply = WaitForKind(MessageKinds.VersionNoteSuggested);
		Assert.That(reply, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(suggestions.VersionNoteCalls, Is.EqualTo(1), "the provider was consulted");
			// An unavailable draft (null) falls back to the non-empty deterministic template.
			Assert.That(reply!.GetPayload<VersionNoteSuggestedPayload>()!.Note, Is.Not.Empty);
		});
	}

	[Test]
	public void VersionNoteRequest_WhenTheProviderThrows_FallsBackToTheDeterministicTemplate()
	{
		string doc = CreateDocument("# Spec\n");
		FakeSuggestionAgent suggestions = new() { Throw = true };
		using HostController controller =
			NewControllerWithDocument(doc, new FakeVersioning(), suggestionAgent: suggestions);

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.VersionNoteRequest, id: "v3"));

		IpcMessage? reply = WaitForKind(MessageKinds.VersionNoteSuggested);
		Assert.That(reply, Is.Not.Null);
		Assert.That(reply!.GetPayload<VersionNoteSuggestedPayload>()!.Note, Is.Not.Empty);
	}

	[Test]
	public void VersionNoteRequest_WithNoSuggestionAgent_RepliesWithTheTemplateAndNeverGoesAsync()
	{
		string doc = CreateDocument("# Spec\n");
		using HostController controller = NewControllerWithDocument(doc, new FakeVersioning());

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.VersionNoteRequest, id: "v0"));

		IpcMessage? reply = WaitForKind(MessageKinds.VersionNoteSuggested);
		Assert.That(reply, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(reply!.Id, Is.EqualTo("v0"));
			Assert.That(reply.GetPayload<VersionNoteSuggestedPayload>()!.Note, Is.Not.Empty);
		});
	}

	[Test]
	public void VersionNoteRequest_ReadsTheDocumentWithoutMutatingItOrTheRepository()
	{
		string doc = CreateDocument("# Spec\n\nBody.\n");
		FakeVersioning versioning = new() { HeadContent = "# Spec\n" };
		FakeSuggestionAgent suggestions = new() { VersionNote = "A note" };
		using HostController controller =
			NewControllerWithDocument(doc, versioning, suggestionAgent: suggestions);

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.VersionNoteRequest, id: "v4"));

		IpcMessage? reply = WaitForKind(MessageKinds.VersionNoteSuggested);
		Assert.That(reply, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(
				reply!.GetPayload<VersionNoteSuggestedPayload>()!.Note,
				Is.EqualTo("A note"),
				"the read-only tools produced the draft");
			// getCurrentDoc / getDiff only READ: no version committed, no draft forked, nothing discarded, and
			// the document on disk is untouched.
			Assert.That(versioning.SaveVersionCalls, Is.Zero);
			Assert.That(versioning.BeginEditCalls, Is.Zero);
			Assert.That(versioning.DiscardCalled, Is.False);
			Assert.That(File.ReadAllText(doc), Is.EqualTo("# Spec\n\nBody.\n"));
		});
	}

	// The concatenated text of every chat.delta emitted so far, in order.
	private string DeltaText()
	{
		lock (_gate)
		{
			return string.Concat(_sent
				.Select(IpcSerializer.TryDeserialize)
				.Where(m => m is not null && m.Kind == MessageKinds.ChatDelta)
				.Select(m => m!.GetPayload<ChatDeltaPayload>()!.Text));
		}
	}

	private IpcMessage? FindKind(string kind)
	{
		lock (_gate)
		{
			foreach (string json in _sent)
			{
				IpcMessage? message = IpcSerializer.TryDeserialize(json);
				if (message is not null && message.Kind == kind)
				{
					return message;
				}
			}
		}

		return null;
	}

	private IpcMessage? WaitForKind(string kind)
	{
		for (int attempt = 0; attempt < 200; attempt++)
		{
			if (FindKind(kind) is { } found)
			{
				return found;
			}

			Thread.Sleep(20);
		}

		return null;
	}
}
