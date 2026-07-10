using System.Globalization;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using SpecDesk.AppInfo;

namespace SpecDesk.Host;

/// <summary>
/// Application logging: the <see cref="ILogger{T}"/> abstraction backed by Serilog. One rolling
/// file under the log directory plus a console sink (Info+). Native and webview events both flow
/// here (the webview ships log records over IPC), so the file is the one place to look when
/// diagnosing behaviour.
///
/// The directory and the file sink's minimum level are environment-overridable
/// (<c>SPECDESK_LOG_DIR</c> / <c>SPECDESK_LOG_LEVEL</c>) so a dev run or the E2E harness can point
/// logs at a known location and dial verbosity without a rebuild. The default remains
/// <c>%LOCALAPPDATA%\SpecDesk\logs</c> at Debug.
/// </summary>
public static class Logging
{
	/// <summary>Directory holding the rolling log files: <c>SPECDESK_LOG_DIR</c> if set, else
	/// <see cref="AppPaths.Logs"/>.</summary>
	public static string LogDirectory { get; } = ResolveLogDirectory(Environment.GetEnvironmentVariable);

	// The file sink's minimum level: SPECDESK_LOG_LEVEL if set (and recognised), else Debug.
	private static readonly LogEventLevel FileLevel = ResolveFileLevel(Environment.GetEnvironmentVariable);

	private const string OutputTemplate =
		"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

	/// <summary>
	/// Resolve the log directory from the environment: <c>SPECDESK_LOG_DIR</c> (normalised to an
	/// absolute path) overrides the default <see cref="AppPaths.Logs"/>. Pure over the env accessor so
	/// it is unit-testable without touching process env — and it deliberately does NOT touch
	/// <see cref="AppPaths"/>, whose default path is pinned byte-for-byte by AppPathsTests.
	/// </summary>
	internal static string ResolveLogDirectory(Func<string, string?> getEnv)
	{
		string? overridden = getEnv("SPECDESK_LOG_DIR");
		if (string.IsNullOrWhiteSpace(overridden))
		{
			return AppPaths.Logs;
		}

		try
		{
			return Path.GetFullPath(overridden);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			// A malformed SPECDESK_LOG_DIR (whitespace-only, a null character, an invalid path) must not
			// crash startup before the logger even exists — this runs in the static initializer, ahead of
			// Main's first statement — so fall back to the default rather than throwing out of type init.
			return AppPaths.Logs;
		}
	}

	/// <summary>
	/// Resolve the file sink's minimum level from <c>SPECDESK_LOG_LEVEL</c> (case-insensitive:
	/// verbose/trace, debug, info/information, warn/warning, error, fatal/critical, and off/none/silent
	/// → Fatal to quiet the file to fatal-only). Unset, empty, or unrecognised →
	/// <see cref="LogEventLevel.Debug"/> (the historical default).
	/// </summary>
	internal static LogEventLevel ResolveFileLevel(Func<string, string?> getEnv)
	{
		return getEnv("SPECDESK_LOG_LEVEL")?.Trim().ToLowerInvariant() switch
		{
			"verbose" or "trace" => LogEventLevel.Verbose,
			"debug" => LogEventLevel.Debug,
			"info" or "information" => LogEventLevel.Information,
			"warn" or "warning" => LogEventLevel.Warning,
			"error" => LogEventLevel.Error,
			"fatal" or "critical" => LogEventLevel.Fatal,
			// There is no level below the file sink; "off"/"none"/"silent" quiet it to fatal-only, the
			// closest to disabling it. Unrecognised falls through to the verbose default (fail-to-more-logs).
			"off" or "none" or "silent" => LogEventLevel.Fatal,
			_ => LogEventLevel.Debug,
		};
	}

	/// <summary>
	/// Build the logger factory (caller disposes it on shutdown, which flushes the file sink).
	/// </summary>
	public static ILoggerFactory CreateFactory()
	{
		Directory.CreateDirectory(LogDirectory);

		// The overall floor is the more verbose of the file level and the console's Information, so each
		// sink is then restricted independently: SPECDESK_LOG_LEVEL controls ONLY the file sink and can't
		// suppress console output. At the default (file=Debug) the floor is Debug and this is
		// byte-for-byte the previous behaviour. LogEventLevel is ordered most-verbose-first, so the more
		// verbose floor is the numerically smaller value.
		var globalFloor = (LogEventLevel)Math.Min((int)FileLevel, (int)LogEventLevel.Information);

		Log.Logger = new LoggerConfiguration()
			.MinimumLevel.Is(globalFloor)
			.Enrich.FromLogContext()
			.WriteTo.File(
				Path.Combine(LogDirectory, AppPaths.LogFilePrefix + ".log"),
				restrictedToMinimumLevel: FileLevel,
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
