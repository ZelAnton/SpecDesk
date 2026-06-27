using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Photino.NET;
using SpecDesk.Core;
using SpecDesk.Git;
using SpecDesk.Markdown;

namespace SpecDesk.Host;

internal static class Program
{
	// The bundled demo assets, copied into a writable, git-initialized sample repo on first run.
	private static readonly string BundledSamples =
		Path.Combine(AppContext.BaseDirectory, "samples");

	private static ILogger _engineLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

	[STAThread]
	private static void Main()
	{
		using ILoggerFactory loggerFactory = Logging.CreateFactory();
		ILogger startup = loggerFactory.CreateLogger("SpecDesk.Host");
		_engineLogger = loggerFactory.CreateLogger("SpecDesk.Host.ImageEngine");
		startup.LogInformation("SpecDesk starting; logs at {LogDirectory}", Logging.LogDirectory);

		// Captured by the closures below; assigned before any message can arrive (Load is last).
		PhotinoWindow? window = null;

		// PoC-4 seeds a writable, git-versioned sample repo so Edit / Save version work out of the
		// box without ever touching SpecDesk's own working tree.
		IDocumentVersioning versioning = new LibGit2DocumentVersioning();
		string sampleRepo = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"SpecDesk",
			"sample-repo");
		string welcomeDoc = SampleRepo.EnsureSeeded(
			sampleRepo, BundledSamples, versioning, loggerFactory.CreateLogger("SpecDesk.Host.SampleRepo"));

		using HostController controller = new(
			render: Renderer.render,
			// SendWebMessage already marshals onto the UI thread internally, so this is safe to
			// call from the background render task as well as from the message handler.
			send: json => window!.SendWebMessage(json),
			dialogs: new PhotinoFileDialogs(() => window!, loggerFactory.CreateLogger("SpecDesk.Host.Dialogs")),
			inserter: InsertImage,
			versioning: versioning,
			logger: loggerFactory.CreateLogger<HostController>(),
			initialDocPath: welcomeDoc);

		window = new PhotinoWindow()
			.SetTitle("SpecDesk")
			.SetUseOsDefaultSize(false)
			.SetSize(1280, 800)
			.Center()
			// Custom scheme handlers must be registered before the page loads. The asset root
			// follows the open document's repo (null until the first document loads).
			.RegisterCustomSchemeHandler(
				"app",
				(object _, string _, string url, out string contentType) =>
					ServeAsset(controller.RepoRoot, url, out contentType))
			.RegisterWebMessageReceivedHandler((_, message) => controller.OnMessage(message))
			// Resolve relative to the app base directory, not the current working directory: the CWD
			// is wherever the user launches the exe (and a single-file build self-extracts its content
			// to a temp dir), so a CWD-relative path would not find wwwroot/.
			.Load(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"));

		window.WaitForClose();
		startup.LogInformation("SpecDesk closing");
		Serilog.Log.CloseAndFlush();
	}

	// The image rule engine adapter: read the repo's .spectool.toml (if any) and run the F# engine.
	private static string? InsertImage(
		string repoRoot,
		string docPath,
		byte[] bytes,
		string? originalName,
		string? mime)
	{
		string? toml = WorkflowSeeds.TryReadRepoToml(repoRoot);
		ImageEngine.InsertOutcome outcome =
			ImageEngine.insertForHost(repoRoot, docPath, toml, bytes, originalName, mime);
		if (outcome.Error is not null)
		{
			_engineLogger.LogWarning("Image engine rejected the paste: {Error}", outcome.Error);
		}

		return outcome.Markdown;
	}

	// Serve files referenced as app://<authority>/<path> from the current repo root. A rejected
	// (traversal), missing, or unreadable file — or no open document yet — returns Stream.Null, so
	// the resource simply fails to load. This runs as a native WebView2 callback, so it must never
	// let an exception escape into the message pump.
	private static Stream ServeAsset(string? root, string url, out string contentType)
	{
		if (!string.IsNullOrEmpty(root))
		{
			ResolvedAsset? asset = AppAssetResolver.Resolve(root, url);
			if (asset is not null)
			{
				try
				{
					Stream stream = File.OpenRead(asset.FilePath);
					contentType = asset.ContentType;
					return stream;
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					// File vanished between resolution and open (TOCTOU), is locked, or is not
					// readable. Fall through to the broken-resource response below.
				}
			}
		}

		contentType = "text/plain";
		return Stream.Null;
	}
}

/// <summary>Photino-backed native file pickers for <see cref="HostController"/>.</summary>
internal sealed class PhotinoFileDialogs(Func<PhotinoWindow> window, ILogger logger) : IFileDialogs
{
	private static readonly (string Name, string[] Extensions)[] Filters =
	[
		("Markdown", ["*.md", "*.markdown"]),
		("All files", ["*.*"]),
	];

	public string? PickOpenFile() =>
		OnUiThread(
			"ShowOpenFile",
			static w =>
			{
				string[] selection = w.ShowOpenFile("Open spec", string.Empty, false, Filters);
				return selection.Length > 0 ? selection[0] : null;
			});

	public string? PickSaveFile(string? suggestedPath) =>
		OnUiThread(
			"ShowSaveFile",
			w =>
			{
				logger.LogDebug("ShowSaveFile(defaultPath={Path})", suggestedPath);
				string selection = w.ShowSaveFile("Save spec", suggestedPath ?? string.Empty, Filters);
				return string.IsNullOrEmpty(selection) ? null : selection;
			});

	// Native file dialogs require the STA UI thread, but the web-message handler that triggers
	// them may run on a background (MTA) thread. Marshal onto the window's UI thread via Invoke
	// (which runs inline when already there) and block for the modal result.
	private string? OnUiThread(string name, Func<PhotinoWindow, string?> show)
	{
		PhotinoWindow w = window();
		TaskCompletionSource<string?> completion = new();
		w.Invoke(() =>
		{
			try
			{
				completion.SetResult(show(w));
			}
			catch (Exception ex)
			{
				// Log (rather than silently swallow) so a failing dialog is diagnosable, then cancel
				// the operation rather than crashing the UI thread.
				logger.LogError(ex, "Native dialog {Dialog} threw", name);
				completion.SetResult(null);
			}
		});
		return completion.Task.GetAwaiter().GetResult();
	}
}
