using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class HostControllerLifecycleTests
{
    private sealed class NoDialogs : IFileDialogs
    {
        public string? PickOpenFile() => null;
        public string? PickOpenFolder() => null;

        public string? PickSaveFile(string? suggestedPath) => null;
    }

    private static Renderer.RenderResult StubRender(string docDir, string text) => new(string.Empty, []);

    private string _tempDir = string.Empty;
    private string _docPath = string.Empty;
    private readonly List<string> _sent = [];
    private readonly object _gate = new();

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "specdesk-life-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _docPath = Path.Combine(_tempDir, "billing.md");
        File.WriteAllText(_docPath, "# Billing");
        lock (_gate)
        {
            _sent.Clear();
        }
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private HostController NewController(
        FakeVersioning versioning,
        TimeSpan? autosaveIdle = null,
        WorkspaceStore? workspace = null,
        string? initialDocPath = null)
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
            initialDocPath ?? _docPath,
            autosaveIdle,
            workspace: workspace);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
        return controller;
    }

    [Test]
    public void Edit_BeginsAWorkingBranchAndEmitsDraftStatus()
    {
        FakeVersioning versioning = new();
        using HostController controller = NewController(versioning);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));

        StatusPayload? status = LatestStatus();
        Assert.Multiple(() =>
        {
            Assert.That(versioning.BeginEditCalls, Is.EqualTo(1));
            Assert.That(status, Is.Not.Null);
            Assert.That(status!.State, Is.EqualTo("draft"));
            Assert.That(status.Label, Does.Contain("Draft"));
            Assert.That(status.Branch, Does.StartWith("spec/billing-"));
        });
    }

    [Test]
    public void Ready_EmitsAuthoritativeRepositoryContextForTheOpenDocument()
    {
        FakeVersioning versioning = new() { Branch = "master", DefaultBranchValue = "master" };
        using HostController controller = NewController(versioning);

        WorkspaceContextPayload? context = WaitForKind(MessageKinds.WorkspaceContext)
            ?.GetPayload<WorkspaceContextPayload>();

        Assert.Multiple(() =>
        {
            Assert.That(context, Is.Not.Null);
            Assert.That(context!.Repository, Is.EqualTo(Path.GetFileName(_tempDir)));
            Assert.That(context.RepositoryRoot, Is.EqualTo(_tempDir));
            Assert.That(context.Branch, Is.EqualTo("master"));
            Assert.That(context.BranchState, Is.EqualTo("named"));
            Assert.That(context.DefaultBranch, Is.EqualTo("master"));
            Assert.That(context.Path, Is.EqualTo("billing.md"));
        });
    }

    [Test]
    public void Ready_WhenHeadIsDetached_ReportsDetachedBranchState()
    {
        FakeVersioning versioning = new() { BranchIsDetached = true };
        using HostController controller = NewController(versioning);

        WorkspaceContextPayload? context = WaitForKind(MessageKinds.WorkspaceContext)
            ?.GetPayload<WorkspaceContextPayload>();

        Assert.Multiple(() =>
        {
            Assert.That(context, Is.Not.Null);
            Assert.That(context!.Repository, Is.Not.Null);
            Assert.That(context.Branch, Is.Null);
            Assert.That(context.BranchState, Is.EqualTo("detached"));
        });
    }

    [Test]
    public void Ready_WhenBranchCannotBeRead_ReportsUnavailableBranchState()
    {
        FakeVersioning versioning = new() { ThrowOnBranchInfo = true };
        using HostController controller = NewController(versioning);

        WorkspaceContextPayload? context = WaitForKind(MessageKinds.WorkspaceContext)
            ?.GetPayload<WorkspaceContextPayload>();

        Assert.Multiple(() =>
        {
            Assert.That(context, Is.Not.Null);
            Assert.That(context!.Repository, Is.Not.Null);
            Assert.That(context.Branch, Is.Null);
            Assert.That(context.BranchState, Is.EqualTo("unavailable"));
        });
    }

    [Test]
    public void Ready_WhenDocumentHasNoRepository_ReportsUnavailableBranchState()
    {
        FakeVersioning versioning = new() { Versioned = false };
        using HostController controller = NewController(versioning);

        WorkspaceContextPayload? context = WaitForKind(MessageKinds.WorkspaceContext)
            ?.GetPayload<WorkspaceContextPayload>();

        Assert.Multiple(() =>
        {
            Assert.That(context, Is.Not.Null);
            Assert.That(context!.Repository, Is.Null);
            Assert.That(context.RepositoryRoot, Is.Null);
            Assert.That(context.Branch, Is.Null);
            Assert.That(context.BranchState, Is.EqualTo("unavailable"));
        });
    }

    [Test]
    public void FileInsideVersionedWorkspace_UsesWorkspaceRepoRootRatherThanDocumentFolder()
    {
        string docs = Path.Combine(_tempDir, "docs");
        Directory.CreateDirectory(docs);
        string guide = Path.Combine(docs, "guide.md");
        File.WriteAllText(guide, "# Guide");
        FakeVersioning versioning = new();
        using HostController controller = NewController(versioning);

        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.FolderOpen, new FolderOpenPayload(_tempDir)));
        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.DocOpen, new DocOpenPayload(guide)));

        WorkspaceContextPayload? context = LatestWorkspaceContext();
        Assert.Multiple(() =>
        {
            Assert.That(context, Is.Not.Null);
            Assert.That(context!.RepositoryRoot, Is.EqualTo(_tempDir));
            Assert.That(context.Path, Is.EqualTo("docs/guide.md"));
        });
    }

    [Test]
    public void RestartedController_UsesPersistedCloneRootForInitialNestedDocument()
    {
        string cloneRoot = Path.Combine(_tempDir, "managed-clone");
        string docs = Path.Combine(cloneRoot, "docs");
        Directory.CreateDirectory(docs);
        string guide = Path.Combine(docs, "guide.md");
        File.WriteAllText(guide, "# Guide");
        string storePath = Path.Combine(_tempDir, "workspace.json");
        WorkspaceStore firstRun = new(storePath);
        firstRun.AddRecent(new WorkspaceItem(cloneRoot, "managed-clone", IsFolder: true));
        firstRun.AddRecent(new WorkspaceItem("\0invalid", "invalid", IsFolder: true));

        FakeVersioning versioning = new();
        WorkspaceStore restartedStore = new(storePath);
        using HostController controller = NewController(
            versioning,
            workspace: restartedStore,
            initialDocPath: guide);

        WorkspaceContextPayload? context = LatestWorkspaceContext();
        Assert.Multiple(() =>
        {
            Assert.That(context, Is.Not.Null);
            Assert.That(context!.RepositoryRoot, Is.EqualTo(cloneRoot));
            Assert.That(context.Path, Is.EqualTo("docs/guide.md"));
        });
    }

    [Test]
    public void RestartedController_UsesAnyPersistedRegisteredCloneEvenWhenItIsNoLongerRecent()
    {
        string cloneRoot = Path.Combine(_tempDir, "managed-clone-2");
        string docs = Path.Combine(cloneRoot, "docs");
        Directory.CreateDirectory(docs);
        string guide = Path.Combine(docs, "guide.md");
        File.WriteAllText(guide, "# Guide");
        WorkspaceStore store = new(Path.Combine(_tempDir, "workspace.json"));
        store.RegisterRepo(new RegisteredRepo(
            "octo/specs",
            "octo/specs",
            "https://github.com/octo/specs",
            "main",
            [new RegisteredClone("copy-2", cloneRoot, [])]));

        FakeVersioning versioning = new();
        using HostController controller = NewController(
            versioning,
            workspace: new WorkspaceStore(Path.Combine(_tempDir, "workspace.json")),
            initialDocPath: guide);

        WorkspaceContextPayload? context = LatestWorkspaceContext();
        Assert.Multiple(() =>
        {
            Assert.That(context, Is.Not.Null);
            Assert.That(context!.RepositoryRoot, Is.EqualTo(cloneRoot));
            Assert.That(context.Path, Is.EqualTo("docs/guide.md"));
        });
    }

    [Test]
    public void Ready_WhenDocumentHasNoRepository_ReportsUnavailableContext()
    {
        FakeVersioning versioning = new() { Versioned = false };
        using HostController controller = NewController(versioning);

        WorkspaceContextPayload? context = WaitForKind(MessageKinds.WorkspaceContext)
            ?.GetPayload<WorkspaceContextPayload>();

        Assert.Multiple(() =>
        {
            Assert.That(context, Is.Not.Null);
            Assert.That(context!.Repository, Is.Null);
            Assert.That(context.RepositoryRoot, Is.Null);
            Assert.That(context.Branch, Is.Null);
            Assert.That(context.BranchState, Is.EqualTo("unavailable"));
            Assert.That(context.Path, Is.EqualTo("billing.md"));
        });
    }

    [Test]
    public void Edit_OnAnUnversionedFolder_ReportsAnError()
    {
        FakeVersioning versioning = new() { Versioned = false };
        using HostController controller = NewController(versioning);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));

        Assert.Multiple(() =>
        {
            Assert.That(versioning.BeginEditCalls, Is.EqualTo(0));
            Assert.That(WaitForKind(MessageKinds.Error), Is.Not.Null);
        });
    }

    [Test]
    public void Edit_WhenAnotherDraftsWorkingTreeIsDirty_ReportsAPlainLanguageErrorAndStaysPublished()
    {
        // BeginEdit refuses (DirtyWorkingTreeException) because another document's autosaved-but-not-
        // saved-as-a-version draft is sitting uncommitted on a different branch; a forced checkout would
        // have silently destroyed it. The lifecycle must not advance to Draft, and the author sees a
        // plain-language message — no git vocabulary (branch names) leaks into it.
        FakeVersioning versioning = new() { DirtyBranchToThrow = "spec/other-doc-20260614" };
        using HostController controller = NewController(versioning);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));

        IpcMessage? error = WaitForKind(MessageKinds.Error);
        StatusPayload? status = LatestStatus();
        Assert.Multiple(() =>
        {
            Assert.That(error, Is.Not.Null);
            Assert.That(error!.GetPayload<ErrorPayload>()!.Message, Does.Not.Contain("spec/other-doc-20260614"));
            Assert.That(error!.GetPayload<ErrorPayload>()!.Message, Does.Contain("unsaved changes"));
            // No lifecycle status was ever emitted for this failed attempt — still Published.
            Assert.That(status, Is.Null);
        });
    }

    [Test]
    public void Edit_WithACustomDraftName_UsesTheSanitizedName()
    {
        FakeVersioning versioning = new();
        using HostController controller = NewController(versioning);

        // Backslashes become '/', spaces and stray punctuation become '_', edges trimmed.
        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.DocEdit,
            new EditPayload(@"My New\Draft!")));

        StatusPayload? status = LatestStatus();
        Assert.Multiple(() =>
        {
            Assert.That(versioning.BeginEditCalls, Is.EqualTo(1));
            Assert.That(status!.Branch, Is.EqualTo("My_New/Draft"));
        });
    }

    [Test]
    public void SuggestBranchName_RepliesWithAnEditableNameEchoingTheId()
    {
        FakeVersioning versioning = new();
        using HostController controller = NewController(versioning);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.BranchNameRequest, id: "b-1"));

        IpcMessage? reply = FindKind(MessageKinds.BranchNameSuggested);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.Not.Null);
            Assert.That(reply!.Id, Is.EqualTo("b-1"));
            Assert.That(reply.GetPayload<BranchNameSuggestedPayload>()!.Name, Does.StartWith("spec/billing-"));
        });
    }

    [Test]
    public void TypingWhileDrafting_DoesNotCommitAndShowsUnsavedChanges()
    {
        FakeVersioning versioning = new();
        using HostController controller = NewController(versioning, TimeSpan.FromMilliseconds(20));
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));

        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.EditorChanged,
            new EditorChangedPayload("# Billing v2"),
            version: 1));

        // Typing flips the status to "Unsaved changes" but never commits — committing is explicit.
        StatusPayload? status = LatestStatus();
        Assert.That(status!.Label, Is.EqualTo("Unsaved changes"));

        // Give the idle disk-autosave time to fire and confirm it still did not create a commit.
        Thread.Sleep(80);
        Assert.That(versioning.SaveVersionCalls, Is.EqualTo(0), "typing must not commit");
    }

    [Test]
    public void EditorChanged_NeverRendersOrEmitsPreviewHtmlOnTheHotPath()
    {
        // #preview (the native Markdig render sink) is permanently hidden and has no consumer today
        // (see HostController.Session.cs' OnEditorChanged) — an edit must not trigger a full render or
        // a preview.html emission, no matter how many edits arrive or how long we wait for a stray
        // background task.
        FakeVersioning versioning = new();
        using HostController controller = NewController(versioning);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));

        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.EditorChanged, new EditorChangedPayload("# Billing v2"), version: 1));
        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.EditorChanged, new EditorChangedPayload("# Billing v3"), version: 2));

        Thread.Sleep(80);
        Assert.That(FindKind(MessageKinds.PreviewHtml), Is.Null);
    }

    [Test]
    public void RenderAndSend_WhenCalledDirectly_StillRendersAndEmitsPreviewHtml()
    {
        // RenderAndSend is no longer wired into OnEditorChanged's hot path (see the test above), but it
        // stays `internal` and fully functional — the ready entry point for a future on-demand consumer
        // (diff/comments) to call directly, exercised here the same way RunDiskAutosave's tests call it
        // directly instead of resurrecting the automatic trigger.
        FakeVersioning versioning = new();
        using HostController controller = NewController(versioning);

        controller.RenderAndSend("# Billing v2", version: 1, docDir: string.Empty);

        Assert.That(FindKind(MessageKinds.PreviewHtml), Is.Not.Null);
    }

    [Test]
    public void SaveVersion_CommitsWithTheGeneratedNoteWhenNoneGiven()
    {
        FakeVersioning versioning = new();
        using HostController controller = NewController(versioning);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));
        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.EditorChanged,
            new EditorChangedPayload("# Billing v2"),
            version: 1));

        // Empty note → the host falls back to the generated version note (template "Update {docSlug}").
        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.DocSaveVersion,
            new SaveVersionPayload(string.Empty)));

        StatusPayload? status = LatestStatus();
        Assert.Multiple(() =>
        {
            Assert.That(versioning.SaveVersionCalls, Is.EqualTo(1));
            Assert.That(versioning.LastCommitMessage, Does.Contain("billing"));
            Assert.That(status!.Label, Is.EqualTo("Version saved"));
            Assert.That(status.State, Is.EqualTo("draft"));
        });
    }

    [Test]
    public void SaveVersion_UsesTheAuthorsEditedNote()
    {
        FakeVersioning versioning = new();
        using HostController controller = NewController(versioning);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));

        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.DocSaveVersion,
            new SaveVersionPayload("Clarify the refund rule")));

        Assert.That(versioning.LastCommitMessage, Is.EqualTo("Clarify the refund rule"));
    }

    [Test]
    public void SuggestVersionNote_RepliesWithAnEditableNoteEchoingTheId()
    {
        FakeVersioning versioning = new();
        using HostController controller = NewController(versioning);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));

        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.VersionNoteRequest,
            id: "note-1"));

        IpcMessage? reply = FindKind(MessageKinds.VersionNoteSuggested);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.Not.Null);
            Assert.That(reply!.Id, Is.EqualTo("note-1"));
            Assert.That(reply.GetPayload<VersionNoteSuggestedPayload>()!.Note, Does.Contain("billing"));
        });
    }

    [Test]
    public void Discard_ReturnsToPublished()
    {
        FakeVersioning versioning = new();
        using HostController controller = NewController(versioning);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocDiscard));

        StatusPayload? status = LatestStatus();
        Assert.Multiple(() =>
        {
            Assert.That(versioning.DiscardCalled, Is.True);
            Assert.That(status!.State, Is.EqualTo("published"));
        });
    }

    [Test]
    public void Restart_WithADraftBranchAlreadyCheckedOut_ResumesAsDraftInsteadOfPublished()
    {
        // Regression test for M-16: the lifecycle state lived only in this object's memory, so a restart
        // (crash / force-quit / relaunch) mid-draft always re-stamped the reopened document Published,
        // even though the repo's working tree was still checked out on the draft branch left by the
        // previous, now-gone process — losing track of the draft and, on the next "Edit", forcing a fresh
        // checkout that would silently reset whatever had been autosaved to disk before the restart.
        const string draftBranch = "spec/billing-20260101";
        // Nothing has called BeginEdit on THIS instance — the branch is already checked out as if a
        // previous session (now gone) left it there. NewController fires "ready", simulating this
        // process's very first load of the document.
        FakeVersioning versioning = new() { Branch = draftBranch };
        using HostController controller = NewController(versioning);

        StatusPayload? status = LatestStatus();
        Assert.Multiple(() =>
        {
            Assert.That(status, Is.Not.Null, "the resumed draft must reach the webview as a status update");
            Assert.That(
                status!.State, Is.EqualTo("draft"), "must not falsely report Published after a restart mid-draft");
            Assert.That(status.Branch, Is.EqualTo(draftBranch));
            Assert.That(versioning.BeginEditCalls, Is.EqualTo(0), "resuming a draft must not force a fresh checkout");
        });

        // Clicking "Edit" again must be a no-op (the lifecycle is already Draft) rather than re-running
        // BeginEdit's forced checkout — which would silently reset any autosaved-but-uncommitted content
        // left over from before the restart.
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));

        StatusPayload? afterEditClick = LatestStatus();
        Assert.Multiple(() =>
        {
            Assert.That(
                versioning.BeginEditCalls, Is.EqualTo(0), "Edit from an already-resumed draft must not re-checkout");
            Assert.That(afterEditClick!.State, Is.EqualTo("draft"));
            Assert.That(afterEditClick.Branch, Is.EqualTo(draftBranch));
        });
    }

    [Test]
    public void Restart_WithADetachedHeadCheckedOut_ResumesAsPublishedInsteadOfADraftOnNoBranch()
    {
        // Regression test for R-01: ResolveInitialLifecycle used to detect detached HEAD via
        // `currentBranch is null`, but the real LibGit2DocumentVersioning.CurrentBranch returned
        // libgit2's own "(no branch)" placeholder for a detached HEAD (never null) — that guard was
        // dead, and the resolver mistook "(no branch)" for a genuine (and bogus) draft branch to resume.
        // FakeVersioning.Branch = null simulates the FIXED CurrentBranch contract for a detached HEAD.
        FakeVersioning versioning = new() { Branch = null };
        using HostController controller = NewController(versioning);

        StatusPayload? status = LatestStatus();
        Assert.Multiple(() =>
        {
            Assert.That(
                status is null || status.State == "published",
                "a detached HEAD on first load must resume as Published, never a draft on a fabricated"
                    + " branch name");
            Assert.That(
                versioning.BeginEditCalls, Is.EqualTo(0), "resolving as Published must not force a checkout");
        });
    }

    [Test]
    public void Restart_WithTheLibgit2NoBranchPlaceholderReported_ResumesAsPublishedInsteadOfADraft()
    {
        // Regression test for R-01 (re-review): the previous regression test only exercised
        // `currentBranch is null`, which the OLD buggy guard already handled correctly — it never
        // proved the ALSO-ADDED `or "(no branch)"` guard in ResolveInitialLifecycle does anything. This
        // pins that specific guard: if some IDocumentVersioning implementation ever forwards libgit2's
        // own placeholder verbatim (instead of translating it to null, as the fixed
        // LibGit2DocumentVersioning.CurrentBranch now does), the resolver must still recognize it as
        // "no real branch to resume" rather than mistaking "(no branch)" for a genuine draft branch name.
        // Removing the `or "(no branch)"` disjunct from ResolveInitialLifecycle would fail this test
        // (the fake's literal "(no branch)" would compare unequal to the base branch "main" and the
        // resolver would wrongly resume a "draft" on that fabricated name).
        FakeVersioning versioning = new() { Branch = "(no branch)" };
        using HostController controller = NewController(versioning);

        StatusPayload? status = LatestStatus();
        Assert.Multiple(() =>
        {
            Assert.That(
                status is null || status.State == "published",
                "the libgit2 \"(no branch)\" placeholder must resume as Published, never a draft on a"
                    + " fabricated branch name");
            Assert.That(
                versioning.BeginEditCalls, Is.EqualTo(0), "resolving as Published must not force a checkout");
        });
    }

    [Test]
    public void Ready_FiredAgainWhileADraftIsOpen_DoesNotReloadTheDocumentOrResetTheLifecycle()
    {
        // Regression test for M-15: a WebView2 recovery / page reload re-fires "ready". Before the fix,
        // OnReady unconditionally reloaded _initialDocPath from disk on every "ready", which would
        // discard the author's in-progress draft and re-stamp the document back to Published.
        FakeVersioning versioning = new();
        using HostController controller = NewController(versioning);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));

        StatusPayload? draftStatus = LatestStatus();
        Assert.That(draftStatus!.State, Is.EqualTo("draft"));
        int docLoadedCountBeforeSecondReady = CountKind(MessageKinds.DocLoaded);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));

        StatusPayload? statusAfterSecondReady = LatestStatus();
        Assert.Multiple(() =>
        {
            Assert.That(
                CountKind(MessageKinds.DocLoaded),
                Is.EqualTo(docLoadedCountBeforeSecondReady),
                "a second ready must not reload the document");
            Assert.That(
                statusAfterSecondReady!.State,
                Is.EqualTo("draft"),
                "a second ready must not re-stamp the lifecycle back to Published");
            Assert.That(statusAfterSecondReady.Branch, Is.EqualTo(draftStatus.Branch));
            Assert.That(versioning.BeginEditCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public void Compare_EmitsTheChangedBlocksEchoingTheVersion()
    {
        FakeVersioning versioning = new() { HeadContent = "# Billing" };
        using HostController controller = NewController(versioning);
        // The working copy (head) adds a paragraph below the heading — one "added" block vs the base.
        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.EditorChanged,
            new EditorChangedPayload("# Billing\n\nNew clause.\n"),
            version: 7));

        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.DiffRequest, new DiffRequestPayload(DiffBaseKinds.LastVersion), version: 7));

        IpcMessage? reply = FindKind(MessageKinds.DiffResult);
        Assert.That(reply, Is.Not.Null);
        DiffResultPayload? payload = reply!.GetPayload<DiffResultPayload>();
        Assert.Multiple(() =>
        {
            // The version rides the envelope so the webview can drop a result it has edited past.
            Assert.That(reply!.Version, Is.EqualTo(7));
            Assert.That(payload!.Entries, Is.Not.Empty);
            Assert.That(payload!.Entries[0], Is.InstanceOf<AddedDiffEntry>());
        });
    }

    [Test]
    public void Compare_OnAnUnversionedFolder_ReportsAnErrorAndSendsNoResult()
    {
        FakeVersioning versioning = new() { Versioned = false };
        using HostController controller = NewController(versioning);

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DiffRequest, version: 1));

        Assert.Multiple(() =>
        {
            Assert.That(WaitForKind(MessageKinds.Error), Is.Not.Null);
            Assert.That(FindKind(MessageKinds.DiffResult), Is.Null);
        });
    }

    [Test]
    public void Compare_WithNoCommittedVersion_EmitsAnEmptyResultThatClearsTheOverlay()
    {
        // No committed base (unborn HEAD / never-committed file): an empty diff, not an error — the
        // webview treats an empty result as "clear the overlay".
        FakeVersioning versioning = new() { HeadContent = null };
        using HostController controller = NewController(versioning);
        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.EditorChanged,
            new EditorChangedPayload("# Billing edited\n"),
            version: 2));

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DiffRequest, version: 2));

        IpcMessage? reply = FindKind(MessageKinds.DiffResult);
        Assert.Multiple(() =>
        {
            Assert.That(reply, Is.Not.Null);
            Assert.That(reply!.GetPayload<DiffResultPayload>()!.Entries, Is.Empty);
        });
    }

    [Test]
    public void Compare_AChangedList_EmitsPerChildEntriesThroughTheWire()
    {
        FakeVersioning versioning = new() { HeadContent = "- one\n- two\n- three\n" };
        using HostController controller = NewController(versioning);
        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.EditorChanged,
            new EditorChangedPayload("- one\n- two changed\n- three\n"),
            version: 3));

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DiffRequest, version: 3));

        DiffResultPayload? payload = FindKind(MessageKinds.DiffResult)?.GetPayload<DiffResultPayload>();
        Assert.That(payload, Is.Not.Null);
        // The whole list is one changed entry, but it carries the per-child (item) diff.
        Assert.That(payload!.Entries.Single(), Is.InstanceOf<ChangedDiffEntry>());
        ChangedDiffEntry entry = (ChangedDiffEntry)payload.Entries.Single();
        Assert.That(entry.Children, Has.Count.EqualTo(1));
        Assert.That(entry.Children[0], Is.InstanceOf<ChangedChildDiff>());
        ChangedChildDiff child = (ChangedChildDiff)entry.Children[0];
        Assert.Multiple(() =>
        {
            Assert.That(child.ChildIndex, Is.EqualTo(1)); // the second item
            Assert.That(child.BaseText, Does.Contain("two")); // base item text, for inline word-diff
        });
    }

    [Test]
    public void Compare_AChangedParagraph_CarriesBaseTextForInlineDiff()
    {
        FakeVersioning versioning = new() { HeadContent = "Original wording here today.\n" };
        using HostController controller = NewController(versioning);
        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.EditorChanged,
            new EditorChangedPayload("Original phrasing here today.\n"),
            version: 4));

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DiffRequest, version: 4));

        DiffResultPayload? payload = FindKind(MessageKinds.DiffResult)?.GetPayload<DiffResultPayload>();
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.Entries.Single(), Is.InstanceOf<ChangedDiffEntry>());
        ChangedDiffEntry entry = (ChangedDiffEntry)payload.Entries.Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.Children, Is.Empty); // a plain block — no children
            Assert.That(entry.BaseText, Does.Contain("wording")); // base rendered text (Formatted word-diff)
            Assert.That(entry.BaseSource, Does.Contain("wording")); // base raw source (Code word-diff)
        });
    }

    [Test]
    public void Compare_WithNoPayload_DefaultsToLastVersion()
    {
        // Defensive backward-compat: a malformed/absent payload (the pre-payload wire shape) falls back
        // to the local "last saved version" compare rather than erroring, same as the other Compare_* tests
        // that omit the payload entirely.
        FakeVersioning versioning = new() { HeadContent = "# Billing" };
        using HostController controller = NewController(versioning);
        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.EditorChanged, new EditorChangedPayload("# Billing\n\nNew clause.\n"), version: 8));

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DiffRequest, version: 8));

        DiffResultPayload? payload = FindKind(MessageKinds.DiffResult)?.GetPayload<DiffResultPayload>();
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.Entries, Is.Not.Empty);
    }

    [Test]
    public void Compare_WithAnUnsupportedBase_ReportsAnErrorAndSendsNoResult()
    {
        // "published"/"pr" are reserved for PoC-7 (not implemented yet) — OnCompare must refuse rather
        // than silently diffing against the wrong base.
        FakeVersioning versioning = new() { HeadContent = "# Billing" };
        using HostController controller = NewController(versioning);

        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.DiffRequest, new DiffRequestPayload(DiffBaseKinds.Published), version: 9));

        Assert.Multiple(() =>
        {
            Assert.That(WaitForKind(MessageKinds.Error), Is.Not.Null);
            Assert.That(FindKind(MessageKinds.DiffResult), Is.Null);
        });
    }

    private StatusPayload? LatestStatus()
    {
        lock (_gate)
        {
            for (int i = _sent.Count - 1; i >= 0; i--)
            {
                IpcMessage? message = IpcSerializer.TryDeserialize(_sent[i]);
                if (message is not null && message.Kind == MessageKinds.Status)
                {
                    return message.GetPayload<StatusPayload>();
                }
            }
        }

        return null;
    }

    private WorkspaceContextPayload? LatestWorkspaceContext()
    {
        lock (_gate)
        {
            for (int i = _sent.Count - 1; i >= 0; i--)
            {
                IpcMessage? message = IpcSerializer.TryDeserialize(_sent[i]);
                if (message is not null && message.Kind == MessageKinds.WorkspaceContext)
                {
                    return message.GetPayload<WorkspaceContextPayload>();
                }
            }
        }

        return null;
    }

    private IpcMessage? WaitForKind(string kind)
    {
        return WaitFor(() =>
            {
                lock (_gate)
                {
                    return _sent.Select(IpcSerializer.TryDeserialize).Any(m => m is not null && m.Kind == kind);
                }
            })
            ? FindKind(kind)
            : null;
    }

    private int CountKind(string kind)
    {
        lock (_gate)
        {
            return _sent
                .Select(IpcSerializer.TryDeserialize)
                .Count(m => m is not null && m.Kind == kind);
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

    private static bool WaitFor(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }
}
