using System.Security.Cryptography;

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
    public byte[] Protect(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The DPAPI token store is Windows-only.");
        }

        return ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The DPAPI token store is Windows-only.");
        }

        return ProtectedData.Unprotect(ciphertext, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }
}
