using Microsoft.Extensions.Logging;
using SpecDesk.Core;

namespace SpecDesk.Host;

/// <summary>
/// Adapts the host to the F# image rule engine: reads the repo's <c>.spectool.toml</c> (if any), runs
/// the engine for a pasted image, and logs a rejection. Extracted from <see cref="Program"/> so the
/// host→engine seam is testable and carries no static logger state.
/// </summary>
internal sealed class ImageInsertAdapter(ILogger logger)
{
    /// <summary>
    /// Insert a pasted image per the repo's image rules; returns the document-relative Markdown link to
    /// splice in, or <c>null</c> when the engine rejected the bytes (the reason is logged, never thrown).
    /// </summary>
    public string? Insert(string repoRoot, string docPath, byte[] bytes, string? originalName, string? mime)
    {
        string? toml = WorkflowSeeds.TryReadRepoToml(repoRoot);
        ImageEngine.InsertOutcome outcome =
            ImageEngine.insertForHost(repoRoot, docPath, toml, bytes, originalName, mime);
        if (outcome.Error is not null)
        {
            logger.LogWarning("Image engine rejected the paste: {Error}", outcome.Error);
        }

        return outcome.Markdown;
    }
}
