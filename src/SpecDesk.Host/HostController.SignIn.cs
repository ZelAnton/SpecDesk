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

	private CancellationTokenSource RotateAccountSessionLocked()
	{
		CancellationTokenSource previous = _accountSessionCts;
		_accountSessionGeneration++;
		_accountSessionCts = new CancellationTokenSource();
		return previous;
	}

	private static void CancelAndDispose(CancellationTokenSource? cancellation)
	{
		if (cancellation is null)
		{
			return;
		}
		cancellation.Cancel();
		cancellation.Dispose();
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
		CancellationTokenSource? previousCts;
		lock (_signInPublishSync)
		{
			lock (_sync)
			{
				if (_disposed)
				{
					return;
				}
				previousCts = _signInCts;
				cts = new CancellationTokenSource();
				_signInCts = cts;
			}
		}
		previousCts?.Cancel();

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
		CancellationTokenSource? retiredAccountSession = null;
		lock (_signInPublishSync)
		{
			lock (_remotePublishSync)
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
						retiredAccountSession = RotateAccountSessionLocked();
						bool clearRemoteTree = _remoteTreeContext is not null
							|| _remoteBrowseIntentRepoId is not null;
						_remoteTreeContext = null;
						bool clearRemoteDocument = _remoteDocument is not null;
						if (clearRemoteDocument)
						{
							ClearActiveDocumentStateLocked();
						}
						PublishRetiredRemoteAccountState(clearRemoteTree, clearRemoteDocument);
					}
					_signInCts = null;
				}
			}
		}
		if (actions is not null)
		{
			PendingRepoActionsTakenForTest?.Set();
			PendingRepoActionsResumeForTest?.Wait();
		}
		CancelAndDispose(retiredAccountSession);
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
			PendingRepoActionsResumedForTest?.Set();
		}
		else
		{
			CompletePendingDocumentOpen(actions);
		}
	}
	private void OnGitHubSignInCancel()
	{
		string? persistenceMessage = null;
		PendingRepoActions? actions;
		CancellationTokenSource? retiredSignIn;
		lock (_signInPublishSync)
		{
			lock (_sync)
			{
				actions = TakePendingRepoActions();
				retiredSignIn = _signInCts;
				_signInCts = null;
			}
		}
		retiredSignIn?.Cancel();
		try
		{
			if (retiredSignIn is not null)
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
		CompletePendingDocumentOpen(actions);
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
	private void OnGitHubSignOut()
	{
		string? persistenceMessage = null;
		PendingRepoActions? pendingActions = null;
		WorkspaceStore.WorkspaceStateSnapshot? registrationRollbackState = null;
		long canceledDocumentOpenRequestId = 0;
		HashSet<CancellationTokenSource> cancellations = [];
		CancelCurrentAccountSession();
		lock (_signInPublishSync)
		{
			lock (_clonePublishSync)
			{
				lock (_remotePublishSync)
				{
					lock (_repositoryDescriptionPublishSync)
					{
						lock (_sync)
						{
							cancellations.Add(RotateAccountSessionLocked());
							RetirePublishInFlightLocked();
							pendingActions = TakePendingRepoActions();
							if (_signInCts is not null) cancellations.Add(_signInCts);
							_signInCts = null;
							_accountDetailsGeneration++;
							if (_accountDetailsCts is not null) cancellations.Add(_accountDetailsCts);
							_accountDetailsCts = null;
							_repositoryDescriptionGeneration++;
							if (_repositoryDescriptionCts is not null) cancellations.Add(_repositoryDescriptionCts);
							_repositoryDescriptionCts = null;
							_cloneGeneration++;
							if (_cloneCts is not null) cancellations.Add(_cloneCts);
							_repoMetadataGeneration++;
							foreach (RepoMetadataLookup lookup in _repoMetadataLookups.Values)
							{
								if (lookup.RollbackRegistrationOnAccountInvalidation
									&& _workspace?.TryRollbackNewRepoRegistration(
										lookup.Registration, out WorkspaceStore.WorkspaceStateSnapshot rollbackState) == true)
								{
									registrationRollbackState = rollbackState;
								}
								cancellations.Add(lookup.Cts);
							}
							_repoMetadataLookups.Clear();
							_remoteBrowseGeneration++;
							_remoteBrowseIntentGeneration++;
							_remoteFileGeneration++;
							bool clearRemoteTree = _remoteTreeContext is not null
								|| _remoteBrowseIntentRepoId is not null;
							_remoteTreeContext = null;
							bool clearRemoteDocument = _remoteDocument is not null;
							if (clearRemoteDocument)
							{
								ClearActiveDocumentStateLocked();
							}
							PublishRetiredRemoteAccountState(clearRemoteTree, clearRemoteDocument);
							if (_remoteBrowseCts is not null) cancellations.Add(_remoteBrowseCts);
							_remoteBrowseCts = null;
							_remoteBrowseRepoId = null;
							_remoteBrowseIntentRepoId = null;
							if (_remoteFileCts is not null) cancellations.Add(_remoteFileCts);
							_remoteFileCts = null;
							_remoteFileRepoId = null;
							canceledDocumentOpenRequestId = _remoteFileRequestId;
							_remoteFileRequestId = 0;
							if (_chatCts is not null) cancellations.Add(_chatCts);
						}
					}
				}
			}
		}
		foreach (CancellationTokenSource cancellation in cancellations)
		{
			cancellation.Cancel();
		}
		try
		{
			_auth?.SignOut();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_logger.LogWarning(ex, "Could not persist the GitHub sign-out marker");
			persistenceMessage =
				"Disconnected for this session, but SpecDesk couldn't remove the saved GitHub authorization. Try disconnecting again after closing apps that may be using it.";
		}
		if (registrationRollbackState is not null)
		{
			EmitWorkspaceState(registrationRollbackState);
		}
		CompletePendingDocumentOpen(pendingActions);
		CompleteDocumentOpen(canceledDocumentOpenRequestId, succeeded: false);
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

	private void PublishRetiredRemoteAccountState(bool clearTree, bool clearDocument)
	{
		if (clearTree)
		{
			Emit(IpcSerializer.SerializeEvent(MessageKinds.Tree, new TreePayload(string.Empty, [])));
		}
		if (clearDocument)
		{
			PublishActiveDocumentCleared();
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
				organizations: [],
				avatarUrl: null);
			return;
		}

		CancellationTokenSource cts;
		CancellationTokenSource? previousCts;
		long generation;
		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}
			previousCts = _accountDetailsCts;
			cts = new CancellationTokenSource();
			_accountDetailsCts = cts;
			generation = ++_accountDetailsGeneration;
		}
		previousCts?.Cancel();

		CancellationToken cancellationToken = cts.Token;
		_ = Task.Run(async () =>
		{
			try
			{
				await Task.WhenAll(
					RefreshAccountDetailsAsync(generation, login, cancellationToken),
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

	private async Task RefreshAccountDetailsAsync(
		long generation, string? login, CancellationToken cancellationToken)
	{
		try
		{
			(IReadOnlyList<string> Organizations, string? AvatarUrl) details =
				await _auth!.WithAccessTokenAsync(async (token, ct) =>
				{
					Task<IReadOnlyList<string>> organizations =
						_repositoryCatalog!.GetOrganizationsAsync(token, ct);
					Task<string?> avatar = TryLoadAccountAvatarAsync(token, ct);
					await Task.WhenAll(organizations, avatar);
					return (await organizations, await avatar);
				},
				cancellationToken);
			PublishAccountDetailsIfCurrent(
				generation, login, details.Organizations, details.AvatarUrl, message: null);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not load GitHub organizations for the account status");
			PublishAccountDetailsIfCurrent(
				generation,
				login,
				[],
				null,
				"Organizations unavailable — check your connection or reconnect GitHub to refresh access.");
		}
	}

	private async Task<string?> TryLoadAccountAvatarAsync(string token, CancellationToken cancellationToken)
	{
		try
		{
			return await _repositoryCatalog!.GetAvatarUrlAsync(token, cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not load the GitHub account avatar");
			return null;
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

	private void PublishAccountDetailsIfCurrent(
		long generation,
		string? login,
		IReadOnlyList<string> organizations,
		string? avatarUrl,
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
				organizations: organizations,
				avatarUrl: avatarUrl);
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
		CancellationTokenSource? cancellation;
		lock (_sync)
		{
			_accountDetailsGeneration++;
			cancellation = _accountDetailsCts;
			_accountDetailsCts = null;
		}
		cancellation?.Cancel();
	}

	private void SendAccount(
		bool signedIn,
		string? login,
		string? message,
		bool available = true,
		IReadOnlyList<string>? organizations = null,
		string? avatarUrl = null)
	{
		Emit(IpcSerializer.SerializeEvent(
			MessageKinds.GitHubAccount,
			new GitHubAccountPayload(available, signedIn, login, message, organizations, avatarUrl)));
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
