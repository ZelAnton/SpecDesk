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
        LastRequest = request;
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}

// A scripted device-flow transport: a fixed device-code response, a queue of poll outcomes, and a fixed
// login. Once the queue is exhausted it stays Pending, so a "never authorized" run reaches the deadline.
internal sealed class FakeDeviceFlowApi : IDeviceFlowApi
{
    private readonly DeviceCodeResponse _deviceCode;
    private readonly Queue<DevicePollOutcome> _polls;
    private readonly string _login;

    public int LoginCalls { get; private set; }

    public string? LastAccessToken { get; private set; }

    /// <summary>GetLoginAsync throws a transient fault on this many leading calls, then returns the login
    /// (models a blip on GET /user right after authorization). Default 0 → succeeds on the first call.</summary>
    public int LoginFailuresBeforeSuccess { get; init; }

    /// <summary>GetLoginAsync always throws a transient fault (models GET /user being down throughout).</summary>
    public bool LoginNeverSucceeds { get; init; }

    public FakeDeviceFlowApi(DeviceCodeResponse deviceCode, string login, params DevicePollOutcome[] polls)
    {
        _deviceCode = deviceCode;
        _login = login;
        _polls = new Queue<DevicePollOutcome>(polls);
    }

    public Task<DeviceCodeResponse> RequestDeviceCodeAsync(
        string clientId, IReadOnlyList<string> scopes, CancellationToken ct) => Task.FromResult(_deviceCode);

    public Task<DevicePollOutcome> ExchangeAsync(string clientId, string deviceCode, CancellationToken ct) =>
        Task.FromResult(_polls.Count > 0 ? _polls.Dequeue() : DevicePollOutcome.Pending());

    public Task<string> GetLoginAsync(string accessToken, CancellationToken ct)
    {
        LoginCalls++;
        LastAccessToken = accessToken;
        if (LoginNeverSucceeds || LoginCalls <= LoginFailuresBeforeSuccess)
        {
            throw new HttpRequestException("simulated GET /user failure");
        }
        return Task.FromResult(_login);
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

    public void Save(StoredToken token) => Saved = token;

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
