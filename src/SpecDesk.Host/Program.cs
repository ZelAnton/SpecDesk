using System.Net.Http;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Photino.NET;
using SpecDesk.AppInfo;
using SpecDesk.Core;
using SpecDesk.Git;
using SpecDesk.GitHub;
using SpecDesk.Markdown;

namespace SpecDesk.Host;

internal static class Program
{
	// The bundled demo assets, copied into a writable, git-initialized sample repo on first run.
	private static readonly string BundledSamples =
		Path.Combine(AppContext.BaseDirectory, "samples");

	// Photino's native Invoke() (Photino.Windows.cpp: PostMessage + an untimed condition-variable
	// wait) gives no guarantee that a posted callback still runs — or even that the caller is ever
	// woken — once the window starts tearing down; a PostMessage racing DestroyWindow can silently
	// fail and the calling thread then blocks forever (see PhotinoFileDialogs.OnUiThread below).
	// Rather than a blind timeout that could cut off a legitimate, arbitrarily long modal dialog
	// while the window is still open, RegisterWindowClosingHandler arms this grace period only once
	// closing has actually begun: any dialog already in flight gets a fair chance to finish, and
	// anything that never completes is abandoned instead of hanging forever.
	private static readonly TimeSpan DialogClosingGrace = TimeSpan.FromSeconds(2);

	[STAThread]
	private static void Main()
	{
		using ILoggerFactory loggerFactory = Logging.CreateFactory();
		ILogger startup = loggerFactory.CreateLogger("SpecDesk.Host");
		startup.LogInformation("SpecDesk starting; logs at {LogDirectory}", Logging.LogDirectory);

		// Refuse to start from a repository main working copy that has drifted behind the local
		// published main: its on-disk sources predate the merged code, so the app would run an OLD UI.
		// A published app (no source tree) and every isolated task worktree are exempt — only the
		// genuine main-copy drift stops here, before any WebView is created, with a path to re-sync
		// (scripts/restore-main-worktree.ps1) instead of silently masking it by rebuilding old sources.
		MainWorktreeGuard.EnsureCurrent(AppContext.BaseDirectory, startup);

		// Prove, by content, that the webview bundle about to be loaded is the one built from the
		// current inputs (dev) or a complete, uncorrupted shipped artifact (published) — never an old
		// bundle trusted only because of its file timestamps. Fails fast with a clear, path-free error
		// (logged above the throw) rather than loading an unknown or partial UI.
		WebviewBundleGuard.EnsureServable(AppContext.BaseDirectory, startup);

		// Captured by the closures below; assigned before any message can arrive (Load is last).
		PhotinoWindow? window = null;

		// PoC-4 seeds a writable, git-versioned sample repo so Edit / Save version work out of the
		// box without ever touching SpecDesk's own working tree. The concrete type also implements
		// IGitPublishing (push / remote / last-note) for the PoC-5 "Send for review" round-trip.
		LibGit2DocumentVersioning versioning = new();
		string welcomeDoc = SampleRepo.EnsureSeeded(
			AppPaths.SampleRepo, BundledSamples, versioning, loggerFactory.CreateLogger("SpecDesk.Host.SampleRepo"));

		// GitHub sign-in (PoC-5): resolve the OAuth App client id (env → compiled default). An empty id
		// means sign-in is unconfigured, so the auth library — and the webview's account affordance — stay
		// off. The HttpClient is shared for the app's lifetime and disposed after the controller (so an
		// in-flight sign-in is signalled to cancel first; at process teardown any residual fault is benign).
		string gitHubClientId =
			Environment.GetEnvironmentVariable(GitHubAuthOptions.ClientIdEnvironmentVariable) is { Length: > 0 } id
				? id
				: GitHubAuthOptions.DefaultClientId;
		using HttpClient gitHubHttp = new();

		// Cancelled (with the grace period above) once the window starts closing; see
		// PhotinoFileDialogs.OnUiThread for how this bounds the native Invoke() race.
		using CancellationTokenSource dialogClosingCts = new();

		IGitHubAuth? gitHubAuth = gitHubClientId.Length > 0
			? new GitHubDeviceFlowAuth(GitHubAuthOptions.ForClient(gitHubClientId), gitHubHttp, AppPaths.Auth)
			: null;

		using HostController controller = new(
			render: Renderer.render,
			// SendWebMessage already marshals onto the UI thread internally, so this is safe to
			// call from the background render task as well as from the message handler.
			send: json => window!.SendWebMessage(json),
			dialogs: new PhotinoFileDialogs(
				() => window!,
				loggerFactory.CreateLogger("SpecDesk.Host.Dialogs"),
				dialogClosingCts.Token),
			inserter: new ImageInsertAdapter(loggerFactory.CreateLogger("SpecDesk.Host.ImageEngine")).Insert,
			versioning: versioning,
			logger: loggerFactory.CreateLogger<HostController>(),
			initialDocPath: welcomeDoc,
			auth: gitHubAuth,
			// The same LibGit2 instance also publishes (push / remote / last note); the PR client shares
			// the app-lifetime HttpClient. Both are harmless when sign-in is unconfigured — "Send for
			// review" gates on a connected account before it touches them.
			publishing: versioning,
			review: new GitHubReviewClient(gitHubHttp));

		// Devtools + right-click context menu, opt-in via SPECDESK_DEVTOOLS for interactive human+agent
		// debugging. Accepts 1/true/yes/on (case-insensitive); off by default and for 0/false/empty. A
		// presence check (like the stale-guard opt-outs) would wrongly enable it on SPECDESK_DEVTOOLS=0,
		// so this is a value check. Both flags set explicitly so the behaviour is pinned regardless of
		// Photino's version default; a shipped app exposes neither.
		string? devToolsEnv = Environment.GetEnvironmentVariable("SPECDESK_DEVTOOLS")?.Trim().ToLowerInvariant();
		bool devTools = devToolsEnv is "1" or "true" or "yes" or "on";

		window = new PhotinoWindow()
			.SetTitle(ProductInfo.Name)
			.SetUseOsDefaultSize(false)
			.SetSize(1280, 800)
			.SetDevToolsEnabled(devTools)
			.SetContextMenuEnabled(devTools)
			.Center()
			// Fires synchronously on the UI thread as soon as WM_CLOSE is dispatched, ahead of the
			// native window (and its message queue) being torn down — see DialogClosingGrace above.
			// Never veto the close; this only arms the grace period for in-flight dialogs.
			.RegisterWindowClosingHandler((_, _) =>
			{
				dialogClosingCts.CancelAfter(DialogClosingGrace);
				return false;
			})
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

	// Serve files referenced as app://<authority>/<path> from the current repo root. A rejected
	// (traversal), missing, or unreadable file — or no open document yet — returns Stream.Null, so
	// the resource simply fails to load. This runs as a native WebView2 callback, so it must never
	// let an exception escape into the message pump. Internal (rather than private) so tests can
	// drive it directly — this is the one method actually responsible for that invariant.
	internal static Stream ServeAsset(string? root, string url, out string contentType)
	{
		if (!string.IsNullOrEmpty(root))
		{
			try
			{
				ResolvedAsset? asset = AppAssetResolver.Resolve(root, url);
				if (asset is not null)
				{
					Stream stream = File.OpenRead(asset.FilePath);
					contentType = asset.ContentType;
					return stream;
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// File vanished between resolution and open (TOCTOU), is locked, or is not
				// readable. Fall through to the broken-resource response below.
			}
			catch (Exception ex)
			{
				// This callback runs inside a native WebView2 P/Invoke, so per the comment above an
				// exception must never escape into the message pump — regardless of cause. Everything
				// expected is handled above; anything else reaching here is a bug, so log it (rather
				// than silently swallow it) before still falling through to the broken-resource
				// response, exactly like every other rejection this method makes.
				Serilog.Log.Error(ex, "Unexpected failure serving app:// asset {Url}", url);
			}
		}

		contentType = "text/plain";
		return Stream.Null;
	}
}

/// <summary>Photino-backed native file pickers for <see cref="HostController"/>.</summary>
internal sealed class PhotinoFileDialogs(Func<PhotinoWindow> window, ILogger logger, CancellationToken closing)
	: IFileDialogs
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
		return WaitOrAbandon(completion.Task, name, logger, closing);
	}

	// Photino's native Invoke() (Photino.Windows.cpp) blocks the calling thread on an untimed
	// condition variable and never checks whether its PostMessage actually reached a still-alive
	// window; if the window is destroyed first, the posted callback never runs and the wait above
	// would otherwise never return. `closing` is cancelled — after a short grace period, so an
	// already in-flight dialog still gets to finish — once the window starts tearing down (see
	// Program.DialogClosingGrace / RegisterWindowClosingHandler), so this abandons the wait instead
	// of blocking forever. Internal so it can be unit-tested without a real native window.
	internal static string? WaitOrAbandon(
		Task<string?> completion, string name, ILogger logger, CancellationToken closing)
	{
		try
		{
			completion.Wait(closing);
		}
		catch (OperationCanceledException)
		{
			// The window is closing and the native callback never signalled completion in time.
			// Whatever result eventually arrives (if ever) is simply discarded.
			logger.LogWarning(
				"Native dialog {Dialog} abandoned: window is closing and Invoke never completed", name);
			return null;
		}
		return completion.GetAwaiter().GetResult();
	}
}
