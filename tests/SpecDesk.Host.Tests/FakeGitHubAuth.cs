using SpecDesk.GitHub;

namespace SpecDesk.Host.Tests;

/// <summary>
/// A minimal <see cref="IGitHubAuth"/> for controller tests: a fixed signed-in state plus a token handed
/// transiently to <see cref="IGitHubAuth.WithAccessTokenAsync{T}"/> (throwing when signed out, mirroring
/// the real store). The device-flow members are unused here — the sign-in UX has its own tests.
/// </summary>
internal sealed class FakeGitHubAuth(bool signedIn, string accessToken = "gho_test") : IGitHubAuth
{
    /// <summary>The current signed-in state; mutable so a test can disconnect the account mid-session
    /// (e.g. after a document is already under review) to exercise the "connect first" guard.</summary>
    public bool SignedIn { get; set; } = signedIn;

    public Task<DeviceCodePrompt> StartSignInAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<SignInResult> AwaitAuthorizationAsync(
        DeviceCodePrompt prompt, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public bool IsSignedIn() => SignedIn;

    public string? SignedInLogin() => SignedIn ? "octocat" : null;

    public Task<T> WithAccessTokenAsync<T>(
        Func<string, CancellationToken, Task<T>> use, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(use);
        if (!SignedIn)
        {
            throw new InvalidOperationException("Not signed in to GitHub.");
        }

        return use(accessToken, cancellationToken);
    }

    public void SignOut()
    {
    }
}
