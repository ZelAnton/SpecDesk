using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace SpecDesk.GitHub.Tests;

// The real DPAPI protector is Windows-only; this smoke-tests the actual crypto on the Windows CI leg.
// [SupportedOSPlatform] (on top of the NUnit [Platform] filter, which the CA1416 analyzer doesn't know
// about) tells the analyzer these direct ProtectedData calls are guarded by the class only running on
// Windows, the same way DpapiTokenProtector's own OperatingSystem.IsWindows() checks do for production code.
[TestFixture]
[Platform("Win")]
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenProtectorTests
{
    [Test]
    public void Protect_then_Unprotect_round_trips_the_bytes()
    {
        DpapiTokenProtector protector = new();
        byte[] plaintext = Encoding.UTF8.GetBytes("gho_secret_token");

        byte[] cipher = protector.Protect(plaintext);

        Assert.Multiple(() =>
        {
            Assert.That(cipher, Is.Not.EqualTo(plaintext)); // actually encrypted, not stored in the clear
            Assert.That(protector.Unprotect(cipher), Is.EqualTo(plaintext));
        });
    }

    // M-11: plain CurrentUser-scoped DPAPI (no additional entropy) is decryptable by any other process
    // running as the same Windows user that knows the file's path — app-specific entropy raises that bar.
    [Test]
    public void Protect_uses_app_specific_entropy_a_plain_dpapi_unprotect_cannot_reverse_it()
    {
        DpapiTokenProtector protector = new();
        byte[] plaintext = Encoding.UTF8.GetBytes("gho_secret_token");

        byte[] cipher = protector.Protect(plaintext);

        // Unprotecting with no entropy at all (what any other same-user process would try) must fail — the
        // whole point of the entropy is that knowing the path/scope alone is no longer enough.
        Assert.Throws<CryptographicException>(
            () => ProtectedData.Unprotect(cipher, optionalEntropy: null, DataProtectionScope.CurrentUser));
    }

    // M-11 migration: a token saved by a version before this entropy existed (optionalEntropy: null) must
    // still decrypt after the upgrade — otherwise every signed-in user would be silently signed out.
    [Test]
    public void Unprotect_still_reads_a_token_saved_before_entropy_was_added()
    {
        byte[] plaintext = Encoding.UTF8.GetBytes("gho_pre_upgrade_token");
        byte[] legacyCipher = ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);

        DpapiTokenProtector protector = new();

        Assert.That(protector.Unprotect(legacyCipher), Is.EqualTo(plaintext));
    }
}
