using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SpecDesk.GitHub;

/// <summary>A pull request SpecDesk opened: its <see cref="Number"/> and the <see cref="Url"/> to show
/// the author (the GitHub web page for the PR).</summary>
public sealed record PullRequest(int Number, string Url);

/// <summary>
/// The GitHub review operations behind the "Send for review" flow. The access token is passed in (the host
/// gets it transiently via <see cref="IGitHubAuth.WithAccessTokenAsync{T}"/>) and used only as the Bearer
/// credential; it is never logged or stored here.
/// </summary>
public interface IGitHubReview
{
    /// <summary>Open a pull request from <paramref name="head"/> into <paramref name="baseBranch"/> in
    /// <paramref name="owner"/>/<paramref name="repo"/>. Throws on a transport / API failure (the host
    /// surfaces a plain "couldn't open the pull request").</summary>
    Task<PullRequest> OpenPullRequestAsync(
        string accessToken,
        string owner,
        string repo,
        string head,
        string baseBranch,
        string title,
        string body,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Production <see cref="IGitHubReview"/>: a single hand-rolled BCL <see cref="HttpClient"/> POST to the
/// REST API (no third-party GitHub SDK), under a per-request timeout, mirroring the device-flow transport.
/// </summary>
public sealed class GitHubReviewClient : IGitHubReview
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    // GitHub's REST API rejects requests without a User-Agent; this identifies the app (any value is fine).
    private static readonly ProductInfoHeaderValue UserAgent = new("SpecDesk", "1.0");

    private readonly HttpClient _http;

    public GitHubReviewClient(HttpClient http) => _http = http;

    public async Task<PullRequest> OpenPullRequestAsync(
        string accessToken,
        string owner,
        string repo,
        string head,
        string baseBranch,
        string title,
        string body,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        // Escape the path segments defensively; owner/repo come from a parsed github.com remote, but
        // never build a request URL from interpolated identifiers without escaping.
        Uri endpoint = new(
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls");
        using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.Add(UserAgent);
        // GitHub's create-PR fields are already lowercase, so no naming policy is needed; `base` is a C#
        // keyword escaped with @ (the serialized JSON key is "base").
        string json = JsonSerializer.Serialize(new { title, head, @base = baseBranch, body });
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token);
        string responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            // 422 (a PR already exists / invalid head or base), 404 (no repo / no push access), 5xx, … —
            // the host shows a plain "couldn't open the pull request"; the status aids the diagnostic log.
            throw new HttpRequestException(
                $"GitHub rejected the pull-request create (HTTP {(int)response.StatusCode}).");
        }

        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;
        return new PullRequest(NumberOf(root, "number"), StringOf(root, "html_url"));
    }

    private static int NumberOf(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out JsonElement element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetInt32(out int value)
            ? value
            : 0;

    private static string StringOf(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out JsonElement element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;
}
