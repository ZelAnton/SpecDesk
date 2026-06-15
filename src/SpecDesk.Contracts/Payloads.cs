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

	// native → webview
	public const string DocLoaded = "doc.loaded";
	public const string PreviewHtml = "preview.html";
	public const string Error = "error";
}

/// <summary>Payload of <c>editor.changed</c> (webview→native). The version rides on the envelope.</summary>
public sealed record EditorChangedPayload(string Text);

/// <summary>One rendered top-level block's 0-based, inclusive source line range.</summary>
public sealed record LineSpan(int LineStart, int LineEnd);

/// <summary>Payload of <c>preview.html</c> (native→webview). The version rides on the envelope.</summary>
public sealed record PreviewPayload(string Html, IReadOnlyList<LineSpan> LineMap);

/// <summary>Payload of <c>doc.loaded</c> (native→webview): a file opened from disk.</summary>
public sealed record DocLoadedPayload(string Path, string Text);

/// <summary>Payload of <c>error</c> (native→webview): a plain-language message, never a stack trace.</summary>
public sealed record ErrorPayload(string Message);
