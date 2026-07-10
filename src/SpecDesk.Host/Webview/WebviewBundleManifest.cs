using System.Text.Json;

namespace SpecDesk.Host;

/// <summary>One output file recorded in the webview bundle manifest: its wwwroot-relative logical
/// path and the SHA-256 (lowercase hex) of its bytes at build time.</summary>
internal sealed record WebviewOutputEntry(string Path, string Sha256);

/// <summary>
/// The parsed content manifest that ties the served webview bundle back to the exact inputs it was
/// built from. Produced by <c>webview/scripts/bundle.mjs</c> and written to
/// <c>wwwroot/webview.manifest.json</c>; the fields and the fingerprint algorithm are mirrored by
/// <see cref="WebviewFingerprint"/> so the host can verify origin by content, never by timestamp.
/// </summary>
internal sealed record WebviewBundleManifest(
    int Schema,
    string Kind,
    string InputFingerprint,
    string OutputFingerprint,
    string BundleParams,
    IReadOnlyList<WebviewOutputEntry> Outputs)
{
    /// <summary>The schema version this build understands. A manifest written by any other schema is
    /// rejected rather than trusted, so an older bundle format is never served as current.</summary>
    public const int SupportedSchema = 1;

    /// <summary>Discriminator that distinguishes our manifest from any unrelated JSON in wwwroot.</summary>
    public const string ExpectedKind = "specdesk-webview-bundle";

    /// <summary>The manifest file name, alongside the bundle it describes, inside wwwroot/.</summary>
    public const string FileName = "webview.manifest.json";

    /// <summary>
    /// Parse a manifest from JSON text. Returns <c>false</c> (with <paramref name="manifest"/> null)
    /// for malformed JSON or a structurally invalid document — the caller treats that exactly like a
    /// missing manifest, so a corrupt manifest can never masquerade as a valid one. Parsing is done
    /// by hand with <see cref="JsonDocument"/> (no reflection) to stay allocation-light and free of
    /// any trimming/AOT surface.
    /// </summary>
    public static bool TryParse(string json, out WebviewBundleManifest? manifest)
    {
        manifest = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetInt(root, "schema", out int schema)
                || !TryGetString(root, "kind", out string kind)
                || !TryGetString(root, "inputFingerprint", out string inputFingerprint)
                || !TryGetString(root, "outputFingerprint", out string outputFingerprint)
                || !TryGetString(root, "bundleParams", out string bundleParams)
                || !root.TryGetProperty("outputs", out JsonElement outputsElement)
                || outputsElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            List<WebviewOutputEntry> outputs = [];
            foreach (JsonElement entry in outputsElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !TryGetString(entry, "path", out string path)
                    || !TryGetString(entry, "sha256", out string sha256))
                {
                    return false;
                }

                outputs.Add(new WebviewOutputEntry(path, sha256));
            }

            manifest = new WebviewBundleManifest(
                schema, kind, inputFingerprint, outputFingerprint, bundleParams, outputs);
            return true;
        }
    }

    private static bool TryGetString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetInt(JsonElement parent, string name, out int value)
    {
        value = 0;
        return parent.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }
}
