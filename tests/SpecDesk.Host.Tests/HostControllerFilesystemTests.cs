using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

/// <summary>
/// The filesystem foundation for the Start screen and the folder navigator: opening a specific file by
/// path (vs. the dialog fallback), opening a folder as the workspace, and serving its file tree.
/// </summary>
[TestFixture]
public sealed class HostControllerFilesystemTests
{
	private sealed class FakeDialogs : IFileDialogs
	{
		public string? OpenFile { get; set; }
		public string? OpenFolder { get; set; }
		public string? PickOpenFile() => OpenFile;
		public string? PickOpenFolder() => OpenFolder;
		public string? PickSaveFile(string? suggestedPath) => null;
	}

	private static Renderer.RenderResult StubRender(string docDir, string text) => new(string.Empty, []);

	private string _root = string.Empty;
	private readonly List<string> _sent = [];
	private readonly object _gate = new();
	private readonly FakeDialogs _dialogs = new();

	[SetUp]
	public void SetUp()
	{
		// A small workspace: two Markdown files at the root, a nested folder with one, a non-Markdown file,
		// and a dot-directory — the last two must never surface in the tree.
		_root = Path.Combine(Path.GetTempPath(), "specdesk-fs-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(Path.Combine(_root, "specs"));
		Directory.CreateDirectory(Path.Combine(_root, ".git"));
		File.WriteAllText(Path.Combine(_root, "README.md"), "# Readme");
		File.WriteAllText(Path.Combine(_root, "notes.txt"), "not markdown");
		File.WriteAllText(Path.Combine(_root, "specs", "billing.md"), "# Billing");
		File.WriteAllText(Path.Combine(_root, ".git", "config.md"), "# hidden");
		lock (_gate)
		{
			_sent.Clear();
		}
		_dialogs.OpenFile = null;
		_dialogs.OpenFolder = null;
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
			_dialogs,
			(_, _, _, _, _) => null,
			new FakeVersioning(),
			NullLogger<HostController>.Instance,
			initialDocPath: null);
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

	[Test]
	public void DocOpen_WithAnExplicitPath_LoadsThatFileAndDoesNotConsultTheDialog()
	{
		using HostController controller = NewController();
		string file = Path.Combine(_root, "README.md");
		// A decoy the dialog would return — if precedence were inverted (dialog-first) this would win instead.
		_dialogs.OpenFile = Path.Combine(_root, "specs", "billing.md");

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload(file)));

		DocLoadedPayload? loaded = Find(MessageKinds.DocLoaded)?.GetPayload<DocLoadedPayload>();
		Assert.That(loaded, Is.Not.Null);
		Assert.That(loaded!.Path, Is.EqualTo(file));
		Assert.That(loaded.Text, Is.EqualTo("# Readme"));
	}

	[Test]
	public void DocOpen_WithAMalformedPath_EmitsAPlainErrorNotASilentDeadRequest()
	{
		using HostController controller = NewController();

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload("bad\0path")));

		Assert.That(Find(MessageKinds.DocLoaded), Is.Null);
		Assert.That(Find(MessageKinds.Error)?.GetPayload<ErrorPayload>()?.Message, Is.Not.Null.And.Not.Empty);
	}

	[Test]
	public void DocOpen_WithAFileOverThePreviewLimit_EmitsAnErrorWithoutLoadingIt()
	{
		using HostController controller = NewController();
		string file = Path.Combine(_root, "large.txt");
		File.WriteAllBytes(file, new byte[(4 * 1024 * 1024) + 1]);

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload(file)));

		Assert.That(Find(MessageKinds.DocLoaded), Is.Null);
		Assert.That(Find(MessageKinds.Error), Is.Not.Null);
	}

	[TestCase(new byte[] { 0x41, 0x00, 0x42 })]
	[TestCase(new byte[] { 0xC3, 0x28 })]
	public void DocOpen_WithBinaryOrInvalidUtf8_EmitsAnErrorWithoutLoadingIt(byte[] content)
	{
		using HostController controller = NewController();
		string file = Path.Combine(_root, "binary.dat");
		File.WriteAllBytes(file, content);

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload(file)));

		Assert.That(Find(MessageKinds.DocLoaded), Is.Null);
		Assert.That(Find(MessageKinds.Error), Is.Not.Null);
	}

	[Test]
	public void DocOpen_WithoutAPath_FallsBackToTheOpenDialog()
	{
		using HostController controller = NewController();
		_dialogs.OpenFile = Path.Combine(_root, "specs", "billing.md");

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload(null)));

		DocLoadedPayload? loaded = Find(MessageKinds.DocLoaded)?.GetPayload<DocLoadedPayload>();
		Assert.That(loaded?.Path, Is.EqualTo(_dialogs.OpenFile));
	}

	[Test]
	public void FolderOpen_WithAnExplicitPath_EmitsTheFileTreeAndHidesIgnoredDirectories()
	{
		using HostController controller = NewController();
		// A decoy the folder picker would return — the explicit path must win over it.
		_dialogs.OpenFolder = Path.Combine(_root, "specs");

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.FolderOpen, new FolderOpenPayload(_root)));

		TreePayload? tree = Find(MessageKinds.Tree)?.GetPayload<TreePayload>();
		Assert.That(tree, Is.Not.Null);
		Assert.That(tree!.Root, Is.EqualTo(Path.GetFullPath(_root)));
		// Directories sort before files. Files of every type are visible; .git remains excluded.
		string[] topLevel = ["specs", "notes.txt", "README.md"];
		string[] specsChildren = ["billing.md"];
		Assert.That(tree.Nodes.Select(n => n.Name), Is.EqualTo(topLevel));
		TreeNode specs = tree.Nodes[0];
		Assert.That(specs.IsDirectory, Is.True);
		Assert.That(specs.Children.Select(n => n.Name), Is.EqualTo(specsChildren));
		Assert.That(tree.Nodes[1].IsDirectory, Is.False);
	}

	[Test]
	public void FolderOpen_WithoutAPath_FallsBackToTheFolderPicker()
	{
		using HostController controller = NewController();
		_dialogs.OpenFolder = _root;

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.FolderOpen, new FolderOpenPayload(null)));

		TreePayload? tree = Find(MessageKinds.Tree)?.GetPayload<TreePayload>();
		Assert.That(tree?.Root, Is.EqualTo(Path.GetFullPath(_root)));
	}

	[Test]
	public void FolderOpen_WithAMissingFolder_EmitsAPlainError()
	{
		using HostController controller = NewController();
		string missing = Path.Combine(_root, "does-not-exist");

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.FolderOpen, new FolderOpenPayload(missing)));

		Assert.That(Find(MessageKinds.Tree), Is.Null);
		Assert.That(Find(MessageKinds.Error)?.GetPayload<ErrorPayload>()?.Message, Is.Not.Null.And.Not.Empty);
	}

	[Test]
	public void TreeRequest_WithAnExplicitPath_ReturnsThatFoldersTree()
	{
		using HostController controller = NewController();
		string specs = Path.Combine(_root, "specs");

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.TreeRequest, new TreeRequestPayload(specs)));

		TreePayload? tree = Find(MessageKinds.Tree)?.GetPayload<TreePayload>();
		Assert.That(tree?.Root, Is.EqualTo(Path.GetFullPath(specs)));
		string[] justBilling = ["billing.md"];
		Assert.That(tree!.Nodes.Select(n => n.Name), Is.EqualTo(justBilling));
	}

	[Test]
	public void TreeRequest_AfterOpeningAFolder_UsesThatWorkspaceRoot()
	{
		using HostController controller = NewController();
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.FolderOpen, new FolderOpenPayload(_root)));
		lock (_gate)
		{
			_sent.Clear();
		}

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.TreeRequest, new TreeRequestPayload(null)));

		TreePayload? tree = Find(MessageKinds.Tree)?.GetPayload<TreePayload>();
		Assert.That(tree?.Root, Is.EqualTo(Path.GetFullPath(_root)));
	}

	[Test]
	public void TreeRequest_WithNoWorkspaceButADocumentOpen_UsesTheDocumentsFolder()
	{
		using HostController controller = NewController();
		// Open a document (no folder opened), then ask for the tree with no path — it should fall back to the
		// open document's own folder.
		string doc = Path.Combine(_root, "specs", "billing.md");
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload(doc)));
		lock (_gate)
		{
			_sent.Clear();
		}

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.TreeRequest, new TreeRequestPayload(null)));

		TreePayload? tree = Find(MessageKinds.Tree)?.GetPayload<TreePayload>();
		Assert.That(tree?.Root, Is.EqualTo(Path.GetFullPath(Path.Combine(_root, "specs"))));
	}

	[Test]
	public void TreeRequest_WhenBothAWorkspaceAndADocumentAreSet_TheWorkspaceWins()
	{
		using HostController controller = NewController();
		// Workspace = _root; open a document in the specs/ subfolder. The workspace takes precedence.
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.FolderOpen, new FolderOpenPayload(_root)));
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.DocOpen, new DocOpenPayload(Path.Combine(_root, "specs", "billing.md"))));
		lock (_gate)
		{
			_sent.Clear();
		}

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.TreeRequest, new TreeRequestPayload(null)));

		TreePayload? tree = Find(MessageKinds.Tree)?.GetPayload<TreePayload>();
		Assert.That(tree?.Root, Is.EqualTo(Path.GetFullPath(_root)));
	}

	[Test]
	public void TreeRequest_WithAMalformedPath_EmitsAPlainErrorNotASilentDeadRequest()
	{
		using HostController controller = NewController();
		// An embedded null makes Path.GetFullPath throw — the request must surface a plain error, not vanish.
		controller.OnMessage(
			IpcSerializer.SerializeEvent(MessageKinds.TreeRequest, new TreeRequestPayload("bad\0path")));

		Assert.That(Find(MessageKinds.Tree), Is.Null);
		Assert.That(Find(MessageKinds.Error)?.GetPayload<ErrorPayload>()?.Message, Is.Not.Null.And.Not.Empty);
	}

	[Test]
	public void TreeRequest_WithNothingOpen_EmitsAnEmptyTreeNotAnError()
	{
		using HostController controller = NewController();

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.TreeRequest, new TreeRequestPayload(null)));

		TreePayload? tree = Find(MessageKinds.Tree)?.GetPayload<TreePayload>();
		Assert.That(tree, Is.Not.Null);
		Assert.That(tree!.Root, Is.Empty);
		Assert.That(tree.Nodes, Is.Empty);
		Assert.That(Find(MessageKinds.Error), Is.Null);
	}
}
