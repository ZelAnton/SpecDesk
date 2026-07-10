using Serilog.Events;
using SpecDesk.AppInfo;

namespace SpecDesk.Host.Tests;

// The log directory and file-sink level are environment-overridable (SPECDESK_LOG_DIR /
// SPECDESK_LOG_LEVEL). These pin the pure resolvers — over an injected env accessor, so no process
// env is mutated — and confirm the unset/empty/unrecognised cases fall back to the historical
// defaults (AppPaths.Logs at Debug), which keeps AppPathsTests' pinned default path untouched.
[TestFixture]
public sealed class LoggingTests
{
	[Test]
	public void ResolveLogDirectory_Unset_ReturnsAppPathsLogs()
	{
		Assert.That(Logging.ResolveLogDirectory(_ => null), Is.EqualTo(AppPaths.Logs));
	}

	[Test]
	public void ResolveLogDirectory_Empty_ReturnsAppPathsLogs()
	{
		Assert.That(Logging.ResolveLogDirectory(_ => ""), Is.EqualTo(AppPaths.Logs));
	}

	[TestCase("   ")]
	[TestCase("\t")]
	public void ResolveLogDirectory_WhitespaceOnly_FallsBackToDefault_WithoutThrowing(string raw)
	{
		// Whitespace-only would reach Path.GetFullPath (IsNullOrEmpty is false) and throw out of the
		// static initializer, crashing startup before the logger exists — must fall back instead.
		Assert.That(Logging.ResolveLogDirectory(_ => raw), Is.EqualTo(AppPaths.Logs));
	}

	[Test]
	public void ResolveLogDirectory_Malformed_FallsBackToDefault_WithoutThrowing()
	{
		Assert.That(Logging.ResolveLogDirectory(_ => "bad\0path"), Is.EqualTo(AppPaths.Logs));
	}

	[Test]
	public void ResolveLogDirectory_Set_ReturnsTheOverrideAsAnAbsolutePath()
	{
		string resolved = Logging.ResolveLogDirectory(key => key == "SPECDESK_LOG_DIR" ? "logs/here" : null);

		Assert.That(resolved, Is.EqualTo(Path.GetFullPath("logs/here")));
		Assert.That(Path.IsPathRooted(resolved), Is.True);
	}

	[TestCase(null, LogEventLevel.Debug)]
	[TestCase("", LogEventLevel.Debug)]
	[TestCase("nonsense", LogEventLevel.Debug)]
	[TestCase("verbose", LogEventLevel.Verbose)]
	[TestCase("TRACE", LogEventLevel.Verbose)]
	[TestCase("debug", LogEventLevel.Debug)]
	[TestCase("Info", LogEventLevel.Information)]
	[TestCase("information", LogEventLevel.Information)]
	[TestCase(" warn ", LogEventLevel.Warning)]
	[TestCase("Warning", LogEventLevel.Warning)]
	[TestCase("error", LogEventLevel.Error)]
	[TestCase("fatal", LogEventLevel.Fatal)]
	[TestCase("critical", LogEventLevel.Fatal)]
	[TestCase("off", LogEventLevel.Fatal)]
	[TestCase("none", LogEventLevel.Fatal)]
	[TestCase("silent", LogEventLevel.Fatal)]
	public void ResolveFileLevel_MapsRecognisedNamesCaseInsensitively_ElseDebug(
		string? raw,
		LogEventLevel expected)
	{
		LogEventLevel level = Logging.ResolveFileLevel(key => key == "SPECDESK_LOG_LEVEL" ? raw : null);

		Assert.That(level, Is.EqualTo(expected));
	}
}
