using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;

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

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
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

    private LogBridge Bridge(ILogger logger) =>
        new(logger, new StubDialogs(), _ => { }, _dir);

    [Test]
    public void Receive_messageAndDataWithEmbeddedNewlines_producesSingleLogLine()
    {
        RecordingLogger logger = new();
        LogBridge bridge = Bridge(logger);

        bridge.Receive(new LogPayload("error", "line one\nline two\r\nline three", "data\nmore data"));

        Assert.That(logger.Lines, Has.Count.EqualTo(1));
        string line = logger.Lines[0];
        Assert.Multiple(() =>
        {
            Assert.That(line, Does.Not.Contain("\n"));
            Assert.That(line, Does.Not.Contain("\r"));
            Assert.That(line, Does.Contain("line one line two line three"));
            Assert.That(line, Does.Contain("data more data"));
        });
    }

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

    [Test]
    public void Export_writeFails_notifiesPlainMessage_withoutRawExceptionText()
    {
        Directory.CreateDirectory(_dir);
        // A directory path as the destination makes File.WriteAllText throw (UnauthorizedAccessException
        // on Windows), which exercises the failure branch without depending on a specific message shape.
        string? notified = null;
        LogBridge bridge = Bridge(new StubDialogs { SaveTarget = _dir }, m => notified = m);
        bridge.Export();
        Assert.Multiple(() =>
        {
            Assert.That(notified, Is.EqualTo("Could not export the log."));
            Assert.That(notified, Does.Not.Contain(_dir));
        });
    }
}
