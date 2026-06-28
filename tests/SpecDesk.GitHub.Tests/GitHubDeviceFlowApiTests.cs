using System.Net;

namespace SpecDesk.GitHub.Tests;

// Covers the production token-exchange transport (GitHubDeviceFlowApi.ExchangeAsync) against a stubbed
// HTTP response — the orchestration tests use the fake seam, so this is the only place the real status →
// poll-outcome mapping (including the transient 5xx / 429 / non-JSON resilience) is exercised.
[TestFixture]
public sealed class GitHubDeviceFlowApiTests
{
    private static async Task<DevicePollOutcome> Exchange(StubHttpMessageHandler handler)
    {
        using HttpClient http = new(handler);
        GitHubDeviceFlowApi api = new(http);
        return await api.ExchangeAsync("client-id", "device-code", CancellationToken.None);
    }

    private static Task<DevicePollOutcome> Exchange(HttpStatusCode status, string body) =>
        Exchange(new StubHttpMessageHandler(status, body));

    private static async Task<DevicePollOutcome> ExchangeWith(HttpMessageHandler handler, CancellationToken ct)
    {
        using HttpClient http = new(handler);
        GitHubDeviceFlowApi api = new(http);
        return await api.ExchangeAsync("client-id", "device-code", ct);
    }

    private static async Task<LoginOutcome> GetLoginWith(HttpMessageHandler handler, CancellationToken ct)
    {
        using HttpClient http = new(handler);
        GitHubDeviceFlowApi api = new(http);
        return await api.GetLoginAsync("gho_token", ct);
    }

    private static Task<LoginOutcome> GetLogin(HttpStatusCode status, string body) =>
        GetLoginWith(new StubHttpMessageHandler(status, body), CancellationToken.None);

    [Test]
    public async Task ExchangeAsync_returns_Authorized_with_the_access_token()
    {
        DevicePollOutcome outcome = await Exchange(
            HttpStatusCode.OK, """{"access_token":"gho_token","token_type":"bearer"}""");

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(DevicePollStatus.Authorized));
            Assert.That(outcome.AccessToken, Is.EqualTo("gho_token"));
        });
    }

    [Test]
    public async Task ExchangeAsync_maps_authorization_pending_to_Pending()
    {
        DevicePollOutcome outcome = await Exchange(HttpStatusCode.OK, """{"error":"authorization_pending"}""");

        Assert.That(outcome.Status, Is.EqualTo(DevicePollStatus.Pending));
    }

    [Test]
    public async Task ExchangeAsync_maps_slow_down_to_SlowDown()
    {
        DevicePollOutcome outcome = await Exchange(HttpStatusCode.OK, """{"error":"slow_down"}""");

        Assert.That(outcome.Status, Is.EqualTo(DevicePollStatus.SlowDown));
    }

    [Test]
    public async Task ExchangeAsync_maps_expired_token_to_Expired()
    {
        DevicePollOutcome outcome = await Exchange(HttpStatusCode.OK, """{"error":"expired_token"}""");

        Assert.That(outcome.Status, Is.EqualTo(DevicePollStatus.Expired));
    }

    [Test]
    public async Task ExchangeAsync_maps_access_denied_to_Denied()
    {
        DevicePollOutcome outcome = await Exchange(HttpStatusCode.OK, """{"error":"access_denied"}""");

        Assert.That(outcome.Status, Is.EqualTo(DevicePollStatus.Denied));
    }

    [Test]
    public async Task ExchangeAsync_maps_a_structured_error_to_Failure_with_the_code()
    {
        DevicePollOutcome outcome = await Exchange(HttpStatusCode.OK, """{"error":"incorrect_client_credentials"}""");

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(DevicePollStatus.Error));
            Assert.That(outcome.Error, Is.EqualTo("incorrect_client_credentials"));
        });
    }

    [Test]
    public async Task ExchangeAsync_prefers_a_present_token_over_an_error_field()
    {
        // A usable token wins: it is checked before the error field, so an odd body carrying both is a
        // successful authorization, not a denial.
        DevicePollOutcome outcome = await Exchange(
            HttpStatusCode.OK, """{"access_token":"gho_token","error":"access_denied"}""");

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(DevicePollStatus.Authorized));
            Assert.That(outcome.AccessToken, Is.EqualTo("gho_token"));
        });
    }

    [Test]
    public async Task ExchangeAsync_treats_an_empty_access_token_as_unknown_error()
    {
        DevicePollOutcome outcome = await Exchange(HttpStatusCode.OK, """{"access_token":""}""");

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(DevicePollStatus.Error));
            Assert.That(outcome.Error, Is.EqualTo("unknown_error"));
        });
    }

    [Test]
    public async Task ExchangeAsync_maps_a_response_with_no_token_or_error_to_Failure_unknown()
    {
        DevicePollOutcome outcome = await Exchange(HttpStatusCode.OK, """{"token_type":"bearer"}""");

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(DevicePollStatus.Error));
            Assert.That(outcome.Error, Is.EqualTo("unknown_error"));
        });
    }

    [Test]
    public async Task ExchangeAsync_treats_a_503_as_Pending_so_the_poll_loop_retries()
    {
        // GitHub down: a 5xx returns an HTML error page, not JSON. Parsing it would throw and abort the
        // sign-in; instead the transport reports Pending so the deadline-bounded loop keeps polling.
        DevicePollOutcome outcome = await Exchange(
            HttpStatusCode.ServiceUnavailable, "<html><body>Service Unavailable</body></html>");

        Assert.That(outcome.Status, Is.EqualTo(DevicePollStatus.Pending));
    }

    [Test]
    public async Task ExchangeAsync_treats_a_500_with_an_empty_body_as_Pending()
    {
        DevicePollOutcome outcome = await Exchange(HttpStatusCode.InternalServerError, "");

        Assert.That(outcome.Status, Is.EqualTo(DevicePollStatus.Pending));
    }

    [Test]
    public async Task ExchangeAsync_treats_a_429_as_SlowDown()
    {
        DevicePollOutcome outcome = await Exchange(HttpStatusCode.TooManyRequests, "rate limited");

        Assert.That(outcome.Status, Is.EqualTo(DevicePollStatus.SlowDown));
    }

    [Test]
    public async Task ExchangeAsync_treats_a_connection_fault_as_Pending()
    {
        // A reset / DNS / TLS failure throws before any HTTP response — ride it out as a retryable poll.
        using ThrowingHttpMessageHandler handler = new(new HttpRequestException("connection reset"));

        DevicePollOutcome outcome = await ExchangeWith(handler, CancellationToken.None);

        Assert.That(outcome.Status, Is.EqualTo(DevicePollStatus.Pending));
    }

    [Test]
    public async Task ExchangeAsync_treats_a_request_timeout_as_Pending()
    {
        // A stalled request surfaces as a TaskCanceledException whose token is NOT the caller's — the
        // per-request timeout, treated as transient.
        using ThrowingHttpMessageHandler handler = new(new TaskCanceledException("request timed out"));

        DevicePollOutcome outcome = await ExchangeWith(handler, CancellationToken.None);

        Assert.That(outcome.Status, Is.EqualTo(DevicePollStatus.Pending));
    }

    [Test]
    public void ExchangeAsync_propagates_caller_cancellation()
    {
        // The caller's own cancellation must NOT be swallowed as a transient — it propagates so the
        // orchestrator maps it to TimedOut.
        using StubHttpMessageHandler handler = new(HttpStatusCode.OK, "{}");
        using CancellationTokenSource cts = new();
        cts.Cancel();

        // CatchAsync (not ThrowsAsync) so the OperationCanceledException subclass HttpClient raises
        // (TaskCanceledException) matches — the point is that it is NOT swallowed into a poll outcome.
        Assert.CatchAsync<OperationCanceledException>(() => ExchangeWith(handler, cts.Token));
    }

    [Test]
    public async Task ExchangeAsync_treats_a_non_JSON_success_body_as_Pending()
    {
        // A 2xx whose body isn't the documented JSON (e.g. an intermediary's page) degrades to a retry
        // rather than faulting the whole sign-in on a parse exception.
        DevicePollOutcome outcome = await Exchange(HttpStatusCode.OK, "not json at all");

        Assert.That(outcome.Status, Is.EqualTo(DevicePollStatus.Pending));
    }

    [TestCase("123")]
    [TestCase("[]")]
    [TestCase("\"a bare string\"")]
    [TestCase("true")]
    [TestCase("null")]
    public async Task ExchangeAsync_treats_a_valid_JSON_non_object_body_as_Pending(string body)
    {
        // Valid JSON but not an object: TryGetProperty would throw on a non-object root, so the transport
        // must classify it as transient (retry) rather than letting that escape and abort the sign-in.
        DevicePollOutcome outcome = await Exchange(HttpStatusCode.OK, body);

        Assert.That(outcome.Status, Is.EqualTo(DevicePollStatus.Pending));
    }

    [TestCase("""{"access_token":123}""")]
    [TestCase("""{"access_token":true}""")]
    [TestCase("""{"error":123}""")]
    public async Task ExchangeAsync_does_not_throw_on_a_non_string_token_or_error_value(string body)
    {
        // GetString() throws on a non-string element; a malformed object must still classify to a clean
        // outcome (here: unknown_error) rather than let that escape and abort the sign-in.
        DevicePollOutcome outcome = await Exchange(HttpStatusCode.OK, body);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(DevicePollStatus.Error));
            Assert.That(outcome.Error, Is.EqualTo("unknown_error"));
        });
    }

    [Test]
    public async Task ExchangeAsync_classifies_a_5xx_before_its_body_so_a_JSON_error_still_retries()
    {
        // A 503 that happens to carry a valid device-flow error body must still be Pending (transient),
        // never Denied — the status is classified before the body is parsed.
        DevicePollOutcome outcome = await Exchange(
            HttpStatusCode.ServiceUnavailable, """{"error":"access_denied"}""");

        Assert.That(outcome.Status, Is.EqualTo(DevicePollStatus.Pending));
    }

    [Test]
    public async Task ExchangeAsync_classifies_a_429_before_its_body()
    {
        DevicePollOutcome outcome = await Exchange(
            HttpStatusCode.TooManyRequests, """{"error":"access_denied"}""");

        Assert.That(outcome.Status, Is.EqualTo(DevicePollStatus.SlowDown));
    }

    [Test]
    public async Task ExchangeAsync_posts_a_form_encoded_device_code_grant()
    {
        StubHttpMessageHandler handler = new(HttpStatusCode.OK, """{"error":"authorization_pending"}""");

        await Exchange(handler);

        Assert.That(handler.LastRequest, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(handler.LastRequest!.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(
                handler.LastRequest.RequestUri,
                Is.EqualTo(new Uri("https://github.com/login/oauth/access_token")));
            Assert.That(
                handler.LastRequest.Headers.Accept.ToString(),
                Does.Contain("application/json"));
            Assert.That(handler.LastRequestBody, Does.Contain("client_id=client-id"));
            Assert.That(handler.LastRequestBody, Does.Contain("device_code=device-code"));
            Assert.That(
                handler.LastRequestBody,
                Does.Contain("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code"));
        });
    }

    [Test]
    public async Task GetLoginAsync_returns_Success_with_the_login()
    {
        LoginOutcome outcome = await GetLogin(HttpStatusCode.OK, """{"login":"octocat","id":1}""");

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(LoginStatus.Success));
            Assert.That(outcome.Login, Is.EqualTo("octocat"));
        });
    }

    [Test]
    public async Task GetLoginAsync_treats_a_5xx_as_Transient()
    {
        LoginOutcome outcome = await GetLogin(HttpStatusCode.ServiceUnavailable, "down");

        Assert.That(outcome.Status, Is.EqualTo(LoginStatus.Transient));
    }

    [Test]
    public async Task GetLoginAsync_treats_a_429_as_Transient()
    {
        LoginOutcome outcome = await GetLogin(HttpStatusCode.TooManyRequests, "rate limited");

        Assert.That(outcome.Status, Is.EqualTo(LoginStatus.Transient));
    }

    [Test]
    public async Task GetLoginAsync_treats_a_non_JSON_body_as_Transient()
    {
        LoginOutcome outcome = await GetLogin(HttpStatusCode.OK, "not json");

        Assert.That(outcome.Status, Is.EqualTo(LoginStatus.Transient));
    }

    [Test]
    public async Task GetLoginAsync_treats_a_rejected_token_as_a_terminal_Failure()
    {
        // A 401/403 won't improve on retry — terminal, so the orchestrator gives up the login at once.
        LoginOutcome outcome = await GetLogin(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(LoginStatus.Failed));
            Assert.That(outcome.Error, Is.EqualTo("http_401"));
        });
    }

    [Test]
    public async Task GetLoginAsync_treats_a_200_without_a_login_as_a_terminal_Failure()
    {
        LoginOutcome outcome = await GetLogin(HttpStatusCode.OK, """{"id":1}""");

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Status, Is.EqualTo(LoginStatus.Failed));
            Assert.That(outcome.Error, Is.EqualTo("no_login"));
        });
    }

    [Test]
    public async Task GetLoginAsync_treats_a_connection_fault_as_Transient()
    {
        using ThrowingHttpMessageHandler handler = new(new HttpRequestException("connection reset"));

        LoginOutcome outcome = await GetLoginWith(handler, CancellationToken.None);

        Assert.That(outcome.Status, Is.EqualTo(LoginStatus.Transient));
    }

    [Test]
    public void GetLoginAsync_propagates_caller_cancellation()
    {
        using StubHttpMessageHandler handler = new(HttpStatusCode.OK, """{"login":"octocat"}""");
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.CatchAsync<OperationCanceledException>(() => GetLoginWith(handler, cts.Token));
    }

    [Test]
    public async Task GetLoginAsync_sends_a_bearer_authorized_user_agent_request()
    {
        StubHttpMessageHandler handler = new(HttpStatusCode.OK, """{"login":"octocat"}""");

        await GetLoginWith(handler, CancellationToken.None);

        Assert.That(handler.LastRequest, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(handler.LastRequest!.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(handler.LastRequest.RequestUri, Is.EqualTo(new Uri("https://api.github.com/user")));
            Assert.That(handler.LastRequest.Headers.Authorization?.Scheme, Is.EqualTo("Bearer"));
            Assert.That(handler.LastRequest.Headers.Authorization?.Parameter, Is.EqualTo("gho_token"));
            Assert.That(handler.LastRequest.Headers.UserAgent.ToString(), Does.Contain("SpecDesk"));
            Assert.That(handler.LastRequest.Headers.Accept.ToString(), Does.Contain("application/vnd.github+json"));
        });
    }
}
