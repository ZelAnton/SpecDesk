namespace SpecDesk.Ai;

/// <summary>
/// A single staged document edit the assistant's <c>proposeEdit</c> tool produced: the full replacement
/// <paramref name="ProposedText"/> it proposes for the current document, plus an optional short,
/// plain-language <paramref name="Summary"/> of the change. It is a <b>proposal only</b> — staging it
/// never mutates the document. The host routes it through the same human confirmation UI as a manual edit
/// (docs/design/08-ai-agent.md's hard safety rule) before anything is applied.
/// </summary>
public sealed record EditProposal(string ProposedText, string? Summary);

/// <summary>The outcome of staging an <see cref="EditProposal"/> on an <see cref="IEditProposalSink"/>.</summary>
public enum EditProposalStatus
{
	/// <summary>The proposal was staged and handed to the confirmation UI; it awaits human confirmation.</summary>
	Staged,

	/// <summary>The proposal could not be staged (there is no open, editable local document to propose an
	/// edit to, or the proposed text was empty). Nothing was staged and nothing changed.</summary>
	Unavailable,
}

/// <summary>
/// The host-side sink a <see cref="ProposeEditTool"/> hands its proposal to. The tool holds <b>only</b>
/// this — never a document-mutation API — so "the assistant never edits the document silently" is
/// structural, not a matter of trust: there is no method here that applies an edit, only one that
/// <see cref="Stage"/>s a proposal for human confirmation. The host's implementation renders the
/// difference and gates application behind the same confirmation as a manual edit.
/// </summary>
public interface IEditProposalSink
{
	/// <summary>Stage <paramref name="proposal"/> for human confirmation. Returns
	/// <see cref="EditProposalStatus.Staged"/> when the host accepted it for confirmation, or
	/// <see cref="EditProposalStatus.Unavailable"/> when there is nothing to stage against. It NEVER
	/// applies the edit — application only ever follows an explicit human confirmation.</summary>
	EditProposalStatus Stage(EditProposal proposal);
}
