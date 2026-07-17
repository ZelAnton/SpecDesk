using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Ai;
using SpecDesk.Contracts;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

/// <summary>
/// The gated proposeEdit flow (HostController.Chat.cs): the assistant's tool only STAGES a proposal
/// (confirm.request), and only a human confirm.result that is still current applies the edit — through the
/// same _text/_contentGeneration/dirty path as a manual edit. Rejection or a concurrent change leaves the
/// document untouched. Driven with fakes so no real agent, git, or network is involved.
/// </summary>
[TestFixture]
public sealed class HostControllerProposeEditTests
{
	private sealed class NoDialogs : IFileDialogs
	{
		public string? PickOpenFile() => null;
		public string? PickOpenFolder() => null;
		public string? PickSaveFile(string? suggestedPath) => null;
	}

	private static Renderer.RenderResult StubRender(string docDir, string text) => new(string.Empty, []);

	private readonly List<string> _sent = [];
	private readonly object _gate = new();
	private string _root = string.Empty;
	private string _path = string.Empty;

	[SetUp]
	public void SetUp()
	{
		lock (_gate)
		{
			_sent.Clear();
		}
		_root = Path.Combine(Path.GetTempPath(), "specdesk-propose-edit-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_root);
		_path = Path.Combine(_root, "spec.md");
		File.WriteAllText(_path, "# Published\n");
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	[Test]
	public void Propose_StagesAConfirmRequestWithTheBeforeAndAfter_WithoutTouchingTheDocument()
	{
		using HostController controller = NewController();
		OpenDraft(controller, "# Draft body\n");
		long generationBefore = PrivateField<long>(controller, "_contentGeneration");

		EditProposalStatus status = controller.ProposeEditTool.Propose("# Proposed body\n", "Tighten the intro");

		ConfirmRequestPayload request = LastConfirmRequest();
		Assert.Multiple(() =>
		{
			Assert.That(status, Is.EqualTo(EditProposalStatus.Staged));
			Assert.That(request.CurrentText, Is.EqualTo("# Draft body\n"));
			Assert.That(request.ProposedText, Is.EqualTo("# Proposed body\n"));
			Assert.That(request.Summary, Is.EqualTo("Tighten the intro"));
			// Nothing applied yet — staging never mutates.
			Assert.That(PrivateField<string>(controller, "_text"), Is.EqualTo("# Draft body\n"));
			Assert.That(PrivateField<long>(controller, "_contentGeneration"), Is.EqualTo(generationBefore));
		});
	}

	[Test]
	public void ConfirmAccepted_AppliesTheConfirmedTextThroughTheOrdinaryEditingPath()
	{
		using HostController controller = NewController();
		OpenDraft(controller, "# Draft body\n");
		long generationBefore = PrivateField<long>(controller, "_contentGeneration");
		controller.ProposeEditTool.Propose("# Proposed body\n", "Tighten the intro");
		string id = LastConfirmRequest().Id;

		Confirm(controller, id, ConfirmDecisions.Accepted, "# Proposed body\n");

		Assert.Multiple(() =>
		{
			Assert.That(PrivateField<string>(controller, "_text"), Is.EqualTo("# Proposed body\n"));
			// Applied through the same content-generation increment a manual edit uses.
			Assert.That(PrivateField<long>(controller, "_contentGeneration"), Is.EqualTo(generationBefore + 1));
			Assert.That(PrivateField<bool>(controller, "_documentMutationLeaseClaimed"), Is.False);
			ConfirmAppliedPayload applied = LastConfirmApplied();
			Assert.That(applied.Id, Is.EqualTo(id));
			Assert.That(applied.Text, Is.EqualTo("# Proposed body\n"));
		});
	}

	[Test]
	public void ConfirmRejected_LeavesTheDocumentUntouchedAndAppliesNothing()
	{
		using HostController controller = NewController();
		OpenDraft(controller, "# Draft body\n");
		long generationBefore = PrivateField<long>(controller, "_contentGeneration");
		controller.ProposeEditTool.Propose("# Proposed body\n", null);
		string id = LastConfirmRequest().Id;

		Confirm(controller, id, ConfirmDecisions.Rejected, null);

		Assert.Multiple(() =>
		{
			Assert.That(PrivateField<string>(controller, "_text"), Is.EqualTo("# Draft body\n"));
			Assert.That(PrivateField<long>(controller, "_contentGeneration"), Is.EqualTo(generationBefore));
			Assert.That(FindKind(MessageKinds.ConfirmApplied), Is.Null, "a rejected proposal is never applied");
		});
	}

	[Test]
	public void ConfirmAccepted_WithAnAuthorEditedText_AppliesTheEditedVersionNotTheOriginalProposal()
	{
		using HostController controller = NewController();
		OpenDraft(controller, "# Draft body\n");
		controller.ProposeEditTool.Propose("# Proposed body\n", null);
		string id = LastConfirmRequest().Id;

		// The author edited the proposal in the confirmation surface before confirming.
		Confirm(controller, id, ConfirmDecisions.Accepted, "# Author edited body\n");

		Assert.Multiple(() =>
		{
			Assert.That(PrivateField<string>(controller, "_text"), Is.EqualTo("# Author edited body\n"));
			Assert.That(LastConfirmApplied().Text, Is.EqualTo("# Author edited body\n"));
		});
	}

	[Test]
	public void ConfirmAccepted_AfterAConcurrentEdit_IsRefusedAndDoesNotCorruptTheDocument()
	{
		using HostController controller = NewController();
		OpenDraft(controller, "# Draft body\n");
		controller.ProposeEditTool.Propose("# Proposed body\n", null);
		string id = LastConfirmRequest().Id;

		// A manual edit lands while the author is reviewing the proposal — it advances the content generation.
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.EditorChanged, new EditorChangedPayload("# Manually changed\n"), version: 9));
		long generationAfterEdit = PrivateField<long>(controller, "_contentGeneration");

		Confirm(controller, id, ConfirmDecisions.Accepted, "# Proposed body\n");

		// The author is told nothing was applied (drained asynchronously — poll for it).
		Assert.That(WaitForKind(MessageKinds.Error), Is.Not.Null, "the author is told nothing was applied");
		Assert.Multiple(() =>
		{
			// The manual edit stands; the stale proposal was not applied over it.
			Assert.That(PrivateField<string>(controller, "_text"), Is.EqualTo("# Manually changed\n"));
			Assert.That(PrivateField<long>(controller, "_contentGeneration"), Is.EqualTo(generationAfterEdit));
			Assert.That(FindKind(MessageKinds.ConfirmApplied), Is.Null);
		});
	}

	[Test]
	public void Propose_OnAPublishedDocumentThatIsNotBeingEdited_IsUnavailableAndStagesNothing()
	{
		using HostController controller = NewController();
		// Loaded but NOT edited — a Published document is read-only; there is nothing to propose an edit to.
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));

		EditProposalStatus status = controller.ProposeEditTool.Propose("# Proposed body\n", null);

		Assert.Multiple(() =>
		{
			Assert.That(status, Is.EqualTo(EditProposalStatus.Unavailable));
			Assert.That(FindKind(MessageKinds.ConfirmRequest), Is.Null);
		});
	}

	[Test]
	public void ConfirmResult_ForAnUnknownProposalId_IsIgnored()
	{
		using HostController controller = NewController();
		OpenDraft(controller, "# Draft body\n");
		controller.ProposeEditTool.Propose("# Proposed body\n", null);

		Confirm(controller, "does-not-match", ConfirmDecisions.Accepted, "# Proposed body\n");

		Assert.Multiple(() =>
		{
			Assert.That(PrivateField<string>(controller, "_text"), Is.EqualTo("# Draft body\n"));
			Assert.That(FindKind(MessageKinds.ConfirmApplied), Is.Null);
		});
	}

	private HostController NewController()
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
			_path,
			TimeSpan.FromMinutes(10));
	}

	private static void OpenDraft(HostController controller, string text)
	{
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.EditorChanged, new EditorChangedPayload(text), version: 1));
	}

	private static void Confirm(HostController controller, string id, string decision, string? text) =>
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.ConfirmResult, new ConfirmResultPayload(id, decision, text)));

	private ConfirmRequestPayload LastConfirmRequest() =>
		WaitForKind(MessageKinds.ConfirmRequest)?.GetPayload<ConfirmRequestPayload>()
			?? throw new InvalidOperationException("No confirm.request was emitted.");

	private ConfirmAppliedPayload LastConfirmApplied() =>
		WaitForKind(MessageKinds.ConfirmApplied)?.GetPayload<ConfirmAppliedPayload>()
			?? throw new InvalidOperationException("No confirm.applied was emitted.");

	// Outbound frames drain on a background task when they are enqueued while a controller monitor is held
	// (see HostController.Emit), so a just-emitted frame may not be in _sent synchronously — poll for it, the
	// same way the chat tests wait for chat.done. (Document STATE is set under _sync before any Emit, so it is
	// read directly; only the emitted frames are asynchronous.)
	private IpcMessage? WaitForKind(string kind)
	{
		for (int attempt = 0; attempt < 200; attempt++)
		{
			IpcMessage? found = FindKind(kind);
			if (found is not null)
			{
				return found;
			}
			Thread.Sleep(5);
		}
		return null;
	}

	private IpcMessage? FindKind(string kind)
	{
		lock (_gate)
		{
			return _sent
				.Select(IpcSerializer.TryDeserialize)
				.LastOrDefault(message => message?.Kind == kind);
		}
	}

	private static T PrivateField<T>(HostController controller, string name) =>
		(T)typeof(HostController)
			.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(controller)!;
}
