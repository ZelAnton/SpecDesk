using Microsoft.Extensions.Logging.Abstractions;

namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class LogBridgeTests
{
    private sealed class StubDialogs : IFileDialogs
    {
        public string? SaveTarget { get; init; }

        public string? PickOpenFile() => null;

        public string? PickSaveFile(string? suggestedPath) => SaveTarget;
    }

    private string _dir = string.Empty;

    [SetUp]
    public void SetUp() =>
        _dir = Path.Combine(Path.GetTempPath(), "specdesk-logtest-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private LogBridge Bridge(IFileDialogs dialogs, Action<string> notify) =>
        new(NullLogger.Instance, dialogs, notify, _dir);

    [Test]
    public void ReadCurrentLog_missingDirectory_returnsPlaceholder()
    {
        LogBridge bridge = Bridge(new StubDialogs(), _ => { });
        Assert.That(bridge.ReadCurrentLog(), Is.EqualTo("(no log directory yet)"));
    }

    [Test]
    public void ReadCurrentLog_noMatchingFiles_returnsPlaceholder()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "unrelated.txt"), "x"); // not a specdesk-*.log
        LogBridge bridge = Bridge(new StubDialogs(), _ => { });
        Assert.That(bridge.ReadCurrentLog(), Is.EqualTo("(no log file yet)"));
    }

    [Test]
    public void ReadCurrentLog_picksNewestByWriteTime_notName()
    {
        Directory.CreateDirectory(_dir);
        // The alphabetically-last file is the OLDER one, so a name-based pick would choose wrong.
        string stale = Path.Combine(_dir, "specdesk-zzz.log");
        string fresh = Path.Combine(_dir, "specdesk-aaa.log");
        File.WriteAllText(stale, "stale");
        File.WriteAllText(fresh, "fresh");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow);
        LogBridge bridge = Bridge(new StubDialogs(), _ => { });
        Assert.That(bridge.ReadCurrentLog(), Is.EqualTo("fresh"));
    }

    [Test]
    public void Export_cancelled_notifiesLogLocation()
    {
        string? notified = null;
        LogBridge bridge = Bridge(new StubDialogs { SaveTarget = null }, m => notified = m);
        bridge.Export();
        Assert.That(notified, Does.Contain("Logs are at").And.Contain(_dir));
    }

    [Test]
    public void Export_writesCurrentLogToDestination_andNotifies()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "specdesk-001.log"), "log-contents");
        string destination = Path.Combine(_dir, "exported.txt"); // not a specdesk-*.log, so not re-read
        string? notified = null;
        LogBridge bridge = Bridge(new StubDialogs { SaveTarget = destination }, m => notified = m);
        bridge.Export();
        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(destination), Is.EqualTo("log-contents"));
            Assert.That(notified, Does.Contain("Log exported to"));
        });
    }
}
