using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

/// <summary>
/// The workspace-state slice of the controller (HostController.Workspace.cs): serving the persisted state
/// (workspace.request → workspace.state), recording recents as a side effect of opening a file/folder,
/// toggling favorites, and registering/unregistering GitHub repos over the wire — plus the pure GitHub-URL
/// parser the register handler uses. Driven with a real temporary workspace.json.
/// </summary>
[TestFixture]
public sealed class HostControllerWorkspaceTests
{
	private sealed class NoDialogs : IFileDialogs
	{
		public string? PickOpenFile() => null;
		public string? PickOpenFolder() => null;
		public string? PickSaveFile(string? suggestedPath) => null;
	}

	private static Renderer.RenderResult StubRender(string docDir, string text) => new(string.Empty, []);

	private string _root = string.Empty;
	private string _wsPath = string.Empty;
	private readonly List<string> _sent = [];
	private readonly object _gate = new();

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(Path.GetTempPath(), "specdesk-wsc-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_root);
		_wsPath = Path.Combine(_root, "workspace.json");
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
			new NoDialogs(),
			(_, _, _, _, _) => null,
			new FakeVersioning(),
			NullLogger<HostController>.Instance,
			initialDocPath: null,
			workspace: new WorkspaceStore(_wsPath));
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
		lock (_gate)
		{
			_sent.Clear();
		}
		return controller;
	}

	// The most recently emitted workspace.state payload (mutations re-emit it, so tests want the latest).
	private WorkspaceStatePayload? LatestState()
	{
		lock (_gate)
		{
			WorkspaceStatePayload? latest = null;
			foreach (string json in _sent)
			{
				IpcMessage? message = IpcSerializer.TryDeserialize(json);
				if (message is not null && message.Kind == MessageKinds.WorkspaceState)
				{
					latest = message.GetPayload<WorkspaceStatePayload>();
				}
			}

			return latest;
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

	private string WriteDoc(string name)
	{
		string path = Path.Combine(_root, name);
		File.WriteAllText(path, "# " + name);
		return path;
	}

	[Test]
	public void WorkspaceRequest_EmitsTheCurrentState()
	{
		using HostController controller = NewController();

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.WorkspaceRequest));

		WorkspaceStatePayload? state = LatestState();
		Assert.That(state, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(state!.Recent, Is.Empty);
			Assert.That(state.Favorites, Is.Empty);
			Assert.That(state.Repositories, Is.Empty);
		});
	}

	[Test]
	public void TheAutoOpenedWelcomeDoc_IsNotRecorded_ButAUserOpenIs()
	{
		string welcome = WriteDoc("welcome.md");
		void Send(string json)
		{
			lock (_gate)
			{
				_sent.Add(json);
			}
		}

		using HostController controller = new(
			StubRender,
			Send,
			new NoDialogs(),
			(_, _, _, _, _) => null,
			new FakeVersioning(),
			NullLogger<HostController>.Instance,
			initialDocPath: welcome,
			workspace: new WorkspaceStore(_wsPath));
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready)); // auto-opens welcome.md

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.WorkspaceRequest));
		Assert.That(LatestState()!.Recent, Is.Empty, "the auto-opened welcome doc must not be a recent");

		// A file the author explicitly opens IS recorded.
		string chosen = WriteDoc("chosen.md");
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload(chosen)));
		string[] expected = [chosen];
		Assert.That(LatestState()!.Recent.Select(r => r.Path), Is.EqualTo(expected));
	}

	[Test]
	public void OpeningAFile_RecordsItAsARecent()
	{
		using HostController controller = NewController();
		string doc = WriteDoc("spec.md");

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload(doc)));

		WorkspaceStatePayload? state = LatestState();
		Assert.That(state, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(state!.Recent, Has.Count.EqualTo(1));
			Assert.That(state.Recent[0].Path, Is.EqualTo(doc));
			Assert.That(state.Recent[0].Label, Is.EqualTo("spec.md"));
			Assert.That(state.Recent[0].IsFolder, Is.False);
		});
	}

	[Test]
	public void OpeningAFolder_RecordsItAsARecentFolder()
	{
		using HostController controller = NewController();
		string folder = Path.Combine(_root, "specs");
		Directory.CreateDirectory(folder);

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.FolderOpen, new FolderOpenPayload(folder)));

		WorkspaceStatePayload? state = LatestState();
		Assert.That(state, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(state!.Recent, Has.Count.EqualTo(1));
			Assert.That(state.Recent[0].Path, Is.EqualTo(Path.GetFullPath(folder)));
			Assert.That(state.Recent[0].IsFolder, Is.True);
		});
	}

	[Test]
	public void OpeningTheSameFileTwice_DedupesToOneRecent()
	{
		using HostController controller = NewController();
		string a = WriteDoc("a.md");
		string b = WriteDoc("b.md");

		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload(a)));
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload(b)));
		controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload(a)));

		WorkspaceStatePayload? state = LatestState();
		Assert.That(state, Is.Not.Null);
		// Re-opening a.md moves it to the front; it is not duplicated. Most-recent first: [a, b].
		string[] expected = [a, b];
		Assert.That(state!.Recent.Select(r => r.Path), Is.EqualTo(expected));
	}

	[Test]
	public void OpeningManyFiles_CapsRecentsAtTwenty()
	{
		using HostController controller = NewController();
		for (int i = 0; i < 25; i++)
		{
			string doc = WriteDoc($"f{i}.md");
			controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocOpen, new DocOpenPayload(doc)));
		}

		WorkspaceStatePayload? state = LatestState();
		Assert.That(state, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(state!.Recent, Has.Count.EqualTo(20));
			// The most recent (f24) is first; the five oldest (f0..f4) fell off the tail.
			Assert.That(state.Recent[0].Label, Is.EqualTo("f24.md"));
			Assert.That(state.Recent.Select(r => r.Label), Has.None.EqualTo("f0.md"));
		});
	}

	[Test]
	public void FavoriteToggle_AddsThenRemoves()
	{
		using HostController controller = NewController();
		string folder = Path.Combine(_root, "faves");
		Directory.CreateDirectory(folder);

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.WorkspaceFavorite, new WorkspaceFavoritePayload(folder, Favorite: true)));
		WorkspaceStatePayload? added = LatestState();
		Assert.That(added, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(added!.Favorites, Has.Count.EqualTo(1));
			Assert.That(added.Favorites[0].Path, Is.EqualTo(folder));
			// The host reconstructs the label and the folder flag from the path.
			Assert.That(added.Favorites[0].Label, Is.EqualTo("faves"));
			Assert.That(added.Favorites[0].IsFolder, Is.True);
		});

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.WorkspaceFavorite, new WorkspaceFavoritePayload(folder, Favorite: false)));
		Assert.That(LatestState()!.Favorites, Is.Empty);
	}

	[Test]
	public void RegisterRepo_WithAValidUrl_StoresANormalizedEntry()
	{
		using HostController controller = NewController();

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.RepoRegister, new RegisterRepoPayload("https://github.com/octo/spec-repo")));

		WorkspaceStatePayload? state = LatestState();
		Assert.That(state, Is.Not.Null);
		Assert.Multiple(() =>
		{
			Assert.That(Find(MessageKinds.Error), Is.Null);
			Assert.That(state!.Repositories, Has.Count.EqualTo(1));
			Assert.That(state.Repositories[0].Id, Is.EqualTo("octo/spec-repo"));
			Assert.That(state.Repositories[0].Name, Is.EqualTo("octo/spec-repo"));
			Assert.That(state.Repositories[0].Url, Is.EqualTo("https://github.com/octo/spec-repo"));
		});
	}

	[Test]
	public void RegisterRepo_WithAnInvalidUrl_EmitsAnErrorAndStoresNothing()
	{
		using HostController controller = NewController();

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.RepoRegister, new RegisterRepoPayload("just-a-string")));

		Assert.Multiple(() =>
		{
			Assert.That(Find(MessageKinds.Error)?.GetPayload<ErrorPayload>()?.Message, Is.Not.Null.And.Not.Empty);
			// No workspace.state is emitted for a rejected register (nothing changed), so the store stays empty.
			Assert.That(LatestState(), Is.Null);
		});
	}

	[Test]
	public void UnregisterRepo_RemovesAPreviouslyRegisteredRepo()
	{
		using HostController controller = NewController();
		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.RepoRegister, new RegisterRepoPayload("owner/name")));
		Assert.That(LatestState()!.Repositories, Has.Count.EqualTo(1));

		controller.OnMessage(IpcSerializer.SerializeEvent(
			MessageKinds.RepoUnregister, new UnregisterRepoPayload("owner/name")));

		Assert.That(LatestState()!.Repositories, Is.Empty);
	}

	[TestCase("https://github.com/octo/spec-repo", "octo", "spec-repo")]
	[TestCase("https://github.com/octo/spec-repo.git", "octo", "spec-repo")]
	[TestCase("https://github.com/octo/spec-repo/", "octo", "spec-repo")]
	[TestCase("octo/spec-repo", "octo", "spec-repo")]
	[TestCase("git@github.com:octo/spec-repo.git", "octo", "spec-repo")]
	[TestCase("  github.com/octo/spec-repo  ", "octo", "spec-repo")]
	public void TryParseGitHubRepo_AcceptsTheSupportedForms(string input, string owner, string name)
	{
		Assert.That(HostController.TryParseGitHubRepo(input, out string parsedOwner, out string parsedName), Is.True);
		Assert.Multiple(() =>
		{
			Assert.That(parsedOwner, Is.EqualTo(owner));
			Assert.That(parsedName, Is.EqualTo(name));
		});
	}

	[TestCase("")]
	[TestCase("   ")]
	[TestCase("just-a-string")]
	[TestCase("https://github.com/octo/spec-repo/tree/main")]
	[TestCase("https://example.com/octo/spec-repo")]
	[TestCase("octo/")]
	[TestCase("/spec-repo")]
	// A non-GitHub host must NOT be mis-read as the owner (the host is anchored to github.com; an owner can't
	// contain the dots a hostname does).
	[TestCase("https://gitlab.com/some-user")]
	[TestCase("http://example.com/owner")]
	[TestCase("https://youtube.com/watch")]
	[TestCase("gitlab.com/some-user")]
	public void TryParseGitHubRepo_RejectsAnythingElse(string input)
	{
		Assert.That(HostController.TryParseGitHubRepo(input, out _, out _), Is.False);
	}
}
