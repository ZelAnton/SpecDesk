using System.Security.Cryptography;
using System.Text;

namespace SpecDesk.GitHub;

/// <summary>Encrypts/decrypts the token bytes at rest. A seam so the file-store logic is testable
/// cross-platform with an identity fake, while production uses the OS keystore (DPAPI).</summary>
internal interface ITokenProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>
/// Windows DPAPI protector: encrypts to the logged-in user (<see cref="DataProtectionScope.CurrentUser"/>),
/// so the token file is unreadable by other users and unusable if copied to another machine. Windows-only
/// — <see cref="ProtectedData"/> throws <see cref="PlatformNotSupportedException"/> elsewhere (the v1
/// target is Windows; a cross-platform keystore is a later concern).
/// </summary>
internal sealed class DpapiTokenProtector : ITokenProtector
{
    // App-specific additional entropy (M-11): plain CurrentUser-scoped DPAPI is readable/decryptable by any
    // other process running as the same Windows user that happens to know the file's path — this raises
    // that bar to "and knows this constant", without needing a per-install secret (the entropy is baked into
    // the binary, not derived from anything sensitive). Stable across releases so an already-saved token
    // keeps decrypting after an upgrade — never change this value once shipped.
    private static readonly byte[] AppEntropy = Encoding.UTF8.GetBytes("SpecDesk.GitHub.TokenStore.v1");

    public byte[] Protect(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The DPAPI token store is Windows-only.");
        }

        return ProtectedData.Protect(plaintext, AppEntropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The DPAPI token store is Windows-only.");
        }

        try
        {
            return ProtectedData.Unprotect(ciphertext, AppEntropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // A token saved before this entropy existed (protected with optionalEntropy: null) — retry the
            // old way so an in-place upgrade doesn't silently sign everyone out. A genuinely corrupt/foreign
            // file still throws here and is treated as "signed out" by TokenStore.Load's own catch.
            return ProtectedData.Unprotect(ciphertext, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
    }
}
