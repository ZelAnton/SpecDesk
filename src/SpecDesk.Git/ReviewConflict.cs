namespace SpecDesk.Git;

/// <summary>
/// Both sides of a document that a competing published change collides with (docs/design/04-git-workflow.md,
/// "Someone else changed this too"). Extracted purely from the object database — <see cref="Mine"/> is the
/// author's working-branch version and <see cref="Theirs"/> is the latest published (base) version — so the
/// host can show the author both plain texts to reconcile. Critically, NEITHER side ever carries git conflict
/// markers (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> / <c>=======</c> / <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>): each is
/// the whole, clean content of one side, never a merged file with inline markers.
/// </summary>
/// <param name="RepoRelativePath">The conflicting document's path relative to the repo root (forward slashes).</param>
/// <param name="Mine">The author's version of the document (their working branch's committed content).</param>
/// <param name="Theirs">The latest published version of the document (the fetched base's content).</param>
public sealed record ReviewShareConflict(string RepoRelativePath, string Mine, string Theirs);

/// <summary>How the author chose to reconcile a <see cref="ReviewShareConflict"/> — which whole side of the
/// document to keep. "Combine" is the host's concern (it reconciles as <see cref="KeepMine"/> and then shows
/// both sides for manual editing), so it is not a distinct value here.</summary>
public enum ConflictResolution
{
    /// <summary>Keep the author's version of the document; fold in the base's non-conflicting changes.</summary>
    KeepMine,

    /// <summary>Take the latest published version of the document; drop the author's conflicting edit to it.</summary>
    KeepTheirs,
}
