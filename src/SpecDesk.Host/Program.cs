using Photino.NET;
using SpecDesk.Markdown;

namespace SpecDesk.Host;

internal static class Program
{
	// PoC-1 placeholder root for the app:// scheme + the demo document opened on launch.
	// TODO(PoC-3/4): point AssetRoot at the opened repo working directory.
	private static readonly string AssetRoot = Path.Combine(AppContext.BaseDirectory, "samples");
	private static readonly string WelcomeDoc = Path.Combine(AssetRoot, "welcome.md");

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
			initialDocPath: WelcomeDoc);

		window = new PhotinoWindow()
			.SetTitle("SpecDesk")
			.SetUseOsDefaultSize(false)
			.SetSize(1280, 800)
			.Center()
			// Custom scheme handlers must be registered before the page loads.
			.RegisterCustomSchemeHandler("app", HandleAppAsset)
			.RegisterWebMessageReceivedHandler((_, message) => controller.OnMessage(message))
			.Load("wwwroot/index.html");

		window.WaitForClose();
	}

	// Serve files referenced as app://<authority>/<path> from the asset root. A rejected
	// (traversal), missing, or unreadable file returns Stream.Null, so the resource simply
	// fails to load. This runs as a native WebView2 callback, so it must never let an exception
	// escape into the message pump.
	private static Stream HandleAppAsset(object sender, string scheme, string url, out string contentType)
	{
		ResolvedAsset? asset = AppAssetResolver.Resolve(AssetRoot, url);
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
