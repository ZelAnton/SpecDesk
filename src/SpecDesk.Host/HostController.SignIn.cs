using Microsoft.Extensions.Logging;
using SpecDesk.Contracts;
using SpecDesk.GitHub;

namespace SpecDesk.Host;

public sealed partial class HostController
{
	private readonly record struct AccountSession(long Generation, CancellationToken CancellationToken);

	// Serializes flow replacement with user-visible sign-in publications. _sync protects state; this gate
	// guarantees an older flow can never publish after a newer flow has become current without holding
	// _sync while calling the transport or resuming repository actions.
	private readonly object _signInPublishSync = new();

	private bool TryCaptureAccountSession(out AccountSession session)
	{
		lock (_signInPublishSync)
		{
			lock (_sync)
			{
				if (_disposed || _auth?.IsSignedIn() != true)
				{
					session = default;
					return false;
				}
				session = new AccountSession(_accountSessionGeneration, _accountSessionCts.Token);
				return true;
			}
		}
	}

	private bool IsAccountSessionCurrent(AccountSession session)
	{
		lock (_signInPublishSync)
		{
			lock (_sync)
			{
				return IsAccountSessionCurrentLocked(session);
			}
		}
	}

	private bool IsAccountSessionCurrentLocked(AccountSession session) =>
		!_disposed
		&& !session.CancellationToken.IsCancellationRequested
		&& session.Generation == _accountSessionGeneration
		&& _auth?.IsSignedIn() == true;

	private bool PublishForAccountSession(AccountSession session, Action publish)
	{
		lock (_signInPublishSync)
		{
			lock (_sync)
			{
				if (!IsAccountSessionCurrentLocked(session))
				{
					return false;
				}
			}
			publish();
			return true;
		}
	}

	private bool StartForAccountSession(AccountSession session, Action start) =>
		PublishForAccountSession(session, start);

	private void RotateAccountSessionLocked()
	{
		CancellationTokenSource previous = _accountSessionCts;
		_accountSessionGeneration++;
		previous.Cancel();
		_accountSessionCts = new CancellationTokenSource();
		previous.Dispose();
	}

	private void CancelCurrentAccountSession()
	{
		CancellationTokenSource current;
		lock (_sync)
		{
			current = _accountSessionCts;
		}
		try
		{
			current.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// A concurrent account-session rotation already retired this source.
		}
	}
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
					await ResetChatAgentAsync();
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
				if (signedIn)
				{
					RotateAccountSessionLocked();
				}
				// Retire this flow atomically with taking its actions. A repository request arriving after
				// this point either observes the stored authorization or starts a fresh flow; it can never
				// enqueue behind a terminal flow whose queue was already drained.
				_signInCts = null;
			}

			SendAccount(signedIn, login, message);
			if (signedIn)
			{
				RefreshAccountOrganizations(login);
			}
			else
			{
				InvalidateAccountDetails();
			}
			if (resume && actions is not null)
			{
				ResumePendingRepoActions(actions);
			}
		}
	}

	private void OnGitHubSignInCancel()
	{
		string? persistenceMessage = null;
		bool hadActiveFlow;
		lock (_signInPublishSync)
		{
			lock (_sync)
			{
				TakePendingRepoActions();
				hadActiveFlow = _signInCts is not null;
				_signInCts?.Cancel();
				_signInCts = null;
			}
			try
			{
				// Cancellation retires the auth-level epoch too. This call is serialized with the token/session
				// commit, so a flow already inside a blocking secure-store write is cleared before Cancel returns.
				if (hadActiveFlow)
				{
					_auth?.SignOut();
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				_logger.LogWarning(ex, "Could not persist the GitHub sign-in cancellation");
				persistenceMessage =
					"Sign-in was cancelled for this session, but SpecDesk couldn't update the saved GitHub authorization. Try disconnecting again after closing apps that may be using it.";
			}
			// The retired task is deliberately stale and therefore silent. Publish the one terminal frame
			// here so the webview closes the code prompt and restores the account affordance.
			if (persistenceMessage is null)
			{
				SendCurrentAccount();
			}
			else
			{
				InvalidateAccountDetails();
				SendAccount(signedIn: false, login: null, persistenceMessage);
			}
		}
	}

	private void OnGitHubSignOut()
	{
		string? persistenceMessage = null;
		CancelCurrentAccountSession();
		lock (_signInPublishSync)
		{
			// Match Dispose's publication-gate order. Each account-bound task must cross its gate before
			// publishing, so retiring every generation while all gates are held makes sign-out a hard
			// publication boundary even when a GitHub client ignores cancellation and completes late.
			lock (_clonePublishSync)
			{
				lock (_remotePublishSync)
				{
					lock (_repositoryDescriptionPublishSync)
					{
						lock (_sync)
						{
							RotateAccountSessionLocked();
							RetirePublishInFlightLocked();
							TakePendingRepoActions();
							_signInCts?.Cancel();
							_signInCts = null;

							_accountDetailsGeneration++;
							_accountDetailsCts?.Cancel();
							_accountDetailsCts = null;

							_repositoryDescriptionGeneration++;
							_repositoryDescriptionCts?.Cancel();
							_repositoryDescriptionCts = null;

							_cloneGeneration++;
							_cloneCts?.Cancel();
							_cloneCts = null;
							_cloneRepoId = null;
							_repoMetadataGeneration++;
							foreach (RepoMetadataLookup lookup in _repoMetadataLookups.Values)
							{
								lookup.Cts.Cancel();
							}
							_repoMetadataLookups.Clear();

							_remoteBrowseGeneration++;
							_remoteBrowseIntentGeneration++;
							_remoteFileGeneration++;
							_remoteBrowseCts?.Cancel();
							_remoteBrowseCts = null;
							_remoteBrowseRepoId = null;
							_remoteBrowseIntentRepoId = null;
							_remoteFileCts?.Cancel();
							_remoteFileCts = null;
							_remoteFileRepoId = null;

							_chatCts?.Cancel();
						}

						// Cancellation and generation invalidation happen before the persistent token is
						// cleared. No account-bound task can publish through a gate after this call.
						try
						{
							_auth?.SignOut();
						}
						catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
						{
							// GitHubDeviceFlowAuth clears its in-memory session before persisting the signed-out
							// marker. Keep the UI disconnected, but tell the author that the saved session may
							// need another cleanup attempt before the next app launch.
							_logger.LogWarning(ex, "Could not persist the GitHub sign-out marker");
							persistenceMessage =
								"Disconnected for this session, but SpecDesk couldn't remove the saved GitHub authorization. Try disconnecting again after closing apps that may be using it.";
						}
					}
				}
			}
			_ = ResetChatAgentAsync();
			if (persistenceMessage is null)
			{
				SendCurrentAccount();
			}
			else
			{
				InvalidateAccountDetails();
				SendAccount(signedIn: false, login: null, persistenceMessage);
			}
		}
	}

	private void SendCurrentAccount()
	{
		// Ready can request the account outside an auth handler. Serialize its read-and-refresh claim with
		// sign-out so no new account-list CTS can appear between invalidation and token removal.
		lock (_signInPublishSync)
		{
			SendCurrentAccountPublished();
		}
	}

	private void SendCurrentAccountPublished()
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
			InvalidateAccountDetails();
			SendAccount(false, login: null, message: null, available: false);
			return;
		}

		bool signedIn = _auth.IsSignedIn();
		string? login = signedIn ? _auth.SignedInLogin() : null;
		SendAccount(signedIn, login, message: null);
		if (signedIn)
		{
			RefreshAccountOrganizations(login);
		}
		else
		{
			InvalidateAccountDetails();
		}
	}

	private void RefreshAccountOrganizations(string? login)
	{
		if (_auth is null)
		{
			return;
		}
		if (_repositoryCatalog is null)
		{
			SendAccount(
				signedIn: true,
				login: login,
				message: "Organizations are unavailable in this build.",
				organizations: []);
			return;
		}

		CancellationTokenSource cts;
		long generation;
		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}
			_accountDetailsCts?.Cancel();
			cts = new CancellationTokenSource();
			_accountDetailsCts = cts;
			generation = ++_accountDetailsGeneration;
		}

		CancellationToken cancellationToken = cts.Token;
		_ = Task.Run(async () =>
		{
			try
			{
				await Task.WhenAll(
					RefreshAccountOrganizationsAsync(generation, login, cancellationToken),
					RefreshAccountRepositoriesAsync(generation, cancellationToken));
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				// A newer account state owns the status bar now.
			}
			finally
			{
				lock (_sync)
				{
					if (ReferenceEquals(_accountDetailsCts, cts))
					{
						_accountDetailsCts = null;
					}
				}
				cts.Dispose();
			}
		});
	}

	private async Task RefreshAccountOrganizationsAsync(
		long generation, string? login, CancellationToken cancellationToken)
	{
		try
		{
			IReadOnlyList<string> organizations = await _auth!.WithAccessTokenAsync(
				(token, ct) => _repositoryCatalog!.GetOrganizationsAsync(token, ct),
				cancellationToken);
			PublishAccountOrganizationsIfCurrent(generation, login, organizations, message: null);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not load GitHub organizations for the account status");
			PublishAccountOrganizationsIfCurrent(
				generation,
				login,
				[],
				"Organizations unavailable — check your connection or reconnect GitHub to refresh access.");
		}
	}

	private async Task RefreshAccountRepositoriesAsync(long generation, CancellationToken cancellationToken)
	{
		try
		{
			IReadOnlyList<GitHubRepositoryOption> repositories = await _auth!.WithAccessTokenAsync(
				(token, ct) => _repositoryCatalog!.GetRepositoriesAsync(token, ct),
				cancellationToken);
			PublishAccountRepositoriesIfCurrent(generation, repositories);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not load repositories for GitHub autocomplete");
			PublishAccountRepositoriesIfCurrent(generation, []);
		}
	}

	private void PublishAccountOrganizationsIfCurrent(
		long generation,
		string? login,
		IReadOnlyList<string> organizations,
		string? message)
	{
		lock (_signInPublishSync)
		{
			lock (_sync)
			{
				if (_disposed || generation != _accountDetailsGeneration || _auth?.IsSignedIn() != true)
				{
					return;
				}
			}
			SendAccount(
				signedIn: true,
				login: login,
				message: message,
				organizations: organizations);
		}
	}

	private void PublishAccountRepositoriesIfCurrent(
		long generation, IReadOnlyList<GitHubRepositoryOption> repositories)
	{
		lock (_signInPublishSync)
		{
			lock (_sync)
			{
				if (_disposed || generation != _accountDetailsGeneration || _auth?.IsSignedIn() != true)
				{
					return;
				}
			}
			SendRepositories(repositories);
		}
	}

	private void InvalidateAccountDetails()
	{
		lock (_sync)
		{
			_accountDetailsGeneration++;
			_accountDetailsCts?.Cancel();
			_accountDetailsCts = null;
		}
	}

	private void SendAccount(
		bool signedIn,
		string? login,
		string? message,
		bool available = true,
		IReadOnlyList<string>? organizations = null)
	{
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.GitHubAccount,
			new GitHubAccountPayload(available, signedIn, login, message, organizations)));
		if (!signedIn)
		{
			SendRepositories([]);
		}
	}

	private void SendRepositories(IReadOnlyList<GitHubRepositoryOption> repositories) =>
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.GitHubRepositories,
			new GitHubRepositoriesPayload(repositories
				.Select(repository => new GitHubRepositoryOptionPayload(
					repository.FullName,
					repository.Description))
				.ToArray())));

	private static string SignInMessage(SignInOutcome outcome) => outcome switch
	{
		SignInOutcome.Expired or SignInOutcome.TimedOut => "Your sign-in code expired. Connect again to retry.",
		SignInOutcome.Denied => "Sign-in was declined on GitHub.",
		SignInOutcome.Unreachable => "Couldn't reach GitHub. Check your connection and try again.",
		SignInOutcome.StorageFailed => "Signed in to GitHub, but couldn't save it on this device. Try again.",
		_ => "Couldn't sign in to GitHub. Please try again.",
	};
}
