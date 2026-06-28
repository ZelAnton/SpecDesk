namespace SpecDesk.GitHub.Tests;

// Manual, opt-in verification of the REAL GitHub device flow end to end — this is how the auth-model risk
// is retired without a separate console app. Never runs in CI ([Explicit] + the LiveGitHub category is
// filtered out). To run it: register a GitHub OAuth App, then
//   SET SPECDESK_GITHUB_CLIENT_ID=<its public client id>
//   dotnet test --filter Category=LiveGitHub
// Follow the printed URL + code to authorize; the test asserts a real login comes back.
[TestFixture]
[Explicit("Hits real GitHub; run manually with SPECDESK_GITHUB_CLIENT_ID set.")]
[Category("LiveGitHub")]
public sealed class LiveDeviceFlowTests
{
    [Test]
    public async Task Real_device_flow_signs_in_and_returns_the_login()
    {
        string? clientId = Environment.GetEnvironmentVariable("SPECDESK_GITHUB_CLIENT_ID");
        if (string.IsNullOrEmpty(clientId))
        {
            Assert.Ignore("Set SPECDESK_GITHUB_CLIENT_ID (a registered OAuth App's client id) to run the live device flow.");
        }

        string authDir = Path.Combine(Path.GetTempPath(), "specdesk-gh-live-" + Guid.NewGuid().ToString("N"));
        using HttpClient http = new();
        GitHubDeviceFlowAuth auth = new(GitHubAuthOptions.ForClient(clientId!), http, authDir);

        try
        {
            DeviceCodePrompt prompt = await auth.StartSignInAsync();
            TestContext.Out.WriteLine($"\n=== Open {prompt.VerificationUri} and enter the code:  {prompt.UserCode}  ===\n");

            SignInResult result = await auth.AwaitAuthorizationAsync(prompt);

            Assert.Multiple(() =>
            {
                Assert.That(result.Outcome, Is.EqualTo(SignInOutcome.Authorized));
                Assert.That(result.Login, Is.Not.Null.And.Not.Empty);
            });
            TestContext.Out.WriteLine($"Signed in as {result.Login}");
        }
        finally
        {
            if (Directory.Exists(authDir))
            {
                Directory.Delete(authDir, recursive: true);
            }
        }
    }
}
