using SpecDesk.Contracts;

namespace SpecDesk.Host;

// The workspace-state slice of HostController (A4): the persisted recents / favorites / registered GitHub
// repositories, served and mutated over IPC (workspace.request → workspace.state; workspace.favorite /
// repo.register / repo.unregister mutate then re-emit workspace.state). Recents are recorded as a side effect
// of opening a file (LoadFile) or a folder (OnOpenFolder). All handlers are inert when _workspace is null,
// the same graceful-degradation pattern as the chat/auth slices. A4 only STORES a registered repo — no GitHub
// cloning yet. The shared fields, locks, constructor, and the IPC router live in HostController.cs.
public sealed partial class HostController
{
	// Emit the current workspace state to the webview. A null store (workspace persistence unconfigured) makes
	// this a no-op, so the mutating handlers below can call it unconditionally, like the chat guards.
	private void EmitWorkspaceState()
	{
		if (_workspace is null)
		{
			return;
		}

		Emit(IpcSerializer.SerializeEvent(MessageKinds.WorkspaceState, _workspace.State()));
	}

	// Serve the current workspace state (the Start screen asks for it on load).
	private void OnWorkspaceRequest() => EmitWorkspaceState();

	// Toggle a file/folder as a favorite. The item is reconstructed from the path so the store keeps a display
	// label and the file/folder distinction even though the webview only sends the path + the new flag.
	private void OnWorkspaceFavorite(IpcMessage message)
	{
		WorkspaceFavoritePayload? payload = SafeGetPayload<WorkspaceFavoritePayload>(message);
		if (payload is null || string.IsNullOrWhiteSpace(payload.Path))
		{
			return;
		}

		WorkspaceItem item = new(payload.Path, LabelFor(payload.Path), Directory.Exists(payload.Path));
		_workspace?.SetFavorite(item, payload.Favorite);
		EmitWorkspaceState();
	}

	// Register a GitHub repository from a URL/spec. Parsing/normalization happens here (the store only holds a
	// validated entry); a string that doesn't name a repo is reported plainly and nothing is stored. A4 stores
	// the entry only — no clone.
	private void OnRegisterRepo(IpcMessage message)
	{
		RegisterRepoPayload? payload = SafeGetPayload<RegisterRepoPayload>(message);
		if (payload is null)
		{
			return;
		}

		if (!TryParseGitHubRepo(payload.Url, out string owner, out string name))
		{
			SendError("That doesn't look like a GitHub repository.");
			return;
		}

		string id = $"{owner}/{name}";
		_workspace?.RegisterRepo(new RegisteredRepo(id, id, $"https://github.com/{owner}/{name}"));
		EmitWorkspaceState();
	}

	// Remove a registered repository by its stable id (owner/name).
	private void OnUnregisterRepo(IpcMessage message)
	{
		UnregisterRepoPayload? payload = SafeGetPayload<UnregisterRepoPayload>(message);
		if (payload is null || string.IsNullOrWhiteSpace(payload.Id))
		{
			return;
		}

		_workspace?.UnregisterRepo(payload.Id);
		EmitWorkspaceState();
	}

	// Record a freshly opened file/folder as the most recent, then push the refreshed state. Called from
	// LoadFile (a file) and OnOpenFolder (a folder). Inert when the store is unconfigured.
	private void RecordRecent(string path, bool isFolder)
	{
		if (_workspace is null)
		{
			return;
		}

		_workspace.AddRecent(new WorkspaceItem(path, LabelFor(path), isFolder));
		EmitWorkspaceState();
	}

	// The display label for a path: its last segment (trimming any trailing separator), falling back to the
	// whole path when there is no segment — e.g. a drive root like "C:\", where GetFileName yields "".
	private static string LabelFor(string path)
	{
		string label = Path.GetFileName(path.TrimEnd('/', '\\'));
		return label.Length > 0 ? label : path;
	}

	// Parse a GitHub repo reference into owner/name. Accepts the three forms the register prompt allows:
	// https://github.com/owner/name(.git), a bare owner/name, and git@github.com:owner/name(.git). Pure and
	// static so it is unit-testable apart from the controller. Rejects anything that isn't exactly an
	// owner/name pair of valid (alphanumeric / - / _ / .) segments.
	internal static bool TryParseGitHubRepo(string input, out string owner, out string name)
	{
		owner = string.Empty;
		name = string.Empty;
		if (string.IsNullOrWhiteSpace(input))
		{
			return false;
		}

		string spec = input.Trim();

		// Reduce each accepted form to a bare "owner/name" path. A scheme'd URL MUST be a github.com URL — a
		// non-github host (gitlab.com/…, example.com/…) is rejected outright rather than mis-read as the owner.
		const string scp = "git@github.com:";
		if (spec.StartsWith(scp, StringComparison.OrdinalIgnoreCase))
		{
			spec = spec[scp.Length..];
		}
		else if (spec.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
			|| spec.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
		{
			spec = StripPrefix(spec, "https://");
			spec = StripPrefix(spec, "http://");
			spec = StripPrefix(spec, "www.");
			if (!spec.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			spec = spec["github.com/".Length..];
		}
		else
		{
			// No scheme: a bare "github.com/owner/name" or "owner/name". Peel an optional github.com host.
			spec = StripPrefix(spec, "www.");
			spec = StripPrefix(spec, "github.com/");
		}

		spec = spec.Trim('/');
		if (spec.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
		{
			spec = spec[..^4];
		}

		string[] segments = spec.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		// The owner must obey GitHub's account-name rules (no dots), which also rejects a host-looking first
		// segment (e.g. "gitlab.com") from the scheme-less path; the repo name allows the wider GitHub set.
		if (segments.Length != 2 || !IsValidOwner(segments[0]) || !IsValidRepoName(segments[1]))
		{
			return false;
		}

		owner = segments[0];
		name = segments[1];
		return true;
	}

	private static string StripPrefix(string value, string prefix) =>
		value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;

	// A GitHub account (owner) name: letters/digits/hyphens only (no dots or underscores — GitHub forbids them),
	// and not starting or ending with a hyphen. Rejecting dots also rejects a host masquerading as the owner.
	private static bool IsValidOwner(string segment)
	{
		if (segment.Length == 0 || segment[0] == '-' || segment[^1] == '-')
		{
			return false;
		}

		foreach (char c in segment)
		{
			if (!char.IsAsciiLetterOrDigit(c) && c != '-')
			{
				return false;
			}
		}

		return true;
	}

	// A GitHub repository name: non-empty, not a "." / ".." path token, and only the characters GitHub permits
	// (letters, digits, hyphen, underscore, dot).
	private static bool IsValidRepoName(string segment)
	{
		if (segment.Length == 0 || segment == "." || segment == "..")
		{
			return false;
		}

		foreach (char c in segment)
		{
			if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
			{
				return false;
			}
		}

		return true;
	}
}
