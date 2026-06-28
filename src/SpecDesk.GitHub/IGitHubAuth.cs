namespace SpecDesk.GitHub;

/// <summary>
/// What to show the user to authorize the device (GitHub OAuth device flow, RFC 8628 §3.2): the
/// <see cref="UserCode"/> they type at <see cref="VerificationUri"/>. The opaque <see cref="DeviceCode"/>
/// is round-tripped into <see cref="IGitHubAuth.AwaitAuthorizationAsync"/> so the implementation stays
/// stateless (like <c>LibGit2DocumentVersioning</c>); it is never shown to the user.
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

    /// <summary>The user code expired before they authorized.</summary>
    Expired,

    /// <summary>The user explicitly denied the authorization.</summary>
    Denied,

    /// <summary>The local wait was cancelled, or its own deadline elapsed — including a transient GitHub /
    /// network outage (an HTTP 5xx / 429, a non-JSON body, a connection fault, or a stalled request) that
    /// is retried but never recovers within the device code's lifetime.</summary>
    TimedOut,

    /// <summary>GitHub's token endpoint returned an error code — e.g. a misconfigured client id; the
    /// <see cref="SignInResult.Error"/> carries the code. (During the initial device-code request a raw
    /// network/transport fault instead surfaces as an exception, which the host's background handler
    /// catches; once polling, such faults are ridden out — see <see cref="SignInOutcome.TimedOut"/>.)</summary>
    Failed,
}

/// <summary>
/// The outcome of awaiting device authorization. On <see cref="SignInOutcome.Authorized"/> it carries
/// the authenticated <see cref="Login"/>; the access token never crosses this boundary.
/// </summary>
public sealed record SignInResult(SignInOutcome Outcome, string? Login, string? Error)
{
    public static SignInResult Authorized(string login) => new(SignInOutcome.Authorized, login, null);

    public static SignInResult Expired() => new(SignInOutcome.Expired, null, null);

    public static SignInResult Denied() => new(SignInOutcome.Denied, null, null);

    public static SignInResult TimedOut() => new(SignInOutcome.TimedOut, null, null);

    public static SignInResult Failed(string error) => new(SignInOutcome.Failed, null, error);
}

/// <summary>
/// GitHub OAuth device-flow sign-in and the secure token store behind it. Kept behind an interface so
/// the host is testable without a real GitHub session and so no Octokit / HTTP / token types leak into
/// <c>SpecDesk.Host</c> — the access token is confined to <c>SpecDesk.GitHub</c>. Async and stateless:
/// the host starts the flow (to display the user code), then awaits authorization on a background task
/// and replies to the webview by echoing the request id, matching the existing host pattern.
/// </summary>
public interface IGitHubAuth
{
    /// <summary>Begin device flow: request a device + user code to display. Throws only on a hard
    /// transport failure (network / HTTP). The returned prompt carries the opaque device code, so the
    /// implementation holds no per-flow state.</summary>
    Task<DeviceCodePrompt> StartSignInAsync(CancellationToken cancellationToken = default);

    /// <summary>Poll until the displayed code is authorized, expires, is denied, or the wait is
    /// cancelled / its deadline elapses. On <see cref="SignInOutcome.Authorized"/> the token is persisted
    /// to the secure store. Every outcome — including a structured GitHub error (<see cref="SignInOutcome.Failed"/>)
    /// — is returned as a <see cref="SignInResult"/>. Transient failures while polling — an HTTP 5xx / 429,
    /// a non-JSON body, a connection fault, or a stalled request — are ridden out as retryable polls, so a
    /// brief GitHub or network blip doesn't fault the sign-in; a sustained outage ends as
    /// <see cref="SignInOutcome.TimedOut"/> when the device code's deadline elapses.</summary>
    Task<SignInResult> AwaitAuthorizationAsync(DeviceCodePrompt prompt, CancellationToken cancellationToken = default);

    /// <summary>Whether a usable token is stored. Local and cheap — does NOT call GitHub.</summary>
    bool IsSignedIn();

    /// <summary>The login of the stored session, or <c>null</c> when signed out. Local — no network call
    /// (the login is persisted alongside the token at sign-in).</summary>
    string? SignedInLogin();

    /// <summary>Forget the stored token. Idempotent (a no-op when already signed out).</summary>
    void SignOut();
}
