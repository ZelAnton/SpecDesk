using SpecDesk.Ai;

namespace SpecDesk.Host.Tests;

/// <summary>
/// A fake <see cref="ISuggestionAgent"/> for controller tests: it returns a preset version note / PR
/// description (or <c>null</c> to exercise the deterministic-template fallback), optionally throwing to
/// exercise the "provider errored" fallback, and records the read-only tools it was handed.
/// </summary>
internal sealed class FakeSuggestionAgent : ISuggestionAgent
{
	/// <summary>The note to return, or <c>null</c> to make the host fall back to the template.</summary>
	public string? VersionNote { get; init; }

	/// <summary>The PR description to return, or <c>null</c> to make the host fall back to the template.</summary>
	public PrDescription? PrDescription { get; init; }

	/// <summary>When set, both methods throw — exercising the host's broad fallback catch.</summary>
	public bool Throw { get; init; }

	/// <summary>The document context of the tools handed to the last call — asserted to confirm the assistant
	/// received the open document via getCurrentDoc without an explicit attachment.</summary>
	public DocumentContext? LastDocument { get; private set; }

	public DocumentDiff? LastDiff { get; private set; }

	public int VersionNoteCalls { get; private set; }

	public int PrCalls { get; private set; }

	public Task<string?> SuggestVersionNoteAsync(
		IReadOnlyDocumentTools tools, CancellationToken cancellationToken = default)
	{
		VersionNoteCalls++;
		Capture(tools);
		if (Throw)
		{
			throw new InvalidOperationException("simulated suggestion failure");
		}
		return Task.FromResult(VersionNote);
	}

	public Task<PrDescription?> SuggestPrDescriptionAsync(
		IReadOnlyDocumentTools tools, CancellationToken cancellationToken = default)
	{
		PrCalls++;
		Capture(tools);
		if (Throw)
		{
			throw new InvalidOperationException("simulated suggestion failure");
		}
		return Task.FromResult(PrDescription);
	}

	private void Capture(IReadOnlyDocumentTools tools)
	{
		LastDocument = tools.GetCurrentDocument();
		LastDiff = tools.GetDiff();
	}
}
