using LibGit2Sharp;

namespace SpecDesk.Git;

/// <summary>
/// The <see cref="IRepositoryCloner"/> implementation backed by LibGit2Sharp. Each call opens no long-lived
/// handle — <see cref="Repository.Clone(string, string, CloneOptions)"/> owns its own — so the type is
/// stateless and thread-safe. The caller supplies the exact destination folder (the host namespaces it by
/// owner and name), so this type never derives a folder name and same-named repos from different owners
/// can't collide.
/// </summary>
public sealed class LibGit2RepositoryCloner : IRepositoryCloner, ILocalRepositoryInspector
{
    public bool IsCloned(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        // A valid clone is a directory that IS a git working tree — a bare existence check would treat
        // partial/faulted debris as "cloned" and open it as a broken (empty) workspace.
        return Directory.Exists(destinationPath) && Repository.IsValid(destinationPath);
    }

    public string CloneOrReuse(string url, string destinationPath, string? accessToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(url);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        ct.ThrowIfCancellationRequested();

        if (Directory.Exists(destinationPath))
        {
            // Already a valid clone — hand back the existing working tree rather than cloning over it (a
            // second clone into a non-empty directory fails anyway). This is the "already cloned" contract.
            if (Repository.IsValid(destinationPath))
            {
                return destinationPath;
            }

            // A leftover directory at the target that is NOT a git working tree (a previous clone cancelled or
            // faulted mid-transfer left partial debris) would make Repository.Clone fail on a non-empty target.
            // Clear it first — this only ever lives under the app's own managed repos folder, so nothing the
            // author placed elsewhere is at risk.
            TryDeleteDirectory(destinationPath);
        }

        CloneOptions options = new();
        if (!string.IsNullOrEmpty(accessToken))
        {
            // Authenticate a private repo. The token is released ONLY to an HTTPS github.com host — see
            // LibGit2DocumentVersioning.ResolveCredentials for why anything else (a re-pointed URL, an SSH or
            // local-file remote) must refuse rather than fall back to the Windows user's credentials. Reused
            // here so the same github-host-only guard that protects the push protects the clone.
            options.FetchOptions.CredentialsProvider =
                (endpoint, _, _) => LibGit2DocumentVersioning.ResolveCredentials(endpoint, accessToken);
        }

        // Abort a stalled transfer when the caller cancels (the host bounds the clone with a CTS, cancelled on
        // window teardown). The connect/handshake phase isn't surfaced through this callback, so a stall there
        // is bounded only by the OS socket timeout — the same LibGit2Sharp limitation the push path documents.
        options.FetchOptions.OnTransferProgress = _ => !ct.IsCancellationRequested;

        try
        {
            // Repository.Clone returns the path to the created .git directory, not the working tree; return
            // destinationPath (the workdir we passed) — that is the folder opened as the workspace.
            Repository.Clone(url, destinationPath, options);
            return destinationPath;
        }
        catch (UserCancelledException) when (ct.IsCancellationRequested)
        {
            // The transfer-progress callback above aborted the clone because the caller cancelled (window
            // teardown). Remove the partial working tree and surface it as a plain OperationCanceledException,
            // so the host treats it like any other cancellation rather than a clone error.
            TryDeleteDirectory(destinationPath);
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex) when (ex is LibGit2SharpException or InvalidOperationException)
        {
            // A genuine clone failure leaves a partial directory; remove it so the next attempt — and the
            // host's IsCloned check — doesn't mistake the debris for a usable clone. LibGit2SharpException is
            // the ordinary failure (a missing/private repo, a network fault); InvalidOperationException is the
            // credentials guard refusing a non-github endpoint (ResolveCredentials) surfaced back through
            // Repository.Clone. Rethrow either for the host to report plainly.
            TryDeleteDirectory(destinationPath);
            throw;
        }
    }

	public LocalRepositoryInfo Inspect(string repositoryPath, string knownDefaultBranch)
	{
		using Repository repository = new(repositoryPath);
		string defaultBranch = knownDefaultBranch;
		if (string.IsNullOrWhiteSpace(defaultBranch))
		{
			Branch? remoteHead = repository.Branches["origin/HEAD"];
			defaultBranch = remoteHead?.TrackedBranch?.FriendlyName ?? string.Empty;
			if (!string.IsNullOrWhiteSpace(defaultBranch)
				&& defaultBranch.StartsWith("origin/", StringComparison.Ordinal))
			{
				defaultBranch = defaultBranch["origin/".Length..];
			}

			if (string.IsNullOrWhiteSpace(defaultBranch))
			{
				// A normal clone checks out the remote default branch, so HEAD is the final reliable fallback.
				defaultBranch = repository.Head.FriendlyName;
			}
		}
		string remotePrefix = "origin/";
		string[] branches = repository.Branches
			.Select(branch => branch.IsRemote && branch.FriendlyName.StartsWith(remotePrefix, StringComparison.Ordinal)
				? branch.FriendlyName[remotePrefix.Length..]
				: branch.FriendlyName)
			.Where(branch => branch.Length > 0
				&& !string.Equals(branch, defaultBranch, StringComparison.OrdinalIgnoreCase)
				&& !string.Equals(branch, "HEAD", StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		return new LocalRepositoryInfo(defaultBranch, branches);
	}

    // Best-effort recursive delete that first clears the read-only attribute git sets on pack files (mirrors
    // the Git tests' own teardown). Never throws: leaving debris behind is preferable to faulting the clone.
    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best-effort — a locked/partly-removed tree is left in place rather than faulting here.
        }
    }
}
