using Microsoft.Extensions.Logging;
using SpecDesk.Contracts;
using SpecDesk.GitHub;

namespace SpecDesk.Host;

public sealed partial class HostController
{
	// Serializes flow replacement with user-visible sign-in publications. _sync protects state; this gate
	// guarantees an older flow can never publish after a newer flow has become current without holding
	// _sync while calling the transport or resuming repository actions.
	private readonly object _signInPublishSync = new();

	private void OnGitHubSignIn()
	{
		if (_auth is null)
		{
			lock (_signInPublishSync)
			{
				lock (_sync)
				{
					if (_disposed)
					{
						return;
					}
				}
				SendCurrentAccount();
			}
			return;
		}

		CancellationTokenSource cts;
		lock (_signInPublishSync)
		{
			lock (_sync)
			{
				if (_disposed)
				{
					return;
				}
				_signInCts?.Cancel();
				cts = new CancellationTokenSource();
				_signInCts = cts;
			}
		}

		CancellationToken token = cts.Token;
		_ = Task.Run(async () =>
		{
			try
			{
				DeviceCodePrompt prompt = await _auth.StartSignInAsync(token);
				if (!PublishPromptIfCurrent(cts, prompt))
				{
					return;
				}

				SignInResult result = await _auth.AwaitAuthorizationAsync(prompt, token);
				if (token.IsCancellationRequested)
				{
					PublishTerminalIfCurrent(cts, signedIn: false, login: null, message: null, resume: false);
				}
				else if (result.Outcome == SignInOutcome.Authorized)
				{
					PublishTerminalIfCurrent(cts, signedIn: true, result.Login, message: null, resume: true);
				}
				else
				{
					PublishTerminalIfCurrent(
						cts, signedIn: false, login: null, SignInMessage(result.Outcome), resume: false);
				}
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				PublishTerminalIfCurrent(cts, signedIn: false, login: null, message: null, resume: false);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "GitHub sign-in could not start");
				PublishTerminalIfCurrent(
					cts, signedIn: false, login: null,
					"Couldn't reach GitHub. Check your connection and try again.", resume: false);
			}
			finally
			{
				lock (_sync)
				{
					if (ReferenceEquals(_signInCts, cts))
					{
						_signInCts = null;
					}
				}
				cts.Dispose();
			}
		});
	}

	private bool PublishPromptIfCurrent(CancellationTokenSource cts, DeviceCodePrompt prompt)
	{
		lock (_signInPublishSync)
		{
			lock (_sync)
			{
				if (_disposed || !ReferenceEquals(_signInCts, cts))
				{
					return false;
				}
			}

			Emit(IpcSerializer.SerializeEvent(
				MessageKinds.GitHubCode,
				new GitHubCodePayload(prompt.UserCode, prompt.VerificationUri.ToString())));
			return true;
		}
	}

	private void PublishTerminalIfCurrent(
		CancellationTokenSource cts,
		bool signedIn,
		string? login,
		string? message,
		bool resume)
	{
		PendingRepoActions? actions = null;
		lock (_signInPublishSync)
		{
			lock (_sync)
			{
				if (_disposed || !ReferenceEquals(_signInCts, cts))
				{
					return;
				}
				actions = TakePendingRepoActions();
				// Retire this flow atomically with taking its actions. A repository request arriving after
				// this point either observes the stored authorization or starts a fresh flow; it can never
				// enqueue behind a terminal flow whose queue was already drained.
				_signInCts = null;
			}

			SendAccount(signedIn, login, message);
			if (resume && actions is not null)
			{
				ResumePendingRepoActions(actions);
			}
		}
	}

	private void OnGitHubSignInCancel()
	{
		lock (_signInPublishSync)
		{
			lock (_sync)
			{
				TakePendingRepoActions();
				_signInCts?.Cancel();
				_signInCts = null;
			}
			// The retired task is deliberately stale and therefore silent. Publish the one terminal frame
			// here so the webview closes the code prompt and restores the account affordance.
			SendCurrentAccount();
		}
	}

	private void OnGitHubSignOut()
	{
		lock (_signInPublishSync)
		{
			lock (_sync)
			{
				TakePendingRepoActions();
				_signInCts?.Cancel();
				_signInCts = null;
			}
			_auth?.SignOut();
			SendCurrentAccount();
		}
	}

	private void SendCurrentAccount()
	{
		lock (_sync)
		{
			if (_signInCts is not null)
			{
				return;
			}
		}

		if (_auth is null)
		{
			SendAccount(false, login: null, message: null, available: false);
			return;
		}
		SendAccount(_auth.IsSignedIn(), _auth.SignedInLogin(), message: null);
	}

	private void SendAccount(bool signedIn, string? login, string? message, bool available = true) =>
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.GitHubAccount,
			new GitHubAccountPayload(available, signedIn, login, message)));

	private static string SignInMessage(SignInOutcome outcome) => outcome switch
	{
		SignInOutcome.Expired or SignInOutcome.TimedOut => "Your sign-in code expired. Connect again to retry.",
		SignInOutcome.Denied => "Sign-in was declined on GitHub.",
		SignInOutcome.Unreachable => "Couldn't reach GitHub. Check your connection and try again.",
		SignInOutcome.StorageFailed => "Signed in to GitHub, but couldn't save it on this device. Try again.",
		_ => "Couldn't sign in to GitHub. Please try again.",
	};
}
