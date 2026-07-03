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

    /// <summary>Request reviewers on pull request <paramref name="pullNumber"/> in
    /// <paramref name="owner"/>/<paramref name="repo"/>. Each entry is an <c>@user</c> or <c>@org/team</c>
    /// handle (the leading <c>@</c> is optional); a handle containing <c>/</c> is treated as a team (its
    /// slug is the segment after the last <c>/</c>), everything else as a user. Returns the number of
    /// reviewers <em>asked for</em> (users + teams sent in the request) — 0, with no HTTP call, when the
    /// handles resolve to nothing usable, so the caller never reports assigning reviewers it didn't send.
    /// (GitHub may still silently ignore a handle it won't honour, e.g. the PR author, so this is the
    /// request count, not a confirmed-assigned count.) Throws on a transport / API failure — the host
    /// requests reviewers best-effort, so a failure never undoes the already-open PR.</summary>
    Task<int> RequestReviewersAsync(
        string accessToken,
        string owner,
        string repo,
        int pullNumber,
        IReadOnlyList<string> reviewers,
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
        using HttpRequestMessage request = NewRequest(HttpMethod.Post, endpoint, accessToken);
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

        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            JsonElement root = document.RootElement;
            return new PullRequest(NumberOf(root, "number"), StringOf(root, "html_url"));
        }
        catch (JsonException)
        {
            // A 2xx with an empty or non-JSON body still means GitHub created the PR — surfacing this as a
            // failure would strand the author in Draft with a pull request already open (and a retry would
            // then hit "already exists"). Treat it as success with unknown coordinates instead.
            return new PullRequest(0, string.Empty);
        }
    }

    public async Task<int> RequestReviewersAsync(
        string accessToken,
        string owner,
        string repo,
        int pullNumber,
        IReadOnlyList<string> reviewers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviewers);
        (IReadOnlyList<string> users, IReadOnlyList<string> teams) = Partition(reviewers);
        int count = users.Count + teams.Count;
        if (count == 0)
        {
            // The handles resolved to nothing usable (e.g. a "codeowners"-only list filtered upstream, or a
            // malformed entry) — make no HTTP call and report that zero were requested.
            return 0;
        }

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        Uri endpoint = new(
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/pulls/{pullNumber}/requested_reviewers");
        using HttpRequestMessage request = NewRequest(HttpMethod.Post, endpoint, accessToken);
        string json = JsonSerializer.Serialize(new { reviewers = users, team_reviewers = teams });
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using HttpResponseMessage response = await _http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            // This is a single all-or-nothing batch: GitHub rejects the whole request (422 if a reviewer
            // isn't a collaborator, 403 if a team review needs read:org, …) rather than assigning the valid
            // ones. The host logs and moves on — the PR is already open, so this is never fatal and the
            // author can add reviewers on GitHub.
            throw new HttpRequestException(
                $"GitHub rejected the reviewer request (HTTP {(int)response.StatusCode}).");
        }

        return count;
    }

    // Build a REST request with the standard GitHub headers (JSON accept, Bearer auth, User-Agent, and a
    // pinned API version so a future rolling-default bump can't silently change the contract).
    private static HttpRequestMessage NewRequest(HttpMethod method, Uri endpoint, string accessToken)
    {
        HttpRequestMessage request = new(method, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.Add(UserAgent);
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    // Split @user / @org/team handles into the API's separate `reviewers` (user logins) and
    // `team_reviewers` (team slugs) lists. The leading @ is optional; a handle containing '/' is a team
    // (its slug is the segment after the last '/'), everything else a user. Blank handles are dropped.
    private static (IReadOnlyList<string> Users, IReadOnlyList<string> Teams) Partition(
        IReadOnlyList<string> reviewers)
    {
        List<string> users = [];
        List<string> teams = [];
        foreach (string raw in reviewers)
        {
            string handle = raw.Trim().TrimStart('@');
            if (handle.Length == 0)
            {
                continue;
            }

            int slash = handle.LastIndexOf('/');
            if (slash < 0)
            {
                users.Add(handle);
            }
            else if (slash + 1 < handle.Length)
            {
                teams.Add(handle[(slash + 1)..]);
            }
        }

        return (users, teams);
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
