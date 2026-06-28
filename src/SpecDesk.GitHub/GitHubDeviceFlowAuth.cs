namespace SpecDesk.GitHub;

/// <summary>
/// The GitHub OAuth device-flow sign-in. Owns the poll loop itself (against a single-shot exchange seam)
/// so backoff, the deadline, cancellation, and error→outcome mapping are all unit-testable offline, and
/// so a denied vs. expired code is distinguished structurally. On success the access token is persisted
/// to the secure store and never crosses the public boundary — only the login does.
/// </summary>
public sealed class GitHubDeviceFlowAuth : IGitHubAuth
{
    private readonly GitHubAuthOptions _options;
    private readonly IDeviceFlowApi _api;
    private readonly ITokenStore _store;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>Production: wires the Octokit/HTTP transport and the DPAPI-encrypted file store under
    /// <paramref name="authDir"/> (the host passes <c>%LOCALAPPDATA%\SpecDesk\auth</c>).</summary>
    public GitHubDeviceFlowAuth(GitHubAuthOptions options, HttpClient http, string authDir)
        : this(
            options,
            new GitHubDeviceFlowApi(http),
            new FileTokenStore(new DpapiTokenProtector(), authDir),
            TimeProvider.System,
            delay: null)
    {
    }

    // Seam for tests: inject a scripted transport, an in-memory/identity-protected store, a controllable
    // clock, and an instant delay so the poll loop runs without real waits or network.
    internal GitHubDeviceFlowAuth(
        GitHubAuthOptions options,
        IDeviceFlowApi api,
        ITokenStore store,
        TimeProvider clock,
        Func<TimeSpan, CancellationToken, Task>? delay)
    {
        ArgumentException.ThrowIfNullOrEmpty(options.ClientId);
        _options = options;
        _api = api;
        _store = store;
        _clock = clock;
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }

    public async Task<DeviceCodePrompt> StartSignInAsync(CancellationToken cancellationToken = default)
    {
        DeviceCodeResponse response =
            await _api.RequestDeviceCodeAsync(_options.ClientId, _options.Scopes, cancellationToken);
        return new DeviceCodePrompt(
            response.UserCode, response.VerificationUri, response.ExpiresIn, response.Interval, response.DeviceCode);
    }

    public async Task<SignInResult> AwaitAuthorizationAsync(
        DeviceCodePrompt prompt, CancellationToken cancellationToken = default)
    {
        try
        {
            TimeSpan delay = prompt.Interval;
            DateTimeOffset deadline = _clock.GetUtcNow() + prompt.ExpiresIn;

            while (true)
            {
                if (_clock.GetUtcNow() >= deadline)
                {
                    return SignInResult.TimedOut();
                }

                DevicePollOutcome poll =
                    await _api.ExchangeAsync(_options.ClientId, prompt.DeviceCode, cancellationToken);
                switch (poll.Status)
                {
                    case DevicePollStatus.Pending:
                        break;
                    case DevicePollStatus.SlowDown:
                        // GitHub asks us to back off; honour the +5s the spec mandates.
                        delay += TimeSpan.FromSeconds(5);
                        break;
                    case DevicePollStatus.Expired:
                        return SignInResult.Expired();
                    case DevicePollStatus.Denied:
                        return SignInResult.Denied();
                    case DevicePollStatus.Error:
                        return SignInResult.Failed(poll.Error ?? "unknown_error");
                    case DevicePollStatus.Authorized:
                        string login = await _api.GetLoginAsync(poll.AccessToken!, cancellationToken);
                        _store.Save(new StoredToken(poll.AccessToken!, login));
                        return SignInResult.Authorized(login);
                }

                await _delay(delay, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host cancelled the wait (e.g. the user dismissed the sign-in). Surface it as a terminal
            // outcome rather than a fault, so the caller handles all endings uniformly (see the doc on TimedOut).
            return SignInResult.TimedOut();
        }
    }

    public bool IsSignedIn() => _store.Load() is not null;

    public string? SignedInLogin() => _store.Load()?.Login;

    public void SignOut() => _store.Clear();
}
