namespace SpecDesk.Ai;

/// <summary>
/// The read-only tool surface the assistant is allowed to use to see the open document
/// (docs/design/08-ai-agent.md's <c>getCurrentDoc</c> / <c>getDiff</c>). It is deliberately the
/// <em>whole</em> allowlist: two accessors that only <b>read</b> a snapshot, and nothing that can edit the
/// document, touch git, run a command, or reach the filesystem. Handing the AI layer only this interface is
/// what makes the "the agent never mutates silently" rule structural rather than a matter of trust — there is
/// no mutating method to call.
/// </summary>
/// <remarks>
/// Everything these tools return is <b>data, not instructions</b>: document text and diff content can never
/// direct the assistant to act. Mutating actions (proposeEdit, commit, push, open PR) stay outside this
/// surface and always pass the app's own confirmation UI.
/// </remarks>
public interface IReadOnlyDocumentTools
{
	/// <summary>The <c>getCurrentDoc</c> tool: the open document's text + metadata, or <c>null</c> when there
	/// is no open local document to read.</summary>
	DocumentContext? GetCurrentDocument();

	/// <summary>The <c>getDiff</c> tool: a structural summary of the working change, or <c>null</c> when there
	/// is no versioned document to diff.</summary>
	DocumentDiff? GetDiff();
}

/// <summary>
/// An immutable, snapshot-backed <see cref="IReadOnlyDocumentTools"/>: it simply returns the
/// <see cref="DocumentContext"/> / <see cref="DocumentDiff"/> it was built with. The host captures a
/// consistent snapshot of the open document under its locks and hands this to the AI layer, so the tools can
/// never observe — let alone change — live host state.
/// </summary>
public sealed class DocumentToolset(DocumentContext? document, DocumentDiff? diff) : IReadOnlyDocumentTools
{
	public DocumentContext? GetCurrentDocument() => document;

	public DocumentDiff? GetDiff() => diff;
}

/// <summary>The explicit, hardened tool allowlist for the assistant: exactly the two read-only tools, named,
/// so the SDK's Empty-mode allowlist and any audit have one authoritative source. Adding a mutating tool here
/// would be a deliberate, reviewable change — the surface is not open-ended.</summary>
public static class AiReadOnlyTools
{
	/// <summary>The <c>getCurrentDoc</c> tool name.</summary>
	public const string GetCurrentDoc = "getCurrentDoc";

	/// <summary>The <c>getDiff</c> tool name.</summary>
	public const string GetDiff = "getDiff";

	/// <summary>The complete allowlist of tools the assistant may use — both read-only, both non-mutating.</summary>
	public static IReadOnlyList<string> Allowlist { get; } = [GetCurrentDoc, GetDiff];
}
