using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Ai;
using SpecDesk.Contracts;
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

		public string? PickSaveFile(string? suggestedPath) => null;
	}

	private static Renderer.RenderResult StubRender(string docDir, string text) => new(string.Empty, []);

	private readonly List<string> _sent = [];
	private readonly object _gate = new();

	[SetUp]
	public void SetUp()
	{
		lock (_gate)
		{
			_sent.Clear();
		}
	}

	private HostController NewController(IChatAgent? agent = null, ITemplateLibrary? templates = null)
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
			new NoDialogs(),
			(_, _, _, _, _) => null,
			new FakeVersioning(),
			NullLogger<HostController>.Instance,
			chatAgent: agent,
			templates: templates);
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
