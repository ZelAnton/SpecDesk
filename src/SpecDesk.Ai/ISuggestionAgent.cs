namespace SpecDesk.Ai;

/// <summary>A drafted pull-request description: an outward-facing <paramref name="Title"/> and
/// <paramref name="Body"/>, both git-vocabulary-free, that the author reviews and edits before sending.</summary>
public sealed record PrDescription(string Title, string Body);

/// <summary>
/// The AI author's helpers that draft workflow text from the read-only document tools
/// (docs/design/08-ai-agent.md's <c>suggestVersionNote</c> / <c>suggestPrDescription</c>): a version note for
/// "Save a version" and a title/body for "Send for review". Both are pure <em>suggestions</em> — the author
/// always sees and can edit or reject them, nothing is auto-applied.
/// </summary>
/// <remarks>
/// The contract that keeps the base workflow independent of the assistant: a method returns <c>null</c> when
/// it cannot produce a suggestion — the provider is unavailable, the call failed, or it exceeded a reasonable
/// timeout — and it never throws for such provider faults. The caller then falls back to its deterministic
/// template (<c>WorkflowSeeds</c>), so "Save a version" / "Send for review" is never blocked by AI being
/// unavailable. The tools are read-only, and their content is treated as data, not instructions.
/// </remarks>
public interface ISuggestionAgent
{
	/// <summary>Draft a version note (a short, plain-language commit message) from the working change, or
	/// <c>null</c> to fall back to the deterministic template.</summary>
	Task<string?> SuggestVersionNoteAsync(IReadOnlyDocumentTools tools, CancellationToken cancellationToken = default);

	/// <summary>Draft a pull-request title and body from the working change, or <c>null</c> to fall back to
	/// the deterministic template.</summary>
	Task<PrDescription?> SuggestPrDescriptionAsync(IReadOnlyDocumentTools tools, CancellationToken cancellationToken = default);
}
