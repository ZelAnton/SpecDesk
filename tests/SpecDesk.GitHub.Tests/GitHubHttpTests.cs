using System.Net;
using SpecDesk.AppInfo;

namespace SpecDesk.GitHub.Tests;

// Regression guard for the shared GitHub HTTP plumbing (GitHubHttp). DeviceFlowApi and GitHubReview each used
// to carry their own copy of the 30-second per-request timeout, the "SpecDesk/1.0" User-Agent, the linked-
// CancellationTokenSource pattern, and the safe JSON-field readers; those are now consolidated into the single
// internal GitHubHttp helper. These tests pin the shared timeout/User-Agent values and prove BOTH transports
// tag their real outgoing requests with the exact shared User-Agent — so a future edit that re-hard-codes a
// divergent value (like the old, ProductInfo-out-of-step "SpecDesk/1.0") in either transport is caught rather
// than silently letting the two safe-reading transports drift apart. The existing per-transport tests only
// assert the User-Agent *contains* "SpecDesk", which such a drift would still pass.
[TestFixture]
public sealed class GitHubHttpTests
{
    [Test]
    public void RequestTimeout_is_the_shared_thirty_second_per_request_budget()
    {
        Assert.That(GitHubHttp.RequestTimeout, Is.EqualTo(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public void UserAgent_is_derived_from_ProductInfo_not_a_stale_hard_coded_version()
    {
        // The pre-consolidation transports each hard-coded a second "1.0" literal out of step with the real
        // product version; the shared value must track ProductInfo (name + assembly version) instead.
        Assert.That(
            GitHubHttp.UserAgent.ToString(),
            Is.EqualTo($"{ProductInfo.Name}/{ProductInfo.Version}"));
    }

    [Test]
    public void NewTimeout_links_the_callers_cancellation_token()
    {
        // The shared linked-CTS pattern: the returned source is armed (not yet cancelled) but honours the
        // caller's token, so cancelling the caller cancels the per-request source.
        using CancellationTokenSource caller = new();
        using CancellationTokenSource timeout = GitHubHttp.NewTimeout(caller.Token);

        Assert.That(timeout.IsCancellationRequested, Is.False);
        caller.Cancel();
        Assert.That(timeout.IsCancellationRequested, Is.True);
    }

    [Test]
    public async Task DeviceFlowApi_tags_its_request_with_the_shared_GitHubHttp_UserAgent()
    {
        StubHttpMessageHandler handler = new(HttpStatusCode.OK, """{"error":"authorization_pending"}""");
        using HttpClient http = new(handler);
        GitHubDeviceFlowApi api = new(http);

        await api.ExchangeAsync("client-id", "device-code", CancellationToken.None);

        Assert.That(handler.LastRequest, Is.Not.Null);
        Assert.That(
            handler.LastRequest!.Headers.UserAgent.ToString(),
            Is.EqualTo(GitHubHttp.UserAgent.ToString()));
    }

    [Test]
    public async Task GitHubReviewClient_tags_its_request_with_the_shared_GitHubHttp_UserAgent()
    {
        StubHttpMessageHandler handler = new(HttpStatusCode.OK, "[]");
        using HttpClient http = new(handler);
        GitHubReviewClient client = new(http);

        await client.ListReviewCommentsAsync("token", "owner", "repo", 42);

        Assert.That(handler.LastRequest, Is.Not.Null);
        Assert.That(
            handler.LastRequest!.Headers.UserAgent.ToString(),
            Is.EqualTo(GitHubHttp.UserAgent.ToString()));
    }
}
