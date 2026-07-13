using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
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

	[SetUp]
	public void SetUp()
	{
		lock (_gate)
		{
			_sent.Clear();
		}
	}

	private HostController NewController(
		IChatAgent? agent = null,
		ITemplateLibrary? templates = null,
		IFileDialogs? dialogs = null)
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
			chatAgent: agent,
			templates: templates);
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
