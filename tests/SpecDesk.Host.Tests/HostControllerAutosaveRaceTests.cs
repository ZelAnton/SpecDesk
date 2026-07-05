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
    public void RunDiskAutosave_QueuedBehindADiscardThatAlreadyCompleted_SkipsTheStaleWrite()
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
            // Long enough that the real timer never fires on its own — this test drives
            // RunDiskAutosave directly and deterministically instead.
            TimeSpan.FromMinutes(10));
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
        controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocEdit));
        controller.OnMessage(IpcSerializer.SerializeEvent(
            MessageKinds.EditorChanged, new EditorChangedPayload("# Billing (unsaved draft)")));

        // Grab the private _repoGate object so the test can hold it itself, standing in for the
        // "an image insert holds _repoGate" trigger from the finding — this is what forces the
        // autosave callback below to actually block rather than racing straight through.
        object repoGate = typeof(HostController)
            .GetField("_repoGate", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(controller)!;

        Task autosave;
        Monitor.Enter(repoGate);
        try
        {
            // Fire the "autosave" callback on its own thread: it takes its (path, text, generation)
            // snapshot under _sync (uncontended — the test thread holds _repoGate, not _sync) and then
            // blocks trying to enter _repoGate, exactly like the real timer callback would.
            autosave = Task.Run(controller.RunDiskAutosave);

            // Bounded wait for the background thread to reach that blocking point. The _sync snapshot
            // is a handful of uncontended field reads, so this margin is generous, not a tight race.
            Assert.That(SpinUntil(() => autosave.Status == TaskStatus.Running, TimeSpan.FromSeconds(2)), Is.True);
            Thread.Sleep(50);

            // "Discard" now runs on the SAME thread that holds _repoGate: .NET monitors are reentrant
            // per-thread, so its own `lock (_repoGate)` section proceeds immediately (no blocking) while
            // the autosave task above stays queued — reproducing "Discard wins the race for the gate".
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.DocDiscard));

            Assert.That(versioning.DiscardCalled, Is.True);
            // Discard's own repo access has already completed while the autosave task is still queued
            // on _repoGate — the file still holds what LoadFile (re-)read from disk (the published
            // content), since the stale autosave write has not happened yet.
            Assert.That(File.ReadAllText(_docPath), Is.EqualTo("# Billing (published)"));
        }
        finally
        {
            // Release _repoGate: the queued autosave callback can now finally proceed.
            Monitor.Exit(repoGate);
        }

        // Bounded wait, not a hang: a wiring bug (the fix regressing) must fail the test, not hang it.
        Assert.That(autosave.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(
            File.ReadAllText(_docPath),
            Is.EqualTo("# Billing (published)"),
            "the stale draft snapshot must not have been written over the reverted, published file");
    }

    [Test]
    public void RunDiskAutosave_SnapshotTakenWhileACheckoutHasBumpedButNotYetResetText_SkipsTheStaleWrite()
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

        controller.RunDiskAutosave();

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
