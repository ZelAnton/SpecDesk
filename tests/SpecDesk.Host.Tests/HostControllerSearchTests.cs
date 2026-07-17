using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

/// <summary>
/// The workspace-wide search kind (T-078): host-side search across the Markdown files under the same
/// authorized perimeter as the Folder panel's tree.request (the active workspace root, else the open
/// document's folder) — distinct from the toolbar's in-document search (webview/src/index.ts).
/// </summary>
[TestFixture]
public sealed class HostControllerSearchTests
{
	private sealed class FakeDialogs : IFileDialogs
	{
		public string? PickOpenFile() => null;
		public string? PickOpenFolder() => null;
		public string? PickSaveFile(string? suggestedPath) => null;
	}

	private static Renderer.RenderResult StubRender(string docDir, string text) => new(string.Empty, []);

	private string _root = string.Empty;
	private readonly List<string> _sent = [];
	private readonly object _gate = new();

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), "specdesk-search-host-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(_root, "specs"));
		File.WriteAllText(Path.Combine(_root, "README.md"), "# Readme\n\nThe refund window is 30 days.\n");
		File.WriteAllText(Path.Combine(_root, "specs", "billing.md"), "See the refund policy.");
		lock (_gate)
		{
			_sent.Clear();
		}
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
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

		HostController controller = new(
			StubRender,
			Send,
			new FakeDialogs(),
			(_, _, _, _, _) => null,
			new FakeVersioning(),
			NullLogger<HostController>.Instance,
			initialDocPath: null,
			workspace: null);
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
		lock (_gate)
		{
			_sent.Clear();
		}
		return controller;
	}

	private IpcMessage? Find(string kind)
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

	private IpcMessage? WaitFor(string kind)
	{
		SpinWait.SpinUntil(() => Find(kind) is not null, TimeSpan.FromSeconds(2));
		return Find(kind);
	}

	[Test]
	public void SearchRequest_WithNothingOpen_EmitsEmptyNonTruncatedResultsWithoutABackgroundSearch()
	{
		using HostController controller = NewController();

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.SearchRequest, new SearchRequestPayload("refund")));

		SearchResultsPayload? payload = WaitFor(MessageKinds.SearchResults)?.GetPayload<SearchResultsPayload>();
		Assert.That(payload, Is.Not.Null);
		Assert.That(payload!.Results, Is.Empty);
		Assert.That(payload.Truncated, Is.False);
	}

	[Test]
	public void SearchRequest_WithABlankQuery_EmitsEmptyResultsWithoutSearching()
	{
		using HostController controller = NewController();
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.FolderOpen, new FolderOpenPayload(_root)));
		lock (_gate)
		{
			_sent.Clear();
		}

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.SearchRequest, new SearchRequestPayload("   ")));

		SearchResultsPayload? payload = WaitFor(MessageKinds.SearchResults)?.GetPayload<SearchResultsPayload>();
		Assert.That(payload, Is.Not.Null);
		Assert.That(payload!.Query, Is.Empty);
		Assert.That(payload.Results, Is.Empty);
	}

	[Test]
	public void SearchRequest_AfterOpeningAFolder_FindsMatchesAcrossItsMarkdownFiles()
	{
		using HostController controller = NewController();
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.FolderOpen, new FolderOpenPayload(_root)));
		lock (_gate)
		{
			_sent.Clear();
		}

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.SearchRequest, new SearchRequestPayload("refund")));

		SearchResultsPayload? payload = WaitFor(MessageKinds.SearchResults)?.GetPayload<SearchResultsPayload>();
		Assert.That(payload, Is.Not.Null);
		Assert.That(payload!.Results, Has.Count.EqualTo(2));
		Assert.That(payload.Results.Any(r => r.Path.EndsWith("README.md", StringComparison.Ordinal) && r.Line == 3), Is.True);
		Assert.That(payload.Results.Any(r => r.Path.EndsWith("billing.md", StringComparison.Ordinal)), Is.True);
	}

	[Test]
	public void SearchRequest_WithNoWorkspaceButADocumentOpen_UsesTheDocumentsFolderNotItsParent()
	{
		using HostController controller = NewController();
		string doc = Path.Combine(_root, "specs", "billing.md");
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload(doc)));
		lock (_gate)
		{
			_sent.Clear();
		}

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.SearchRequest, new SearchRequestPayload("refund")));

		SearchResultsPayload? payload = WaitFor(MessageKinds.SearchResults)?.GetPayload<SearchResultsPayload>();
		Assert.That(payload, Is.Not.Null);
		// Only specs/billing.md is inside the document's own folder — README.md at the root is out of reach.
		Assert.That(payload!.Results, Has.Count.EqualTo(1));
		Assert.That(payload.Results[0].Path, Does.EndWith("billing.md"));
	}

	[Test]
	public void SearchRequest_RepliesCorrelatedToItsEnvelopeId()
	{
		using HostController controller = NewController();
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.FolderOpen, new FolderOpenPayload(_root)));
		lock (_gate)
		{
			_sent.Clear();
		}

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.SearchRequest, new SearchRequestPayload("refund"), id: "search-1"));

		IpcMessage? reply = WaitFor(MessageKinds.SearchResults);
		Assert.That(reply, Is.Not.Null);
		Assert.That(reply!.Id, Is.EqualTo("search-1"));
	}
}
