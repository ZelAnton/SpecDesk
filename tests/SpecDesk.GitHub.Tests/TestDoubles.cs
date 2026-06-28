namespace SpecDesk.GitHub.Tests;

// A scripted device-flow transport: a fixed device-code response, a queue of poll outcomes, and a fixed
// login. Once the queue is exhausted it stays Pending, so a "never authorized" run reaches the deadline.
internal sealed class FakeDeviceFlowApi : IDeviceFlowApi
{
    private readonly DeviceCodeResponse _deviceCode;
    private readonly Queue<DevicePollOutcome> _polls;
    private readonly string _login;

    public int LoginCalls { get; private set; }

    public string? LastAccessToken { get; private set; }

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
