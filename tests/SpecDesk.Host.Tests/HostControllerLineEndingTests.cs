using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

// S-11: CodeMirror's document model normalizes every line break to a bare "\n", so the text the
// webview reports back (EditorChangedPayload) is always LF-only even when the file on disk uses CRLF
// (the Windows installer default of core.autocrlf=true keeps working-tree .md files CRLF). Writing that
// LF text back to disk verbatim rewrites every line ending in the file the moment a single character
// changes — the whole-document diff blast radius the block-splice serializer exists to avoid.
// HostController must detect the document's on-disk line-ending style at load/discard and re-apply it
// at every disk-write site (OnSave, RunDiskAutosave, OnSaveVersion).
[TestFixture]
public sealed class HostControllerLineEndingTests
{
    private sealed class NoDialogs : IFileDialogs
    {
        public string? PickOpenFile() => null;

        public string? PickSaveFile(string? suggestedPath) => null;
    }

    private static Renderer.RenderResult StubRender(string docDir, string text) => new(string.Empty, []);

    private string _tempDir = string.Empty;
    private string _docPath = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "specdesk-eol-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _docPath = Path.Combine(_tempDir, "billing.md");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private HostController NewController(FakeVersioning versioning) =>
        new(
            StubRender,
            _ => { },
            new NoDialogs(),
            (_, _, _, _, _) => null,
            versioning,
            NullLogger<HostController>.Instance,
            _docPath,
            // Long enough that the real timer never fires on its own — these tests drive
            // RunDiskAutosave directly and deterministically, like HostControllerAutosaveRaceTests.
            TimeSpan.FromMinutes(10));

    [Test]
    public void RunDiskAutosave_PreservesCrlfWhenOneLineIsEdited()
    {
        File.WriteAllText(_docPath, "# Billing\r\n\r\nOriginal body.\r\n");
        FakeVersioning versioning = new();
        using HostController controller = NewController(versioning);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));

        // What the webview actually reports: CodeMirror's document model, LF-only, one line edited.
        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.EditorChanged, new EditorChangedPayload("# Billing\n\nEdited body.\n")));

        controller.RunDiskAutosave();

        Assert.That(
            File.ReadAllText(_docPath),
            Is.EqualTo("# Billing\r\n\r\nEdited body.\r\n"),
            "the file's original CRLF style must be preserved, not silently rewritten to LF");
    }

    [Test]
    public void OnSaveVersion_PreservesCrlf()
    {
        File.WriteAllText(_docPath, "# Billing\r\n\r\nOriginal body.\r\n");
        FakeVersioning versioning = new();
        using HostController controller = NewController(versioning);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));
        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.EditorChanged, new EditorChangedPayload("# Billing\n\nEdited body.\n")));

        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.DocSaveVersion, new SaveVersionPayload("Edit body")));

        Assert.That(File.ReadAllText(_docPath), Is.EqualTo("# Billing\r\n\r\nEdited body.\r\n"));
    }

    [Test]
    public void OnSave_PreservesCrlf()
    {
        File.WriteAllText(_docPath, "# Billing\r\n\r\nOriginal body.\r\n");
        FakeVersioning versioning = new();
        using HostController controller = NewController(versioning);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));
        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.EditorChanged, new EditorChangedPayload("# Billing\n\nEdited body.\n")));

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSave));

        Assert.That(File.ReadAllText(_docPath), Is.EqualTo("# Billing\r\n\r\nEdited body.\r\n"));
    }

    [Test]
    public void OnSave_BeforeAnyEditorChanged_DoesNotDoubleUpCr()
    {
        // R-004 regression guard: right after loading (or discarding), `_text` still holds the RAW,
        // possibly-CRLF file content — the webview hasn't reported a (necessarily LF-only) edit yet.
        // Naively replacing "\n" with "\r\n" on that raw text would double every "\r" ("\r\n" → "\r\r\n"),
        // corrupting a CRLF file on a plain, no-op "Save" with no edit at all.
        File.WriteAllText(_docPath, "# Billing\r\n\r\nOriginal body.\r\n");
        FakeVersioning versioning = new();
        using HostController controller = NewController(versioning);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocSave));

        Assert.That(File.ReadAllText(_docPath), Is.EqualTo("# Billing\r\n\r\nOriginal body.\r\n"));
    }

    [Test]
    public void OnSaveVersion_BeforeAnyEditorChanged_DoesNotDoubleUpCr()
    {
        // Same R-004 hazard as above, through the OnSaveVersion write site: "Edit" then "Save a version"
        // immediately, with no keystroke reported by the webview in between.
        File.WriteAllText(_docPath, "# Billing\r\n\r\nOriginal body.\r\n");
        FakeVersioning versioning = new();
        using HostController controller = NewController(versioning);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));

        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.DocSaveVersion, new SaveVersionPayload("No-op save version")));

        Assert.That(File.ReadAllText(_docPath), Is.EqualTo("# Billing\r\n\r\nOriginal body.\r\n"));
    }

    [Test]
    public void Discard_ThenAFreshEdit_RedetectsCrlfFromTheRevertedFile()
    {
        // FakeVersioning.Discard doesn't touch the file on disk, so what LoadFile originally read (CRLF)
        // is still there for OnDiscard's own re-read to re-detect from — proving the style comes from a
        // fresh read of the reverted content, not from whatever the discarded draft's edits implied.
        File.WriteAllText(_docPath, "# Billing\r\n\r\nOriginal body.\r\n");
        FakeVersioning versioning = new();
        using HostController controller = NewController(versioning);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));
        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.EditorChanged, new EditorChangedPayload("# Billing\n\nEdited body.\n"), version: 1));

        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocDiscard));
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));
        // A strictly higher version — the render coordinator drops a same-or-stale version as a
        // duplicate, so a second edit in the same test must advance it, just like a real second keystroke
        // (whose CodeMirror version counter only ever goes up) would.
        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.EditorChanged, new EditorChangedPayload("# Billing\n\nEdited again.\n"), version: 2));
        controller.RunDiskAutosave();

        Assert.That(File.ReadAllText(_docPath), Is.EqualTo("# Billing\r\n\r\nEdited again.\r\n"));
    }

    [Test]
    public void LfDocument_StaysLfAfterAnEdit()
    {
        File.WriteAllText(_docPath, "# Billing\n\nOriginal body.\n");
        FakeVersioning versioning = new();
        using HostController controller = NewController(versioning);
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));
        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.EditorChanged, new EditorChangedPayload("# Billing\n\nEdited body.\n")));

        controller.RunDiskAutosave();

        Assert.That(File.ReadAllText(_docPath), Is.EqualTo("# Billing\n\nEdited body.\n"));
    }

    [TestCase("", "\n")]
    [TestCase("no newlines at all", "\n")]
    [TestCase("a\nb\nc\n", "\n")]
    [TestCase("a\r\nb\r\nc\r\n", "\r\n")]
    [TestCase("a\r\nb\r\nc\n", "\r\n")] // CRLF strictly more common → CRLF wins
    [TestCase("a\nb\r\nc\n", "\n")] // LF strictly more common → LF wins
    [TestCase("a\r\nb\n", "\n")] // tied → the plain "\n" default
    public void DetectLineEnding_PicksTheDominantStyle(string raw, string expected)
    {
        Assert.That(HostController.DetectLineEnding(raw), Is.EqualTo(expected));
    }
}
