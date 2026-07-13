using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SpecDesk.GitHub;

public sealed record GitHubRepositoryMetadata(string DefaultBranch);
public sealed record GitHubRepositoryEntry(string Path, bool IsDirectory, long Size);
public sealed record GitHubRepositoryOption(string FullName, string? Description);

public interface IGitHubRepositoryCatalog
{
	Task<IReadOnlyList<string>> GetOrganizationsAsync(
		string accessToken, CancellationToken cancellationToken = default) =>
		Task.FromResult<IReadOnlyList<string>>([]);

	Task<IReadOnlyList<GitHubRepositoryOption>> GetRepositoriesAsync(
		string accessToken, CancellationToken cancellationToken = default) =>
		Task.FromResult<IReadOnlyList<GitHubRepositoryOption>>([]);

	Task<GitHubRepositoryMetadata> GetMetadataAsync(
		string owner, string name, string accessToken, CancellationToken cancellationToken = default);

	Task<IReadOnlyList<GitHubRepositoryEntry>> GetTreeAsync(
		string owner, string name, string branch, string accessToken,
		CancellationToken cancellationToken = default);

	Task<string> GetFileAsync(
		string owner, string name, string branch, string path, string accessToken,
		CancellationToken cancellationToken = default);
}

/// <summary>Reads repository metadata through GitHub's REST API.</summary>
public sealed class GitHubRepositoryCatalog(HttpClient http) : IGitHubRepositoryCatalog
{
	private const int MaxFileBytes = 4 * 1024 * 1024;
	private const int MaxTreeEntries = 5000;
	private const int MaxTreeBytes = 8 * 1024 * 1024;
	private const int MaxOrganizationPages = 100;
	private const int MaxRepositoryPages = 100;

	public async Task<IReadOnlyList<string>> GetOrganizationsAsync(
		string accessToken, CancellationToken cancellationToken = default)
	{
		HashSet<string> organizations = new(StringComparer.OrdinalIgnoreCase);
		for (int page = 1; page <= MaxOrganizationPages; page++)
		{
			using HttpRequestMessage request = CreateRequest(
				$"https://api.github.com/user/orgs?per_page=100&page={page}", accessToken);
			using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
			response.EnsureSuccessStatusCode();
			await response.Content.LoadIntoBufferAsync(MaxTreeBytes, cancellationToken);
			using JsonDocument json = JsonDocument.Parse(
				await response.Content.ReadAsStreamAsync(cancellationToken));
			if (json.RootElement.ValueKind != JsonValueKind.Array)
			{
				throw new InvalidDataException("GitHub returned an invalid organization list.");
			}

			int count = 0;
			foreach (JsonElement item in json.RootElement.EnumerateArray())
			{
				count++;
				if (item.TryGetProperty("login", out JsonElement login)
					&& login.ValueKind == JsonValueKind.String
					&& !string.IsNullOrWhiteSpace(login.GetString()))
				{
					organizations.Add(login.GetString()!);
				}
			}
			if (count < 100)
			{
				break;
			}
		}

		return organizations.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
	}

	public async Task<IReadOnlyList<GitHubRepositoryOption>> GetRepositoriesAsync(
		string accessToken, CancellationToken cancellationToken = default)
	{
		Dictionary<string, GitHubRepositoryOption> repositories = new(StringComparer.OrdinalIgnoreCase);
		string? pageUrl =
			"https://api.github.com/user/repos?affiliation=owner,collaborator,organization_member&sort=full_name&per_page=100";
		for (int page = 1; pageUrl is not null && page <= MaxRepositoryPages; page++)
		{
			using HttpRequestMessage request = CreateRequest(pageUrl, accessToken);
			using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
			response.EnsureSuccessStatusCode();
			await response.Content.LoadIntoBufferAsync(MaxTreeBytes, cancellationToken);
			using JsonDocument json = JsonDocument.Parse(
				await response.Content.ReadAsStreamAsync(cancellationToken));
			if (json.RootElement.ValueKind != JsonValueKind.Array)
			{
				throw new InvalidDataException("GitHub returned an invalid repository list.");
			}

			foreach (JsonElement item in json.RootElement.EnumerateArray())
			{
				string? fullName = item.TryGetProperty("full_name", out JsonElement nameValue)
					&& nameValue.ValueKind == JsonValueKind.String
					? nameValue.GetString()
					: null;
				if (string.IsNullOrWhiteSpace(fullName))
				{
					continue;
				}
				string? description = item.TryGetProperty("description", out JsonElement descriptionValue)
					&& descriptionValue.ValueKind == JsonValueKind.String
					? descriptionValue.GetString()
					: null;
				repositories.TryAdd(fullName, new GitHubRepositoryOption(fullName, description));
			}
			pageUrl = NextPageUrl(response);
			if (pageUrl is null)
			{
				break;
			}
		}

		return repositories.Values
			.OrderBy(repository => repository.FullName, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	private static string? NextPageUrl(HttpResponseMessage response)
	{
		if (!response.Headers.TryGetValues("Link", out IEnumerable<string>? values))
		{
			return null;
		}
		foreach (string link in values.SelectMany(value => value.Split(',')))
		{
			if (!link.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			int start = link.IndexOf('<');
			int end = link.IndexOf('>', start + 1);
			if (start >= 0 && end > start
				&& Uri.TryCreate(link[(start + 1)..end], UriKind.Absolute, out Uri? uri)
				&& string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase))
			{
				return uri.AbsoluteUri;
			}
		}
		return null;
	}

	public async Task<GitHubRepositoryMetadata> GetMetadataAsync(
		string owner, string name, string accessToken, CancellationToken cancellationToken = default)
	{
		using HttpRequestMessage request = CreateRequest(
			$"https://api.github.com/repos/{Escape(owner)}/{Escape(name)}", accessToken);
		using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
		response.EnsureSuccessStatusCode();
		await response.Content.LoadIntoBufferAsync(MaxTreeBytes, cancellationToken);
		using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
		if (!json.RootElement.TryGetProperty("default_branch", out JsonElement branch)
			|| branch.ValueKind != JsonValueKind.String
			|| string.IsNullOrWhiteSpace(branch.GetString()))
		{
			throw new InvalidDataException("GitHub returned no default branch.");
		}

		return new GitHubRepositoryMetadata(branch.GetString()!);
	}

	public async Task<IReadOnlyList<GitHubRepositoryEntry>> GetTreeAsync(
		string owner, string name, string branch, string accessToken,
		CancellationToken cancellationToken = default)
	{
		using HttpRequestMessage request = CreateRequest(
			$"https://api.github.com/repos/{Escape(owner)}/{Escape(name)}/git/trees/{Escape(branch)}?recursive=1",
			accessToken);
		using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
		response.EnsureSuccessStatusCode();
		await response.Content.LoadIntoBufferAsync(MaxTreeBytes, cancellationToken);
		using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
		if (!json.RootElement.TryGetProperty("tree", out JsonElement tree) || tree.ValueKind != JsonValueKind.Array)
		{
			throw new InvalidDataException("GitHub returned no repository tree.");
		}
		if (json.RootElement.TryGetProperty("truncated", out JsonElement truncated)
			&& truncated.ValueKind == JsonValueKind.True)
		{
			throw new InvalidDataException("The repository tree is too large for a complete preview.");
		}

		List<GitHubRepositoryEntry> entries = [];
		foreach (JsonElement item in tree.EnumerateArray())
		{
			if (entries.Count >= MaxTreeEntries)
			{
				throw new InvalidDataException("The repository tree is too large for a complete preview.");
			}
			string? path = item.TryGetProperty("path", out JsonElement pathValue) ? pathValue.GetString() : null;
			string? type = item.TryGetProperty("type", out JsonElement typeValue) ? typeValue.GetString() : null;
			if (string.IsNullOrWhiteSpace(path) || type is not ("tree" or "blob"))
			{
				continue;
			}

			long size = item.TryGetProperty("size", out JsonElement sizeValue) && sizeValue.TryGetInt64(out long parsed)
				? parsed
				: 0;
			entries.Add(new GitHubRepositoryEntry(path, type == "tree", size));
		}

		return entries;
	}

	public async Task<string> GetFileAsync(
		string owner, string name, string branch, string path, string accessToken,
		CancellationToken cancellationToken = default)
	{
		string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
		{
			throw new ArgumentException("Repository path must be a contained file path.", nameof(path));
		}
		string safePath = string.Join('/', segments.Select(Escape));
		using HttpRequestMessage request = CreateRequest(
			$"https://api.github.com/repos/{Escape(owner)}/{Escape(name)}/contents/{safePath}?ref={Escape(branch)}",
			accessToken);
		request.Headers.Accept.Clear();
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
		using HttpResponseMessage response = await http.SendAsync(
			request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		response.EnsureSuccessStatusCode();
		if (response.Content.Headers.ContentLength is > MaxFileBytes)
		{
			throw new InvalidDataException("The selected file is too large to preview.");
		}

		using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
		byte[] buffer = new byte[MaxFileBytes + 1];
		int length = 0;
		while (length < buffer.Length)
		{
			int read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken);
			if (read == 0)
			{
				break;
			}
			length += read;
		}

		if (length > MaxFileBytes || buffer.AsSpan(0, length).IndexOf((byte)0) >= 0)
		{
			throw new InvalidDataException("The selected file cannot be previewed as text.");
		}

		try
		{
			return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
				.GetString(buffer, 0, length);
		}
		catch (DecoderFallbackException ex)
		{
			throw new InvalidDataException("The selected file is not valid UTF-8 text.", ex);
		}
	}

	private static HttpRequestMessage CreateRequest(string url, string accessToken)
	{
		HttpRequestMessage request = new(HttpMethod.Get, url);
		request.Headers.UserAgent.ParseAdd("SpecDesk/1.0");
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
		request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
		return request;
	}

	private static string Escape(string value) => Uri.EscapeDataString(value);
}
