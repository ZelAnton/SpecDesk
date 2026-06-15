using System.Globalization;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace SpecDesk.Host;

/// <summary>
/// Application logging: the <see cref="ILogger{T}"/> abstraction backed by Serilog. One rolling
/// file (Debug+) under <c>%LOCALAPPDATA%\SpecDesk\logs</c> plus a console sink (Info+). Native and
/// webview events both flow here (the webview ships log records over IPC), so the file is the one
/// place to look when diagnosing behaviour.
/// </summary>
public static class Logging
{
	/// <summary>Directory holding the rolling log files.</summary>
	public static string LogDirectory { get; } =
		Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"SpecDesk",
			"logs");

	private const string OutputTemplate =
		"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

	/// <summary>
	/// Build the logger factory (caller disposes it on shutdown, which flushes the file sink).
	/// </summary>
	public static ILoggerFactory CreateFactory()
	{
		Directory.CreateDirectory(LogDirectory);

		Log.Logger = new LoggerConfiguration()
			.MinimumLevel.Debug()
			.Enrich.FromLogContext()
			.WriteTo.File(
				Path.Combine(LogDirectory, "specdesk-.log"),
				rollingInterval: RollingInterval.Day,
				retainedFileCountLimit: 7,
				outputTemplate: OutputTemplate,
				formatProvider: CultureInfo.InvariantCulture)
			.WriteTo.Console(
				restrictedToMinimumLevel: LogEventLevel.Information,
				outputTemplate: OutputTemplate,
				formatProvider: CultureInfo.InvariantCulture)
			.CreateLogger();

		return LoggerFactory.Create(builder => builder.AddSerilog(Log.Logger, dispose: true));
	}
}
