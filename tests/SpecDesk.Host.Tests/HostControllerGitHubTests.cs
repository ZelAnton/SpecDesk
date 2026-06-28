using Microsoft.Extensions.Logging.Abstractions;
using SpecDesk.Contracts;
using SpecDesk.GitHub;
using SpecDesk.Markdown;

namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class HostControllerGitHubTests
{
    private sealed class NoDialogs : IFileDialogs
    {
        public string? PickOpenFile() => null;

        public string? PickSaveFile(string? suggestedPath) => null;
    }

    private static Renderer.RenderResult StubRender(string docDir, string text) => new(string.Empty, []);

    // A scripted IGitHubAuth: a fixed prompt + result, controllable signed-in state, and an optional
    // "never authorizes" mode (awaits cancellation) so the cancel path can be exercised.
    private sealed class FakeGitHubAuth(SignInResult result) : IGitHubAuth
    {
        private static readonly DeviceCodePrompt Prompt = new(
            "WXYZ-1234", new Uri("https://github.com/login/device"),
            TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5), "device-code");

        public bool SignedIn { get; set; }

        public string? Login { get; set; }

        public int SignOutCalls { get; private set; }

        /// <summary>When true, AwaitAuthorizationAsync blocks until cancelled (the user never authorizes).</summary>
        public bool BlockUntilCancelled { get; init; }

        public Task<DeviceCodePrompt> StartSignInAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Prompt);

        public async Task<SignInResult> AwaitAuthorizationAsync(
            DeviceCodePrompt prompt, CancellationToken cancellationToken = default)
        {
            if (BlockUntilCancelled)
            {
                // Mirror the real library: once polling, a cancel is folded into TimedOut, not thrown.
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return SignInResult.TimedOut();
                }
            }
            return result;
        }

        public bool IsSignedIn() => SignedIn;

        public string? SignedInLogin() => Login;

        public void SignOut()
        {
            SignOutCalls++;
            SignedIn = false;
            Login = null;
        }
    }

    private static (HostController Controller, List<string> Sent, object Gate) Build(IGitHubAuth? auth)
    {
        List<string> sent = [];
        object gate = new();
        void Send(string json)
        {
            lock (gate)
            {
                sent.Add(json);
            }
        }

        HostController controller = new(
            StubRender, Send, new NoDialogs(), (_, _, _, _, _) => null,
            new FakeVersioning(), NullLogger<HostController>.Instance, auth: auth);
        return (controller, sent, gate);
    }

    // Handlers reply from a background task; poll briefly for the expected message.
    private static IpcMessage? WaitForKind(List<string> sent, object gate, string kind)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            lock (gate)
            {
                foreach (string json in sent)
                {
                    IpcMessage? message = IpcSerializer.TryDeserialize(json);
                    if (message is not null && message.Kind == kind)
                    {
                        return message;
                    }
                }
            }

            Thread.Sleep(20);
        }

        return null;
    }

    [Test]
    public void SignIn_emits_the_code_then_the_authorized_account()
    {
        (HostController controller, List<string> sent, object gate) = Build(new FakeGitHubAuth(SignInResult.Authorized("octocat")));
        using (controller)
        {
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignIn));

            IpcMessage? code = WaitForKind(sent, gate, MessageKinds.GitHubCode);
            IpcMessage? account = WaitForKind(sent, gate, MessageKinds.GitHubAccount);

            Assert.That(code, Is.Not.Null);
            Assert.That(account, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(code!.GetPayload<GitHubCodePayload>()!.UserCode, Is.EqualTo("WXYZ-1234"));
                GitHubAccountPayload payload = account!.GetPayload<GitHubAccountPayload>()!;
                Assert.That(payload.SignedIn, Is.True);
                Assert.That(payload.Login, Is.EqualTo("octocat"));
                Assert.That(payload.Available, Is.True);
            });
        }
    }

    [Test]
    public void A_non_authorized_signIn_emits_a_plain_language_message()
    {
        (HostController controller, List<string> sent, object gate) = Build(new FakeGitHubAuth(SignInResult.Expired()));
        using (controller)
        {
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignIn));

            IpcMessage? account = WaitForKind(sent, gate, MessageKinds.GitHubAccount);

            Assert.That(account, Is.Not.Null);
            GitHubAccountPayload payload = account!.GetPayload<GitHubAccountPayload>()!;
            Assert.Multiple(() =>
            {
                Assert.That(payload.SignedIn, Is.False);
                Assert.That(payload.Message, Does.Contain("expired"));
            });
        }
    }

    [Test]
    public void SignOut_clears_the_account()
    {
        FakeGitHubAuth auth = new(SignInResult.Authorized("octocat")) { SignedIn = true, Login = "octocat" };
        (HostController controller, List<string> sent, object gate) = Build(auth);
        using (controller)
        {
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignOut));

            IpcMessage? account = WaitForKind(sent, gate, MessageKinds.GitHubAccount);

            Assert.That(account, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(auth.SignOutCalls, Is.EqualTo(1));
                Assert.That(account!.GetPayload<GitHubAccountPayload>()!.SignedIn, Is.False);
            });
        }
    }

    [Test]
    public void Ready_emits_the_current_account()
    {
        FakeGitHubAuth auth = new(SignInResult.Authorized("octocat")) { SignedIn = true, Login = "octocat" };
        (HostController controller, List<string> sent, object gate) = Build(auth);
        using (controller)
        {
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));

            IpcMessage? account = WaitForKind(sent, gate, MessageKinds.GitHubAccount);

            Assert.That(account, Is.Not.Null);
            GitHubAccountPayload payload = account!.GetPayload<GitHubAccountPayload>()!;
            Assert.Multiple(() =>
            {
                Assert.That(payload.Available, Is.True);
                Assert.That(payload.SignedIn, Is.True);
                Assert.That(payload.Login, Is.EqualTo("octocat"));
            });
        }
    }

    [Test]
    public void An_unconfigured_host_reports_the_account_unavailable()
    {
        (HostController controller, List<string> sent, object gate) = Build(auth: null);
        using (controller)
        {
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.Ready));
            // A sign-in request on an unconfigured host must not crash, and re-reports unavailable.
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignIn));

            IpcMessage? account = WaitForKind(sent, gate, MessageKinds.GitHubAccount);

            Assert.That(account, Is.Not.Null);
            Assert.That(account!.GetPayload<GitHubAccountPayload>()!.Available, Is.False);
        }
    }

    [Test]
    public void Cancelling_a_signIn_falls_back_to_signed_out()
    {
        FakeGitHubAuth auth = new(SignInResult.Authorized("octocat")) { BlockUntilCancelled = true };
        (HostController controller, List<string> sent, object gate) = Build(auth);
        using (controller)
        {
            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignIn));
            // Wait for the code to appear (the flow is now blocked awaiting authorization), then cancel.
            Assert.That(WaitForKind(sent, gate, MessageKinds.GitHubCode), Is.Not.Null);

            controller.OnMessage(IpcSerializer.SerializeEvent(MessageKinds.GitHubSignInCancel));

            IpcMessage? account = WaitForKind(sent, gate, MessageKinds.GitHubAccount);
            Assert.That(account, Is.Not.Null);
            GitHubAccountPayload payload = account!.GetPayload<GitHubAccountPayload>()!;
            Assert.Multiple(() =>
            {
                Assert.That(payload.SignedIn, Is.False);
                // A user cancel must NOT surface the "code expired" message (the host folds TimedOut from
                // its own cancellation back to a plain signed-out state).
                Assert.That(payload.Message, Is.Null);
            });
        }
    }
}
