using Microsoft.Extensions.Logging;
using SpecDesk.Git;
using LibGit2SharpException = LibGit2Sharp.LibGit2SharpException;

namespace SpecDesk.Host;

/// <summary>
/// Seeds a writable, git-versioned copy of the bundled sample spec into <c>%LOCALAPPDATA%</c> on
/// first run. This gives the local-git PoC a repository to exercise Edit / Save version / Discard
/// against, isolated from SpecDesk's own working tree (a <c>git init</c> in place would create a
/// nested repo inside the dev checkout).
/// </summary>
internal static class SampleRepo
{
	public static string EnsureSeeded(
		string repoRoot,
		string bundledSamples,
		IDocumentVersioning versioning,
		ILogger logger)
	{
		string welcome = Path.Combine(repoRoot, "welcome.md");
		try
		{
			Directory.CreateDirectory(repoRoot);

			// Copy the bundled assets in only when the repo hasn't been seeded yet, so the author's
			// own edits to the sample survive a restart.
			if (!File.Exists(welcome) && Directory.Exists(bundledSamples))
			{
				foreach (string source in Directory.GetFiles(bundledSamples))
				{
					File.Copy(source, Path.Combine(repoRoot, Path.GetFileName(source)), overwrite: true);
				}
			}

			if (!versioning.IsVersioned(repoRoot))
			{
				versioning.Initialize(repoRoot, "Seed sample spec repo");
				logger.LogInformation("Seeded sample repo at {Repo}", repoRoot);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LibGit2SharpException)
		{
			// A demo repo we cannot seed should not stop the app launching; Edit will simply report
			// the folder isn't versioned.
			logger.LogError(ex, "Could not seed the sample repo at {Repo}", repoRoot);
		}

		return welcome;
	}
}
