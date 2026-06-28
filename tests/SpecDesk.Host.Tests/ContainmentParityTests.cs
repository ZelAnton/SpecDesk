using SpecDesk.Core;

namespace SpecDesk.Host.Tests;

[TestFixture]
public sealed class ContainmentParityTests
{
    // The read-path (app://, AppAssetResolver) and write-path (image engine, ImageEngine) containment
    // guards share the same descent + escape-rejection algorithm in two languages. Both are individually
    // correct and tested; the standing risk is that a future hardening of one silently leaves the other
    // behind in that SECURITY-CRITICAL shared part. This pins the two to agree on a shared adversarial set
    // so such drift breaks CI. The one intentional difference — the root directory itself, which the write
    // path counts as inside (the image folder may be the repo root) and the read path excludes (it serves
    // files, not the directory) — is deliberately not asserted here. (Reparse-point traversal — the other
    // half of each guard — needs filesystem junction fixtures and is covered separately.)

    private static string Root() =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "specdesk-root")));

    [Test]
    public void Both_containment_guards_agree_on_descent_paths_and_reject_escapes()
    {
        string root = Root();
        (string Candidate, bool Inside)[] cases =
        [
            (Path.Combine(root, "a", "b.png"), true), // a path directly inside
            (Path.Combine(root, "deep", "deeper", "x.md"), true), // a deep path inside
            (Path.Combine(root, "..", "evil.png"), false), // climbs out via ..
            (Path.Combine(root, "a", "..", "..", "out.png"), false), // climbs out from within
            (root + "-evil", false), // a sibling sharing the root's name prefix (the near-miss)
            (Path.GetFullPath(Path.Combine(Path.GetTempPath(), "specdesk-other-root")), false), // unrelated
        ];

        Assert.Multiple(() =>
        {
            foreach ((string candidate, bool inside) in cases)
            {
                Assert.That(AppAssetResolver.IsInside(root, candidate), Is.EqualTo(inside),
                    $"read-path (AppAssetResolver.IsInside) wrong for '{candidate}'");
                Assert.That(ImageEngine.isInside(root, candidate), Is.EqualTo(inside),
                    $"write-path (ImageEngine.isInside) wrong for '{candidate}'");
            }
        });
    }
}
