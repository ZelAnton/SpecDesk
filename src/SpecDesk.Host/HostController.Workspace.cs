using Microsoft.Extensions.Logging;
using SpecDesk.AppInfo;
using SpecDesk.Contracts;
using SpecDesk.Git;
using SpecDesk.GitHub;
// LibGit2Sharp is referenced only for its exception type; do not bring the whole namespace in (it defines a
// LogLevel that collides with Microsoft.Extensions.Logging.LogLevel).
using LibGit2SharpException = LibGit2Sharp.LibGit2SharpException;

namespace SpecDesk.Host;

// The workspace-state slice of HostController (A4): the persisted recents / favorites / registered GitHub
// repositories, served and mutated over IPC (workspace.request → workspace.state; workspace.favorite /
// repo.register / repo.unregister mutate then re-emit workspace.state). Recents are recorded as a side effect
// of opening a file (LoadFile) or a folder (OnOpenFolder). All handlers are inert when _workspace is null,
// the same graceful-degradation pattern as the chat/auth slices. A4 only STORES a registered repo — no GitHub
// cloning yet. The shared fields, locks, constructor, and the IPC router live in HostController.cs.
public sealed partial class HostController
{
	private enum PendingRepoActionKind
	{
		Register,
		Open,
	}

	private sealed record PendingRepoAction(PendingRepoActionKind Kind, string Owner, string Name);
	private sealed record PendingRepoActions(
		PendingRepoAction[] Registrations,
		PendingRepoAction? Open);

	// Repository access is an authenticated GitHub operation. Requests made while signed out are retained
	// until the device-flow completes, so clicking Add/Open opens GitHub in the normal browser and then
	// continues the exact action instead of making the author repeat it.
	private readonly Dictionary<string, PendingRepoAction> _pendingRepoRegistrations =
		new(StringComparer.OrdinalIgnoreCase);
	private PendingRepoAction? _pendingRepoOpen;

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

		if (!EnsureGitHubAccess(new PendingRepoAction(PendingRepoActionKind.Register, owner, name)))
		{
			return;
		}

		RegisterRepoCore(owner, name);
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

	// Cancels an in-flight repository clone (window teardown). Guarded by _sync; a non-null value also
	// single-flights the clone, so only one runs at a time — a repo open can take many seconds. Cancelled and
	// nulled on Dispose (which lets the task's own finally dispose it), mirroring _chatCts / _signInCts.
	private CancellationTokenSource? _cloneCts;

	// A6 "Open a GitHub repository": clone it into the managed repos folder (AppPaths.Repos) and open that
	// folder as the workspace. A repo already cloned is opened straight away (no network, no background task);
	// an un-cloned one clones on a background task — it can take many seconds — single-flighted with a CTS
	// cancelled on teardown, mirroring the chat slice. Inert when the cloner isn't wired (graceful, like the
	// other optional dependencies). The GitHub access token, when signed in, is taken transiently
	// (WithAccessTokenAsync) for the clone and is never stored or logged here.
	private void OnOpenRepo(IpcMessage message)
	{
		RepoOpenPayload? payload = SafeGetPayload<RepoOpenPayload>(message);
		if (payload is null)
		{
			return;
		}

		if (!TryParseGitHubRepo(payload.Url, out string owner, out string name))
		{
			SendError("That doesn't look like a GitHub repository.");
			return;
		}

		if (_cloner is null)
		{
			// No cloner configured — nothing to open (graceful degradation, like a null _workspace / _auth).
			return;
		}

		if (!EnsureGitHubAccess(new PendingRepoAction(PendingRepoActionKind.Open, owner, name)))
		{
			return;
		}

		OpenRepoCore(owner, name);
	}

	private void OpenRepoCore(string owner, string name)
	{
		IRepositoryCloner? cloner = _cloner;
		if (cloner is null)
		{
			return;
		}
		string cloneUrl = $"https://github.com/{owner}/{name}.git";
		string repoId = $"{owner}/{name}";
		// The clone's exact local folder: namespaced by BOTH owner and name, so two repos that share a name
		// across different owners (acme/specs vs globex/specs) never collide on disk. The host owns this path
		// and hands it to the cloner, so the "already cloned?" check and the clone target can't drift apart.
		string localPath = Path.Combine(AppPaths.Repos, $"{owner}_{name}");

		if (cloner.IsCloned(localPath))
		{
			// Already cloned (a valid working tree, not just any leftover folder) — open it straight away
			// (synchronous, no network). Register it too so a repo opened from an existing clone still lands in
			// the picker.
			RegisterOpenedRepo(owner, name);
			OpenWorkspaceFolder(localPath);
			return;
		}

		// Single-flight the clone (guarded by _sync), cancelled on dispose — mirrors the chat turn. Drop a
		// second request while one is already in flight rather than running two concurrent clones.
		CancellationTokenSource cts;
		lock (_sync)
		{
			if (_cloneCts is not null)
			{
				_logger.LogDebug("Ignoring a repo open while a clone is already in flight");
				return;
			}

			cts = new CancellationTokenSource();
			_cloneCts = cts;
		}

		CancellationToken token = cts.Token;
		IGitHubAuth? auth = _auth;
		_ = Task.Run(async () =>
		{
			try
			{
				// A signed-in session clones with the access token so a private repo authenticates (the token
				// is confined to WithAccessTokenAsync's scope); otherwise clone anonymously (public repos). The
				// clone itself is synchronous, so it runs on its own Task.Run inside the token scope.
				string localClone;
				if (auth is not null && auth.IsSignedIn())
				{
					localClone = await auth.WithAccessTokenAsync(
						(accessToken, innerCt) =>
							Task.Run(() => cloner.CloneOrReuse(cloneUrl, localPath, accessToken, innerCt), innerCt),
						token);
				}
				else
				{
					localClone = await Task.Run(() => cloner.CloneOrReuse(cloneUrl, localPath, null, token), token);
				}

				// Register + surface the freshly cloned repo, then open it as the workspace. Emit is
				// thread-safe; OpenWorkspaceFolder's _workspaceRoot mutation takes _sync itself.
				RegisterOpenedRepo(owner, name);
				OpenWorkspaceFolder(localClone);
				_logger.LogInformation("Opened repository {Repo} at {Path}", repoId, localClone);
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				// The window is tearing down (or the open was cancelled) — stay quiet; the webview is gone.
			}
			catch (Exception ex) when (
				ex is LibGit2SharpException or InvalidOperationException or IOException or UnauthorizedAccessException)
			{
				// The clone failed (a missing/private repo, an auth refusal, a network or disk fault) — one
				// plain line, never a token or a stack trace. InvalidOperationException also covers
				// ResolveCredentials refusing a non-GitHub host, and WithAccessTokenAsync when signed out.
				_logger.LogError(ex, "Could not open the repository {Repo}", repoId);
				SendError("Could not open that repository. Check the name and your connection, then try again.");
			}
			finally
			{
				lock (_sync)
				{
					if (ReferenceEquals(_cloneCts, cts))
					{
						_cloneCts = null;
					}
				}

				cts.Dispose();
			}
		});
	}

	private bool EnsureGitHubAccess(PendingRepoAction action)
	{
		lock (_signInPublishSync)
		{
			lock (_sync)
			{
				if (_disposed)
				{
					return false;
				}
			}
			if (_auth is null)
			{
				_logger.LogWarning(
					"GitHub access is not configured; set {ClientIdVariable} for development builds",
					GitHubAuthOptions.ClientIdEnvironmentVariable);
				SendError("GitHub access isn't available in this build. Ask your administrator for help.");
				return false;
			}
		}

		bool startSignIn;
		lock (_sync)
		{
			if (_disposed)
			{
				return false;
			}
			// Check the persisted auth state under the same lock used by ResumePendingRepoActions. Without
			// this atomic check+enqueue, authorization could finish between them, drain an empty queue, and
			// leave the newly enqueued action stranded behind a flow that is already complete.
			if (_auth.IsSignedIn())
			{
				return true;
			}

			if (action.Kind == PendingRepoActionKind.Register)
			{
				// Repeated clicks for the same repository collapse to one durable registration request.
				_pendingRepoRegistrations[$"{action.Owner}/{action.Name}"] = action;
			}
			else
			{
				// Opening a repository changes the single central workspace. A later Open supersedes an
				// earlier one while authorization is pending, matching ordinary navigation semantics and
				// avoiding a burst of clones after the author returns from GitHub.
				_pendingRepoOpen = action;
			}

			startSignIn = _signInCts is null;
		}

		if (startSignIn)
		{
			OnGitHubSignIn();
		}

		return false;
	}

	private PendingRepoActions TakePendingRepoActions()
	{
		PendingRepoActions actions = new([.. _pendingRepoRegistrations.Values], _pendingRepoOpen);
		_pendingRepoRegistrations.Clear();
		_pendingRepoOpen = null;
		return actions;
	}

	private void ResumePendingRepoActions(PendingRepoActions actions)
	{
		foreach (PendingRepoAction action in actions.Registrations)
		{
			RegisterRepoCore(action.Owner, action.Name);
		}

		if (actions.Open is not null)
		{
			OpenRepoCore(actions.Open.Owner, actions.Open.Name);
		}
	}

	private void ClearPendingRepoActions()
	{
		lock (_sync)
		{
			_pendingRepoRegistrations.Clear();
			_pendingRepoOpen = null;
		}
	}

	private void RegisterRepoCore(string owner, string name)
	{
		string id = $"{owner}/{name}";
		_workspace?.RegisterRepo(new RegisteredRepo(id, id, $"https://github.com/{owner}/{name}"));
		EmitWorkspaceState();
	}

	// Register the just-opened repo (de-duplicated by id in the store) and re-emit workspace.state, so opening
	// a GitHub repo also keeps it in the Repositories picker. Same normalized shape as OnRegisterRepo.
	private void RegisterOpenedRepo(string owner, string name)
	{
		string id = $"{owner}/{name}";
		_workspace?.RegisterRepo(new RegisteredRepo(id, id, $"https://github.com/{owner}/{name}"));
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
