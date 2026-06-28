using System.IO;
using System.Net;
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

/// <summary>How a single login (GET /user) lookup ended.</summary>
internal enum LoginStatus
{
    /// <summary>Got the login.</summary>
    Success,

    /// <summary>A transient fault (server down / rate-limit / connection blip / stall) — worth retrying.</summary>
    Transient,

    /// <summary>A non-transient fault (the token was rejected, or a malformed response): retrying won't help.</summary>
    Failed,
}

/// <summary>One login lookup's outcome: the login on <see cref="LoginStatus.Success"/>, a transient signal
/// the orchestrator retries, or a terminal failure it gives up on. Mirrors <see cref="DevicePollOutcome"/>
/// so the orchestrator loops on structure instead of catching a raw exception.</summary>
internal sealed record LoginOutcome(LoginStatus Status, string? Login, string? Error)
{
    public static LoginOutcome Success(string login) => new(LoginStatus.Success, login, null);

    public static LoginOutcome Transient() => new(LoginStatus.Transient, null, null);

    public static LoginOutcome Failure(string error) => new(LoginStatus.Failed, null, error);
}

/// <summary>The three GitHub network calls of device flow, behind a seam so the orchestration is unit-
/// tested with a scripted fake and never touches the network.</summary>
internal interface IDeviceFlowApi
{
    Task<DeviceCodeResponse> RequestDeviceCodeAsync(string clientId, IReadOnlyList<string> scopes, CancellationToken ct);

    /// <summary>A SINGLE token-exchange poll (no internal loop), so the orchestrator owns the backoff and
    /// gets the structured error code rather than a parsed exception message.</summary>
    Task<DevicePollOutcome> ExchangeAsync(string clientId, string deviceCode, CancellationToken ct);

    /// <summary>A SINGLE login (GET /user) lookup, returned as a structured outcome (no internal loop, no
    /// thrown transport faults) so the orchestrator retries transients and gives up on terminal failures
    /// without catching a raw exception. Only the caller's own cancellation throws.</summary>
    Task<LoginOutcome> GetLoginAsync(string accessToken, CancellationToken ct);
}

/// <summary>
/// Production transport: Octokit for the device-code request, and hand-rolled single BCL HttpClient calls
/// for the token exchange (POST) and the <c>GET /user</c> login lookup. Octokit's
/// <c>CreateAccessTokenForDeviceFlow</c> owns the poll loop and flattens every terminal error into one
/// message-parsed exception, and its calls take no per-request timeout / cancellation token — so owning the
/// HTTP gives a structured poll/login outcome, a per-request timeout, and honoured cancellation. Uses the
/// BCL only (no extra package).
/// </summary>
internal sealed class GitHubDeviceFlowApi : IDeviceFlowApi
{
    private const string TokenEndpoint = "https://github.com/login/oauth/access_token";
    private const string UserEndpoint = "https://api.github.com/user";

    // A single request's wall-clock budget — well under HttpClient's 100s default so a stalled exchange or
    // login lookup is detected promptly. Applied via a linked CancellationTokenSource so a (possibly shared)
    // injected HttpClient is never mutated. A stall maps to a retryable outcome, bounded by the loop.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    // GitHub's REST API rejects requests without a User-Agent; this identifies the app (any value is fine).
    private static readonly ProductInfoHeaderValue UserAgent = new("SpecDesk", "1.0");

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
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            return await ExchangeOnceAsync(clientId, deviceCode, timeout.Token);
        }
        catch (HttpRequestException)
        {
            // A connection-level transient (reset / DNS / TLS): no HTTP response arrived. Like a 5xx,
            // ride it out as a retryable poll — the loop is bounded by the device code's deadline — rather
            // than faulting the whole sign-in on a network blip mid-poll.
            return DevicePollOutcome.Pending();
        }
        catch (IOException)
        {
            // A broken read mid-body. The default ResponseContentRead buffering wraps this into the
            // HttpRequestException above, but guard it directly too so correctness doesn't depend on the
            // completion option — a transient read failure is still just a retryable poll.
            return DevicePollOutcome.Pending();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The per-request timeout fired (the linked token, not the caller's cancellation): a stalled
            // poll. Treat it as transient and retry on the next tick. Real caller cancellation — where
            // ct itself is cancelled — is not caught here and propagates to the orchestrator (→ TimedOut).
            return DevicePollOutcome.Pending();
        }
    }

    private async Task<DevicePollOutcome> ExchangeOnceAsync(string clientId, string deviceCode, CancellationToken ct)
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

        // Transient server states are not terminal: the device flow is a poll loop, so a 5xx (GitHub
        // down — it returns a non-JSON error page) maps to Pending and a 429 (rate-limited) to SlowDown.
        // The orchestrator's loop then retries, bounded by the device code's deadline (a graceful TimedOut
        // if GitHub never recovers), instead of parsing HTML and faulting the whole sign-in.
        if ((int)response.StatusCode >= 500)
        {
            return DevicePollOutcome.Pending();
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return DevicePollOutcome.SlowDown();
        }

        string body = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument? json = TryParseJson(body);
        if (json is null || json.RootElement.ValueKind != JsonValueKind.Object)
        {
            // A success/4xx response whose body isn't the documented JSON object — an intermediary's error
            // page (not JSON at all), or a bare JSON primitive/array. Treat it as transient and let the
            // loop retry, rather than faulting the sign-in: a non-object root would throw from the
            // TryGetProperty calls below, which only accept an object or null element.
            return DevicePollOutcome.Pending();
        }

        JsonElement root = json.RootElement;
        if (root.TryGetProperty("access_token", out JsonElement token) && StringValue(token) is { Length: > 0 } value)
        {
            return DevicePollOutcome.Authorized(value);
        }

        string error = root.TryGetProperty("error", out JsonElement e) ? StringValue(e) ?? "" : "";
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

    /// <summary>The element's value when it is a JSON string, else <c>null</c> — so a malformed response
    /// with a non-string <c>access_token</c>/<c>error</c> (a number, bool, object…) degrades to "no usable
    /// value" instead of throwing from <see cref="JsonElement.GetString"/> (which requires String or Null).</summary>
    private static string? StringValue(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    /// <summary>Parse the exchange response body, or <c>null</c> when it is not JSON.</summary>
    private static JsonDocument? TryParseJson(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // Not JSON — an HTML error page or a truncated/garbled body. The caller treats this as a
            // transient, retryable poll rather than faulting the whole sign-in on a parse exception.
            return null;
        }
    }

    public async Task<LoginOutcome> GetLoginAsync(string accessToken, CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            return await GetLoginOnceAsync(accessToken, timeout.Token);
        }
        catch (HttpRequestException)
        {
            // A connection-level transient (reset / DNS / TLS), or a content-read fault the default
            // buffering wraps here: no usable response. Retry — the orchestrator gives up the login (never
            // the token) only after a bounded number of attempts.
            return LoginOutcome.Transient();
        }
        catch (IOException)
        {
            // A broken read mid-body, guarded directly too so correctness doesn't depend on the buffering
            // completion option. Still transient.
            return LoginOutcome.Transient();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The per-request timeout fired (the linked token, not the caller's cancellation): a stalled
            // lookup. Report Transient so the orchestrator retries. Real caller cancellation propagates.
            return LoginOutcome.Transient();
        }
    }

    private async Task<LoginOutcome> GetLoginOnceAsync(string accessToken, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, UserEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.Add(UserAgent);

        using HttpResponseMessage response = await _http.SendAsync(request, ct);

        // A 5xx (server down) or 429 (rate-limited) is transient — retry. Any other non-success — a 401, or
        // a 403 (a rejected / under-scoped token, or a primary rate limit we don't separate out) — is
        // treated as terminal: retrying wouldn't help a bad token, and a rate-limited lookup still degrades
        // harmlessly to an empty (re-derivable) login with the token kept.
        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return LoginOutcome.Transient();
        }
        if (!response.IsSuccessStatusCode)
        {
            return LoginOutcome.Failure($"http_{(int)response.StatusCode}");
        }

        string body = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument? json = TryParseJson(body);
        if (json is null || json.RootElement.ValueKind != JsonValueKind.Object)
        {
            // A 200 whose body isn't the documented JSON object (an intermediary's page) — treat as transient.
            return LoginOutcome.Transient();
        }
        if (json.RootElement.TryGetProperty("login", out JsonElement loginElement)
            && StringValue(loginElement) is { Length: > 0 } login)
        {
            return LoginOutcome.Success(login);
        }
        // A 200 with no usable login field is unexpected and won't change on retry.
        return LoginOutcome.Failure("no_login");
    }
}
