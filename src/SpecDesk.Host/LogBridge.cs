using Microsoft.Extensions.Logging;
using SpecDesk.Contracts;

namespace SpecDesk.Host;

/// <summary>
/// The diagnostic log channel, lifted out of <see cref="HostController"/>: forwards webview-originated
/// log lines to the host logger, and exports the current rolling log file on request. It holds no
/// document/lifecycle state and takes no locks, so it is independent of the controller's threading. The
/// log directory is injected (rather than read from the static <c>Logging.LogDirectory</c>) so the
/// rolling-file selection is unit-testable.
/// </summary>
public sealed class LogBridge
{
	private readonly ILogger _logger;
	private readonly IFileDialogs _dialogs;
	private readonly Action<string> _notify;
	private readonly string _logDirectory;

	/// <param name="notify">Surface a plain status/notice line to the author (the host wires this to its
	/// error/status channel; the export outcome is reported through it).</param>
	public LogBridge(ILogger logger, IFileDialogs dialogs, Action<string> notify, string logDirectory)
	{
		ArgumentNullException.ThrowIfNull(logger);
		ArgumentNullException.ThrowIfNull(dialogs);
		ArgumentNullException.ThrowIfNull(notify);
		ArgumentNullException.ThrowIfNull(logDirectory);
		_logger = logger;
		_dialogs = dialogs;
		_notify = notify;
		_logDirectory = logDirectory;
	}

	/// <summary>Forward a webview log line to the host logger at the mapped level.</summary>
	public void Receive(LogPayload payload)
	{
		ArgumentNullException.ThrowIfNull(payload);
		LogLevel level = payload.Level switch
		{
			"error" => LogLevel.Error,
			"warn" => LogLevel.Warning,
			"info" => LogLevel.Information,
			_ => LogLevel.Debug,
		};

		string message = SanitizeForLog(payload.Message);
		if (payload.Data is null)
		{
			_logger.Log(level, "[webview] {Message}", message);
		}
		else
		{
			_logger.Log(level, "[webview] {Message} {Data}", message, SanitizeForLog(payload.Data));
		}
	}

	/// <summary>Strip line breaks from an untrusted webview-supplied field before it reaches the log
	/// template, so a payload cannot forge extra log entries by embedding CR/LF sequences.</summary>
	private static string SanitizeForLog(string value) =>
		value.Replace("\r\n", " ", StringComparison.Ordinal)
			.Replace('\r', ' ')
			.Replace('\n', ' ');

	/// <summary>Offer to save the current log file elsewhere. Reports the outcome (cancelled / exported /
	/// failed) via the notify callback. This also exercises the save dialog, whose exception (if any) the
	/// dialog layer logs.</summary>
	public void Export()
	{
		string? destination = _dialogs.PickSaveFile(Path.Combine(_logDirectory, "specdesk-export.log"));
		if (destination is null)
		{
			_notify($"Logs are at {_logDirectory}");
			return;
		}

		try
		{
			File.WriteAllText(destination, ReadCurrentLog());
			_logger.LogInformation("Exported log to {Path}", destination);
			_notify($"Log exported to {destination}");
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_logger.LogError(ex, "Failed to export log to {Path}", destination);
			_notify("Could not export the log.");
		}
	}

	/// <summary>Read the newest rolling log file with shared access (Serilog keeps it open for writing),
	/// or a plain placeholder when there is no directory / no log file yet.</summary>
	public string ReadCurrentLog()
	{
		if (!Directory.Exists(_logDirectory))
		{
			return "(no log directory yet)";
		}

		string[] files = Directory.GetFiles(_logDirectory, "specdesk-*.log");
		if (files.Length == 0)
		{
			return "(no log file yet)";
		}

		string newest = files.OrderBy(File.GetLastWriteTimeUtc).Last();
		using FileStream stream = new(newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
		using StreamReader reader = new(stream);
		return reader.ReadToEnd();
	}
}
