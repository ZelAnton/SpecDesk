using Microsoft.Extensions.Logging;
using SpecDesk.Contracts;
using SpecDesk.GitHub;

namespace SpecDesk.Host;

// The GitHub sign-in slice of HostController: the device-flow sign-in / cancel / sign-out and the
// account-affordance frames sent to the webview.
// The shared fields, locks, constructor, and the IPC router live in HostController.cs.
public sealed partial class HostController
{
	// Connect the author's GitHub account: show the one-time code, then poll for authorization on a
	// background task (it runs for minutes). Cancellable; only one flow at a time.
	private void OnGitHubSignIn()
	{
		if (_auth is null)
		{
			SendCurrentAccount();
			return;
		}

		CancellationTokenSource cts;
		lock (_sync)
		{
			// Cancel the previous flow but do NOT dispose it here: its still-running task captured that
			// token and may be about to build a linked source from it, and disposing under it would throw
			// ObjectDisposedException that escapes as a spurious "Couldn't reach GitHub". Each task disposes
			// its OWN cts in its finally instead — a cancelled-but-alive token just yields clean cancellation.
			_signInCts?.Cancel();
			cts = new CancellationTokenSource();
			_signInCts = cts;
		}

		CancellationToken token = cts.Token;
		_ = Task.Run(async () =>
		{
			try
			{
				DeviceCodePrompt prompt = await _auth.StartSignInAsync(token);
				Emit(IpcSerializer.SerializeEvent(
					MessageKinds.GitHubCode,
					new GitHubCodePayload(prompt.UserCode, prompt.VerificationUri.ToString())));

				SignInResult result = await _auth.AwaitAuthorizationAsync(prompt, token);
				if (token.IsCancellationRequested)
				{
					// The author dismissed the sign-in. AwaitAuthorizationAsync folds our own cancellation
					// into TimedOut (it never throws once polling), so check the token here and fall back to
					// the signed-out affordance rather than showing the "code expired" message. But only if
					// this is still the current flow: a newer sign-in may have already replaced _signInCts (it
					// cancels the previous flow's token as part of starting), and this stale flow's fallback
					// must not close the newer flow's device-code prompt.
					EmitSignedOutIfStillCurrent(cts);
				}
				else if (result.Outcome == SignInOutcome.Authorized)
				{
					SendAccount(true, result.Login, message: null);
				}
				else
				{
					SendAccount(false, login: null, SignInMessage(result.Outcome));
				}
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				// Cancelled during the up-front device-code request (StartSignInAsync still throws on cancel,
				// unlike the poll) — fall back to the signed-out affordance, but only if a newer flow hasn't
				// already replaced this one (see the comment above).
				EmitSignedOutIfStillCurrent(cts);
			}
			catch (Exception ex)
			{
				// The up-front device-code request failed (transport / a GitHub error / a timeout).
				_logger.LogError(ex, "GitHub sign-in could not start");
				SendAccount(false, login: null, "Couldn't reach GitHub. Check your connection and try again.");
			}
			finally
			{
				// Dispose this flow's cts now that its token is no longer in use. Only clear the field if it
				// is still the current flow — a newer sign-in may have replaced it (and owns its own cts).
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

	private void OnGitHubSignInCancel()
	{
		lock (_sync)
		{
			_signInCts?.Cancel();
		}
	}

	// The cancelled-flow fallback for OnGitHubSignIn: emit the signed-out affordance only if this flow's
	// cts is still the current one. A newer sign-in replaces _signInCts before cancelling the previous
	// token, so a stale flow unwinding after that replacement must stay quiet rather than clobber the
	// newer flow's device-code prompt with an unrelated "signed out" frame.
	private void EmitSignedOutIfStillCurrent(CancellationTokenSource cts)
	{
		bool stillCurrent;
		lock (_sync)
		{
			stillCurrent = ReferenceEquals(_signInCts, cts);
		}

		if (stillCurrent)
		{
			SendCurrentAccount();
		}
	}

	private void OnGitHubSignOut()
	{
		_auth?.SignOut();
		SendCurrentAccount();
	}

	/// <summary>Emit the account affordance state from the store (signed in / out, or unavailable).</summary>
	private void SendCurrentAccount()
	{
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

	/// <summary>The author-facing line for a non-authorized sign-in ending (plain words, no OAuth jargon).</summary>
	private static string SignInMessage(SignInOutcome outcome) => outcome switch
	{
		SignInOutcome.Expired or SignInOutcome.TimedOut => "Your sign-in code expired. Connect again to retry.",
		SignInOutcome.Denied => "Sign-in was declined on GitHub.",
		SignInOutcome.Unreachable => "Couldn't reach GitHub. Check your connection and try again.",
		SignInOutcome.StorageFailed => "Signed in to GitHub, but couldn't save it on this device. Try again.",
		_ => "Couldn't sign in to GitHub. Please try again.",
	};
}
