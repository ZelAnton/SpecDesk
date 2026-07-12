using SpecDesk.Contracts;

namespace SpecDesk.Ai;

/// <summary>
/// The prompt library the assistant's template picker shows: the author's personal (local, host-owned)
/// templates plus a remote (configured-URL) set. The host depends on this seam so it can be faked in
/// tests without a file or the network.
/// </summary>
public interface ITemplateLibrary
{
	/// <summary>Gather the personal and remote templates. Never throws for a remote failure — the remote
	/// list is empty then (see <see cref="RemoteTemplateSource"/>).</summary>
	Task<TemplatesPayload> GetTemplatesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="ITemplateLibrary"/>: personal templates from a <see cref="PromptTemplateStore"/>
/// (falling back to built-in starters when the author has none yet) and remote templates from a
/// <see cref="RemoteTemplateSource"/>.
/// </summary>
public sealed class TemplateLibrary : ITemplateLibrary
{
	private readonly PromptTemplateStore _store;
	private readonly RemoteTemplateSource _remote;

	public TemplateLibrary(PromptTemplateStore store, RemoteTemplateSource remote)
	{
		ArgumentNullException.ThrowIfNull(store);
		ArgumentNullException.ThrowIfNull(remote);
		_store = store;
		_remote = remote;
	}

	/// <summary>The starter personal library, offered when the author has no saved templates yet. Aligned
	/// with the assistant's intended tasks (docs/design/08-ai-agent.md): summarize the change, tighten a
	/// section, draft a version note / PR description.</summary>
	public static IReadOnlyList<PromptTemplate> DefaultPersonalTemplates { get; } =
	[
		new("summarize-changes", "Summarize the changes",
			"Summarize what changed in this document since the last saved version, in plain language."),
		new("tighten-section", "Tighten the selected section",
			"Rewrite the selected section to be clearer and more concise, keeping the meaning and any defined terms."),
		new("draft-version-note", "Draft a version note",
			"Draft a short version note (a plain-language commit message) describing the current change."),
		new("draft-pr", "Draft a review description",
			"Draft a title and description for sending this document for review, based on what changed."),
	];

	public async Task<TemplatesPayload> GetTemplatesAsync(CancellationToken cancellationToken = default)
	{
		IReadOnlyList<PromptTemplate> personal = _store.Load();
		if (personal.Count == 0)
		{
			personal = DefaultPersonalTemplates;
		}

		IReadOnlyList<PromptTemplate> remote = await _remote.FetchAsync(cancellationToken);
		return new TemplatesPayload(personal, remote);
	}
}
