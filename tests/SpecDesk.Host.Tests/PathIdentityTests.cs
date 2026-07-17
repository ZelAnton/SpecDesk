namespace SpecDesk.Host.Tests;

/// <summary>
/// Locks the two string-comparison path policies that <see cref="PathIdentity"/> is the single home
/// for. Their divergence is deliberate and safety-relevant (the S-03 class of "wrong document"
/// faults): session-document identity is case-sensitive and fails closed, while filesystem-location
/// identity is case-insensitive. The directory-entry policy is covered by the deletion tests.
/// </summary>
[TestFixture]
public sealed class PathIdentityTests
{
	[Test]
	public void SameSessionPath_IsCaseSensitiveSoACaseVariantIsNeverTheSameDocument()
	{
		Assert.Multiple(() =>
		{
			Assert.That(
				PathIdentity.SameSessionPath(@"C:\repo\Spec.md", @"C:\repo\Spec.md"), Is.True,
				"an unchanged path is still the current document");
			Assert.That(
				PathIdentity.SameSessionPath(@"C:\repo\Spec.md", @"C:\repo\spec.md"), Is.False,
				"a case-only difference must fail closed, never conflate two directory entries");
			Assert.That(
				PathIdentity.SameSessionPath(@"C:\repo\Spec.md", @"C:\repo\Other.md"), Is.False);
		});
	}

	[Test]
	public void SameSessionPath_TreatsNullsAsAnAbsentButUnchangedValue()
	{
		Assert.Multiple(() =>
		{
			Assert.That(PathIdentity.SameSessionPath(null, null), Is.True);
			Assert.That(PathIdentity.SameSessionPath(null, @"C:\repo\Spec.md"), Is.False);
			Assert.That(PathIdentity.SameSessionPath(@"C:\repo\Spec.md", null), Is.False);
		});
	}

	[Test]
	public void SameFilesystemPath_IsCaseInsensitiveForACaseVariantOfTheSameLocation()
	{
		// Relative inputs normalise against the same working directory, so this exercises the policy's
		// case-insensitivity without depending on a platform-specific absolute path shape.
		Assert.Multiple(() =>
		{
			Assert.That(
				PathIdentity.SameFilesystemPath(
					Path.Combine("repo", "Spec.md"), Path.Combine("repo", "spec.md")),
				Is.True,
				"a case variant of the same stored location must resolve to the same entry");
			Assert.That(
				PathIdentity.SameFilesystemPath(
					Path.Combine("repo", "Spec.md"), Path.Combine("repo", "Other.md")),
				Is.False);
		});
	}

	[Test]
	public void SameFilesystemPath_TreatsAMalformedPathAsEqualToNothing()
	{
		Assert.That(PathIdentity.SameFilesystemPath("bad\0path", "bad\0path"), Is.False);
	}
}
