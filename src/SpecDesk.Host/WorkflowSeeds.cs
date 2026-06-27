using SpecDesk.Core;

namespace SpecDesk.Host;

/// <summary>
/// Document-naming and version-note seeding helpers, lifted out of <see cref="HostController"/>.
/// They derive the suggested draft (branch) name and version-note (commit message) seeds from the
/// document path and the repo's <c>.spectool.toml</c> workflow config, and sanitize an author-typed
/// draft name to a valid git ref. State-free and side-effect-light (only <see cref="TryReadRepoToml"/>
/// touches the disk), so the sanitizer and slug are unit-tested directly; the seed composers just
/// thread the config through <see cref="WorkflowConfig"/>, which has its own tests.
/// </summary>
public static class WorkflowSeeds
{
	/// <summary>The kebab slug of a document file name (no extension) — the token naming templates expand.</summary>
	public static string DocSlug(string docPath) =>
		Slug.slugify(Slug.Case.Kebab, Path.GetFileNameWithoutExtension(docPath));

	/// <summary>Reads the repo's <c>.spectool.toml</c> (the workflow config), or <c>null</c> if absent/unreadable.</summary>
	public static string? TryReadRepoToml(string repoRoot)
	{
		string path = Path.Combine(repoRoot, ".spectool.toml");
		try
		{
			return File.Exists(path) ? File.ReadAllText(path) : null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>The generated, editable seed for a version note (the commit message), from the repo's
	/// <c>.spectool.toml</c> [commit] template (or the default), expanded with the document tokens.</summary>
	public static string SuggestedVersionNote(string repoRoot, string docPath) =>
		WorkflowConfig.commitMessageForHost(TryReadRepoToml(repoRoot), DocSlug(docPath), DateTimeOffset.Now);

	/// <summary>The generated, editable seed for a draft (branch) name, from the repo's
	/// <c>.spectool.toml</c> [branch] template (or the default), expanded with the document tokens.</summary>
	public static string SuggestedBranchName(string repoRoot, string docPath) =>
		WorkflowConfig.branchNameForHost(TryReadRepoToml(repoRoot), DocSlug(docPath), DateTimeOffset.Now);

	/// <summary>
	/// Reduce an author-typed draft name to a valid git branch ref, matching the webview's live
	/// cleanup: backslashes become '/', and anything outside letters/digits and '-_/.' becomes '_'.
	/// Runs of the separators '-_/' collapse to one, ref-illegal edges are trimmed (leading/trailing
	/// '-_/.', a trailing ".lock", and ".."). Returns "" when nothing usable remains, so the caller
	/// falls back to the generated name. Defensive: a still-invalid result is caught by BeginEdit and
	/// surfaced as a plain error.
	/// </summary>
	public static string SanitizeBranchName(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return string.Empty;
		}

		System.Text.StringBuilder builder = new(raw.Length);
		foreach (char original in raw.Trim())
		{
			char ch = original == '\\' ? '/' : original;
			bool keep = char.IsLetterOrDigit(ch) || ch is '-' or '_' or '/' or '.';
			char mapped = keep ? ch : '_';
			// Collapse consecutive separators so "a   b" / "a///b" don't produce noisy runs.
			if (mapped is '-' or '_' or '/' && builder.Length > 0 && builder[^1] is '-' or '_' or '/')
			{
				continue;
			}

			builder.Append(mapped);
		}

		string cleaned = builder.ToString().Trim('-', '_', '/', '.').Replace("..", "_", StringComparison.Ordinal);
		if (cleaned.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
		{
			cleaned = cleaned[..^5].Trim('-', '_', '/', '.');
		}

		return cleaned;
	}
}
