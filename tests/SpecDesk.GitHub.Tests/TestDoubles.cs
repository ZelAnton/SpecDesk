using System.Net;

namespace SpecDesk.GitHub.Tests;

// A one-shot HTTP transport: every request gets the same scripted status + body, and the last request
// (and its form body) are captured so a test can assert the exchange POST is well-formed. Lets the real
// GitHubDeviceFlowApi.ExchangeAsync run against a controlled response with no network.
internal sealed class StubHttpMessageHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRequest = request;
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}

// A multi-request transport for paged/list orchestration. Each request consumes one scripted response and
// records its URI so tests can verify escaping and page progression.
internal sealed class ScriptedHttpMessageHandler(
    params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new(responses);

    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request.RequestUri ?? throw new InvalidOperationException("request URI missing"));
        if (!_responses.TryDequeue(out (HttpStatusCode Status, string Body) response))
        {
            throw new InvalidOperationException("No scripted response remains.");
        }

        return Task.FromResult(new HttpResponseMessage(response.Status)
        {
            Content = new StringContent(response.Body),
        });
    }
}

// An HTTP transport that always faults the send with a given exception — models a connection-level
// transient (HttpRequestException) or a request timeout (TaskCanceledException) during the exchange poll.
internal sealed class ThrowingHttpMessageHandler(Exception toThrow) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromException<HttpResponseMessage>(toThrow);
}

// A scripted device-flow transport: a fixed device-code response, a queue of poll outcomes, and a fixed
// login. Once the queue is exhausted it stays Pending (or Transient — see TransientWhenExhausted), so a
// "never authorized" run reaches the deadline.
internal sealed class FakeDeviceFlowApi : IDeviceFlowApi
{
    private readonly DeviceCodeResponse _deviceCode;
    private readonly Queue<DevicePollOutcome> _polls;
    private readonly string _login;

    public int LoginCalls { get; private set; }

    public string? LastAccessToken { get; private set; }

    /// <summary>Once the scripted poll queue is exhausted, return Transient instead of Pending (models a
    /// never-reachable GitHub, so the deadline ends as Unreachable). Default false → Pending as before.</summary>
    public bool TransientWhenExhausted { get; init; }

    /// <summary>GetLoginAsync returns a transient outcome on this many leading calls, then the login
    /// (models a blip on GET /user right after authorization). Default 0 → succeeds on the first call.</summary>
    public int LoginFailuresBeforeSuccess { get; init; }

    /// <summary>GetLoginAsync always returns a transient outcome (models GET /user being down throughout).</summary>
    public bool LoginNeverSucceeds { get; init; }

    /// <summary>GetLoginAsync returns a terminal failure (models a rejected/under-scoped token — no retry).</summary>
    public bool LoginFatal { get; init; }

    public FakeDeviceFlowApi(DeviceCodeResponse deviceCode, string login, params DevicePollOutcome[] polls)
    {
        _deviceCode = deviceCode;
        _login = login;
        _polls = new Queue<DevicePollOutcome>(polls);
    }

    public Task<DeviceCodeResponse> RequestDeviceCodeAsync(
        string clientId, IReadOnlyList<string> scopes, CancellationToken ct) => Task.FromResult(_deviceCode);

    public Task<DevicePollOutcome> ExchangeAsync(string clientId, string deviceCode, CancellationToken ct) =>
        Task.FromResult(_polls.Count > 0
            ? _polls.Dequeue()
            : TransientWhenExhausted ? DevicePollOutcome.Transient() : DevicePollOutcome.Pending());

    public Task<LoginOutcome> GetLoginAsync(string accessToken, CancellationToken ct)
    {
        LoginCalls++;
        LastAccessToken = accessToken;
        if (LoginFatal)
        {
            return Task.FromResult(LoginOutcome.Failure("rejected"));
        }
        if (LoginNeverSucceeds || LoginCalls <= LoginFailuresBeforeSuccess)
        {
            return Task.FromResult(LoginOutcome.Transient());
        }
        return Task.FromResult(LoginOutcome.Success(_login));
    }
}

// Passthrough protector: lets FileTokenStore's file/serialization logic run without DPAPI (cross-platform).
internal sealed class IdentityTokenProtector : ITokenProtector
{
    public byte[] Protect(byte[] plaintext) => plaintext;

    public byte[] Unprotect(byte[] ciphertext) => ciphertext;
}

// In-memory token store for the orchestration tests (the file logic is tested separately).
internal sealed class InMemoryTokenStore : ITokenStore
{
    public StoredToken? Saved { get; private set; }

    /// <summary>When set, <see cref="Save"/> throws — to exercise the "authorized but couldn't persist the
    /// token locally" path (a disk / DPAPI fault).</summary>
    public bool ThrowOnSave { get; set; }

    public void Save(StoredToken token)
    {
        if (ThrowOnSave)
        {
            throw new IOException("token save boom");
        }

        Saved = token;
    }

    public StoredToken? Load() => Saved;

    public void Clear() => Saved = null;
}

// A controllable clock for the deadline check; the test's delay callback advances it.
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan amount) => _now += amount;
}
