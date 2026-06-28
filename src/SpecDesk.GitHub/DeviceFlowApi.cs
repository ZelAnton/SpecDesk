using System.Net.Http.Headers;
using System.Text.Json;
using Octokit;

namespace SpecDesk.GitHub;

/// <summary>The device + user codes GitHub returns to start the flow.</summary>
internal sealed record DeviceCodeResponse(
    string DeviceCode,
    string UserCode,
    Uri VerificationUri,
    TimeSpan ExpiresIn,
    TimeSpan Interval);

/// <summary>The result of one token-exchange poll.</summary>
internal enum DevicePollStatus
{
    Pending,
    SlowDown,
    Expired,
    Denied,
    Authorized,
    Error,
}

/// <summary>One poll's outcome: the access token on <see cref="DevicePollStatus.Authorized"/>, or the
/// raw error code on <see cref="DevicePollStatus.Error"/>.</summary>
internal sealed record DevicePollOutcome(DevicePollStatus Status, string? AccessToken, string? Error)
{
    public static DevicePollOutcome Pending() => new(DevicePollStatus.Pending, null, null);

    public static DevicePollOutcome SlowDown() => new(DevicePollStatus.SlowDown, null, null);

    public static DevicePollOutcome Expired() => new(DevicePollStatus.Expired, null, null);

    public static DevicePollOutcome Denied() => new(DevicePollStatus.Denied, null, null);

    public static DevicePollOutcome Authorized(string token) => new(DevicePollStatus.Authorized, token, null);

    public static DevicePollOutcome Failure(string code) => new(DevicePollStatus.Error, null, code);
}

/// <summary>The three GitHub network calls of device flow, behind a seam so the orchestration is unit-
/// tested with a scripted fake and never touches the network.</summary>
internal interface IDeviceFlowApi
{
    Task<DeviceCodeResponse> RequestDeviceCodeAsync(string clientId, IReadOnlyList<string> scopes, CancellationToken ct);

    /// <summary>A SINGLE token-exchange poll (no internal loop), so the orchestrator owns the backoff and
    /// gets the structured error code rather than a parsed exception message.</summary>
    Task<DevicePollOutcome> ExchangeAsync(string clientId, string deviceCode, CancellationToken ct);

    Task<string> GetLoginAsync(string accessToken, CancellationToken ct);
}

/// <summary>
/// Production transport: Octokit for the device-code request and the <c>GET /user</c> login lookup, and a
/// hand-rolled single POST for the token exchange — Octokit's <c>CreateAccessTokenForDeviceFlow</c> owns
/// the poll loop and flattens every terminal error into one message-parsed exception, so it can't give us
/// one structured poll. Uses the BCL only (no extra package).
/// </summary>
internal sealed class GitHubDeviceFlowApi : IDeviceFlowApi
{
    private const string TokenEndpoint = "https://github.com/login/oauth/access_token";
    private static readonly Octokit.ProductHeaderValue Product = new("SpecDesk");

    private readonly HttpClient _http;

    public GitHubDeviceFlowApi(HttpClient http) => _http = http;

    public async Task<DeviceCodeResponse> RequestDeviceCodeAsync(
        string clientId, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        OauthDeviceFlowRequest request = new(clientId);
        foreach (string scope in scopes)
        {
            request.Scopes.Add(scope);
        }

        OauthDeviceFlowResponse response =
            await new GitHubClient(Product).Oauth.InitiateDeviceFlow(request, ct);
        return new DeviceCodeResponse(
            response.DeviceCode,
            response.UserCode,
            new Uri(response.VerificationUri),
            TimeSpan.FromSeconds(response.ExpiresIn),
            TimeSpan.FromSeconds(response.Interval));
    }

    public async Task<DevicePollOutcome> ExchangeAsync(string clientId, string deviceCode, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, TokenEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["device_code"] = deviceCode,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
        });

        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;

        if (root.TryGetProperty("access_token", out JsonElement token) && token.GetString() is { Length: > 0 } value)
        {
            return DevicePollOutcome.Authorized(value);
        }

        string error = root.TryGetProperty("error", out JsonElement e) ? e.GetString() ?? "" : "";
        return error switch
        {
            "authorization_pending" => DevicePollOutcome.Pending(),
            "slow_down" => DevicePollOutcome.SlowDown(),
            "expired_token" => DevicePollOutcome.Expired(),
            "access_denied" => DevicePollOutcome.Denied(),
            "" => DevicePollOutcome.Failure("unknown_error"),
            _ => DevicePollOutcome.Failure(error),
        };
    }

    public async Task<string> GetLoginAsync(string accessToken, CancellationToken ct)
    {
        GitHubClient client = new(Product) { Credentials = new Credentials(accessToken) };
        User user = await client.User.Current();
        return user.Login;
    }
}
