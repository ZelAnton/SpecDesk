namespace SpecDesk.Host;

/// <summary>The outcome of verifying a served webview bundle against its manifest (and, in a dev run,
/// against the live webview inputs).</summary>
internal enum WebviewBundleStatus
{
    /// <summary>Bundle is intact and — where the source tree was available — matches current inputs.</summary>
    UpToDate,

    /// <summary>No manifest beside the bundle: its origin is unknown, so it is not servable.</summary>
    ManifestMissing,

    /// <summary>The manifest exists but is malformed/unparseable — treated exactly like missing.</summary>
    ManifestUnreadable,

    /// <summary>The manifest was written by a different schema/kind and cannot be trusted.</summary>
    SchemaMismatch,

    /// <summary>A file the manifest lists as an output is absent (a partial/incomplete bundle).</summary>
    OutputMissing,

    /// <summary>An output file's content does not match the hash the manifest recorded (corruption).</summary>
    OutputCorrupt,

    /// <summary>Dev run only: the live webview inputs no longer match the manifest — a stale bundle.</summary>
    InputMismatch,
}

/// <summary>The result of a bundle verification: the status, a path-free human reason, and the
/// manifest's fingerprints (when a manifest was readable) for diagnostics.</summary>
internal sealed record WebviewBundleVerification(
    WebviewBundleStatus Status,
    string Reason,
    string? InputFingerprint,
    string? OutputFingerprint)
{
    /// <summary>The bundle is self-consistent with its manifest (schema, presence, output hashes).
    /// This is the invariant a published app must hold even with no source tree nearby.</summary>
    public bool IsIntact => Status is not (
        WebviewBundleStatus.ManifestMissing
        or WebviewBundleStatus.ManifestUnreadable
        or WebviewBundleStatus.SchemaMismatch
        or WebviewBundleStatus.OutputMissing
        or WebviewBundleStatus.OutputCorrupt);

    /// <summary>The bundle is intact AND fresh (or freshness was not applicable because no source
    /// tree was present). The only non-fresh intact status is <see cref="WebviewBundleStatus.InputMismatch"/>.</summary>
    public bool IsUpToDate => Status == WebviewBundleStatus.UpToDate;
}

/// <summary>
/// Verifies that a built webview bundle in a wwwroot directory is one this build can trust to serve —
/// by content, never by file timestamp. This is the host-side half of the mechanism the MSBuild
/// target and CI share (<c>webview/scripts/verify-bundle.mjs</c> is the build-side half); both read
/// the same manifest and use the same fingerprint algorithm (<see cref="WebviewFingerprint"/>).
/// </summary>
internal static class WebviewBundleVerifier
{
    /// <summary>
    /// Verify the bundle in <paramref name="wwwrootDir"/>. Always checks integrity (manifest present,
    /// current schema, every output present and matching its recorded hash). When
    /// <paramref name="webviewSourceDir"/> is non-null (a dev run with the source tree beside the
    /// build) it additionally checks freshness — that the live inputs still produce the manifest's
    /// input fingerprint. A published app passes <c>null</c> and gets integrity-only verification,
    /// which needs no source tree.
    /// </summary>
    public static WebviewBundleVerification Verify(string wwwrootDir, string? webviewSourceDir)
    {
        string manifestPath = Path.Combine(wwwrootDir, WebviewBundleManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            return new WebviewBundleVerification(
                WebviewBundleStatus.ManifestMissing,
                $"no {WebviewBundleManifest.FileName} beside the bundle",
                null,
                null);
        }

        string json = File.ReadAllText(manifestPath);
        if (!WebviewBundleManifest.TryParse(json, out WebviewBundleManifest? manifest) || manifest is null)
        {
            return new WebviewBundleVerification(
                WebviewBundleStatus.ManifestUnreadable,
                $"{WebviewBundleManifest.FileName} is malformed",
                null,
                null);
        }

        if (manifest.Schema != WebviewBundleManifest.SupportedSchema
            || manifest.Kind != WebviewBundleManifest.ExpectedKind)
        {
            return new WebviewBundleVerification(
                WebviewBundleStatus.SchemaMismatch,
                $"manifest schema/kind is {manifest.Schema}/{manifest.Kind}, expected "
                    + $"{WebviewBundleManifest.SupportedSchema}/{WebviewBundleManifest.ExpectedKind}",
                manifest.InputFingerprint,
                manifest.OutputFingerprint);
        }

        foreach (WebviewOutputEntry output in manifest.Outputs)
        {
            string outputPath = Path.Combine(wwwrootDir, output.Path);
            if (!File.Exists(outputPath))
            {
                return new WebviewBundleVerification(
                    WebviewBundleStatus.OutputMissing,
                    $"output {output.Path} is missing",
                    manifest.InputFingerprint,
                    manifest.OutputFingerprint);
            }

            if (!string.Equals(WebviewFingerprint.Sha256HexOfFile(outputPath), output.Sha256, StringComparison.Ordinal))
            {
                return new WebviewBundleVerification(
                    WebviewBundleStatus.OutputCorrupt,
                    $"output {output.Path} does not match its recorded hash",
                    manifest.InputFingerprint,
                    manifest.OutputFingerprint);
            }
        }

        if (webviewSourceDir is not null)
        {
            string currentInput =
                WebviewFingerprint.ComputeInputFingerprint(webviewSourceDir, manifest.BundleParams);
            if (!string.Equals(currentInput, manifest.InputFingerprint, StringComparison.Ordinal))
            {
                return new WebviewBundleVerification(
                    WebviewBundleStatus.InputMismatch,
                    $"webview inputs changed since the bundle was built (now {currentInput})",
                    manifest.InputFingerprint,
                    manifest.OutputFingerprint);
            }
        }

        return new WebviewBundleVerification(
            WebviewBundleStatus.UpToDate,
            webviewSourceDir is null ? "bundle is intact" : "bundle matches current inputs",
            manifest.InputFingerprint,
            manifest.OutputFingerprint);
    }
}
