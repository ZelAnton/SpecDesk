using System.Text.Json;
using SpecDesk.Contracts;

namespace SpecDesk.Ai;

/// <summary>
/// The author's personal prompt-template library, persisted as a plain JSON array of
/// <see cref="PromptTemplate"/> in a host-owned file (see <c>AppPaths.PromptTemplates</c>). Mirrors the
/// pattern of <c>SpecDesk.GitHub.FileTokenStore</c>: System.Text.Json, directory-create on save, and a
/// corruption-tolerant load — a missing, empty, partially-written, or malformed file reads as "no personal
/// templates" (the library then offers its built-in starters) rather than faulting the assistant.
/// </summary>
public sealed class PromptTemplateStore
{
	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
	};

	private readonly string _path;

	public PromptTemplateStore(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		_path = path;
	}

	/// <summary>Load the personal templates, or an empty list when the file is absent, empty, or corrupt.</summary>
	public IReadOnlyList<PromptTemplate> Load()
	{
		if (!File.Exists(_path))
		{
			return [];
		}

		try
		{
			using FileStream stream = File.OpenRead(_path);
			PromptTemplate[]? loaded = JsonSerializer.Deserialize<PromptTemplate[]>(stream, SerializerOptions);
			// Drop any entry the file left without an id/title (a hand-edited or partially-written record) so
			// the picker never shows a blank row; the body may legitimately be empty.
			return loaded is null
				? []
				: Array.FindAll(loaded, t => t is { Id.Length: > 0, Title.Length: > 0 });
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
		{
			// A corrupt/unreadable library is treated as empty: the author still gets the built-in starters,
			// and the app never crashes on a tampered or inaccessible file. (Same policy as FileTokenStore.)
			return [];
		}
	}

	/// <summary>Persist <paramref name="templates"/>, creating the parent directory if needed.</summary>
	public void Save(IReadOnlyList<PromptTemplate> templates)
	{
		ArgumentNullException.ThrowIfNull(templates);
		Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
		File.WriteAllText(_path, JsonSerializer.Serialize(templates, SerializerOptions));
	}
}
