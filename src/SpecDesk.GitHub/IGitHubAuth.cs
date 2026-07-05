namespace SpecDesk.GitHub;

/// <summary>
/// What to show the user to authorize the device (GitHub OAuth device flow, RFC 8628 §3.2): the
/// <see cref="UserCode"/> they type at <see cref="VerificationUri"/>. The opaque <see cref="DeviceCode"/>
/// is round-tripped into <see cref="IGitHubAuth.AwaitAuthorizationAsync"/> so the implementation holds no
/// per-flow state; it is never shown to the user.
/// </summary>
public sealed record DeviceCodePrompt(
    string UserCode,
    Uri VerificationUri,
    TimeSpan ExpiresIn,
    TimeSpan Interval,
    string DeviceCode);

/// <summary>How awaiting device authorization ended.</summary>
public enum SignInOutcome
{
    /// <summary>The user authorized; a token was obtained and persisted.</summary>
    Authorized,

    /// <summary>GitHub reported the user code expired (an <c>expired_token</c> response) before they
    /// authorized — distinct from <see cref="TimedOut"/>, where our local deadline elapsed first; both mean
    /// "the code expired — try again".</summary>
    Expired,

    /// <summary>The user explicitly denied the authorization.</summary>
    Denied,

    /// <summary>The wait ended without authorization: either the device code's lifetime elapsed with GitHub
    /// reachable (the user didn't authorize in time), or the host cancelled the wait. (For "the deadline
    /// elapsed having never reached GitHub" see <see cref="Unreachable"/>.) The host shows "the code expired
    /// — try again", and suppresses it for its own cancellation, which it initiated.</summary>
    TimedOut,

    /// <summary>The device code's lifetime elapsed without ever reaching GitHub — every poll was a transient
    /// fault (an HTTP 5xx, a non-JSON body, a connection fault, or a stalled request), so we never saw a
    /// real protocol response. Distinct from <see cref="TimedOut"/> so the host can show "couldn't reach
    /// GitHub — check your connection" rather than "you took too long".</summary>
    Unreachable,

    /// <summary>GitHub's token endpoint returned an error code — e.g. a misconfigured client id; the
    /// <see cref="SignInResult.Error"/> carries the code. (During the initial device-code request a raw
    /// network/transport fault instead surfaces as an exception, which the host's background handler
    /// catches; once polling, such faults are ridden out — see <see cref="TimedOut"/> / <see cref="Unreachable"/>.)</summary>
    Failed,

    /// <summary>The user authorized and a valid token was obtained, but persisting it to the local secure
    /// store failed (a disk or DPAPI fault). GitHub and the network were fine, so this is distinct from
    /// <see cref="Unreachable"/> / <see cref="Failed"/>; the sign-in can't take effect (nothing loads back),
    /// so the host reports "signed in, but couldn't save it on this device".</summary>
    StorageFailed,
}

/// <summary>
/// The outcome of awaiting device authorization. On <see cref="SignInOutcome.Authorized"/> it carries the
/// authenticated <see cref="Login"/>; the access token never crosses this boundary. <see cref="Login"/> is
/// non-null only on Authorized, where it is the GitHub handle — or <c>""</c> when the sign-in succeeded but
/// the login lookup couldn't complete (the token is still valid; the handle is re-derivable). So treat
/// Authorized with an empty <see cref="Login"/> as "signed in, handle unknown", not as a failure.
/// </summary>
public sealed record SignInResult(SignInOutcome Outcome, string? Login, string? Error)
{
    public static SignInResult Authorized(string login) => new(SignInOutcome.Authorized, login, null);

    public static SignInResult Expired() => new(SignInOutcome.Expired, null, null);

    public static SignInResult Denied() => new(SignInOutcome.Denied, null, null);

    public static SignInResult TimedOut() => new(SignInOutcome.TimedOut, null, null);

    public static SignInResult Unreachable() => new(SignInOutcome.Unreachable, null, null);

    public static SignInResult Failed(string error) => new(SignInOutcome.Failed, null, error);

    public static SignInResult StorageFailed() => new(SignInOutcome.StorageFailed, null, null);
}

/// <summary>
/// GitHub OAuth device-flow sign-in and the secure token store behind it. Kept behind an interface so
/// the host is testable without a real GitHub session and so no HTTP / token types leak into
/// <c>SpecDesk.Host</c> — the access token is confined to <c>SpecDesk.GitHub</c>. Async and stateless:
/// the host starts the flow (to display the user code), then awaits authorization on a background task
/// and replies to the webview by echoing the request id, matching the existing host pattern.
/// </summary>
public interface IGitHubAuth
{
    /// <summary>Begin device flow: request a device + user code to display. Nothing is in flight yet, so
    /// any failure throws (rather than returning a result) for the host to show a "couldn't start sign-in"
    /// error and let the user retry — a transport failure, a GitHub API error (e.g. a misconfigured client
    /// id), a request timeout, or cancellation. The varied exception types mean the host's start handler
    /// should catch broadly. The returned prompt carries the opaque device code, so the implementation
    /// holds no per-flow state.</summary>
    Task<DeviceCodePrompt> StartSignInAsync(CancellationToken cancellationToken = default);

    /// <summary>Poll until the displayed code is authorized, expires, is denied, or the wait is
    /// cancelled / its deadline elapses. On <see cref="SignInOutcome.Authorized"/> the token is persisted
    /// to the secure store. Every outcome — including a structured GitHub error (<see cref="SignInOutcome.Failed"/>)
    /// and a failure persisting the token after a successful authorization (<see cref="SignInOutcome.StorageFailed"/>,
    /// a disk / permissions / encryption fault — GitHub and the network were fine) — is returned as a
    /// <see cref="SignInResult"/>, never thrown; the host's background handler can await this call without a
    /// surrounding try/catch for anything past the initial <see cref="StartSignInAsync"/> request. Transient
    /// failures while polling — an HTTP 5xx, a non-JSON body, a connection fault, or a stalled request — are
    /// ridden out as retryable polls (a 429 is honoured as a back-off and, being a real GitHub response,
    /// counts as reaching GitHub), so a brief GitHub or network blip doesn't fault the sign-in. When the
    /// device code's deadline elapses, the result is <see cref="SignInOutcome.TimedOut"/> if GitHub was
    /// reached (a real protocol response was seen — the user just didn't authorize in time) or
    /// <see cref="SignInOutcome.Unreachable"/> if every poll was a transient fault.</summary>
    Task<SignInResult> AwaitAuthorizationAsync(DeviceCodePrompt prompt, CancellationToken cancellationToken = default);

    /// <summary>Whether a usable token is stored. Local and cheap — does NOT call GitHub.</summary>
    bool IsSignedIn();

    /// <summary>The login of the stored session: a non-empty GitHub handle when known, <c>""</c> when signed
    /// in but the handle couldn't be looked up at sign-in (re-derivable later; the token still works), or
    /// <c>null</c> when signed out. So distinguish all three — an empty string is NOT "signed out". Local —
    /// no network call (the login is persisted alongside the token at sign-in).</summary>
    string? SignedInLogin();

    /// <summary>Run <paramref name="use"/> with the stored access token, giving it the token transiently —
    /// for a git push or a GitHub API call — without exposing the token as a returned or stored value (it
    /// stays confined to this scope). Throws <see cref="InvalidOperationException"/> when signed out, so
    /// gate with <see cref="IsSignedIn"/> first.</summary>
    Task<T> WithAccessTokenAsync<T>(
        Func<string, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default);

    /// <summary>Forget the stored token. Idempotent (a no-op when already signed out).</summary>
    void SignOut();
}
