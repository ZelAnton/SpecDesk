namespace SpecDesk.GitHub.Tests;

[TestFixture]
public sealed class GitHubDeviceFlowAuthTests
{
    private static readonly Uri DeviceUri = new("https://github.com/login/device");
    private static readonly GitHubAuthOptions Options = GitHubAuthOptions.ForClient("test-client-id");

    private static DeviceCodeResponse DeviceCode(TimeSpan? expires = null, TimeSpan? interval = null) =>
        new("device-code", "WXYZ-1234", DeviceUri, expires ?? TimeSpan.FromMinutes(15), interval ?? TimeSpan.FromSeconds(5));

    private static DeviceCodePrompt Prompt(TimeSpan? expires = null, TimeSpan? interval = null) =>
        new("WXYZ-1234", DeviceUri, expires ?? TimeSpan.FromMinutes(15), interval ?? TimeSpan.FromSeconds(5), "device-code");

    // Builds the auth with an in-memory store and an instant delay that records the requested waits and
    // advances the clock (so the deadline check sees time pass without any real waiting).
    private static (GitHubDeviceFlowAuth Auth, List<TimeSpan> Delays, InMemoryTokenStore Store) Build(FakeDeviceFlowApi api)
    {
        TestClock clock = new(DateTimeOffset.UnixEpoch);
        List<TimeSpan> delays = [];
        InMemoryTokenStore store = new();
        Task Delay(TimeSpan d, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            delays.Add(d);
            clock.Advance(d);
            return Task.CompletedTask;
        }

        return (new GitHubDeviceFlowAuth(Options, api, store, clock, Delay), delays, store);
    }

    [Test]
    public async Task StartSignInAsync_maps_the_device_code_response_to_a_prompt()
    {
        FakeDeviceFlowApi api = new(DeviceCode(), "octocat");
        (GitHubDeviceFlowAuth auth, _, _) = Build(api);

        DeviceCodePrompt prompt = await auth.StartSignInAsync();

        Assert.Multiple(() =>
        {
            Assert.That(prompt.UserCode, Is.EqualTo("WXYZ-1234"));
            Assert.That(prompt.VerificationUri, Is.EqualTo(DeviceUri));
            Assert.That(prompt.ExpiresIn, Is.EqualTo(TimeSpan.FromMinutes(15)));
            Assert.That(prompt.Interval, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(prompt.DeviceCode, Is.EqualTo("device-code"));
        });
    }

    [Test]
    public async Task Authorized_on_first_poll_signs_in_and_persists_token_and_login()
    {
        FakeDeviceFlowApi api = new(DeviceCode(), "octocat", DevicePollOutcome.Authorized("gho_token"));
        (GitHubDeviceFlowAuth auth, List<TimeSpan> delays, InMemoryTokenStore store) = Build(api);

        SignInResult result = await auth.AwaitAuthorizationAsync(Prompt());

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SignInOutcome.Authorized));
            Assert.That(result.Login, Is.EqualTo("octocat"));
            Assert.That(store.Saved, Is.EqualTo(new StoredToken("gho_token", "octocat")));
            Assert.That(api.LastAccessToken, Is.EqualTo("gho_token"));
            Assert.That(delays, Is.Empty); // authorized first → never waited
            Assert.That(auth.SignedInLogin(), Is.EqualTo("octocat"));
            Assert.That(auth.IsSignedIn(), Is.True);
        });
    }

    [Test]
    public async Task A_transient_login_failure_is_retried_then_signs_in()
    {
        FakeDeviceFlowApi api = new(DeviceCode(), "octocat", DevicePollOutcome.Authorized("gho_token"))
        {
            LoginFailuresBeforeSuccess = 2,
        };
        (GitHubDeviceFlowAuth auth, _, InMemoryTokenStore store) = Build(api);

        SignInResult result = await auth.AwaitAuthorizationAsync(Prompt());

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SignInOutcome.Authorized));
            Assert.That(result.Login, Is.EqualTo("octocat"));
            Assert.That(api.LoginCalls, Is.EqualTo(3)); // two failures, then success
            Assert.That(store.Saved, Is.EqualTo(new StoredToken("gho_token", "octocat")));
        });
    }

    [Test]
    public async Task A_persistent_login_failure_still_persists_the_token_with_an_empty_login()
    {
        // The user authorized; GET /user being briefly down must not discard the (irreplaceable) token.
        FakeDeviceFlowApi api = new(DeviceCode(), "octocat", DevicePollOutcome.Authorized("gho_token"))
        {
            LoginNeverSucceeds = true,
        };
        (GitHubDeviceFlowAuth auth, List<TimeSpan> delays, InMemoryTokenStore store) = Build(api);

        SignInResult result = await auth.AwaitAuthorizationAsync(Prompt());

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SignInOutcome.Authorized));
            Assert.That(result.Login, Is.EqualTo(string.Empty));
            Assert.That(api.LoginCalls, Is.EqualTo(3)); // bounded by the retry budget
            // Two 1s login-retry backoffs (after attempts 1 and 2; the 3rd gives up) — not the 5s poll interval.
            Assert.That(delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1) }));
            Assert.That(store.Saved, Is.EqualTo(new StoredToken("gho_token", string.Empty)));
            Assert.That(auth.IsSignedIn(), Is.True); // the authorization is preserved
        });
    }

    [Test]
    public async Task Cancellation_during_the_login_retry_returns_TimedOut()
    {
        // The host cancels while the post-auth login lookup is being retried: it must surface as TimedOut
        // (via the retry backoff observing the token), not be swallowed into a sign-in.
        FakeDeviceFlowApi api = new(DeviceCode(), "octocat", DevicePollOutcome.Authorized("gho_token"))
        {
            LoginNeverSucceeds = true,
        };
        (GitHubDeviceFlowAuth auth, _, InMemoryTokenStore store) = Build(api);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        SignInResult result = await auth.AwaitAuthorizationAsync(Prompt(), cts.Token);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SignInOutcome.TimedOut));
            Assert.That(store.Saved, Is.Null); // cancelled before any save
        });
    }

    [Test]
    public async Task Pending_then_authorized_waits_one_interval_then_signs_in()
    {
        FakeDeviceFlowApi api = new(DeviceCode(), "octocat", DevicePollOutcome.Pending(), DevicePollOutcome.Authorized("t"));
        (GitHubDeviceFlowAuth auth, List<TimeSpan> delays, _) = Build(api);

        SignInResult result = await auth.AwaitAuthorizationAsync(Prompt(interval: TimeSpan.FromSeconds(5)));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SignInOutcome.Authorized));
            Assert.That(delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(5) }));
        });
    }

    [Test]
    public async Task Pending_twice_then_authorized_waits_twice()
    {
        FakeDeviceFlowApi api = new(DeviceCode(), "octocat",
            DevicePollOutcome.Pending(), DevicePollOutcome.Pending(), DevicePollOutcome.Authorized("t"));
        (GitHubDeviceFlowAuth auth, List<TimeSpan> delays, _) = Build(api);

        SignInResult result = await auth.AwaitAuthorizationAsync(Prompt(interval: TimeSpan.FromSeconds(5)));

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SignInOutcome.Authorized));
            Assert.That(delays, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public async Task Slow_down_increases_the_poll_interval_by_five_seconds()
    {
        FakeDeviceFlowApi api = new(DeviceCode(), "octocat",
            DevicePollOutcome.SlowDown(), DevicePollOutcome.Pending(), DevicePollOutcome.Authorized("t"));
        (GitHubDeviceFlowAuth auth, List<TimeSpan> delays, _) = Build(api);

        await auth.AwaitAuthorizationAsync(Prompt(interval: TimeSpan.FromSeconds(5)));

        // Base interval 5s + 5s after slow_down → every subsequent wait is 10s.
        Assert.That(delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10) }));
    }

    [Test]
    public async Task Expired_token_returns_Expired_without_throwing()
    {
        FakeDeviceFlowApi api = new(DeviceCode(), "octocat", DevicePollOutcome.Expired());
        (GitHubDeviceFlowAuth auth, _, InMemoryTokenStore store) = Build(api);

        SignInResult result = await auth.AwaitAuthorizationAsync(Prompt());

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SignInOutcome.Expired));
            Assert.That(store.Saved, Is.Null);
        });
    }

    [Test]
    public async Task Access_denied_returns_Denied_without_throwing()
    {
        FakeDeviceFlowApi api = new(DeviceCode(), "octocat", DevicePollOutcome.Denied());
        (GitHubDeviceFlowAuth auth, _, _) = Build(api);

        SignInResult result = await auth.AwaitAuthorizationAsync(Prompt());

        Assert.That(result.Outcome, Is.EqualTo(SignInOutcome.Denied));
    }

    [Test]
    public async Task Unknown_error_returns_Failed_with_the_code()
    {
        FakeDeviceFlowApi api = new(DeviceCode(), "octocat", DevicePollOutcome.Failure("incorrect_client_credentials"));
        (GitHubDeviceFlowAuth auth, _, _) = Build(api);

        SignInResult result = await auth.AwaitAuthorizationAsync(Prompt());

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcome, Is.EqualTo(SignInOutcome.Failed));
            Assert.That(result.Error, Is.EqualTo("incorrect_client_credentials"));
        });
    }

    [Test]
    public async Task Pending_past_the_expiry_deadline_returns_TimedOut()
    {
        // No scripted outcome → always Pending; the delay advances the clock past ExpiresIn.
        FakeDeviceFlowApi api = new(DeviceCode(), "octocat");
        (GitHubDeviceFlowAuth auth, _, _) = Build(api);

        SignInResult result = await auth.AwaitAuthorizationAsync(
            Prompt(expires: TimeSpan.FromSeconds(12), interval: TimeSpan.FromSeconds(5)));

        Assert.That(result.Outcome, Is.EqualTo(SignInOutcome.TimedOut));
    }

    [Test]
    public async Task A_cancelled_wait_returns_TimedOut()
    {
        FakeDeviceFlowApi api = new(DeviceCode(), "octocat", DevicePollOutcome.Pending());
        (GitHubDeviceFlowAuth auth, _, _) = Build(api);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        SignInResult result = await auth.AwaitAuthorizationAsync(Prompt(), cts.Token);

        Assert.That(result.Outcome, Is.EqualTo(SignInOutcome.TimedOut));
    }

    [Test]
    public void An_empty_client_id_is_rejected_at_construction()
    {
        Assert.Throws<ArgumentException>(() =>
            _ = new GitHubDeviceFlowAuth(
                GitHubAuthOptions.ForClient(""), new FakeDeviceFlowApi(DeviceCode(), "x"),
                new InMemoryTokenStore(), TimeProvider.System, delay: null));
    }
}
