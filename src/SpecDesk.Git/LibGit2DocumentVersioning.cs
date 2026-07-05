using LibGit2Sharp;

namespace SpecDesk.Git;

/// <summary>
/// The <see cref="IDocumentVersioning"/> + <see cref="IGitPublishing"/> implementation backed by
/// LibGit2Sharp. Each call opens and disposes its own <see cref="Repository"/> handle — the operations
/// are infrequent (edit start, save a version, discard, push), so a short-lived handle keeps the class
/// stateless and thread-safe.
/// </summary>
public sealed class LibGit2DocumentVersioning : IDocumentVersioning, IGitPublishing
{
    // Used when the repository has no committer identity configured (a freshly seeded demo repo).
    private static readonly Identity FallbackIdentity = new("SpecDesk", "specdesk@localhost");

    public bool IsVersioned(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        return Directory.Exists(repoRoot) && Repository.IsValid(repoRoot);
    }

    public string? ReadHeadContent(string repoRoot, string repoRelativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        ArgumentException.ThrowIfNullOrEmpty(repoRelativePath);

        using Repository repo = new(repoRoot);
        // No commit yet (unborn HEAD) → there is no committed version to diff against.
        if (repo.Head.Tip is null)
        {
            return null;
        }

        // Tree[path] is null when the file is not tracked at HEAD (new, never committed); its target is
        // a Blob for a file (not a Tree/subdir). GetContentText decodes the blob as text.
        return repo.Head.Tip.Tree[repoRelativePath]?.Target is Blob blob ? blob.GetContentText() : null;
    }

    public void Initialize(string repoRoot, string commitMessage)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        ArgumentException.ThrowIfNullOrEmpty(commitMessage);

        Repository.Init(repoRoot);
        using Repository repo = new(repoRoot);
        // Land the first commit on `main` rather than libgit2's default `master`. Repointing the
        // symbolic HEAD is only valid while it is still unborn (before any commit exists); on an
        // already-initialized repo that has commits, leave HEAD alone so we never repoint it at a
        // non-existent ref and break the repo.
        if (repo.Info.IsHeadUnborn)
        {
            repo.Refs.UpdateTarget("HEAD", "refs/heads/main");
        }

        Commands.Stage(repo, "*");
        TryCommit(repo, commitMessage);
    }

    public string? CurrentBranch(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        using Repository repo = new(repoRoot);
        return repo.Head.FriendlyName;
    }

    public EditSession BeginEdit(string repoRoot, string branchName, string preferredBase)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        ArgumentException.ThrowIfNullOrEmpty(branchName);
        ArgumentException.ThrowIfNullOrEmpty(preferredBase);

        using Repository repo = new(repoRoot);

        // Fork from the published base when it exists, never assuming HEAD is already on it: a
        // previous session may have left a working branch checked out. Fall back to the current
        // branch only when the configured base is absent.
        Branch? baseRef = repo.Branches[preferredBase];
        // Fall back to the current branch only when HEAD is on one. A detached HEAD has no friendly
        // base name to fork from (and to return to on Discard), so refuse rather than fabricate the
        // libgit2 placeholder "(no branch)", which would later be stored as the base and misused.
        if (baseRef is null && repo.Info.IsHeadDetached)
        {
            throw new InvalidOperationException("The repository is not on a branch.");
        }

        string baseName = baseRef?.FriendlyName ?? repo.Head.FriendlyName;
        Commit? tip = baseRef?.Tip ?? repo.Head.Tip;
        if (tip is null)
        {
            // A working branch forks from the latest published commit; there isn't one yet.
            throw new InvalidOperationException("The repository has no commits yet.");
        }

        // The working branch must differ from the base: otherwise Discard would later check out the
        // base and then delete the same branch, destroying the author's published history.
        if (string.Equals(branchName, baseName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The draft name must differ from the published branch.");
        }

        // Refuse rather than silently discard someone else's uncommitted work: autosave writes the working
        // copy to disk WITHOUT committing, so a *different* document's draft can leave the tree dirty on
        // its own branch while this document's edit begins. A forced checkout below resets the ENTIRE
        // working tree to branchName's commit, not just this document's file — if the tree is currently
        // dirty on some other branch, that draft's unsaved autosave would be wiped with no way to recover
        // it. Only skip the check when the dirty tree already belongs to the branch we're about to check
        // out: resuming/restarting the SAME document's own abandoned session is expected to reset its own
        // stray state (a separate, narrower concern — see the cross-restart lifecycle gap tracked
        // elsewhere) and carries no risk of destroying a different document's work.
        bool alreadyOnTargetBranch =
            !repo.Info.IsHeadDetached && string.Equals(repo.Head.FriendlyName, branchName, StringComparison.Ordinal);
        if (!alreadyOnTargetBranch && repo.RetrieveStatus(new StatusOptions { IncludeUntracked = false }).IsDirty)
        {
            string dirtyBranch = repo.Info.IsHeadDetached ? repo.Head.Tip?.Sha ?? "HEAD" : repo.Head.FriendlyName;
            throw new DirtyWorkingTreeException(dirtyBranch);
        }

        Branch working = repo.Branches[branchName] ?? repo.CreateBranch(branchName, tip);
        // Force the checkout: in the new model autosave writes the working copy to disk WITHOUT
        // committing, so a prior session on the SAME branch, abandoned without "Save a version" (and
        // without Discard), leaves the working tree dirty. A plain checkout would throw
        // CheckoutConflictException and block editing entirely. Beginning an edit means "switch to this
        // branch's tip"; only the uncommitted stray changes are reset (saved versions on the branch are
        // preserved). Matches Discard's use of a forced checkout. The guard above has already ruled out
        // the tree being dirty on any OTHER branch.
        Commands.Checkout(repo, working, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
        return new EditSession(working.FriendlyName, baseName);
    }

    public CommitResult SaveVersion(string repoRoot, string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        ArgumentException.ThrowIfNullOrEmpty(message);

        using Repository repo = new(repoRoot);
        // Stage every working-tree change, not just the document: a draft's assets (e.g. an image
        // pasted into the editor and written into the repo) must ride along in the same commit, or
        // they would be orphaned — a broken link in the commit and a leftover file after a discard.
        // This suits the single-author local draft model; finer-grained staging is a later concern.
        Commands.Stage(repo, "*");
        Commit? commit = TryCommit(repo, message);
        return commit is null
            ? new CommitResult(false, null, DateTimeOffset.Now)
            : new CommitResult(true, commit.Sha, commit.Author.When);
    }

    public void Discard(string repoRoot, string workingBranch, string baseBranch)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        ArgumentException.ThrowIfNullOrEmpty(workingBranch);
        ArgumentException.ThrowIfNullOrEmpty(baseBranch);

        using Repository repo = new(repoRoot);

        // Never delete the base branch itself. If the working and base names coincide (a name
        // collision, or a session that reused the published branch), removing it would destroy the
        // author's history and leave HEAD detached — there is then nothing to discard.
        if (string.Equals(workingBranch, baseBranch, StringComparison.Ordinal))
        {
            return;
        }

        Branch? @base = repo.Branches[baseBranch];
        if (@base is not null)
        {
            // Force the checkout: discarding is meant to throw away the draft, so uncommitted
            // working-tree changes must not block the return to the published version.
            Commands.Checkout(repo, @base, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
        }

        // Removing the working branch is safe now that it is no longer checked out. Skip it when it was
        // never created/already gone, or — if the base was absent so the checkout above was skipped —
        // when it is still the current HEAD (libgit2 refuses to remove the checked-out branch).
        Branch? working = repo.Branches[workingBranch];
        if (working is not null && !working.IsCurrentRepositoryHead)
        {
            repo.Branches.Remove(workingBranch);
        }
    }

    public string? RemoteUrl(string repoRoot, string remoteName = "origin")
    {
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        ArgumentException.ThrowIfNullOrEmpty(remoteName);

        using Repository repo = new(repoRoot);
        return repo.Network.Remotes[remoteName]?.Url;
    }

    public string? LastVersionNote(string repoRoot, string branchName)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        ArgumentException.ThrowIfNullOrEmpty(branchName);

        using Repository repo = new(repoRoot);
        // MessageShort is the commit's first line (the version-note subject); null when the branch
        // doesn't exist or points at no commit.
        return repo.Branches[branchName]?.Tip?.MessageShort;
    }

    public bool HasCommitsToReview(string repoRoot, string branchName, string baseBranch)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        ArgumentException.ThrowIfNullOrEmpty(branchName);
        ArgumentException.ThrowIfNullOrEmpty(baseBranch);

        using Repository repo = new(repoRoot);
        Commit? branchTip = repo.Branches[branchName]?.Tip;
        if (branchTip is null)
        {
            return false;
        }

        Commit? baseTip = repo.Branches[baseBranch]?.Tip;
        if (baseTip is null)
        {
            // No base branch to compare against — any commit on the working branch is unreviewed.
            return true;
        }

        // AheadBy counts commits reachable from the branch tip but not the base; >0 means there is
        // something to open a pull request for. (AheadBy is null only for unrelated histories — treat
        // that as "has commits" so we never block on a corner case.)
        HistoryDivergence divergence = repo.ObjectDatabase.CalculateHistoryDivergence(branchTip, baseTip);
        return divergence.AheadBy is null or > 0;
    }

    public void PushBranch(
        string repoRoot,
        string branchName,
        string accessToken,
        string remoteName = "origin",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        ArgumentException.ThrowIfNullOrEmpty(branchName);
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        ArgumentException.ThrowIfNullOrEmpty(remoteName);
        cancellationToken.ThrowIfCancellationRequested();

        using Repository repo = new(repoRoot);
        Remote remote = repo.Network.Remotes[remoteName]
            ?? throw new InvalidOperationException($"The repository has no '{remoteName}' remote.");
        Branch branch = repo.Branches[branchName]
            ?? throw new InvalidOperationException($"The repository has no branch '{branchName}'.");

        // Network.Push returns normally even when the remote rejects the ref update (non-fast-forward, a
        // protected branch, a pre-receive hook) — libgit2 only reports that through this callback. Without
        // it, a rejected push would look identical to a successful one to every caller.
        string? rejectedReference = null;
        string? rejectionMessage = null;
        PushOptions options = new()
        {
            // See ResolveCredentials for why this only ever hands the token to an HTTPS github.com host.
            CredentialsProvider = (url, _, _) => ResolveCredentials(url, accessToken),
            // Abort a stalled transfer when the caller cancels (the host bounds the send with a timeout).
            // The initial connect/handshake phase isn't surfaced through this callback, so a stall there
            // is bounded only by the OS socket timeout — a known LibGit2Sharp limitation.
            OnPushTransferProgress = (_, _, _) => !cancellationToken.IsCancellationRequested,
            // Fires once per ref the remote refused to update; capture it so it can be turned into an
            // exception after Push returns (Push itself never throws for a server-side rejection).
            OnPushStatusError = pushStatusError =>
            {
                rejectedReference = pushStatusError.Reference;
                rejectionMessage = pushStatusError.Message;
            },
        };
        // The single-ref form pushes the local branch to the remote ref of the same name.
        repo.Network.Push(remote, branch.CanonicalName, options);
        ThrowIfRejected(rejectedReference, rejectionMessage);
    }

    // Extracted from PushBranch so the rejection → exception translation is directly unit-testable: a real
    // server-side rejection (a protected branch, a refusing pre-receive hook) can only be produced by an
    // actual GitHub remote or a hook-capable git transport, neither of which the local-bare-repo test
    // fixture can exercise — this lets the test drive the same code with the plain values the callback
    // would have captured.
    internal static void ThrowIfRejected(string? rejectedReference, string? rejectionMessage)
    {
        if (rejectedReference is not null)
        {
            throw new InvalidOperationException(
                $"The remote rejected the push of '{rejectedReference}': {rejectionMessage}");
        }
    }

    // GitHub accepts the OAuth token as the password over HTTPS with any non-empty username (the
    // convention is "x-access-token"); the token never appears in a URL. `url` is the endpoint libgit2 is
    // actually authenticating against — which reflects the remote's pushurl, not merely the fetch URL the
    // caller validated — so we only release the token to an HTTPS github.com host.
    //
    // Anything else (a pushurl re-pointed at a look-alike, an SSH remote, a local-file remote, a bare
    // "http://" mirror of github.com) must NOT fall back to `DefaultCredentials()`: that value is
    // `GIT_CREDENTIAL_DEFAULT`, which hands over the current Windows user's Negotiate/NTLM session — an
    // NTLM challenge/response an attacker-controlled host could capture or relay. Refusing outright (rather
    // than returning `null`, whose native-interop null-handling is undocumented) is the only choice that
    // deterministically never negotiates a credential with a non-GitHub host — extracted as its own method
    // so this decision is directly unit-testable (the local-bare-repo test fixture never invokes
    // `CredentialsProvider` at all, since local-file transport skips authentication entirely).
    internal static Credentials ResolveCredentials(string? url, string accessToken) =>
        IsGitHubHttps(url)
            ? new UsernamePasswordCredentials { Username = "x-access-token", Password = accessToken }
            : throw new InvalidOperationException(
                $"Refusing to authenticate against a non-GitHub push endpoint '{url}'.");

    // Whether a URL libgit2 is authenticating against is an HTTPS github.com endpoint — the only target
    // the OAuth token may be sent to. Kept here (rather than reusing SpecDesk.GitHub's richer parser) so
    // the Git layer stays free of a dependency on SpecDesk.GitHub.
    private static bool IsGitHubHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase);

    // Commit the staged tree, or return null when there is nothing staged to commit (an unchanged
    // document on autosave, or an empty seed folder).
    private static Commit? TryCommit(Repository repo, string message)
    {
        Signature signature =
            repo.Config.BuildSignature(DateTimeOffset.Now)
            ?? new Signature(FallbackIdentity, DateTimeOffset.Now);
        try
        {
            return repo.Commit(message, signature, signature);
        }
        catch (EmptyCommitException)
        {
            return null;
        }
    }
}
