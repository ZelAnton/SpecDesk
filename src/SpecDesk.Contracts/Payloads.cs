namespace SpecDesk.Contracts;

/// <summary>
/// The dotted message kinds exchanged for the PoC-2 editor/preview flow. The full protocol is
/// documented in docs/design/09-ipc-protocol.md; constants keep the host and tests from drifting
/// on string literals.
/// </summary>
public static class MessageKinds
{
	// webview → native
	public const string Ready = "ready";
	public const string EditorChanged = "editor.changed";
	public const string ActionOpen = "action.open";
	public const string ActionSave = "action.save";
	public const string ActionEdit = "action.edit";
	public const string ActionSaveVersion = "action.saveVersion";
	public const string ActionDiscard = "action.discard";
	public const string BranchNameRequest = "branch.name.request";
	public const string VersionNoteRequest = "version.note.request";
	public const string ImagePaste = "image.paste";
	public const string Log = "log";
	public const string ExportLog = "action.exportLog";
	public const string ActionOpenExternal = "action.openExternal";

	// native → webview
	public const string DocLoaded = "doc.loaded";
	public const string PreviewHtml = "preview.html";
	public const string ImageInserted = "image.inserted";
	public const string BranchNameSuggested = "branch.name.suggested";
	public const string VersionNoteSuggested = "version.note.suggested";
	public const string Status = "status";
	public const string Error = "error";
}

/// <summary>Payload of <c>editor.changed</c> (webview→native). The version rides on the envelope.</summary>
public sealed record EditorChangedPayload(string Text);

/// <summary>One rendered top-level block's 0-based, inclusive source line range.</summary>
public sealed record LineSpan(int LineStart, int LineEnd);

/// <summary>Payload of <c>preview.html</c> (native→webview). The version rides on the envelope.</summary>
public sealed record PreviewPayload(string Html, IReadOnlyList<LineSpan> LineMap);

/// <summary>
/// Payload of <c>doc.loaded</c> (native→webview): a file opened from disk. <c>DocDir</c> is the
/// document's directory relative to the repo root (forward slashes, "" at root) — the webview uses
/// it to resolve relative image links to <c>app://repo/…</c> in the formatted (WYSIWYG) view, the
/// same rewrite the native preview renderer applies.
/// </summary>
public sealed record DocLoadedPayload(string Path, string Text, string DocDir);

/// <summary>Payload of <c>error</c> (native→webview): a plain-language message, never a stack trace.</summary>
public sealed record ErrorPayload(string Message);

/// <summary>Payload of <c>image.paste</c> (webview→native): one captured image as base64.</summary>
public sealed record ImagePastePayload(string Base64, string? OriginalName, string? Mime);

/// <summary>Payload of <c>image.inserted</c> (native→webview): the Markdown link to insert
/// (empty when the image could not be processed).</summary>
public sealed record ImageInsertedPayload(string Markdown);

/// <summary>
/// Payload of <c>status</c> (native→webview): the document lifecycle state surfaced to the author.
/// <paramref name="State"/> is the wire state name (published/draft/inReview/changesRequested/
/// approved) for styling; <paramref name="Label"/> is the author-facing text to display (including
/// transient "Unsaved changes" / "Version saved"); <paramref name="Branch"/> is the working branch
/// name, diagnostic only and never shown to the author. Git vocabulary stays out of the UI by design.
/// </summary>
public sealed record StatusPayload(string State, string Label, string? Branch);

/// <summary>Payload of <c>action.edit</c> (webview→native): the author's chosen draft (branch) name.
/// <c>null</c>/empty means "use the generated name". The host sanitizes it to a valid git ref.</summary>
public sealed record EditPayload(string? BranchName);

/// <summary>Payload of <c>branch.name.suggested</c> (native→webview): the generated, editable draft
/// (branch) name to prefill the "name this draft" prompt shown on Edit.</summary>
public sealed record BranchNameSuggestedPayload(string Name);

/// <summary>Payload of <c>action.saveVersion</c> (webview→native): the author's version note (the
/// commit message in plain words) for the explicit "Save a version" commit.</summary>
public sealed record SaveVersionPayload(string Note);

/// <summary>Payload of <c>version.note.suggested</c> (native→webview): the generated, editable
/// version note to prefill the "Save a version" prompt.</summary>
public sealed record VersionNoteSuggestedPayload(string Note);

/// <summary>Payload of <c>log</c> (webview→native): a structured log record routed to the host logger.
/// <paramref name="Level"/> is one of debug/info/warn/error; <paramref name="Data"/> is optional JSON.</summary>
public sealed record LogPayload(string Level, string Message, string? Data);

/// <summary>Payload of <c>action.openExternal</c> (webview→native): a link the author clicked in the
/// rendered/formatted view. The host re-validates the scheme and only ever opens absolute http/https
/// URLs in the OS default browser — the webview is untrusted, so a javascript:/file:/data: URL cannot
/// reach the shell.</summary>
public sealed record OpenExternalPayload(string Url);
