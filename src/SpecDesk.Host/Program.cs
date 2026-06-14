using Photino.NET;
using SpecDesk.Contracts;

namespace SpecDesk.Host;

internal static class Program
{
	// PoC-1 placeholder root for the app:// scheme.
	// TODO(PoC-3/4): point AssetRoot at the opened repo working directory.
	private static readonly string AssetRoot = Path.Combine(AppContext.BaseDirectory, "samples");

	[STAThread]
	private static void Main()
	{
		// PoC-0 routes a single kind: an "echo" request is answered with an "echo.reply"
		// carrying the same id and payload back. Later PoCs register real handlers here.
		IpcRouter router = new IpcRouter()
			.Register("echo", static request =>
				new IpcMessage("echo.reply", Id: request.Id, Payload: request.Payload));

		PhotinoWindow window = new PhotinoWindow()
			.SetTitle("SpecDesk")
			.SetUseOsDefaultSize(false)
			.SetSize(1024, 768)
			.Center()
			// Custom scheme handlers must be registered before the page loads.
			.RegisterCustomSchemeHandler("app", HandleAppAsset)
			.RegisterWebMessageReceivedHandler((sender, message) =>
			{
				string? reply = router.Handle(message);
				if (reply is not null && sender is PhotinoWindow source)
				{
					source.SendWebMessage(reply);
				}
			})
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
