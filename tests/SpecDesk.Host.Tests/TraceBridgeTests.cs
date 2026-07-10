using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;

namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class TraceBridgeTests
{
	private string _dir = string.Empty;

	[SetUp]
	public void SetUp() =>
		_dir = Path.Combine(Path.GetTempPath(), "specdesk-tracetest-" + Guid.NewGuid().ToString("N"));

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(_dir))
		{
			Directory.Delete(_dir, recursive: true);
		}
	}

	private TraceBridge Bridge() => new(NullLogger.Instance, _dir);

	private static TraceDumpPayload Dump(params TraceEntryPayload[] entries) =>
		new(0, 0, entries);

	[Test]
	public void Receive_WritesATimestampedJsonFileBesideTheLog()
	{
		Bridge().Receive(Dump(new TraceEntryPayload(0, 0, "scroll", "scroll.write", "{\"line\":5}")));

		string[] files = Directory.GetFiles(_dir, AppInfo.AppPaths.LogFilePrefix + "trace-*.json");
		Assert.That(files, Has.Length.EqualTo(1));
		Assert.That(File.ReadAllText(files[0]), Does.Contain("scroll.write"));
	}

	[Test]
	public void RenderTail_StampsEachEntryAtWallClock_T0EpochPlusT()
	{
		long t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
		TraceBridge bridge = Bridge();
		bridge.Receive(new TraceDumpPayload(
			t0, 0, [new TraceEntryPayload(0, 250, "scroll", "scroll.write", "{\"line\":5}")]));

		string? tail = bridge.RenderTail(10);

		string expectedStamp = DateTimeOffset
			.FromUnixTimeMilliseconds(t0 + 250)
			.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
		Assert.That(tail, Is.Not.Null);
		Assert.That(tail!.TrimEnd('\n'), Is.EqualTo($"{expectedStamp} [scroll] scroll.write {{\"line\":5}}"));
	}

	[Test]
	public void RenderTail_WithNoDumpReceived_ReturnsNull()
	{
		Assert.That(Bridge().RenderTail(10), Is.Null);
	}

	[Test]
	public void RenderTail_HostileTimestamp_DoesNotThrow_AndStillRenders()
	{
		TraceBridge bridge = Bridge();
		// A numeric-but-out-of-range t0Epoch deserializes fine (only non-numeric is rejected upstream);
		// without the clamp it would throw out of FromUnixTimeMilliseconds and silently abort the export.
		bridge.Receive(new TraceDumpPayload(3e14, 0, [new TraceEntryPayload(0, 0, "x", "e", null)]));

		string? tail = null;
		Assert.DoesNotThrow(() => tail = bridge.RenderTail(10));
		Assert.That(tail, Is.Not.Null.And.Contains("[x] e"));
	}

	[Test]
	public void Receive_StampsTheFilenameWithTheInjectedClock()
	{
		DateTime fixedNow = new(2026, 3, 4, 5, 6, 7);
		TraceBridge bridge = new(NullLogger.Instance, _dir, () => fixedNow);
		bridge.Receive(Dump(new TraceEntryPayload(0, 0, "x", "e", null)));

		string[] files = Directory.GetFiles(_dir, AppInfo.AppPaths.LogFilePrefix + "trace-*.json");
		Assert.That(files, Has.Length.EqualTo(1));
		Assert.That(Path.GetFileName(files[0]), Does.Contain("trace-20260304-050607"));
	}

	[Test]
	public void Receive_OverlongData_IsCappedToTheHostLimit()
	{
		TraceBridge bridge = Bridge();
		// The host re-caps a raw frame's data at 500 chars (defence-in-depth; the webview also caps it),
		// so neither the rendered tail nor the persisted file carries the full 5000-char run.
		bridge.Receive(Dump(new TraceEntryPayload(0, 0, "x", "e", new string('a', 5000))));

		Assert.That(bridge.RenderTail(10) ?? "", Does.Not.Contain(new string('a', 501)));
	}

	[Test]
	public void Receive_OversizedDump_CapsAtTheRingSize_WithoutThrowing()
	{
		TraceEntryPayload[] entries = Enumerable
			.Range(0, 2500)
			.Select(i => new TraceEntryPayload(i, i, "x", "e", null))
			.ToArray();
		TraceBridge bridge = Bridge();

		Assert.DoesNotThrow(() => bridge.Receive(Dump(entries)));

		int lines = (bridge.RenderTail(10_000) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
		Assert.That(lines, Is.EqualTo(2000));
	}

	[Test]
	public void Receive_EmbeddedNewlines_AreStrippedSoOneEntryIsOneLine()
	{
		TraceBridge bridge = Bridge();
		bridge.Receive(Dump(new TraceEntryPayload(0, 0, "cat", "ev\nil", "da\r\nta")));

		string tail = bridge.RenderTail(10) ?? "";
		// A single entry must render as a single line — embedded newlines can't forge extra lines.
		Assert.That(tail.TrimEnd('\n'), Does.Not.Contain("\n"));
		Assert.That(tail, Does.Contain("ev il"));
		Assert.That(tail, Does.Contain("da ta"));

		string[] files = Directory.GetFiles(_dir, AppInfo.AppPaths.LogFilePrefix + "trace-*.json");
		Assert.That(File.ReadAllText(files[0]), Does.Contain("ev il"));
	}
}
