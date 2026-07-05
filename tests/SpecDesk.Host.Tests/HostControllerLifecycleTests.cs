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

    private HostController NewController(FakeVersioning versioning, TimeSpan? autosaveIdle = null)
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
            _docPath,
            autosaveIdle);
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

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DiffRequest, version: 7));

        IpcMessage? reply = FindKind(MessageKinds.DiffResult);
        Assert.That(reply, Is.Not.Null);
        DiffResultPayload? payload = reply!.GetPayload<DiffResultPayload>();
        Assert.Multiple(() =>
        {
            // The version rides the envelope so the webview can drop a result it has edited past.
            Assert.That(reply!.Version, Is.EqualTo(7));
            Assert.That(payload!.Entries, Is.Not.Empty);
            Assert.That(payload!.Entries[0].Kind, Is.EqualTo("added"));
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
        DiffEntryPayload entry = payload!.Entries.Single();
        Assert.Multiple(() =>
        {
            // The whole list is one changed entry, but it carries the per-child (item) diff.
            Assert.That(entry.Kind, Is.EqualTo("changed"));
            Assert.That(entry.Children, Has.Count.EqualTo(1));
            Assert.That(entry.Children[0].Kind, Is.EqualTo("changed"));
            Assert.That(entry.Children[0].ChildIndex, Is.EqualTo(1)); // the second item
            Assert.That(entry.Children[0].BaseText, Does.Contain("two")); // base item text, for inline word-diff
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
        DiffEntryPayload entry = payload!.Entries.Single();
        Assert.Multiple(() =>
        {
            Assert.That(entry.Kind, Is.EqualTo("changed"));
            Assert.That(entry.Children, Is.Empty); // a plain block — no children
            Assert.That(entry.BaseText, Does.Contain("wording")); // base rendered text (Formatted word-diff)
            Assert.That(entry.BaseSource, Does.Contain("wording")); // base raw source (Code word-diff)
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
