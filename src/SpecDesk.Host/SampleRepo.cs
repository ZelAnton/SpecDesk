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
	// M-19: the durable "the bundled copy fully completed" signal, checked instead of (not merely in
	// addition to) welcome.md's existence. Directory.GetFiles makes no copy-order guarantee, so a crash
	// mid-copy could otherwise leave welcome.md in place while sibling files are still missing, and a
	// naive "does welcome.md exist" check would then skip re-seeding forever, permanently committing a
	// partial tree. Written last, via a temp file + atomic rename, so a crash never leaves a
	// half-written marker that could itself be mistaken for completion.
	private const string SeedMarkerFileName = ".specdesk-seed-complete";

	public static string EnsureSeeded(
		string repoRoot,
		string bundledSamples,
		IDocumentVersioning versioning,
		ILogger logger)
	{
		string welcome = Path.Combine(repoRoot, "welcome.md");
		string seedMarker = Path.Combine(repoRoot, SeedMarkerFileName);
		try
		{
			Directory.CreateDirectory(repoRoot);

			// Copy the bundled assets in only when neither this marker nor a prior successful Initialize
			// proves the seed already completed, so the author's own edits to the sample survive a
			// restart, but a crash mid-copy (before the repo is versioned) is retried rather than
			// silently skipped.
			bool alreadySeeded = File.Exists(seedMarker) || versioning.IsVersioned(repoRoot);
			if (!alreadySeeded && Directory.Exists(bundledSamples))
			{
				foreach (string source in Directory.GetFiles(bundledSamples))
				{
					File.Copy(source, Path.Combine(repoRoot, Path.GetFileName(source)), overwrite: true);
				}

				string markerTemp = seedMarker + ".tmp";
				File.WriteAllText(markerTemp, string.Empty);
				File.Move(markerTemp, seedMarker, overwrite: true);
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
