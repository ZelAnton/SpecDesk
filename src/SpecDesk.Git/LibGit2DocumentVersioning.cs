using LibGit2Sharp;

namespace SpecDesk.Git;

/// <summary>
/// The <see cref="IDocumentVersioning"/> implementation backed by LibGit2Sharp. Each call opens and
/// disposes its own <see cref="Repository"/> handle — the operations are infrequent (edit start,
/// save a version, discard), so a short-lived handle keeps the class stateless and thread-safe.
/// </summary>
public sealed class LibGit2DocumentVersioning : IDocumentVersioning
{
    // Used when the repository has no committer identity configured (a freshly seeded demo repo).
    private static readonly Identity FallbackIdentity = new("SpecDesk", "specdesk@localhost");

    public bool IsVersioned(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        return Directory.Exists(repoRoot) && Repository.IsValid(repoRoot);
    }

    public void Initialize(string repoRoot, string commitMessage)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        ArgumentException.ThrowIfNullOrEmpty(commitMessage);

        Repository.Init(repoRoot);
        using Repository repo = new(repoRoot);
        // Land the first commit on `main` rather than libgit2's default `master`. Repointing the
        // symbolic HEAD is only valid while it is still unborn (before any commit exists).
        repo.Refs.UpdateTarget("HEAD", "refs/heads/main");
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
        string baseName = baseRef?.FriendlyName ?? repo.Head.FriendlyName;
        Commit? tip = baseRef?.Tip ?? repo.Head.Tip;
        if (tip is null)
        {
            // A working branch forks from the latest published commit; there isn't one yet.
            throw new InvalidOperationException("The repository has no commits yet.");
        }

        Branch working = repo.Branches[branchName] ?? repo.CreateBranch(branchName, tip);
        // Force the checkout: in the new model autosave writes the working copy to disk WITHOUT
        // committing, so a prior session abandoned without "Save a version" (and without Discard)
        // leaves the working tree dirty. A plain checkout would throw CheckoutConflictException and
        // block editing entirely. Beginning an edit means "switch to this branch's tip"; only the
        // uncommitted stray changes are reset (saved versions on the branch are preserved). Matches
        // Discard's use of a forced checkout.
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
        Branch? @base = repo.Branches[baseBranch];
        if (@base is not null)
        {
            // Force the checkout: discarding is meant to throw away the draft, so uncommitted
            // working-tree changes must not block the return to the published version.
            Commands.Checkout(repo, @base, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
        }

        // Removing the working branch is safe now that it is no longer checked out. If it was never
        // created (or already gone) there is simply nothing to remove.
        if (repo.Branches[workingBranch] is not null)
        {
            repo.Branches.Remove(workingBranch);
        }
    }

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
