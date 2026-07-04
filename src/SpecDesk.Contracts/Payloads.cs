namespace SpecDesk.Contracts;

/// <summary>
/// The dotted message kinds exchanged for the PoC-2 editor/preview flow. The full protocol is
/// documented in docs/design/09-ipc-protocol.md; constants keep the host and tests from drifting
/// on string literals.
/// </summary>
public static class MessageKinds
{
	// Kinds follow `domain.verb`; the cross-cutting channels (ready / log / error / status) stay bare.
	// webview → native
	public const string Ready = "ready";
	public const string EditorChanged = "editor.changed";
	public const string DocOpen = "doc.open";
	public const string DocSave = "doc.save";
	public const string DocEdit = "doc.edit";
	public const string DocSaveVersion = "doc.saveVersion";
	public const string DocSendForReview = "doc.sendForReview";
	public const string DocUpdateReview = "doc.updateReview";
	public const string ReviewRefresh = "review.refresh";
	public const string DocDiscard = "doc.discard";
	public const string BranchNameRequest = "branch.name.request";
	public const string VersionNoteRequest = "version.note.request";
	public const string PrSuggestedRequest = "pr.suggested.request";
	public const string PrListRequest = "pr.list.request";
	public const string ImagePaste = "image.paste";
	public const string Log = "log";
	public const string LogExport = "log.export";
	public const string LinkOpen = "link.open";
	public const string DiffRequest = "diff.request";
	public const string GitHubSignIn = "github.signIn";
	public const string GitHubSignInCancel = "github.signInCancel";
	public const string GitHubSignOut = "github.signOut";

	// native → webview
	public const string DocLoaded = "doc.loaded";
	public const string PreviewHtml = "preview.html";
	public const string ImageInserted = "image.inserted";
	public const string BranchNameSuggested = "branch.name.suggested";
	public const string VersionNoteSuggested = "version.note.suggested";
	public const string PrSuggested = "pr.suggested";
	public const string PrList = "pr.list";
	public const string Status = "status";
	public const string Error = "error";
	public const string DiffResult = "diff.result";
	public const string GitHubCode = "github.code";
	public const string GitHubAccount = "github.account";
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

/// <summary>Payload of <c>doc.edit</c> (webview→native): the author's chosen draft (branch) name.
/// <c>null</c>/empty means "use the generated name". The host sanitizes it to a valid git ref.</summary>
public sealed record EditPayload(string? BranchName);

/// <summary>Payload of <c>branch.name.suggested</c> (native→webview): the generated, editable draft
/// (branch) name to prefill the "name this draft" prompt shown on Edit.</summary>
public sealed record BranchNameSuggestedPayload(string Name);

/// <summary>Payload of <c>doc.saveVersion</c> (webview→native): the author's version note (the
/// commit message in plain words) for the explicit "Save a version" commit.</summary>
public sealed record SaveVersionPayload(string Note);

/// <summary>Payload of <c>doc.sendForReview</c> (webview→native): the author-confirmed, outward-facing
/// pull-request <paramref name="Title"/> and <paramref name="Body"/>, edited from the host's suggestion
/// before submit. Either may be blank (the author cleared it) — the host falls back to a generated title
/// so GitHub never rejects an empty title, and allows an empty body. Absent (a bare send) also falls back
/// to the generated text, so the round-trip stays robust if the confirm step is skipped.</summary>
public sealed record SendForReviewPayload(string Title, string Body);

/// <summary>Payload of <c>pr.suggested</c> (native→webview): whether the review can be sent right now and,
/// if so, the generated, editable pull-request <paramref name="Title"/> and <paramref name="Body"/> to
/// prefill the "send for review" confirm prompt. <paramref name="Blocked"/> is a plain-language reason the
/// send can't proceed (not connected, not a GitHub repo, no saved version) — non-null means the webview
/// shows it and does NOT open the prompt, so the author never composes text into a send that would be
/// rejected. Null means ready: open the prompt with the title/body. Reviewer-facing but git-vocabulary-free.</summary>
public sealed record PrSuggestedPayload(string Title, string Body, string? Blocked);

/// <summary>Payload of <c>version.note.suggested</c> (native→webview): the generated, editable
/// version note to prefill the "Save a version" prompt.</summary>
public sealed record VersionNoteSuggestedPayload(string Note);

/// <summary>One open review in the author's review list (native→webview, inside <see cref="PrListPayload"/>).
/// <paramref name="Repo"/> is <c>owner/name</c>; <paramref name="Role"/> is <c>author</c> (they opened it) or
/// <c>reviewer</c> (they were asked to review); <paramref name="Status"/> is the wire review-state name
/// (<c>inReview</c> / <c>changesRequested</c> / <c>approved</c>) for styling, and <paramref name="Label"/> is
/// its author-facing text — host-authoritative (from the same source as the status bar), so the panel never
/// re-implements the vocabulary. No git vocabulary reaches the author.</summary>
public sealed record PrListItemPayload(
    int Number, string Title, string Url, string Repo, string Role, string Status, string Label);

/// <summary>Payload of <c>pr.list</c> (native→webview, correlated to <c>pr.list.request</c> by id): the open
/// pull requests the signed-in user is involved in (as author or requested reviewer), most recently updated
/// first. <paramref name="Error"/> is a plain-language reason the list couldn't be loaded (not connected, a
/// transport failure) — non-null means <paramref name="Items"/> is empty and the webview shows the reason.</summary>
public sealed record PrListPayload(IReadOnlyList<PrListItemPayload> Items, string? Error);

/// <summary>Payload of <c>log</c> (webview→native): a structured log record routed to the host logger.
/// <paramref name="Level"/> is one of debug/info/warn/error; <paramref name="Data"/> is optional JSON.</summary>
public sealed record LogPayload(string Level, string Message, string? Data);

/// <summary>Payload of <c>link.open</c> (webview→native): a link the author clicked in the
/// rendered/formatted view. The host re-validates the scheme and only ever opens absolute http/https
/// (in the browser) or mailto: (in the mail client) URLs — the webview is untrusted, so a
/// javascript:/file:/data: URL cannot reach the shell, and a mailto: query is stripped.</summary>
public sealed record OpenExternalPayload(string Url);

/// <summary>
/// One changed child (table row / list item) of a changed container (native→webview, inside a
/// <see cref="DiffEntryPayload"/>'s <c>Children</c>). Ordinals match the container's rendered children.
/// For added/changed/moved, <paramref name="ChildIndex"/> is the 0-based HEAD child ordinal; for removed,
/// <paramref name="AnchorIndex"/> is the head child it sat before and <paramref name="RemovedText"/> is the
/// base child's flattened text (ChildIndex is unused). <paramref name="BaseText"/> is the base child's
/// flattened text for a changed child (inline word-diff inside the row/item); "" otherwise.
/// </summary>
public sealed record ChildDiffPayload(
    string Kind,
    int ChildIndex,
    int AnchorIndex,
    string RemovedText,
    string BaseText);

/// <summary>
/// One changed top-level block in a rendered diff (native→webview, inside <see cref="DiffResultPayload"/>).
/// Unchanged blocks are omitted. <paramref name="Kind"/> is added/removed/changed/moved.
/// For added/changed/moved, <paramref name="LineStart"/>/<paramref name="LineEnd"/> are the 0-based,
/// inclusive HEAD source-line range of the (after) block; the webview decorates those lines/blocks.
/// For removed, the block is not in the head, so <paramref name="AnchorLine"/> is the head line it sat
/// before and <paramref name="RemovedText"/> is its base source (for a marker); LineStart/LineEnd are unused.
/// <paramref name="Children"/> is non-empty only for a changed list/table whose individual rows/items
/// changed — then the UI highlights those children instead of washing the whole container.
/// <paramref name="BaseText"/> / <paramref name="BaseSource"/> are the base rendered text and base raw
/// source of a changed plain block (paragraph/heading), for the webview's inline word-diff in the
/// Formatted and Code panes respectively; "" otherwise.
/// </summary>
public sealed record DiffEntryPayload(
    string Kind,
    int LineStart,
    int LineEnd,
    int AnchorLine,
    string RemovedText,
    IReadOnlyList<ChildDiffPayload> Children,
    string BaseText,
    string BaseSource);

/// <summary>Payload of <c>diff.result</c> (native→webview): the changed blocks of the working copy vs the
/// last committed version, in document order. The editor-content version rides on the envelope so the
/// webview can drop a result the document has already been edited past.</summary>
public sealed record DiffResultPayload(IReadOnlyList<DiffEntryPayload> Entries);

/// <summary>Payload of <c>github.code</c> (native→webview): the one-time code the author types at
/// <paramref name="VerificationUri"/> to connect their GitHub account. Shown verbatim; never a secret
/// token (the access token stays inside SpecDesk.GitHub).</summary>
public sealed record GitHubCodePayload(string UserCode, string VerificationUri);

/// <summary>
/// Payload of <c>github.account</c> (native→webview): the GitHub connection state for the account
/// affordance. <paramref name="Available"/> is false when sign-in isn't configured (no client id) — the
/// UI hides the affordance entirely. <paramref name="SignedIn"/> with a <paramref name="Login"/> (the
/// GitHub handle, possibly empty if it couldn't be looked up) means connected. <paramref name="Message"/>
/// is an author-facing line for a transient/failed sign-in (e.g. "Sign-in code expired"); never jargon.
/// </summary>
public sealed record GitHubAccountPayload(bool Available, bool SignedIn, string? Login, string? Message);
