using Microsoft.Extensions.Logging;
using SpecDesk.Contracts;

namespace SpecDesk.Host;

// The workspace-wide search kind (T-078; see the central RegisterMessageHandlers). Distinct from the
// toolbar's in-document search (webview/src/index.ts): this one searches every Markdown file under the
// authorized workspace root, not just the open document.
public sealed partial class HostController
{
	private void RegisterSearchHandlers()
	{
		_messageHandlers.Register(MessageKinds.SearchRequest, OnSearchRequest);
	}

	// Reply (correlated by envelope id) with the bounded set of matches for the query. Runs on a background
	// task because WorkspaceSearch reads file content (I/O-bound, unlike the shallow FileTreeBuilder listing)
	// — a large tree must not stall the outbound sender. Not account-bound and not cancellable mid-flight:
	// WorkspaceSearch's own time/entry/result budgets already bound the work, so this needs none of the
	// generation/CTS machinery the GitHub-backed requests use.
	private void OnSearchRequest(IpcMessage message)
	{
		SearchRequestPayload? payload = SafeGetPayload<SearchRequestPayload>(message);
		string query = payload?.Query?.Trim() ?? string.Empty;
		string? id = message.Id;
		if (query.Length == 0)
		{
			EmitSearchResults(query, [], truncated: false, id);
			return;
		}

		// The same authorized perimeter as the Folder panel's tree.request (OnTreeRequest): the active
		// workspace root, falling back to the open document's folder. Never an explicit webview-supplied
		// path — there is nothing here for a caller to redirect outside it.
		string? root;
		lock (_sync)
		{
			root = _workspaceRoot ?? (_currentPath is not null ? Path.GetDirectoryName(_currentPath) : null);
		}
		if (string.IsNullOrEmpty(root))
		{
			EmitSearchResults(query, [], truncated: false, id);
			return;
		}

		_ = Task.Run(() =>
		{
			WorkspaceSearchOutcome outcome;
			try
			{
				outcome = WorkspaceSearch.Search(root, query);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				_logger.LogWarning(ex, "Could not search the workspace at {Root}", root);
				EmitSearchResults(query, [], truncated: false, id);
				return;
			}
			IReadOnlyList<SearchResultPayload> results = outcome.Hits
				.Select(hit => new SearchResultPayload(hit.Path, hit.Line, hit.Snippet))
				.ToList();
			EmitSearchResults(query, results, outcome.Truncated, id);
		});
	}

	private void EmitSearchResults(
		string query, IReadOnlyList<SearchResultPayload> results, bool truncated, string? id) =>
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.SearchResults, new SearchResultsPayload(query, results, truncated), id: id));
}
