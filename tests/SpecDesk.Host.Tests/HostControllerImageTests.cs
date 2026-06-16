using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class HostControllerImageTests
{
	private sealed class NoDialogs : IFileDialogs
	{
		public string? PickOpenFile() => null;

		public string? PickSaveFile(string? suggestedPath) => null;
	}

	private static Renderer.RenderResult StubRender(string docDir, string text) =>
		new(string.Empty, []);

	[Test]
	public void ImagePaste_RepliesWithInsertedMarkdownEchoingTheId()
	{
		List<string> sent = [];
		object gate = new();
		void Send(string json)
		{
			lock (gate)
			{
				sent.Add(json);
			}
		}

		string tempDir = Path.Combine(Path.GetTempPath(), "specdesk-host-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempDir);
		string docPath = Path.Combine(tempDir, "billing.md");
		File.WriteAllText(docPath, "# Billing");

		try
		{
			ImageInserter inserter = (_, _, _, _, _) => "![x](images/billing/x.png)";
			using HostController controller = new(
				StubRender,
				Send,
				new NoDialogs(),
				inserter,
				new FakeVersioning(),
				NullLogger<HostController>.Instance,
				docPath);

			// Load the document so the controller has a current path + repo root.
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
			// Enter edit mode (forks a working branch): images are only inserted into a draft.
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.ActionEdit));

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.ImagePaste,
				new ImagePastePayload("AAAA", null, null),
				id: "req-1"));

			IpcMessage? reply = WaitForKind(sent, gate, MessageKinds.ImageInserted);

			Assert.That(reply, Is.Not.Null);
			Assert.Multiple(() =>
			{
				Assert.That(reply!.Id, Is.EqualTo("req-1"));
				Assert.That(reply.GetPayload<ImageInsertedPayload>()!.Markdown, Is.EqualTo("![x](images/billing/x.png)"));
			});
		}
		finally
		{
			Directory.Delete(tempDir, recursive: true);
		}
	}

	[Test]
	public void ImagePaste_WhilePublished_IsIgnoredAndDoesNotInsert()
	{
		List<string> sent = [];
		object gate = new();
		void Send(string json)
		{
			lock (gate)
			{
				sent.Add(json);
			}
		}

		string tempDir = Path.Combine(Path.GetTempPath(), "specdesk-host-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempDir);
		string docPath = Path.Combine(tempDir, "billing.md");
		File.WriteAllText(docPath, "# Billing");

		try
		{
			bool inserterCalled = false;
			ImageInserter inserter = (_, _, _, _, _) =>
			{
				inserterCalled = true;
				return "![x](images/billing/x.png)";
			};
			using HostController controller = new(
				StubRender,
				Send,
				new NoDialogs(),
				inserter,
				new FakeVersioning(),
				NullLogger<HostController>.Instance,
				docPath);

			// Loaded but never put into edit mode → still read-only / Published.
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.ImagePaste,
				new ImagePastePayload("AAAA", null, null),
				id: "req-2"));

			IpcMessage? reply = WaitForKind(sent, gate, MessageKinds.ImageInserted);
			Assert.Multiple(() =>
			{
				Assert.That(reply, Is.Not.Null);
				Assert.That(reply!.GetPayload<ImageInsertedPayload>()!.Markdown, Is.Empty);
				Assert.That(inserterCalled, Is.False, "the image engine must not run for a published doc");
			});
		}
		finally
		{
			Directory.Delete(tempDir, recursive: true);
		}
	}

	// The inserter replies from a background task; poll briefly for the expected message.
	private static IpcMessage? WaitForKind(List<string> sent, object gate, string kind)
	{
		for (int attempt = 0; attempt < 100; attempt++)
		{
			lock (gate)
			{
				foreach (string json in sent)
				{
					IpcMessage? message = IpcSerializer.TryDeserialize(json);
					if (message is not null && message.Kind == kind)
					{
						return message;
					}
				}
			}

			Thread.Sleep(20);
		}

		return null;
	}
}
