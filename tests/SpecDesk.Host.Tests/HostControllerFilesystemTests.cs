using System.Diagnostics;
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
		// A small workspace: Markdown and plain-text files, a nested folder, and a hidden metadata directory.
		// The ordinary files surface at their direct level; the metadata directory never does.
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

	private HostController NewController(WorkspaceStore? workspace = null)
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
			initialDocPath: null,
			workspace: workspace);
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
		lock (_gate)
		{
			_sent.Clear();
		}
		return controller;
	}

	private void ClearSent()
	{
		lock (_gate)
		{
			_sent.Clear();
		}
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
		Assert.That(loaded.Branch, Is.EqualTo("main"));
	}

	[Test]
	public void DocOpen_WithARequestId_EmitsMatchingSuccessfulCompletion()
	{
		using HostController controller = NewController();
		string file = Path.Combine(_root, "README.md");

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.DocOpen,
			new DocOpenPayload(file, RequestId: 41)));

		DocOpenCompletedPayload? completed = Find(MessageKinds.DocOpenCompleted)
			?.GetPayload<DocOpenCompletedPayload>();
		Assert.That(completed, Is.EqualTo(new DocOpenCompletedPayload(41, Succeeded: true)));
	}

	[Test]
	public void DocOpen_WhenPickerIsCancelled_EmitsMatchingFailedCompletion()
	{
		using HostController controller = NewController();

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.DocOpen,
			new DocOpenPayload(null, RequestId: 42)));

		DocOpenCompletedPayload? completed = Find(MessageKinds.DocOpenCompleted)
			?.GetPayload<DocOpenCompletedPayload>();
		Assert.That(completed, Is.EqualTo(new DocOpenCompletedPayload(42, Succeeded: false)));
	}

	[Test]
	public void DocOpen_WithAMalformedPath_EmitsAPlainErrorNotASilentDeadRequest()
	{
		using HostController controller = NewController();

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.DocOpen,
			new DocOpenPayload("bad\0path", RequestId: 43)));

		Assert.Multiple(() =>
		{
			Assert.That(Find(MessageKinds.DocLoaded), Is.Null);
			Assert.That(Find(MessageKinds.Error)?.GetPayload<ErrorPayload>()?.Message, Is.Not.Null.And.Not.Empty);
			Assert.That(
				Find(MessageKinds.DocOpenCompleted)?.GetPayload<DocOpenCompletedPayload>(),
				Is.EqualTo(new DocOpenCompletedPayload(43, Succeeded: false)));
		});
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

		TreePayload? tree = WaitFor(MessageKinds.Tree)?.GetPayload<TreePayload>();
		Assert.That(tree, Is.Not.Null);
		Assert.That(tree!.Root, Is.EqualTo(Path.GetFullPath(_root)));
		// Directories sort before files. Files of every type are visible; .git remains excluded.
		string[] topLevel = ["specs", "notes.txt", "README.md"];
		Assert.That(tree.Nodes.Select(n => n.Name), Is.EqualTo(topLevel));
		TreeNode specs = tree.Nodes[0];
		Assert.That(specs.IsDirectory, Is.True);
		Assert.That(specs.HasChildren, Is.True);
		Assert.That(specs.Children, Is.Empty, "opening a folder reads only its root level");
		Assert.That(tree.Nodes[1].IsDirectory, Is.False);
		WorkspaceContextPayload? context = Find(MessageKinds.WorkspaceContext)
			?.GetPayload<WorkspaceContextPayload>();
		Assert.Multiple(() =>
		{
			Assert.That(context?.RepositoryRoot, Is.EqualTo(Path.GetFullPath(_root)));
			Assert.That(context?.Branch, Is.EqualTo("main"));
			Assert.That(context?.BranchState, Is.EqualTo("named"));
		});
	}

	[Test]
	public void FolderOpen_WithoutAPath_FallsBackToTheFolderPicker()
	{
		using HostController controller = NewController();
		_dialogs.OpenFolder = _root;

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.FolderOpen, new FolderOpenPayload(null)));

		TreePayload? tree = WaitFor(MessageKinds.Tree)?.GetPayload<TreePayload>();
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
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.FolderOpen, new FolderOpenPayload(_root)));
		Assert.That(WaitFor(MessageKinds.Tree), Is.Not.Null);
		lock (_gate)
		{
			_sent.Clear();
		}

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.TreeRequest, new TreeRequestPayload(specs, RequestId: 41)));

		TreePayload? tree = WaitFor(MessageKinds.Tree)?.GetPayload<TreePayload>();
		Assert.That(tree?.Root, Is.EqualTo(Path.GetFullPath(specs)));
		Assert.That(tree?.RequestId, Is.EqualTo(41));
		string[] justBilling = ["billing.md"];
		Assert.That(tree!.Nodes.Select(n => n.Name), Is.EqualTo(justBilling));
	}

	[Test]
	public void TreeRequest_WithAParentOrSiblingPrefixPath_DoesNotExposeThatDirectory()
	{
		using HostController controller = NewController();
		string sibling = _root + "-sibling";
		Directory.CreateDirectory(sibling);
		File.WriteAllText(Path.Combine(sibling, "secret.md"), "# Secret");
		try
		{
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.FolderOpen, new FolderOpenPayload(_root)));
			Assert.That(WaitFor(MessageKinds.Tree), Is.Not.Null);
			lock (_gate)
			{
				_sent.Clear();
			}

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.TreeRequest, new TreeRequestPayload(sibling, RequestId: 42)));

			TreePayload? tree = WaitFor(MessageKinds.Tree)?.GetPayload<TreePayload>();
			Assert.Multiple(() =>
			{
				Assert.That(tree?.RequestId, Is.EqualTo(42));
				Assert.That(tree?.Nodes, Is.Empty);
				Assert.That(tree?.Nodes.Select(node => node.Name), Does.Not.Contain("secret.md"));
			});
		}
		finally
		{
			Directory.Delete(sibling, recursive: true);
		}
	}

	[Test]
	public void TreeRequest_WithAnAbsolutePathOutsideTheWorkspace_DoesNotExposeThatDirectory()
	{
		using HostController controller = NewController();
		string outside = Path.GetDirectoryName(_root)!;
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.FolderOpen, new FolderOpenPayload(_root)));
		Assert.That(WaitFor(MessageKinds.Tree), Is.Not.Null);
		lock (_gate)
		{
			_sent.Clear();
		}

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.TreeRequest, new TreeRequestPayload(outside, RequestId: 43)));

		TreePayload? tree = WaitFor(MessageKinds.Tree)?.GetPayload<TreePayload>();
		Assert.Multiple(() =>
		{
			Assert.That(tree?.RequestId, Is.EqualTo(43));
			Assert.That(tree?.Nodes, Is.Empty);
		});
	}

	[Test]
	public void TreeRequest_ThroughAReparseDirectory_DoesNotExposeItsTarget()
	{
		using HostController controller = NewController();
		string outside = _root + "-outside";
		string link = Path.Combine(_root, "linked");
		Directory.CreateDirectory(outside);
		File.WriteAllText(Path.Combine(outside, "secret.md"), "# Secret");
		try
		{
			try
			{
				Directory.CreateSymbolicLink(link, outside);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
				or PlatformNotSupportedException)
			{
				Assert.Ignore($"Symbolic links are unavailable on this platform: {ex.Message}");
			}

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.FolderOpen, new FolderOpenPayload(_root)));
			Assert.That(WaitFor(MessageKinds.Tree), Is.Not.Null);
			lock (_gate)
			{
				_sent.Clear();
			}
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.TreeRequest, new TreeRequestPayload(link, RequestId: 44)));

			TreePayload? tree = WaitFor(MessageKinds.Tree)?.GetPayload<TreePayload>();
			Assert.Multiple(() =>
			{
				Assert.That(tree?.RequestId, Is.EqualTo(44));
				Assert.That(tree?.Nodes, Is.Empty);
			});
		}
		finally
		{
			if (Directory.Exists(link))
			{
				Directory.Delete(link);
			}
			Directory.Delete(outside, recursive: true);
		}
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

		TreePayload? tree = WaitFor(MessageKinds.Tree)?.GetPayload<TreePayload>();
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

		TreePayload? tree = WaitFor(MessageKinds.Tree)?.GetPayload<TreePayload>();
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

		TreePayload? tree = WaitFor(MessageKinds.Tree)?.GetPayload<TreePayload>();
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

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.TreeRequest, new TreeRequestPayload(null, RequestId: 45)));

		TreePayload? tree = WaitFor(MessageKinds.Tree)?.GetPayload<TreePayload>();
		Assert.That(tree, Is.Not.Null);
		Assert.That(tree!.Root, Is.Empty);
		Assert.That(tree.Nodes, Is.Empty);
		Assert.That(tree.RequestId, Is.EqualTo(45));
		Assert.That(Find(MessageKinds.Error), Is.Null);
	}

	[Test]
	public void FileDelete_InsideCurrentDiskRoot_DeletesOnlyThatFileAndCompletes()
	{
		using HostController controller = NewController();
		string file = Path.Combine(_root, "README.md");
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.FolderOpen, new FolderOpenPayload(_root)));
		ClearSent();

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.FileDelete, new FileDeletePayload(file, _root, RequestId: 61)));

		FileDeleteCompletedPayload? completed = WaitFor(MessageKinds.FileDeleteCompleted)
			?.GetPayload<FileDeleteCompletedPayload>();
		Assert.Multiple(() =>
		{
			Assert.That(completed, Is.EqualTo(new FileDeleteCompletedPayload(
				file, _root, 61, Succeeded: true)));
			Assert.That(File.Exists(file), Is.False);
			Assert.That(Directory.Exists(Path.Combine(_root, "specs")), Is.True);
			Assert.That(Find(MessageKinds.Error), Is.Null);
		});
	}

	[Test]
	public void FileDelete_WithMalformedOrUncorrelatedPayloadIsIgnored()
	{
		using HostController controller = NewController();
		string file = Path.Combine(_root, "README.md");
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.FolderOpen, new FolderOpenPayload(_root)));
		ClearSent();

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.FileDelete, new { path = file, root = _root, requestId = "not-a-number" }));
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.FileDelete, new FileDeletePayload(file, _root, RequestId: 0)));

		Assert.Multiple(() =>
		{
			Assert.That(File.Exists(file), Is.True);
			Assert.That(Find(MessageKinds.FileDeleteCompleted), Is.Null);
		});
	}

	[TestCase("traversal")]
	[TestCase("sibling-prefix")]
	[TestCase("root-mismatch")]
	public void FileDelete_RejectsPathsNotOwnedByTheCurrentDiskRoot(string scenario)
	{
		using HostController controller = NewController();
		string sibling = _root + "-sibling";
		Directory.CreateDirectory(sibling);
		string outside = Path.Combine(sibling, "keep.md");
		File.WriteAllText(outside, "# Keep");
		try
		{
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.FolderOpen, new FolderOpenPayload(_root)));
			ClearSent();
			string target = scenario == "traversal"
				? Path.Combine(_root, "specs", "..", "..", Path.GetFileName(sibling), "keep.md")
				: outside;
			string suppliedRoot = scenario == "root-mismatch" ? Path.Combine(_root, "specs") : _root;

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.FileDelete, new FileDeletePayload(target, suppliedRoot, RequestId: 62)));

			FileDeleteCompletedPayload? completed = WaitFor(MessageKinds.FileDeleteCompleted)
				?.GetPayload<FileDeleteCompletedPayload>();
			Assert.Multiple(() =>
			{
				Assert.That(completed?.Succeeded, Is.False);
				Assert.That(completed?.Error, Is.Not.Null.And.Not.Empty);
				Assert.That(File.Exists(outside), Is.True);
			});
		}
		finally
		{
			Directory.Delete(sibling, recursive: true);
		}
	}

	[Test]
	public void FileDelete_RejectsAFileReachedThroughAReparsePoint()
	{
		using HostController controller = NewController();
		string outside = _root + "-outside.md";
		string link = Path.Combine(_root, "linked.md");
		File.WriteAllText(outside, "# Keep");
		try
		{
			try
			{
				File.CreateSymbolicLink(link, outside);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
				or PlatformNotSupportedException)
			{
				Assert.Ignore($"Symbolic links are unavailable on this platform: {ex.Message}");
			}
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.FolderOpen, new FolderOpenPayload(_root)));
			ClearSent();

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.FileDelete, new FileDeletePayload(link, _root, RequestId: 63)));

			FileDeleteCompletedPayload? completed = WaitFor(MessageKinds.FileDeleteCompleted)
				?.GetPayload<FileDeleteCompletedPayload>();
			Assert.Multiple(() =>
			{
				Assert.That(completed?.Succeeded, Is.False);
				Assert.That(completed?.Error, Does.Contain("link").IgnoreCase);
				Assert.That(File.Exists(outside), Is.True);
			});
		}
		finally
		{
			if (File.Exists(link)) File.Delete(link);
			if (File.Exists(outside)) File.Delete(outside);
		}
	}

	[Test]
	public void FileDelete_WhenContainmentReportsReparseTraversalFailsClosedDeterministically()
	{
		using HostController controller = NewController();
		string file = Path.Combine(_root, "README.md");
		controller.FileDeleteReparseCheckForTest = (root, target) =>
		{
			Assert.That(root, Is.EqualTo(Path.GetFullPath(_root)));
			Assert.That(target, Is.EqualTo(Path.GetFullPath(file)));
			return true;
		};
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.FolderOpen, new FolderOpenPayload(_root)));
		ClearSent();

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.FileDelete, new FileDeletePayload(file, _root, RequestId: 631)));

		FileDeleteCompletedPayload? completed = WaitFor(MessageKinds.FileDeleteCompleted)
			?.GetPayload<FileDeleteCompletedPayload>();
		Assert.Multiple(() =>
		{
			Assert.That(completed?.Succeeded, Is.False);
			Assert.That(completed?.Error, Does.Contain("link").IgnoreCase);
			Assert.That(File.Exists(file), Is.True);
		});
	}

	[Test]
	public void FileDelete_HandleContainmentDistinguishesACaseOnlySibling()
	{
		const string root = @"C:\specdesk\Root";
		Assert.Multiple(() =>
		{
			Assert.That(
				WindowsHandleFileDeletion.IsCanonicalHandleDescendant(root, @"C:\specdesk\Root\victim.md"),
				Is.True);
			Assert.That(
				WindowsHandleFileDeletion.IsCanonicalHandleDescendant(root, @"c:\specdesk\Root\victim.md"),
				Is.True,
				"the drive designator is not a case-sensitive directory entry");
			Assert.That(
				WindowsHandleFileDeletion.IsCanonicalHandleDescendant(root, @"C:\specdesk\root\victim.md"),
				Is.False,
				"canonical handle ancestry must preserve directory-entry casing");
			Assert.That(
				WindowsHandleFileDeletion.IsCanonicalHandleDescendant(root, @"C:\specdesk\Root-sibling\victim.md"),
				Is.False);
			Assert.That(WindowsHandleFileDeletion.IsCanonicalHandleDescendant(root, root), Is.False);
			Assert.That(
				WindowsHandleFileDeletion.IsCanonicalHandleDescendant(@"C:\", @"C:\victim.md"),
				Is.True);
			Assert.That(
				WindowsHandleFileDeletion.IsCanonicalHandleDescendant(
					@"\\server\share\Root", @"\\SERVER\SHARE\Root\victim.md"),
				Is.True,
				"UNC server and share names form the case-insensitive volume authority");
			Assert.That(
				WindowsHandleFileDeletion.IsCanonicalHandleDescendant(
					@"\\server\share", @"\\SERVER\SHARE\Root\victim.md"),
				Is.True);
			Assert.That(
				WindowsHandleFileDeletion.IsCanonicalHandleDescendant(
					@"\\server\share", @"\\SERVER\SHARE"),
				Is.False);
			Assert.That(
				WindowsHandleFileDeletion.IsCanonicalHandleDescendant(
					@"\\server\share\Root", @"\\SERVER\SHARE\root\victim.md"),
				Is.False,
				"directory entries after the UNC share must remain case-sensitive");
			Assert.That(
				WindowsHandleFileDeletion.IsCanonicalHandleDescendant(
					@"\\server\share\Root", @"\\server\other\Root\victim.md"),
				Is.False);
			Assert.That(
				WindowsHandleFileDeletion.IsCanonicalHandleDescendant(
					@"\\?\UNC\server\share\Root", @"\\SERVER\SHARE\Root\victim.md"),
				Is.True);
			Assert.That(
				WindowsHandleFileDeletion.IsCanonicalHandleDescendant(
					@"\\?\C:\specdesk\Root", @"c:\specdesk\Root\victim.md"),
				Is.True);
			Assert.That(
				WindowsHandleFileDeletion.AreSameCanonicalHandlePath(
					@"C:\specdesk\Root\", @"c:\specdesk\Root"),
				Is.True);
			Assert.That(
				WindowsHandleFileDeletion.AreSameCanonicalHandlePath(
					@"C:\specdesk\Root", @"C:\specdesk\root"),
				Is.False);
			Assert.That(
				WindowsHandleFileDeletion.AreSameCanonicalHandlePath(
					@"\\server\share\Root", @"\\SERVER\SHARE\Root\"),
				Is.True);
			Assert.That(
				WindowsHandleFileDeletion.AreSameCanonicalHandlePath(
					@"\\server\share\", @"\\SERVER\SHARE"),
				Is.True);
			Assert.That(
				WindowsHandleFileDeletion.AreSameCanonicalHandlePath(
					@"\\server\share\Root", @"\\SERVER\SHARE\root"),
				Is.False);
			Assert.That(
				WindowsHandleFileDeletion.AreSameCanonicalHandlePath(
					@"\\?\UNC\server\share\Root", @"\\SERVER\SHARE\Root"),
				Is.True);
			Assert.That(
				WindowsHandleFileDeletion.AreSameCanonicalHandlePath(
					@"\\?\C:\specdesk\Root", @"c:\specdesk\Root"),
				Is.True);
		});
	}

	[Test]
	public void FileDelete_CaseSensitiveCaseOnlySiblingCannotEscapeTheDiskRoot()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Ignore("Per-directory case sensitivity is a Windows filesystem feature.");
		}

		string parent = Path.Combine(_root, "case-sensitive-parent");
		Directory.CreateDirectory(parent);
		if (!TrySetDirectoryCaseSensitivity(parent, enable: true, out string reason))
		{
			Assert.Ignore($"Per-directory case sensitivity is unavailable: {reason}");
		}

		string authoritativeRoot = Path.Combine(parent, "Root");
		string caseOnlySibling = Path.Combine(parent, "root");
		string victim = Path.Combine(caseOnlySibling, "victim.md");
		try
		{
			Directory.CreateDirectory(authoritativeRoot);
			Directory.CreateDirectory(caseOnlySibling);
			File.WriteAllText(victim, "# Keep outside the authoritative root");
			Assert.That(Directory.GetDirectories(parent), Has.Length.EqualTo(2),
				"the filesystem must expose Root and root as separate directories for this test");

			using HostController controller = NewController();
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.FolderOpen, new FolderOpenPayload(authoritativeRoot)));
			ClearSent();

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.FileDelete,
				new FileDeletePayload(victim, authoritativeRoot, RequestId: 633)));

			FileDeleteCompletedPayload? completed = WaitFor(MessageKinds.FileDeleteCompleted)
				?.GetPayload<FileDeleteCompletedPayload>();
			Assert.Multiple(() =>
			{
				Assert.That(completed?.Succeeded, Is.False);
				Assert.That(completed?.Error, Does.Contain("outside").IgnoreCase);
				Assert.That(File.Exists(victim), Is.True,
					"a case-only sibling is not a descendant of the authoritative Disk root");
			});
		}
		finally
		{
			if (Directory.Exists(authoritativeRoot)) Directory.Delete(authoritativeRoot, recursive: true);
			if (Directory.Exists(caseOnlySibling)) Directory.Delete(caseOnlySibling, recursive: true);
			_ = TrySetDirectoryCaseSensitivity(parent, enable: false, out _);
			if (Directory.Exists(parent)) Directory.Delete(parent);
		}
	}

	[Test]
	public void FileDelete_IntermediateJunctionSwapCannotRedirectHandleDeletion()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Ignore("Handle-bound deletion and directory junctions are Windows-specific.");
		}

		using HostController controller = NewController();
		string intermediate = Path.Combine(_root, "race-parent");
		string original = Path.Combine(_root, "race-parent-original");
		string outside = _root + "-race-outside";
		string target = Path.Combine(intermediate, "victim.md");
		string originalTarget = Path.Combine(original, "victim.md");
		string sentinel = Path.Combine(outside, "victim.md");
		bool junctionWasReparse = false;
		Directory.CreateDirectory(intermediate);
		Directory.CreateDirectory(outside);
		File.WriteAllText(target, "# Original inside Disk");
		File.WriteAllText(sentinel, "# Outside sentinel");
		try
		{
			controller.FileDeleteReparseCheckForTest = (_, _) =>
			{
				Directory.Move(intermediate, original);
				CreateDirectoryJunction(intermediate, outside);
				junctionWasReparse = File.GetAttributes(intermediate)
					.HasFlag(FileAttributes.ReparsePoint);
				return false;
			};
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.FolderOpen, new FolderOpenPayload(_root)));
			ClearSent();

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.FileDelete, new FileDeletePayload(target, _root, RequestId: 632)));

			FileDeleteCompletedPayload? completed = WaitFor(MessageKinds.FileDeleteCompleted)
				?.GetPayload<FileDeleteCompletedPayload>();
			Assert.Multiple(() =>
			{
				Assert.That(junctionWasReparse, Is.True, "mklink /J must create an actual reparse point");
				Assert.That(completed?.Succeeded, Is.False,
					"a namespace swap may fail closed when Windows refuses disposition after rename");
				Assert.That(File.Exists(originalTarget), Is.True,
					"a failed handle disposition must leave the original file intact");
				Assert.That(File.Exists(sentinel), Is.True,
					"the new pathname target outside Disk must never be deleted");
				Assert.That(File.ReadAllText(sentinel), Is.EqualTo("# Outside sentinel"));
			});
		}
		finally
		{
			if (Directory.Exists(intermediate))
			{
				RemoveDirectoryJunction(intermediate, _root);
			}
			if (Directory.Exists(outside))
			{
				Directory.Delete(outside, recursive: true);
			}
		}
	}

	[Test]
	public void FileDelete_OfTheOpenDocumentClosesItsEditorAndClearsContext()
	{
		using HostController controller = NewController();
		string file = Path.Combine(_root, "README.md");
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.FolderOpen, new FolderOpenPayload(_root)));
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.DocOpen, new DocOpenPayload(file)));
		ClearSent();

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.FileDelete, new FileDeletePayload(file, _root, RequestId: 64)));

		Assert.That(WaitFor(MessageKinds.FileDeleteCompleted)
			?.GetPayload<FileDeleteCompletedPayload>()?.Succeeded, Is.True);
		DocLoadedPayload? cleared = Find(MessageKinds.DocLoaded)?.GetPayload<DocLoadedPayload>();
		WorkspaceContextPayload? context = Find(MessageKinds.WorkspaceContext)
			?.GetPayload<WorkspaceContextPayload>();
		Assert.Multiple(() =>
		{
			Assert.That(cleared?.Path, Is.Empty);
			Assert.That(cleared?.Text, Is.Empty);
			Assert.That(context?.Repository, Is.Null);
			Assert.That(context?.Path, Is.Empty);
			Assert.That(File.Exists(file), Is.False);
		});
	}

	[Test]
	public void FileDelete_CaseOnlySiblingKeepsTheDirtyActiveDocumentOpen()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Ignore("Per-directory case sensitivity is a Windows filesystem feature.");
		}

		string caseRoot = Path.Combine(_root, "case-sensitive-documents");
		Directory.CreateDirectory(caseRoot);
		if (!TrySetDirectoryCaseSensitivity(caseRoot, enable: true, out string reason))
		{
			Assert.Ignore($"Per-directory case sensitivity is unavailable: {reason}");
		}

		string upper = Path.Combine(caseRoot, "A.md");
		string lower = Path.Combine(caseRoot, "a.md");
		string statePath = Path.Combine(_root, "case-sensitive-workspace.json");
		const string pendingText = "# Unsaved upper-case document";
		try
		{
			File.WriteAllText(upper, "# Upper");
			File.WriteAllText(lower, "# Lower");
			WorkspaceStore store = new(statePath);
			WorkspaceItem upperItem = new(upper, "A.md", IsFolder: false);
			WorkspaceItem lowerItem = new(lower, "a.md", IsFolder: false);
			store.AddRecent(upperItem);
			store.AddRecent(lowerItem);
			store.SetFavorite(upperItem, favorite: true);
			store.SetFavorite(lowerItem, favorite: true);
			using (HostController controller = NewController(store))
			{
				controller.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.FolderOpen, new FolderOpenPayload(caseRoot)));
				controller.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.DocOpen, new DocOpenPayload(upper)));
				controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));
				controller.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.EditorChanged, new EditorChangedPayload(pendingText), version: 1));
				ClearSent();

				controller.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.FileDelete,
					new FileDeletePayload(lower, caseRoot, RequestId: 641)));

				Assert.That(WaitFor(MessageKinds.FileDeleteCompleted)
					?.GetPayload<FileDeleteCompletedPayload>()?.Succeeded, Is.True);
				Assert.That(Find(MessageKinds.DocLoaded), Is.Null,
					"deleting a case-only sibling must not clear the active editor");
				Assert.That(SpinWait.SpinUntil(
					() => File.Exists(upper) && File.ReadAllText(upper) == pendingText,
					TimeSpan.FromSeconds(2)), Is.True,
					"the active document's pending text must remain available for autosave");
			}

			Assert.Multiple(() =>
			{
				Assert.That(File.Exists(lower), Is.False);
				Assert.That(File.Exists(upper), Is.True);
				Assert.That(File.ReadAllText(upper), Is.EqualTo(pendingText));
				Assert.That(store.State().Recent.Select(item => item.Path),
					Does.Contain(upper).And.Not.Contain(lower));
				Assert.That(store.State().Favorites.Select(item => item.Path),
					Does.Contain(upper).And.Not.Contain(lower));
			});
		}
		finally
		{
			if (File.Exists(upper)) File.Delete(upper);
			if (File.Exists(lower)) File.Delete(lower);
			if (File.Exists(statePath)) File.Delete(statePath);
			_ = TrySetDirectoryCaseSensitivity(caseRoot, enable: false, out _);
			if (Directory.Exists(caseRoot)) Directory.Delete(caseRoot);
		}
	}

	[Test]
	public void FileDelete_RemovesTheExactFileFromRecentsAndFavorites()
	{
		string statePath = _root + "-workspace.json";
		try
		{
			WorkspaceStore store = new(statePath);
			string file = Path.Combine(_root, "README.md");
			WorkspaceItem item = new(file, "README.md", IsFolder: false);
			store.AddRecent(item);
			store.SetFavorite(item, favorite: true);
			using HostController controller = NewController(store);
			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.FolderOpen, new FolderOpenPayload(_root)));
			ClearSent();

			controller.OnMessage(IpcSerializer.SerializeEvent(
				MessageKinds.FileDelete, new FileDeletePayload(file, _root, RequestId: 65)));

			Assert.That(WaitFor(MessageKinds.FileDeleteCompleted)
				?.GetPayload<FileDeleteCompletedPayload>()?.Succeeded, Is.True);
			Assert.Multiple(() =>
			{
				Assert.That(store.State().Recent.Any(existing => existing.Path == file), Is.False);
				Assert.That(store.State().Favorites.Any(existing => existing.Path == file), Is.False);
			});
		}
		finally
		{
			if (File.Exists(statePath)) File.Delete(statePath);
		}
	}

	[Test]
	public void FileDelete_RefusesDirectoriesMissingAndReadOnlyFilesWithoutRemovingAnything()
	{
		using HostController controller = NewController();
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.FolderOpen, new FolderOpenPayload(_root)));
		string readOnly = Path.Combine(_root, "readonly.md");
		File.WriteAllText(readOnly, "# Keep");
		File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);
		try
		{
			(string Path, long Id)[] requests =
			[
				(Path.Combine(_root, "specs"), 66),
				(Path.Combine(_root, "missing.md"), 67),
				(readOnly, 68),
			];
			foreach ((string path, long id) in requests)
			{
				ClearSent();
				controller.OnMessage(IpcSerializer.SerializeEvent(
					MessageKinds.FileDelete, new FileDeletePayload(path, _root, id)));
				Assert.That(WaitFor(MessageKinds.FileDeleteCompleted)
					?.GetPayload<FileDeleteCompletedPayload>()?.Succeeded, Is.False);
			}
			Assert.Multiple(() =>
			{
				Assert.That(Directory.Exists(Path.Combine(_root, "specs")), Is.True);
				Assert.That(File.Exists(readOnly), Is.True);
			});
		}
		finally
		{
			File.SetAttributes(readOnly, FileAttributes.Normal);
		}
	}

	[Test]
	public void FileDelete_WhenTheFileIsLockedReportsFailureAndKeepsIt()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Ignore("Windows is the desktop target whose open handle prevents deletion.");
		}
		using HostController controller = NewController();
		string file = Path.Combine(_root, "locked.md");
		File.WriteAllText(file, "# Keep");
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.FolderOpen, new FolderOpenPayload(_root)));
		ClearSent();
		using FileStream held = new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.FileDelete, new FileDeletePayload(file, _root, RequestId: 69)));

		FileDeleteCompletedPayload? completed = WaitFor(MessageKinds.FileDeleteCompleted)
			?.GetPayload<FileDeleteCompletedPayload>();
		Assert.Multiple(() =>
		{
			Assert.That(completed?.Succeeded, Is.False);
			Assert.That(completed?.Error, Does.Contain("in use").IgnoreCase);
			Assert.That(File.Exists(file), Is.True);
		});
	}

	private static void CreateDirectoryJunction(string junction, string target)
	{
		RunCmd($"mklink /J \"{junction}\" \"{target}\"");
	}

	private static void RemoveDirectoryJunction(string junction, string expectedRoot)
	{
		string fullJunction = Path.GetFullPath(junction);
		string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedRoot));
		Assert.That(fullJunction.StartsWith(
			fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), Is.True,
			"the test may remove only its verified junction beneath the disposable fixture root");
		// fsutil deletes only the reparse metadata. The now-ordinary directory entry is empty and can then
		// be removed non-recursively without ever walking the outside target.
		RunProcess("fsutil.exe", $"reparsepoint delete \"{fullJunction}\"");
		Directory.Delete(fullJunction);
	}

	private static void RunCmd(string command)
	{
		RunProcess("cmd.exe", $"/d /c {command}");
	}

	private static void RunProcess(string fileName, string arguments)
	{
		using Process process = Process.Start(new ProcessStartInfo(fileName, arguments)
		{
			CreateNoWindow = true,
			UseShellExecute = false,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
		}) ?? throw new InvalidOperationException("Could not start cmd for the adversarial junction test.");
		string stdout = process.StandardOutput.ReadToEnd();
		string stderr = process.StandardError.ReadToEnd();
		process.WaitForExit();
		Assert.That(process.ExitCode, Is.Zero, $"cmd failed: {stdout} {stderr}");
	}

	private static bool TrySetDirectoryCaseSensitivity(string path, bool enable, out string reason)
	{
		ProcessStartInfo startInfo = new("fsutil.exe")
		{
			CreateNoWindow = true,
			UseShellExecute = false,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
		};
		startInfo.ArgumentList.Add("file");
		startInfo.ArgumentList.Add("SetCaseSensitiveInfo");
		startInfo.ArgumentList.Add(path);
		startInfo.ArgumentList.Add(enable ? "enable" : "disable");
		using Process? process = Process.Start(startInfo);
		if (process is null)
		{
			reason = "fsutil.exe could not be started";
			return false;
		}
		string stdout = process.StandardOutput.ReadToEnd();
		string stderr = process.StandardError.ReadToEnd();
		process.WaitForExit();
		reason = $"{stdout} {stderr}".Trim();
		return process.ExitCode == 0;
	}
}
