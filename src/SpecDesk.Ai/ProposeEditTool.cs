namespace SpecDesk.Ai;

/// <summary>
/// The assistant's single gated <b>mutating</b> tool — docs/design/08-ai-agent.md's <c>proposeEdit</c>.
/// It proposes a full replacement for the current document by <em>staging</em> the proposal on an
/// <see cref="IEditProposalSink"/> and nothing else: it holds no document-mutation capability, touches no
/// git and no filesystem, and returns without applying anything. So it structurally cannot edit the
/// document, bypass confirmation, commit, or push — the host's sink renders the difference and gates
/// application behind the same human confirmation as a manual edit.
/// </summary>
/// <remarks>
/// Deliberately kept OUT of <see cref="AiReadOnlyTools.Allowlist"/>: that list is the assistant's
/// read-only surface, and adding a mutating tool there would be a category error. <c>proposeEdit</c> is a
/// separate, named, gated tool so an audit can see the one mutating affordance at a glance. Text the tool
/// receives is treated as content, never as instructions.
/// </remarks>
public sealed class ProposeEditTool
{
	/// <summary>The <c>proposeEdit</c> tool name. Not part of the read-only allowlist — this is the gated
	/// mutating tool, tracked separately on purpose.</summary>
	public const string Name = "proposeEdit";

	private readonly IEditProposalSink _sink;

	/// <summary>Bind the tool to the host-provided <paramref name="sink"/> it stages proposals on. The sink
	/// is the tool's ONLY capability; it has no other way to affect the document.</summary>
	public ProposeEditTool(IEditProposalSink sink)
	{
		ArgumentNullException.ThrowIfNull(sink);
		_sink = sink;
	}

	/// <summary>
	/// Propose replacing the current document with <paramref name="proposedText"/> (with an optional
	/// short <paramref name="summary"/> of the change). Returns whether the proposal was staged for human
	/// confirmation — it <b>never</b> applies the edit. A null or empty <paramref name="proposedText"/> is
	/// rejected as <see cref="EditProposalStatus.Unavailable"/>: there is nothing to stage.
	/// </summary>
	public EditProposalStatus Propose(string proposedText, string? summary = null)
	{
		if (string.IsNullOrEmpty(proposedText))
		{
			return EditProposalStatus.Unavailable;
		}

		string? trimmedSummary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim();
		return _sink.Stage(new EditProposal(proposedText, trimmedSummary));
	}
}
