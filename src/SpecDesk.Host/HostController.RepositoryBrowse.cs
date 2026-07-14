using Microsoft.Extensions.Logging;
using SpecDesk.Contracts;
using SpecDesk.Core;
using SpecDesk.GitHub;

namespace SpecDesk.Host;

public sealed partial class HostController
{
	private long _remoteBrowseGeneration;
	private long _remoteBrowseIntentGeneration;
	private string? _remoteBrowseIntentRepoId;
	private long _remoteFileGeneration;
	private CancellationTokenSource? _remoteBrowseCts;
	private CancellationTokenSource? _remoteFileCts;
	private string? _remoteBrowseRepoId;
	private string? _remoteFileRepoId;
	private long _remoteFileRequestId;
	private readonly object _remotePublishSync = new();
	private const int MaxRemotePathChars = 4096;
	private const int MaxRemotePathDepth = 64;
	private const int MaxRemoteTreeNodes = 20000;

	private void OnBrowseRepo(IpcMessage message)
	{
		RepoBrowsePayload? payload = SafeGetPayload<RepoBrowsePayload>(message);
		RegisteredRepo? repo = payload is null ? null : _workspace?.FindRepo(payload.Id);
		if (repo is null || !TryParseGitHubRepo(repo.Url, out string owner, out string name))
		{
			SendError("That repository is no longer registered.");
			return;
		}

		if (_repositoryCatalog is null)
		{
			SendError("Browsing repositories online isn't available in this build.");
			return;
		}

		if (_auth is null)
		{
			SendError("GitHub access isn't available in this build. Ask your administrator for help.");
			return;
		}

		string id = $"{owner}/{name}";
		string? requestedBranch = payload?.Branch;
		long navigationGeneration = ReserveWorkspaceNavigationIntent();
		long intentGeneration = BeginRemoteBrowseIntent(id, navigationGeneration);
		if (intentGeneration == 0
			|| !EnsureGitHubAccess(new PendingRepoAction(
				PendingRepoActionKind.Browse,
				owner,
				name,
				NavigationGeneration: navigationGeneration,
				RemoteGeneration: intentGeneration,
				Branch: requestedBranch)))
		{
			return;
		}

		BrowseRepoCore(owner, name, intentGeneration, navigationGeneration, requestedBranch);
	}

	private long BeginRemoteBrowseIntent(string id, long navigationGeneration)
	{
		long canceledRequestId;
		long intentGeneration;
		CancellationTokenSource? browseCts;
		CancellationTokenSource? fileCts;
		lock (_remotePublishSync)
		{
			lock (_sync)
			{
				if (_disposed)
				{
					return 0;
				}
				_remoteBrowseGeneration++;
				_remoteFileGeneration++;
				browseCts = _remoteBrowseCts;
				fileCts = _remoteFileCts;
				_remoteBrowseRepoId = null;
				_remoteFileRepoId = null;
				canceledRequestId = _remoteFileRequestId;
				_remoteFileRequestId = 0;
				_remoteBrowseIntentRepoId = id;
				intentGeneration = ++_remoteBrowseIntentGeneration;
				AcceptWorkspaceNavigationIntentLocked(navigationGeneration);
				_workspaceRoot = null;
				_workspaceRootGeneration++;
				Emit(IpcSerializer.SerializeEvent(
					MessageKinds.Tree,
					new TreePayload(id, [])));
			}
		}
		browseCts?.Cancel();
		fileCts?.Cancel();
		CompleteDocumentOpen(canceledRequestId, succeeded: false);
		return intentGeneration;
	}
	private void BrowseRepoCore(
		string owner,
		string name,
		long intentGeneration,
		long navigationGeneration,
		string? requestedBranch = null)
	{
		if (_repositoryCatalog is null || _auth is null)
		{
			return;
		}

		string id = $"{owner}/{name}";
		RegisteredRepo? descriptor;
		CancellationTokenSource cts;
		long generation;
		long canceledRequestId;
		CancellationTokenSource? previousBrowseCts;
		CancellationTokenSource? previousFileCts;
		lock (_remotePublishSync)
		{
			lock (_sync)
			{
				if (_disposed
					|| intentGeneration != _remoteBrowseIntentGeneration
					|| navigationGeneration != _workspaceNavigationIntentGeneration)
				{
					return;
				}
				descriptor = _workspace?.FindRepo(id);
				if (descriptor is null)
				{
					return;
				}
				previousBrowseCts = _remoteBrowseCts;
				previousFileCts = _remoteFileCts;
				_remoteFileRepoId = null;
				canceledRequestId = _remoteFileRequestId;
				_remoteFileRequestId = 0;
				generation = Interlocked.Increment(ref _remoteBrowseGeneration);
				Interlocked.Increment(ref _remoteFileGeneration);
				cts = new CancellationTokenSource();
				_remoteBrowseCts = cts;
				_remoteBrowseRepoId = id;
			}
		}
		previousBrowseCts?.Cancel();
		previousFileCts?.Cancel();
		CompleteDocumentOpen(canceledRequestId, succeeded: false);
		CancellationToken cancellationToken = cts.Token;
		_ = Task.Run(async () =>
		{
			try
			{
				await _auth.WithAccessTokenAsync(async (token, ct) =>
				{
					string branch = string.IsNullOrWhiteSpace(requestedBranch)
						? descriptor.DefaultBranch
						: requestedBranch;
					if (string.IsNullOrWhiteSpace(branch))
					{
						GitHubRepositoryMetadata metadata =
							await _repositoryCatalog.GetMetadataAsync(owner, name, token, ct);
						branch = metadata.DefaultBranch;
						lock (_remotePublishSync)
						{
							lock (_sync)
							{
								bool current = !_disposed
									&& generation == _remoteBrowseGeneration
									&& navigationGeneration == _workspaceNavigationIntentGeneration
									&& IsSameRegisteredRepository(id, descriptor.Url);
								if (!current)
								{
									return false;
								}
								_workspace?.SetRepoDefaultBranch(descriptor, branch);
								EmitWorkspaceState();
							}
						}
					}

					IReadOnlyList<GitHubRepositoryEntry> entries =
						await _repositoryCatalog.GetTreeAsync(owner, name, branch, token, ct);
					lock (_remotePublishSync)
					{
						lock (_sync)
						{
							bool current = !_disposed
								&& generation == _remoteBrowseGeneration
								&& navigationGeneration == _workspaceNavigationIntentGeneration
								&& IsSameRegisteredRepository(id, descriptor.Url);
							if (current)
							{
								RemoteBrowseTerminalPublishingForTest?.Invoke();
								Emit(IpcSerializer.SerializeEvent(
									MessageKinds.Tree, BuildRemoteTree(owner, name, branch, entries)));
							}
						}
					}
					return true;
				}, cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				// Superseded navigation or app teardown; a newer surface owns the UI now.
			}
			catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or InvalidOperationException)
			{
				_logger.LogError(ex, "Could not browse GitHub repository {Repo}", id);
				PublishRemoteBrowseErrorIfCurrent(
					generation,
					navigationGeneration,
					id,
					descriptor,
					"Could not read that repository. Check your connection and access, then try again.");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Unexpected failure browsing GitHub repository {Repo}", id);
				PublishRemoteBrowseErrorIfCurrent(
					generation, navigationGeneration, id, descriptor, "Could not read that repository.");
			}
			finally
			{
				lock (_sync)
				{
					if (ReferenceEquals(_remoteBrowseCts, cts))
					{
						_remoteBrowseCts = null;
						_remoteBrowseRepoId = null;
					}
				}
				cts.Dispose();
			}
		});
	}

	private sealed class RemoteTreeNode(string name, string path, bool isDirectory)
	{
		public string Name { get; } = name;
		public string Path { get; } = path;
		public bool IsDirectory { get; } = isDirectory;
		public Dictionary<string, RemoteTreeNode> Children { get; } =
			new(StringComparer.Ordinal);
	}

	internal static TreePayload BuildRemoteTree(
		string owner, string name, string branch, IReadOnlyList<GitHubRepositoryEntry> entries)
	{
		RemoteTreeNode root = new(name, RemotePath(owner, name, branch, string.Empty), true);
		int nodes = 0;
		foreach (GitHubRepositoryEntry entry in entries)
		{
			if (entry.Path.Length == 0 || entry.Path.Length > MaxRemotePathChars)
			{
				throw new InvalidDataException("GitHub tree path is empty or too long.");
			}
			string[] parts = entry.Path.Split('/');
			if (parts.Length == 0 || parts.Length > MaxRemotePathDepth)
			{
				throw new InvalidDataException("GitHub tree path is too deep.");
			}
			if (parts.Any(part => part is "" or "." or ".."))
			{
				throw new InvalidDataException("GitHub tree path contains an invalid segment.");
			}
			RemoteTreeNode parent = root;
			for (int index = 0; index < parts.Length; index++)
			{
				string relative = string.Join('/', parts.Take(index + 1));
				bool directory = index < parts.Length - 1 || entry.IsDirectory;
				if (!parent.Children.TryGetValue(parts[index], out RemoteTreeNode? child))
				{
					if (++nodes > MaxRemoteTreeNodes)
					{
						throw new InvalidDataException("GitHub tree expands beyond the preview limit.");
					}
					child = new RemoteTreeNode(
						parts[index], RemotePath(owner, name, branch, relative), directory);
					parent.Children.Add(parts[index], child);
				}
				parent = child;
			}
		}

		return new TreePayload($"{owner}/{name}", ToWireNodes(root.Children.Values));
	}

	private static TreeNode[] ToWireNodes(IEnumerable<RemoteTreeNode> nodes) => nodes
		.OrderByDescending(node => node.IsDirectory)
		.ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
		.Select(node => new TreeNode(node.Name, node.Path, node.IsDirectory, ToWireNodes(node.Children.Values)))
		.ToArray();

	private static string RemotePath(string owner, string name, string branch, string path) =>
		$"github://{owner}/{name}/{Uri.EscapeDataString(branch)}/{Uri.EscapeDataString(path)}";

	internal static bool TryParseRemotePath(
		string value, out string owner, out string name, out string branch, out string path)
	{
		owner = name = branch = path = string.Empty;
		const string prefix = "github://";
		if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		string[] parts = value[prefix.Length..].Split('/', 4);
		if (parts.Length != 4 || !IsValidOwner(parts[0]) || !IsValidRepoName(parts[1]))
		{
			return false;
		}

		owner = parts[0];
		name = parts[1];
		try
		{
			branch = Uri.UnescapeDataString(parts[2]);
			path = Uri.UnescapeDataString(parts[3]);
		}
		catch (UriFormatException)
		{
			return false;
		}
		return branch.Length > 0 && path.Length > 0 && !path.Split('/').Any(part => part is "" or "." or "..");
	}

	private void LoadRemoteFile(
		string owner,
		string name,
		string branch,
		string path,
		string wirePath,
		long requestId,
		long navigationGeneration)
	{
		if (_repositoryCatalog is null)
		{
			SendError("Browsing repositories online isn't available in this build.");
			CompleteDocumentOpen(requestId, succeeded: false);
			return;
		}
		string id = $"{owner}/{name}";
		long generation;
		CancellationTokenSource? previousFileCts = null;
		bool repositoryMissing = false;
		lock (_remotePublishSync)
		{
			lock (_sync)
			{
				if (_disposed)
				{
					return;
				}
				repositoryMissing = _workspace?.FindRepo(id) is null;
				if (!repositoryMissing)
				{
					previousFileCts = _remoteFileCts;
					generation = Interlocked.Increment(ref _remoteFileGeneration);
					_remoteFileRepoId = id;
					_remoteFileRequestId = requestId;
				}
				else
				{
					generation = 0;
				}
			}
		}
		if (repositoryMissing)
		{
			SendError("That repository is no longer registered.");
			CompleteDocumentOpen(requestId, succeeded: false);
			return;
		}
		previousFileCts?.Cancel();
		PendingRepoAction action = new(
			PendingRepoActionKind.File,
			owner,
			name,
			NavigationGeneration: navigationGeneration,
			RemoteGeneration: generation,
			Branch: branch,
			Path: path,
			WirePath: wirePath,
			RequestId: requestId);
		if (!EnsureGitHubAccess(action))
		{
			bool pending;
			lock (_sync)
			{
				pending = _pendingRepoOpen?.Kind == PendingRepoActionKind.File
					&& _pendingRepoOpen.RequestId == requestId;
			}
			if (!pending)
			{
				CompleteDocumentOpen(requestId, succeeded: false);
			}
			return;
		}
		LoadRemoteFileCore(
			owner, name, branch, path, wirePath, generation, navigationGeneration, requestId);
	}

	private void LoadRemoteFileCore(
		string owner,
		string name,
		string branch,
		string path,
		string wirePath,
		long generation,
		long navigationGeneration,
		long requestId)
	{
		if (_repositoryCatalog is null || _auth is null)
		{
			CompleteDocumentOpen(requestId, succeeded: false);
			return;
		}
		CancellationTokenSource? cts = null;
		CancellationTokenSource? previousFileCts = null;
		lock (_remotePublishSync)
		{
			lock (_sync)
			{
				string id = $"{owner}/{name}";
				if (!_disposed
					&& generation == _remoteFileGeneration
					&& navigationGeneration == _workspaceNavigationIntentGeneration
					&& _remoteFileRequestId == requestId
					&& string.Equals(_remoteFileRepoId, id, StringComparison.OrdinalIgnoreCase)
					&& _workspace?.FindRepo(id) is not null)
				{
					previousFileCts = _remoteFileCts;
					cts = new CancellationTokenSource();
					_remoteFileCts = cts;
					_remoteFileRepoId = id;
				}
			}
		}
		if (cts is null)
		{
			CompleteDocumentOpen(requestId, succeeded: false);
			return;
		}
		previousFileCts?.Cancel();
		CancellationToken cancellationToken = cts.Token;
		_ = Task.Run(async () =>
		{
			try
			{
				string text = await _auth.WithAccessTokenAsync(
					(token, ct) => _repositoryCatalog.GetFileAsync(owner, name, branch, path, token, ct),
					cancellationToken);
				bool published;
				lock (_remotePublishSync)
				{
					lock (_sync)
					{
						published = !_disposed
							&& generation == _remoteFileGeneration
							&& navigationGeneration == _workspaceNavigationIntentGeneration
							&& _remoteFileRequestId == requestId;
						if (published)
						{
							RemoteFileTerminalPublishingForTest?.Invoke();
							CancelAutosave();
							_text = text;
							_currentPath = null;
							_repoRoot = null;
							_remoteDocument = new RemoteDocumentContext(owner, name, branch, path);
							_session = new DraftSession(
								Lifecycle.stateName(Lifecycle.State.Published), null, null, false, 0, 0,
								Interlocked.Read(ref _draftGeneration));
							_remoteFileRequestId = 0;
							Emit(IpcSerializer.SerializeEvent(
								MessageKinds.DocLoaded,
								new DocLoadedPayload(
									wirePath, text, string.Empty, ReadOnly: true,
									Repository: $"{owner}/{name}", Branch: branch, RepositoryPath: path)));
							SendWorkspaceContext();
							CompleteDocumentOpen(requestId, succeeded: true);
						}
					}
				}
				if (!published)
				{
					CompleteDocumentOpen(requestId, succeeded: false);
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				// The invalidating navigation normally retires the request first; this is the fallback for
				// cancellation sources that do not own that navigation transition.
				CompleteDocumentOpen(requestId, succeeded: false);
			}
			catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or InvalidOperationException)
			{
				_logger.LogError(ex, "Could not preview remote file {Repo}/{Path}", $"{owner}/{name}", path);
				PublishRemoteFileErrorIfCurrent(generation, navigationGeneration, requestId, "Could not preview that file.");
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Unexpected failure previewing remote file {Repo}/{Path}", $"{owner}/{name}", path);
				PublishRemoteFileErrorIfCurrent(generation, navigationGeneration, requestId, "Could not preview that file.");
			}
			finally
			{
				lock (_sync)
				{
					if (ReferenceEquals(_remoteFileCts, cts))
					{
						_remoteFileCts = null;
						_remoteFileRepoId = null;
					}
				}
				cts.Dispose();
			}
		});
	}

	private sealed record RemoteDocumentContext(string Owner, string Name, string Branch, string Path);

	private bool TryInvalidateRemoteNavigationForWorkspacePublication(
		WorkspaceRootPublication publication)
	{
		long canceledRequestId;
		CancellationTokenSource? browseCts;
		CancellationTokenSource? fileCts;
		lock (_remotePublishSync)
		{
			lock (_sync)
			{
				if (!IsWorkspaceRootPublicationCurrentLocked(publication))
				{
					return false;
				}
				_remoteBrowseGeneration++;
				_remoteBrowseIntentGeneration++;
				_remoteBrowseIntentRepoId = null;
				browseCts = _remoteBrowseCts;
				_remoteBrowseRepoId = null;
				_remoteFileGeneration++;
				fileCts = _remoteFileCts;
				_remoteFileRepoId = null;
				canceledRequestId = _remoteFileRequestId;
				_remoteFileRequestId = 0;
				if (_pendingRepoOpen?.Kind is PendingRepoActionKind.Browse or PendingRepoActionKind.File)
				{
					_pendingRepoOpen = null;
				}
			}
		}
		browseCts?.Cancel();
		fileCts?.Cancel();
		CompleteDocumentOpen(canceledRequestId, succeeded: false);
		return true;
	}

	private void InvalidateRemoteNavigation(bool browse, bool file)
	{
		long canceledRequestId = 0;
		CancellationTokenSource? browseCts = null;
		CancellationTokenSource? fileCts = null;
		lock (_remotePublishSync)
		{
			lock (_sync)
			{
				if (browse)
				{
					_remoteBrowseGeneration++;
					_remoteBrowseIntentGeneration++;
					_remoteBrowseIntentRepoId = null;
					browseCts = _remoteBrowseCts;
					_remoteBrowseRepoId = null;
					if (_pendingRepoOpen?.Kind == PendingRepoActionKind.Browse)
					{
						_pendingRepoOpen = null;
					}
				}
				if (file)
				{
					_remoteFileGeneration++;
					fileCts = _remoteFileCts;
					_remoteFileRepoId = null;
					canceledRequestId = _remoteFileRequestId;
					_remoteFileRequestId = 0;
					if (_pendingRepoOpen?.Kind == PendingRepoActionKind.File)
					{
						_pendingRepoOpen = null;
					}
				}
			}
		}
		browseCts?.Cancel();
		fileCts?.Cancel();
		CompleteDocumentOpen(canceledRequestId, succeeded: false);
	}
	private void InvalidateRemoteRepository(string id)
	{
		long canceledRequestId = 0;
		CancellationTokenSource? browseCts = null;
		CancellationTokenSource? fileCts = null;
		lock (_remotePublishSync)
		{
			lock (_sync)
			{
				if (string.Equals(_remoteBrowseRepoId, id, StringComparison.OrdinalIgnoreCase))
				{
					_remoteBrowseGeneration++;
					_remoteBrowseIntentGeneration++;
					browseCts = _remoteBrowseCts;
					_remoteBrowseRepoId = null;
				}
				if (string.Equals(_remoteBrowseIntentRepoId, id, StringComparison.OrdinalIgnoreCase))
				{
					_remoteBrowseIntentGeneration++;
					_remoteBrowseIntentRepoId = null;
				}
				if (string.Equals(_remoteFileRepoId, id, StringComparison.OrdinalIgnoreCase))
				{
					_remoteFileGeneration++;
					fileCts = _remoteFileCts;
					_remoteFileRepoId = null;
					canceledRequestId = _remoteFileRequestId;
					_remoteFileRequestId = 0;
				}
				if (_pendingRepoOpen?.Kind == PendingRepoActionKind.File
					&& string.Equals(
						$"{_pendingRepoOpen.Owner}/{_pendingRepoOpen.Name}", id,
						StringComparison.OrdinalIgnoreCase))
				{
					canceledRequestId = _pendingRepoOpen.RequestId;
					_pendingRepoOpen = null;
				}
			}
		}
		browseCts?.Cancel();
		fileCts?.Cancel();
		CompleteDocumentOpen(canceledRequestId, succeeded: false);
	}
	private void PublishRemoteBrowseErrorIfCurrent(
		long generation,
		long navigationGeneration,
		string id,
		RegisteredRepo descriptor,
		string message)
	{
		lock (_remotePublishSync)
		{
			lock (_sync)
			{
				bool current = !_disposed
					&& generation == _remoteBrowseGeneration
					&& navigationGeneration == _workspaceNavigationIntentGeneration
					&& IsSameRegisteredRepository(id, descriptor.Url);
				if (current)
				{
					SendError(message);
				}
			}
		}
	}

	private bool IsSameRegisteredRepository(string id, string url)
	{
		RegisteredRepo? current = _workspace?.FindRepo(id);
		return current is not null && string.Equals(current.Url, url, StringComparison.OrdinalIgnoreCase);
	}

	private void PublishRemoteFileErrorIfCurrent(
		long generation, long navigationGeneration, long requestId, string message)
	{
		lock (_remotePublishSync)
		{
			lock (_sync)
			{
				bool current = !_disposed
					&& generation == _remoteFileGeneration
					&& navigationGeneration == _workspaceNavigationIntentGeneration
					&& requestId == _remoteFileRequestId;
				if (current)
				{
					_remoteFileRequestId = 0;
					SendError(message);
					CompleteDocumentOpen(requestId, succeeded: false);
				}
			}
		}
	}
}
