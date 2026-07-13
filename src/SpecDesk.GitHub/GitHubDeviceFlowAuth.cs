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
    private readonly object _sessionGate = new();
    private StoredToken? _session;
    private long _authorizationEpoch;
    private long _sessionEpoch;

    /// <summary>Production: wires the BCL HttpClient transport and the DPAPI-encrypted file store under
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
        _session = _store.Load();
    }

    public async Task<DeviceCodePrompt> StartSignInAsync(CancellationToken cancellationToken = default)
    {
        long epoch;
        lock (_sessionGate)
        {
            epoch = ++_authorizationEpoch;
        }
        DeviceCodeResponse response =
            await _api.RequestDeviceCodeAsync(_options.ClientId, _options.Scopes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sessionGate)
        {
            if (epoch != _authorizationEpoch)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
        return new DeviceCodePrompt(
            response.UserCode, response.VerificationUri, response.ExpiresIn, response.Interval, response.DeviceCode)
        {
            AuthorizationEpoch = epoch,
        };
    }

    public async Task<SignInResult> AwaitAuthorizationAsync(
        DeviceCodePrompt prompt, CancellationToken cancellationToken = default)
    {
        long epoch;
        lock (_sessionGate)
        {
            // Direct callers may construct a prompt themselves (the public API has always allowed that).
            // Give such a flow a fresh epoch; a prompt returned by StartSignInAsync keeps the epoch that was
            // claimed before its network request, so starting a newer flow invalidates the older one early.
            epoch = prompt.AuthorizationEpoch;
            if (epoch == 0)
            {
                epoch = ++_authorizationEpoch;
            }
            else if (epoch != _authorizationEpoch)
            {
                return SignInResult.TimedOut();
            }
        }
        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(
            () => CancelAuthorization(epoch));
        try
        {
            TimeSpan delay = prompt.Interval;
            DateTimeOffset deadline = _clock.GetUtcNow() + prompt.ExpiresIn;

            // Whether any poll got a real GitHub protocol response (pending / slow_down). If the deadline
            // elapses having only ever seen transient faults, GitHub was unreachable, not just slow.
            bool reachedGitHub = false;

            while (true)
            {
                if (_clock.GetUtcNow() >= deadline)
                {
                    return reachedGitHub ? SignInResult.TimedOut() : SignInResult.Unreachable();
                }

                DevicePollOutcome poll =
                    await _api.ExchangeAsync(_options.ClientId, prompt.DeviceCode, cancellationToken);
                switch (poll.Status)
                {
                    case DevicePollStatus.Pending:
                        reachedGitHub = true;
                        break;
                    case DevicePollStatus.Transient:
                        // A server/network fault — retry like Pending, but it doesn't prove we reached GitHub.
                        break;
                    case DevicePollStatus.SlowDown:
                        // GitHub asks us to back off; honour the +5s the spec mandates.
                        reachedGitHub = true;
                        delay += TimeSpan.FromSeconds(5);
                        break;
                    case DevicePollStatus.Expired:
                        return SignInResult.Expired();
                    case DevicePollStatus.Denied:
                        return SignInResult.Denied();
                    case DevicePollStatus.Error:
                        return SignInResult.Failed(poll.Error ?? "unknown_error");
                    case DevicePollStatus.Authorized:
                        SignInResult result =
                            await CompleteAuthorizationAsync(poll.AccessToken!, epoch, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        return result;
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
    private async Task<SignInResult> CompleteAuthorizationAsync(
        string accessToken, long epoch, CancellationToken ct)
    {
        string login = await FetchLoginWithRetryAsync(accessToken, ct);
        lock (_sessionGate)
        {
            if (ct.IsCancellationRequested || epoch != _authorizationEpoch)
            {
                throw new OperationCanceledException(ct);
            }
            try
            {
                // Keep the durable write and in-memory publication in the same commit gate as SignOut.
                // If cancellation arrives while Save blocks, its registered callback waits for this gate,
                // then clears this exact epoch before CancellationTokenSource.Cancel returns.
                _store.Save(new StoredToken(accessToken, login));
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                // Authorization succeeded, but persisting the token locally failed (a disk / DPAPI fault). The
                // network and GitHub were fine, so surface it distinctly rather than letting it propagate as a
                // "couldn't reach GitHub" exception. IsSignedIn stays false (nothing was stored), which is honest.
                return SignInResult.StorageFailed();
            }

            _session = new StoredToken(accessToken, login);
            _sessionEpoch = epoch;
        }
        return SignInResult.Authorized(login);
    }

    private void CancelAuthorization(long epoch)
    {
        lock (_sessionGate)
        {
            if (epoch == _authorizationEpoch)
            {
                _authorizationEpoch++;
            }
            if (_sessionEpoch != epoch)
            {
                return;
            }

            _session = null;
            _sessionEpoch = 0;
            try
            {
                _store.Clear();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cancellation still retires the process session. The host follows a user-initiated cancel
                // with SignOut, which retries durable cleanup and reports a storage failure if it persists.
            }
        }
    }

    private async Task<string> FetchLoginWithRetryAsync(string accessToken, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= LoginAttempts; attempt++)
        {
            LoginOutcome outcome = await _api.GetLoginAsync(accessToken, ct);
            if (outcome.Status == LoginStatus.Success)
            {
                return outcome.Login ?? string.Empty;
            }
            if (outcome.Status == LoginStatus.Failed)
            {
                // A non-transient fault (the token was rejected, or a malformed response). Retrying won't
                // help — give up the login (not the token); the caller persists the token with empty login.
                return string.Empty;
            }
            // Transient (a 5xx / rate-limit / connection blip / stall): we already hold a valid token, so
            // back off and retry; the final attempt gives up the login and keeps the token. Caller
            // cancellation surfaces from GetLoginAsync / the delay and propagates to TimedOut.
            if (attempt < LoginAttempts)
            {
                await _delay(LoginRetryDelay, ct);
            }
        }
        return string.Empty;
    }

    public bool IsSignedIn()
    {
        lock (_sessionGate)
        {
            return _session is not null;
        }
    }

    public string? SignedInLogin()
    {
        lock (_sessionGate)
        {
            return _session?.Login;
        }
    }

    public Task<T> WithAccessTokenAsync<T>(
        Func<string, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(use);
        StoredToken token;
        lock (_sessionGate)
        {
            token = _session ?? throw new InvalidOperationException("Not signed in to GitHub.");
        }
        return use(token.AccessToken, cancellationToken);
    }

    public void SignOut()
    {
        lock (_sessionGate)
        {
            // Invalidate every pending flow and serialize durable cleanup with the token/session commit. If
            // an authorization is currently saving, SignOut waits and clears it afterwards; if SignOut wins,
            // the stale flow observes the new epoch and cannot save at all.
            _authorizationEpoch++;
            _session = null;
            _sessionEpoch = 0;
            // Persistence failures remain visible to the caller: this process is safely signed out, but the
            // caller must not report a durable sign-out if the store could not record it for the next launch.
            _store.Clear();
        }
    }
}
