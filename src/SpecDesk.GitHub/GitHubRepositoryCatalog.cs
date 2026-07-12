using System.Net.Http.Headers;
using System.Text.Json;

namespace SpecDesk.GitHub;

public sealed record GitHubRepositoryMetadata(string DefaultBranch);

public interface IGitHubRepositoryCatalog
{
	Task<GitHubRepositoryMetadata> GetMetadataAsync(
		string owner, string name, string accessToken, CancellationToken cancellationToken = default);
}

/// <summary>Reads repository metadata through GitHub's REST API.</summary>
public sealed class GitHubRepositoryCatalog(HttpClient http) : IGitHubRepositoryCatalog
{
	public async Task<GitHubRepositoryMetadata> GetMetadataAsync(
		string owner, string name, string accessToken, CancellationToken cancellationToken = default)
	{
		using HttpRequestMessage request = CreateRequest(
			$"https://api.github.com/repos/{Escape(owner)}/{Escape(name)}", accessToken);
		using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
		response.EnsureSuccessStatusCode();
		using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
		if (!json.RootElement.TryGetProperty("default_branch", out JsonElement branch)
			|| branch.ValueKind != JsonValueKind.String
			|| string.IsNullOrWhiteSpace(branch.GetString()))
		{
			throw new InvalidDataException("GitHub returned no default branch.");
		}

		return new GitHubRepositoryMetadata(branch.GetString()!);
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
