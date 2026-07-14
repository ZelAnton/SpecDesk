using System.Diagnostics;
using System.Reflection;
using System.Threading;
using SpecDesk.Contracts;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

// Reproduces S-05: a disk-autosave callback that captured its (path, text) snapshot while the draft
// was still active, but was then queued waiting for _repoGate behind a concurrent "Discard", must not
// write that stale snapshot once it finally gets the gate — Discard has by then already reverted the
// working tree to the published branch, and the stale write would silently resurrect the discarded
// draft's text as an uncommitted change on that published branch.
[TestFixture]
public sealed class HostControllerAutosaveRaceTests
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

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "specdesk-autosave-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _docPath = Path.Combine(_tempDir, "billing.md");
        File.WriteAllText(_docPath, "# Billing (published)");
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Test]
    public void RunDiskAutosave_ClaimedBeforeDiscardFinishesBeforeTheCheckoutCanStart()
    {
        FakeVersioning versioning = new();
        using HostController controller = new(
            StubRender,
            _ => { },
            new NoDialogs(),
            (_, _, _, _, _) => null,
            versioning,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HostController>.Instance,
            _docPath,
            TimeSpan.FromMinutes(10));
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));
        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.EditorChanged, new EditorChangedPayload("# Billing (unsaved draft)")));
        object repoGate = typeof(HostController)
            .GetField("_repoGate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(controller)!;
        FieldInfo leaseField = typeof(HostController)
            .GetField("_documentMutationLeaseClaimed", BindingFlags.NonPublic | BindingFlags.Instance)!;

        Task autosave;
        Monitor.Enter(repoGate);
        try
        {
            autosave = Task.Run(controller.RunDiskAutosave);
            Assert.That(
                SpinUntil(() => (bool)leaseField.GetValue(controller)!, TimeSpan.FromSeconds(2)),
                Is.True);

            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocDiscard));
            Assert.Multiple(() =>
            {
                Assert.That(versioning.DiscardCalled, Is.False);
                Assert.That(File.ReadAllText(_docPath), Is.EqualTo("# Billing (published)"));
            });
        }
        finally
        {
            Monitor.Exit(repoGate);
        }

        Assert.That(autosave.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(File.ReadAllText(_docPath), Is.EqualTo("# Billing (unsaved draft)"));
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocDiscard));
        Assert.Multiple(() =>
        {
            Assert.That(versioning.DiscardCalled, Is.True);
        });
    }
    [Test]
    public void RunDiskAutosave_DuringCheckoutTransitionCannotClaimTheStaleText()
    {
        // Targets the narrower window inside OnDiscard's own _repoGate section: between its bump of
        // _draftGeneration (right after the revert) and _text actually catching up in the later _sync
        // block (that gap now covers "read the reverted file", i.e. real I/O). A snapshot landing in
        // exactly that window must still capture a generation strictly behind the live counter — which
        // is what _textGeneration (tagged onto _text at the point _text was last written, not read live)
        // guarantees. Reproduced directly and deterministically here, without needing real threads: bump
        // _draftGeneration by hand (standing in for "Discard's repoGate section already ran"), leaving
        // _text/_textGeneration/_state exactly as they were before — precisely what a snapshot taken
        // mid-revert would have seen.
        FakeVersioning versioning = new();
        using HostController controller = new(
            StubRender,
            _ => { },
            new NoDialogs(),
            (_, _, _, _, _) => null,
            versioning,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HostController>.Instance,
            _docPath,
            TimeSpan.FromMinutes(10));
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));
        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.EditorChanged, new EditorChangedPayload("# Billing (unsaved draft)")));

        FieldInfo generationField = typeof(HostController)
            .GetField("_draftGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!;
        long before = (long)generationField.GetValue(controller)!;
        // Stand in for "a checkout's _repoGate section already bumped the counter" without touching
        // _text/_textGeneration/_state at all — exactly the torn intermediate state a snapshot taken
        // mid-checkout would observe.
        generationField.SetValue(controller, before + 1);
        FieldInfo transitionField = typeof(HostController)
            .GetField("_documentRepositoryTransition", BindingFlags.NonPublic | BindingFlags.Instance)!;
        transitionField.SetValue(controller, true);
        controller.RunDiskAutosave();
        transitionField.SetValue(controller, false);

        Assert.That(
            File.ReadAllText(_docPath),
            Is.EqualTo("# Billing (published)"),
            "a snapshot generation stale relative to the live counter must not be written, even though " +
            "_text/_state were never reset");
    }

    private static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }
}
