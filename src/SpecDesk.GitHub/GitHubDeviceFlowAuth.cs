namespace SpecDesk.GitHub;

/// <summary>
/// The GitHub OAuth device-flow sign-in. Owns the poll loop itself (against a single-shot exchange seam)
/// so backoff, the deadline, cancellation, and error→outcome mapping are all unit-testable offline, and
/// so a denied vs. expired code is distinguished structurally. On success the access token is persisted
/// to the secure store and never crosses the public boundary — only the login does.
/// </summary>
public sealed class GitHubDeviceFlowAuth : IGitHubAuth
{
    // The login lookup (GET /user) runs once, right after authorization. We already hold the irreplaceable
    // access token by then, so a transient blip on that call is retried a few times before we give up on
    // the (re-derivable) login — never on the token.
    private const int LoginAttempts = 3;
    private static readonly TimeSpan LoginRetryDelay = TimeSpan.FromSeconds(1);

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
                        return await CompleteAuthorizationAsync(poll.AccessToken!, cancellationToken);
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

    // Finish a successful authorization: fetch the login (bounded retry), then persist the token whatever
    // the login outcome. The token is the irreplaceable artifact — a failed login lookup must degrade to an
    // empty login (re-derivable on the next authenticated call), never discard the sign-in.
    private async Task<SignInResult> CompleteAuthorizationAsync(string accessToken, CancellationToken ct)
    {
        string login = await FetchLoginWithRetryAsync(accessToken, ct);
        _store.Save(new StoredToken(accessToken, login));
        return SignInResult.Authorized(login);
    }

    private async Task<string> FetchLoginWithRetryAsync(string accessToken, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await _api.GetLoginAsync(accessToken, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The host cancelled the sign-in — propagate so AwaitAuthorizationAsync maps it to TimedOut.
                throw;
            }
            catch (Exception) when (attempt < LoginAttempts)
            {
                // A transient fault on GET /user (a 5xx, a rate-limit, a connection blip) in the moment
                // after authorization. We already hold a valid token, so back off and retry the lookup.
                await _delay(LoginRetryDelay, ct);
            }
            catch (Exception)
            {
                // The final attempt failed: give up the login, not the token. The caller persists the token
                // with an empty login rather than losing a completed authorization.
                return string.Empty;
            }
        }
    }

    public bool IsSignedIn() => _store.Load() is not null;

    public string? SignedInLogin() => _store.Load()?.Login;

    public void SignOut() => _store.Clear();
}
