using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpecDesk.AppInfo;
using SpecDesk.Contracts;

namespace SpecDesk.Host;

/// <summary>
/// Persists a webview trace dump beside the rolling log. Mirrors <see cref="LogBridge"/>'s shape: it
/// holds no document/lifecycle state, takes no locks, and the log directory is injected. On a dump it
/// writes a timestamped JSON file and retains the latest payload; <see cref="RenderTail"/> renders the
/// tail of that retained dump at wall-clock times (matching the Serilog timestamp format) for the
/// LogBridge export to append, so the native log and the webview trace sit in one file.
/// </summary>
public sealed class TraceBridge
{
	// Upper bound on the entries kept from one dump — mirrors the webview ring capacity, so a hostile or
	// oversized dump can neither grow memory nor the persisted file without bound.
	private const int MaxEntries = 2000;

	// Host-side cap on a single entry's stringified data (the webview already caps at this) — enforced
	// again here so a hostile RAW frame can't bloat the persisted file or the appended export tail.
	private const int MaxDataChars = 500;

	// PascalCase (the serializer default) — deliberately NOT the camelCase wire naming. This is a
	// standalone human/machine diagnostic file, not a wire frame, so it does not reuse IpcMessage's
	// serializer options; changing it to match the wire would be wrong, not a consistency fix.
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	private readonly ILogger _logger;
	private readonly string _logDirectory;
	private readonly Func<DateTime> _now;

	private TraceDumpPayload? _latest;

	public TraceBridge(ILogger logger, string logDirectory)
		: this(logger, logDirectory, () => DateTime.Now)
	{
	}

	/// <param name="now">Wall clock for the dump-file stamp, injected so tests are deterministic.</param>
	internal TraceBridge(ILogger logger, string logDirectory, Func<DateTime> now)
	{
		ArgumentNullException.ThrowIfNull(logger);
		ArgumentNullException.ThrowIfNull(logDirectory);
		ArgumentNullException.ThrowIfNull(now);
		_logger = logger;
		_logDirectory = logDirectory;
		_now = now;
	}

	/// <summary>Retain the dump (capped + newline-sanitized) and write it as a JSON file beside the log.
	/// Any IO failure is logged and swallowed — a diagnostics dump must never escape into the message
	/// pump (this runs on the same OnMessage path as every other frame).</summary>
	public void Receive(TraceDumpPayload payload)
	{
		ArgumentNullException.ThrowIfNull(payload);
		int received = payload.Entries?.Count ?? 0;
		if (received > MaxEntries)
		{
			_logger.LogWarning(
				"Webview trace dump had {Received} entries; keeping the first {Kept}.", received, MaxEntries);
		}

		TraceDumpPayload retained = Sanitize(payload);
		_latest = retained;

		try
		{
			Directory.CreateDirectory(_logDirectory);
			string stamp = _now().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
			string path = Path.Combine(_logDirectory, $"{AppPaths.LogFilePrefix}trace-{stamp}.json");
			File.WriteAllText(path, JsonSerializer.Serialize(retained, JsonOptions));
			_logger.LogInformation("Webview trace dumped to {Path}", path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// The log/export directory may be unwritable; the retained in-memory dump still feeds
			// RenderTail, so the export tail works even when the JSON file couldn't be written.
			_logger.LogError(ex, "Failed to persist webview trace dump");
		}
	}

	/// <summary>Render the last <paramref name="n"/> entries of the latest retained dump as text lines,
	/// each stamped with wall-clock time (<c>T0Epoch + T</c>) in the Serilog timestamp format so the two
	/// timelines line up. Null when no dump has been received.</summary>
	public string? RenderTail(int n)
	{
		if (_latest is null)
		{
			return null;
		}

		IReadOnlyList<TraceEntryPayload> entries = _latest.Entries;
		int take = Math.Clamp(n, 0, entries.Count);
		StringBuilder sb = new();
		for (int i = entries.Count - take; i < entries.Count; i++)
		{
			TraceEntryPayload entry = entries[i];
			// T0Epoch/T are untrusted doubles straight off the wire; an out-of-range (or NaN/Infinity →
			// long.MinValue) value would throw ArgumentOutOfRangeException out of FromUnixTimeMilliseconds,
			// which would escape all the way to OnMessage's catch-all and silently abort the user's log
			// export. Clamp to the representable range so the tail always renders.
			long millis = Math.Clamp(
				(long)(_latest.T0Epoch + entry.T),
				DateTimeOffset.MinValue.ToUnixTimeMilliseconds(),
				DateTimeOffset.MaxValue.ToUnixTimeMilliseconds());
			DateTime wall = DateTimeOffset.FromUnixTimeMilliseconds(millis).LocalDateTime;
			sb.Append(wall.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
			sb.Append(" [").Append(entry.Cat).Append("] ").Append(entry.Event);
			if (!string.IsNullOrEmpty(entry.Data))
			{
				sb.Append(' ').Append(entry.Data);
			}
			sb.Append('\n');
		}
		return sb.ToString();
	}

	/// <summary>Cap the entry count and strip line breaks from each entry's untrusted fields (the same
	/// guard <see cref="LogBridge.SanitizeForLog"/> applies), so a dumped entry cannot forge extra log
	/// lines when its tail is appended to the exported log.</summary>
	private static TraceDumpPayload Sanitize(TraceDumpPayload payload)
	{
		IReadOnlyList<TraceEntryPayload> source = payload.Entries ?? [];
		int count = Math.Min(source.Count, MaxEntries);
		List<TraceEntryPayload> sanitized = new(count);
		for (int i = 0; i < count; i++)
		{
			TraceEntryPayload entry = source[i];
			string? data = entry.Data is null
				? null
				: LogBridge.SanitizeForLog(
					entry.Data.Length <= MaxDataChars ? entry.Data : entry.Data[..MaxDataChars]);
			sanitized.Add(new TraceEntryPayload(
				entry.Seq,
				entry.T,
				LogBridge.SanitizeForLog(entry.Cat ?? string.Empty),
				LogBridge.SanitizeForLog(entry.Event ?? string.Empty),
				data));
		}
		return new TraceDumpPayload(payload.T0Epoch, payload.FirstSeq, sanitized);
	}
}
