using System.Text.Json.Serialization;

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
	public const string TraceDump = "trace.dump";
	public const string LinkOpen = "link.open";
	public const string DiffRequest = "diff.request";
	public const string GitHubSignIn = "github.signIn";
	public const string GitHubSignInCancel = "github.signInCancel";
	public const string GitHubSignOut = "github.signOut";
	public const string ChatSend = "chat.send";
	public const string TemplatesRequest = "templates.request";
	public const string FolderOpen = "folder.open";
	public const string TreeRequest = "tree.request";
	public const string WorkspaceRequest = "workspace.request";
	public const string WorkspaceFavorite = "workspace.favorite";
	public const string RepoRegister = "repo.register";
	public const string RepoUnregister = "repo.unregister";
	public const string RepoOpen = "repo.open";

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
	public const string ChatDelta = "chat.delta";
	public const string ChatDone = "chat.done";
	public const string Templates = "templates";
	public const string Tree = "tree";
	public const string WorkspaceState = "workspace.state";
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

/// <summary>
/// Payload of <c>doc.open</c> (webview→native). <c>Path</c> opens that specific file directly (the Start
/// screen's "open a file", or a click in the folder tree); <c>null</c> falls back to the native open dialog.
/// </summary>
public sealed record DocOpenPayload(string? Path);

/// <summary>
/// Payload of <c>folder.open</c> (webview→native). <c>Path</c> opens that folder as the workspace directly;
/// <c>null</c> falls back to the native folder-picker dialog. Opening a folder makes its Markdown tree the
/// left rail's file navigator (a <c>tree</c> event follows).
/// </summary>
public sealed record FolderOpenPayload(string? Path);

/// <summary>
/// Payload of <c>tree.request</c> (webview→native): request the Markdown file tree. <c>Path</c> scopes it to
/// a folder; <c>null</c> uses the current workspace folder (else the open document's folder).
/// </summary>
public sealed record TreeRequestPayload(string? Path);

/// <summary>
/// One node of the file tree (native→webview). A directory has <c>IsDirectory=true</c> and its
/// <c>Children</c>; a file has no children (an empty list). <c>Path</c> is the absolute path (opened via
/// <c>doc.open</c> when a file is clicked); <c>Name</c> is the display label (the file/folder name).
/// </summary>
public sealed record TreeNode(string Name, string Path, bool IsDirectory, IReadOnlyList<TreeNode> Children);

/// <summary>
/// Payload of <c>tree</c> (native→webview): the workspace folder's Markdown file tree. <c>Root</c> is the
/// folder's absolute path (its display name is the last segment); <c>Nodes</c> are its top-level entries.
/// </summary>
public sealed record TreePayload(string Root, IReadOnlyList<TreeNode> Nodes);

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

/// <summary>One entry of a <c>trace.dump</c> (webview→native): a flattened webview trace-ring entry.
/// <paramref name="T"/> is <c>performance.now()</c> ms; wall-clock time is <c>T0Epoch + T</c> (see
/// <see cref="TraceDumpPayload"/>). <paramref name="Data"/> is the entry's structural data, already
/// JSON-stringified and capped webview-side.</summary>
public sealed record TraceEntryPayload(long Seq, double T, string Cat, string Event, string? Data);

/// <summary>Payload of <c>trace.dump</c> (webview→native): a snapshot of the in-page trace ring, sent
/// when the author exports the log so the host can persist it beside the Serilog file and append its
/// tail to the export. <paramref name="T0Epoch"/> (<c>Date.now() - performance.now()</c> at ring init)
/// reconstructs each entry's wall-clock time as <c>T0Epoch + Entry.T</c>.</summary>
public sealed record TraceDumpPayload(double T0Epoch, long FirstSeq, IReadOnlyList<TraceEntryPayload> Entries);

/// <summary>Payload of <c>link.open</c> (webview→native): a link the author clicked in the
/// rendered/formatted view. The host re-validates the scheme and only ever opens absolute http/https
/// (in the browser) or mailto: (in the mail client) URLs — the webview is untrusted, so a
/// javascript:/file:/data: URL cannot reach the shell, and a mailto: query is stripped.</summary>
public sealed record OpenExternalPayload(string Url);

/// <summary>
/// One changed child (table row / list item) of a changed container (native→webview, inside a
/// <see cref="ChangedDiffEntry"/>'s <c>Children</c>). Ordinals match the container's rendered children.
/// Discriminated by <c>kind</c> on the wire — each derived record serializes a <c>kind</c> property plus
/// ONLY its own fields, so an illegal shape (a removed child with a head ordinal, a changed child with no
/// base) can't be encoded with sentinels: it is simply not a valid record. The webview mirrors this as a
/// discriminated union in protocol.ts.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AddedChildDiff), "added")]
[JsonDerivedType(typeof(MovedChildDiff), "moved")]
[JsonDerivedType(typeof(ChangedChildDiff), "changed")]
[JsonDerivedType(typeof(RemovedChildDiff), "removed")]
public abstract record ChildDiffPayload;

/// <summary>An added child: the 0-based HEAD child ordinal the new row/item occupies.</summary>
public sealed record AddedChildDiff(int ChildIndex) : ChildDiffPayload;

/// <summary>A moved child: the 0-based HEAD child ordinal the reordered row/item now occupies.</summary>
public sealed record MovedChildDiff(int ChildIndex) : ChildDiffPayload;

/// <summary>A changed child: its 0-based HEAD child ordinal, the base child's flattened text
/// (<paramref name="BaseText"/>, the Formatted pane's inline word-diff inside the row/item) and the base
/// child's raw source slice (<paramref name="BaseSource"/>, the Code pane's inline word-diff) — symmetric
/// to <see cref="ChangedDiffEntry"/>'s BaseText/BaseSource for a whole changed block.</summary>
public sealed record ChangedChildDiff(int ChildIndex, string BaseText, string BaseSource) : ChildDiffPayload;

/// <summary>A removed child: the head child it sat before (<paramref name="AnchorIndex"/>) and the deleted
/// child's flattened text (<paramref name="RemovedText"/>, for the marker). It has no head ordinal.</summary>
public sealed record RemovedChildDiff(int AnchorIndex, string RemovedText) : ChildDiffPayload;

/// <summary>
/// One changed top-level block in a rendered diff (native→webview, inside <see cref="DiffResultPayload"/>).
/// Unchanged blocks are omitted. Discriminated by <c>kind</c> on the wire — each derived record serializes
/// a <c>kind</c> property plus ONLY its own fields, so an illegal shape (a removed block with a head line
/// range, a changed block with no base) is unrepresentable rather than sentinel-encoded. The webview keys
/// styling / labels off <c>kind</c> and mirrors this as a discriminated union in protocol.ts.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AddedDiffEntry), "added")]
[JsonDerivedType(typeof(MovedDiffEntry), "moved")]
[JsonDerivedType(typeof(ChangedDiffEntry), "changed")]
[JsonDerivedType(typeof(RemovedDiffEntry), "removed")]
public abstract record DiffEntryPayload;

/// <summary>An added block: its 0-based, inclusive HEAD source-line range (the webview decorates it).</summary>
public sealed record AddedDiffEntry(int LineStart, int LineEnd) : DiffEntryPayload;

/// <summary>A moved block: its 0-based, inclusive HEAD source-line range (the reordered head position).</summary>
public sealed record MovedDiffEntry(int LineStart, int LineEnd) : DiffEntryPayload;

/// <summary>
/// A changed block: its 0-based, inclusive HEAD source-line range plus, for a changed list/table whose
/// individual rows/items changed, the per-child <paramref name="Children"/> (the UI highlights those
/// instead of washing the whole container). <paramref name="BaseText"/> / <paramref name="BaseSource"/>
/// are the base rendered text and base raw source of a changed plain block (paragraph/heading), for the
/// webview's inline word-diff in the Formatted and Code panes respectively; "" for a container that
/// descended to children.
/// </summary>
public sealed record ChangedDiffEntry(
    int LineStart,
    int LineEnd,
    IReadOnlyList<ChildDiffPayload> Children,
    string BaseText,
    string BaseSource) : DiffEntryPayload;

/// <summary>
/// A removed block: not in the head, so it carries the head line it sat before
/// (<paramref name="AnchorLine"/>) and its base source text (<paramref name="RemovedText"/>, for a marker).
/// It has no head line range.
/// </summary>
public sealed record RemovedDiffEntry(int AnchorLine, string RemovedText) : DiffEntryPayload;

/// <summary>
/// Diagnostic signal on <see cref="DiffResultPayload"/>: the (base, head) pair overflowed AstDiff's
/// node-pair size guard (<c>maxNodePairs</c>) and fell back to a flat, coarse Removed+Added listing —
/// sent as this compact count INSTEAD of enumerating every base/head block, which would ship every removed
/// block's full text over IPC and paint thousands of decorations in the webview. <see
/// cref="DiffResultPayload.Entries"/> is empty whenever this is present.
/// </summary>
public sealed record DiffOverflowPayload(int RemovedCount, int AddedCount);

/// <summary>Payload of <c>diff.result</c> (native→webview): the changed blocks of the working copy vs the
/// last committed version, in document order. The editor-content version rides on the envelope so the
/// webview can drop a result the document has already been edited past. <see cref="Overflow"/> is present
/// only for a pair too large to diff in detail (see <see cref="DiffOverflowPayload"/>), in which case
/// <paramref name="Entries"/> is empty.</summary>
public sealed record DiffResultPayload(IReadOnlyList<DiffEntryPayload> Entries, DiffOverflowPayload? Overflow = null);

/// <summary>
/// Wire values for <c>diff.request</c>'s <see cref="DiffRequestPayload.Base"/> (mirror of the webview's
/// <c>DiffBaseKind</c> in protocol.ts). Only <see cref="LastVersion"/> is wired today — the local "Show
/// changes" compare (working copy vs the last saved version, PoC-6); <see cref="Published"/> (vs the
/// published/main version) and <see cref="Pr"/> (vs an open pull request's head) are reserved for PoC-7's
/// in-flight-review compares and are not yet implemented by <c>HostController.OnCompare</c>.
/// </summary>
public static class DiffBaseKinds
{
	public const string LastVersion = "lastVersion";
	public const string Published = "published";
	public const string Pr = "pr";
}

/// <summary>Payload of <c>diff.request</c> (webview→native): which base to diff the working copy against
/// (the webview overlay owns this choice — see <see cref="DiffBaseKinds"/>). <paramref name="Pr"/> is the
/// pull request number, present only when <paramref name="Base"/> is <see cref="DiffBaseKinds.Pr"/>.</summary>
public sealed record DiffRequestPayload(string Base, int? Pr = null);

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

/// <summary>Payload of <c>chat.send</c> (webview→native): the author's message to the AI assistant
/// (see docs/design/08-ai-agent.md). The host streams the reply back as <see cref="ChatDeltaPayload"/>
/// chunks followed by a terminal <see cref="ChatDonePayload"/>.</summary>
public sealed record ChatSendPayload(string Text);

/// <summary>Payload of <c>chat.delta</c> (native→webview): one streamed chunk of the assistant's reply.
/// Chunks arrive in order and are appended to the in-progress assistant message until <c>chat.done</c>.
/// Unsolicited (carries no correlation id): the webview shows a single streaming turn at a time.</summary>
public sealed record ChatDeltaPayload(string Text);

/// <summary>Payload of <c>chat.done</c> (native→webview): the assistant turn identified by <paramref
/// name="Id"/> has finished streaming. The webview re-enables the composer and finalizes the message.
/// The <paramref name="Id"/> is a host-assigned per-turn token so a late/duplicate done can be ignored.</summary>
public sealed record ChatDonePayload(string Id);

/// <summary>One prompt-library entry (shared by the personal store, the remote source, and the wire).
/// <paramref name="Id"/> is a stable identifier, <paramref name="Title"/> the picker label, and
/// <paramref name="Body"/> the prompt text inserted into the chat composer when chosen.</summary>
public sealed record PromptTemplate(string Id, string Title, string Body);

/// <summary>Payload of <c>templates</c> (native→webview, correlated to <c>templates.request</c> by id):
/// the prompt library available to insert into the chat composer. <paramref name="Personal"/> is the
/// author's local, host-owned library; <paramref name="Remote"/> is fetched from a configured URL and is
/// empty when none is configured or the fetch fails (the request degrades gracefully, never errors).</summary>
public sealed record TemplatesPayload(
	IReadOnlyList<PromptTemplate> Personal,
	IReadOnlyList<PromptTemplate> Remote);

/// <summary>One recently-opened or favorited entry (native→webview, inside <see cref="WorkspaceStatePayload"/>).
/// <paramref name="Path"/> is the absolute file/folder path (opened via <c>doc.open</c>/<c>folder.open</c> when
/// chosen); <paramref name="Label"/> is the display name (usually the last path segment); <paramref name="IsFolder"/>
/// distinguishes a folder from a file.</summary>
public sealed record WorkspaceItem(string Path, string Label, bool IsFolder);

/// <summary>One registered GitHub repository the author works with (native→webview, inside
/// <see cref="WorkspaceStatePayload"/>). A4 only stores the entry — no cloning yet. <paramref name="Id"/> is a
/// stable key (<c>owner/name</c>); <paramref name="Name"/> is the display (<c>owner/name</c>);
/// <paramref name="Url"/> is the normalized <c>https://github.com/owner/name</c> URL.</summary>
public sealed record RegisteredRepo(string Id, string Name, string Url);

/// <summary>Payload of <c>workspace.state</c> (native→webview): the persisted workspace store — the author's
/// <paramref name="Recent"/> items (most-recent first), their <paramref name="Favorites"/>, and the
/// <paramref name="Repositories"/> they registered. Emitted on request and after every mutation.</summary>
public sealed record WorkspaceStatePayload(
	IReadOnlyList<WorkspaceItem> Recent,
	IReadOnlyList<WorkspaceItem> Favorites,
	IReadOnlyList<RegisteredRepo> Repositories);

/// <summary>Payload of <c>workspace.favorite</c> (webview→native): toggle whether the file/folder at
/// <paramref name="Path"/> is a favorite (<paramref name="Favorite"/> true adds it, false removes it).</summary>
public sealed record WorkspaceFavoritePayload(string Path, bool Favorite);

/// <summary>Payload of <c>repo.register</c> (webview→native): register a GitHub repository from a URL or spec
/// (<c>https://github.com/owner/name(.git)</c>, <c>owner/name</c>, or <c>git@github.com:owner/name(.git)</c>).
/// The host parses/normalizes it before storing.</summary>
public sealed record RegisterRepoPayload(string Url);

/// <summary>Payload of <c>repo.unregister</c> (webview→native): remove the registered repository whose
/// <see cref="RegisteredRepo.Id"/> matches <paramref name="Id"/>.</summary>
public sealed record UnregisterRepoPayload(string Id);

/// <summary>Payload of <c>repo.open</c> (webview→native): open a GitHub repository named by <paramref
/// name="Url"/> (an <c>owner/name</c> or a GitHub URL). The host clones it into a managed local folder — or
/// reuses the clone if it is already there — and opens that folder as the workspace, emitting a <c>tree</c>;
/// an unparseable value is reported as an <c>error</c>.</summary>
public sealed record RepoOpenPayload(string Url);
