using Microsoft.Extensions.Logging;

namespace SpecDesk.Host;

/// <summary>Thrown when the webview bundle beside the app cannot be trusted to serve, so startup
/// stops with a clear error instead of loading an unknown or broken UI.</summary>
internal sealed class WebviewBundleException(string message) : Exception(message);

/// <summary>
/// The startup gate that decides whether the webview bundle staged next to the running app is one we
/// may actually serve. It turns the <see cref="WebviewBundleVerifier"/> outcome into an action:
/// log the fingerprints of the UI that is about to run, or refuse to run at all.
/// <list type="bullet">
/// <item>An <b>intact but stale</b> bundle (dev run, live inputs changed) is refused too — that is
/// exactly the "old bundle survived" case this feature exists to catch — unless the operator opts in
/// with the narrow, explicit <see cref="AllowStaleEnvironmentVariable"/> escape hatch.</item>
/// <item>A <b>non-intact</b> bundle (missing/partial/corrupt/old-schema manifest) is always refused,
/// in dev and in a published app alike; there is no override for serving an unknown artifact.</item>
/// </list>
/// Diagnostics carry the input and output fingerprints — enough to pin down exactly which UI ran —
/// and never an absolute local path.
/// </summary>
internal static class WebviewBundleGuard
{
    /// <summary>Set (to any non-empty value) to downgrade a dev-run stale-bundle refusal to a warning,
    /// for the deliberate "run an older build without rebuilding" case. It cannot relax an integrity
    /// failure — a missing/corrupt bundle is never servable.</summary>
    public const string AllowStaleEnvironmentVariable = "SPECDESK_WEBVIEW_ALLOW_STALE";

    /// <summary>
    /// Verify the bundle under <paramref name="baseDirectory"/>/wwwroot and either log the UI's
    /// fingerprints (on success) or throw <see cref="WebviewBundleException"/> (on refusal). Returns
    /// the verification so a caller/test can inspect it. <paramref name="allowStale"/> defaults to the
    /// environment opt-in but is a parameter so it is testable without touching process environment.
    /// </summary>
    public static WebviewBundleVerification EnsureServable(
        string baseDirectory, ILogger logger, bool? allowStale = null)
    {
        bool stalePermitted = allowStale
            ?? !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AllowStaleEnvironmentVariable));

        string wwwrootDir = Path.Combine(baseDirectory, "wwwroot");
        string? sourceDir = LocateWebviewSource(baseDirectory);
        string mode = sourceDir is null ? "published" : "dev";

        WebviewBundleVerification result = WebviewBundleVerifier.Verify(wwwrootDir, sourceDir);

        if (result.IsUpToDate)
        {
            logger.LogInformation(
                "Webview bundle verified ({Mode}): inputs={InputFingerprint} outputs={OutputFingerprint}",
                mode, result.InputFingerprint, result.OutputFingerprint);
            return result;
        }

        if (result.IsIntact && stalePermitted)
        {
            // Intact but stale, and the operator explicitly opted in: proceed, but record loudly which
            // (stale) UI is being served so the log still identifies exactly what ran.
            logger.LogWarning(
                "Webview bundle is stale but served on request ({Mode}, {Variable} set): {Reason}; "
                    + "outputs={OutputFingerprint}",
                mode, AllowStaleEnvironmentVariable, result.Reason, result.OutputFingerprint);
            return result;
        }

        logger.LogCritical(
            "Refusing to serve the webview bundle ({Mode}): {Status} — {Reason}. "
                + "inputs={InputFingerprint} outputs={OutputFingerprint}",
            mode, result.Status, result.Reason, result.InputFingerprint, result.OutputFingerprint);
        throw new WebviewBundleException(
            $"Webview bundle cannot be served ({result.Status}): {result.Reason}");
    }

    /// <summary>
    /// Find the <c>webview/</c> source tree for a dev run by walking up from
    /// <paramref name="baseDirectory"/> to the repository root (the directory holding
    /// <c>SpecDesk.slnx</c>) and taking its <c>webview/</c> subdirectory. Returns <c>null</c> for a
    /// published app, whose bundle ships without any source tree — the signal to verify integrity
    /// only.
    /// </summary>
    private static string? LocateWebviewSource(string baseDirectory)
    {
        for (DirectoryInfo? dir = new(baseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "SpecDesk.slnx")))
            {
                continue;
            }

            string candidate = Path.Combine(dir.FullName, "webview");
            return Directory.Exists(candidate) ? candidate : null;
        }

        return null;
    }
}
