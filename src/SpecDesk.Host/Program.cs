using Photino.NET;
using SpecDesk.Core;
using SpecDesk.Markdown;

namespace SpecDesk.Host;

internal static class Program
{
	// The demo document opened on launch; its directory becomes the repo root (and app:// root).
	private static readonly string WelcomeDoc =
		Path.Combine(AppContext.BaseDirectory, "samples", "welcome.md");

	[STAThread]
	private static void Main()
	{
		// Captured by the closures below; assigned before any message can arrive (Load is last).
		PhotinoWindow? window = null;

		HostController controller = new(
			render: Renderer.render,
			// SendWebMessage already marshals onto the UI thread internally, so this is safe to
			// call from the background render task as well as from the message handler.
			send: json => window!.SendWebMessage(json),
			dialogs: new PhotinoFileDialogs(() => window!),
			inserter: InsertImage,
			initialDocPath: WelcomeDoc);

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
			.Load("wwwroot/index.html");

		window.WaitForClose();
	}

	// The image rule engine adapter: read the repo's .spectool.toml (if any) and run the F# engine.
	private static string? InsertImage(
		string repoRoot,
		string docPath,
		byte[] bytes,
		string? originalName,
		string? mime)
	{
		string? toml = TryReadToml(Path.Combine(repoRoot, ".spectool.toml"));
		ImageEngine.InsertOutcome outcome =
			ImageEngine.insertForHost(repoRoot, docPath, toml, bytes, originalName, mime);
		return outcome.Markdown;
	}

	private static string? TryReadToml(string path)
	{
		try
		{
			return File.Exists(path) ? File.ReadAllText(path) : null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// A config we cannot read is treated as absent — the engine falls back to defaults.
			return null;
		}
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
internal sealed class PhotinoFileDialogs(Func<PhotinoWindow> window) : IFileDialogs
{
	private static readonly (string Name, string[] Extensions)[] Filters =
	[
		("Markdown", ["*.md", "*.markdown"]),
		("All files", ["*.*"]),
	];

	public string? PickOpenFile() =>
		OnUiThread(static w =>
		{
			string[] selection = w.ShowOpenFile("Open spec", string.Empty, false, Filters);
			return selection.Length > 0 ? selection[0] : null;
		});

	public string? PickSaveFile(string? suggestedPath) =>
		OnUiThread(w =>
		{
			string selection = w.ShowSaveFile("Save spec", suggestedPath ?? string.Empty, Filters);
			return string.IsNullOrEmpty(selection) ? null : selection;
		});

	// Native file dialogs require the STA UI thread, but the web-message handler that triggers
	// them may run on a background (MTA) thread. Marshal onto the window's UI thread via Invoke
	// (which runs inline when already there) and block for the modal result.
	private string? OnUiThread(Func<PhotinoWindow, string?> show)
	{
		PhotinoWindow w = window();
		TaskCompletionSource<string?> completion = new();
		w.Invoke(() =>
		{
			try
			{
				completion.SetResult(show(w));
			}
			catch (Exception)
			{
				// A dialog failure cancels the operation rather than crashing the UI thread.
				completion.SetResult(null);
			}
		});
		return completion.Task.GetAwaiter().GetResult();
	}
}
