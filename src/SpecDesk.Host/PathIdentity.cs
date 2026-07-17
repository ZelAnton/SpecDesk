namespace SpecDesk.Host;

/// <summary>
/// The single home for "is this the same path?" decisions in the Host. The host asks three
/// deliberately different questions about paths, and conflating them is exactly what reintroduces
/// the S-03 class of "wrong document" faults (losing or corrupting work because two different
/// on-disk objects were treated as the same document, or vice versa). Each question therefore has
/// one named policy — the two string-comparison policies live here; the destructive directory-entry
/// policy lives next to the native path canonicalisation it depends on, in
/// <see cref="WindowsHandleFileDeletion"/>:
///
/// <list type="number">
/// <item><b>Filesystem-location identity</b> — <see cref="SameFilesystemPath"/>: "do these two path
/// strings name the same on-disk location under the platform's default case-insensitive
/// filesystem?" Normalises each side with <c>Path.GetFullPath</c> and compares
/// <see cref="StringComparison.OrdinalIgnoreCase"/>. This is the <i>inclusive</i> policy: it is used
/// to match user-supplied or stored paths (workspace roots, registered clones, recents, the delete
/// request's "is this the active document / inside this Disk root" gating) where a case variant of
/// the same location must resolve to the same entry rather than to a spurious duplicate. Callers
/// still reach it through the existing <c>SameFullPath</c> wrapper on the controller.</item>
///
/// <item><b>Session-document identity</b> — <see cref="SameSessionPath"/>: "does an in-flight session
/// operation still target the exact same open document / repository root it captured a snapshot of?"
/// Compares the already-normalised <c>_currentPath</c> / <c>_repoRoot</c> snapshots
/// <see cref="StringComparison.Ordinal"/>, i.e. case-<i>sensitively</i>, and therefore fails closed:
/// if the stored path string changed at all — including only in case — the operation is treated as
/// stale and abandoned rather than allowed to write a stale buffer, or clear the editor, against
/// what may be a different directory entry on a per-directory case-sensitive NTFS folder. This is the
/// S-03-safe direction, and it mirrors the strictness of the destructive directory-entry policy.
/// Used by every <c>IsDocument*Current*</c> / <c>TryClaimDocumentMutation</c> currency check and by
/// the folder-tree request currency check.</item>
///
/// <item><b>Directory-entry identity (destructive operations)</b> — lives in
/// <see cref="WindowsHandleFileDeletion.AreSameCanonicalHandlePath"/> and
/// <see cref="WindowsHandleFileDeletion.IsCanonicalHandleDescendant"/>: kernel final paths compared
/// with a case-<i>sensitive</i> tail (real directory entries can differ only in case on a
/// case-sensitive NTFS directory) and a case-insensitive volume authority (a drive designator or an
/// UNC server/share pair is not a directory entry). Bound to a validated kernel handle so a namespace
/// swap fails closed. This is what <see cref="WindowsHandleFileDeletion"/> deletes through, and what
/// the delete path uses to confirm the active document was not swapped for a case variant before it
/// clears the editor.</item>
/// </list>
///
/// The two "identity for an in-flight or destructive operation" policies (session-document and
/// directory-entry) are case-sensitive and fail closed on purpose; only the "did the user mean this
/// same stored location" policy is case-insensitive.
/// </summary>
internal static class PathIdentity
{
	/// <summary>
	/// Filesystem-location identity (policy 1 in the type remarks): case-insensitive after
	/// <c>Path.GetFullPath</c> normalisation. A malformed path on either side is never equal to
	/// anything. Use for matching user-supplied / stored paths under the default case-insensitive
	/// filesystem, not for guarding an in-flight session or destructive operation.
	/// </summary>
	internal static bool SameFilesystemPath(string left, string right)
	{
		string? leftFull = TryFullPath(left);
		string? rightFull = TryFullPath(right);
		return leftFull is not null && rightFull is not null
			&& string.Equals(leftFull, rightFull, StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Session-document identity (policy 2 in the type remarks): exact, case-sensitive comparison of
	/// two already-normalised path snapshots (<c>_currentPath</c> / <c>_repoRoot</c> and the value
	/// captured with them). Deliberately fails closed on any difference — including case only — so an
	/// in-flight operation never proceeds against a path that was reassigned underneath it. Nulls are
	/// permitted and two nulls compare equal (an absent repo root that stayed absent is still current).
	/// </summary>
	internal static bool SameSessionPath(string? left, string? right) =>
		string.Equals(left, right, StringComparison.Ordinal);

	private static string? TryFullPath(string path)
	{
		try
		{
			return Path.GetFullPath(path);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return null;
		}
	}
}
